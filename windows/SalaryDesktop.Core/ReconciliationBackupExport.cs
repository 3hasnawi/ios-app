using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using System.Text;
using Claunia.PropertyList;

namespace SalaryDesktop.Core;

public static class ReconciliationBackupExport
{
    // JSON في أرشيف النسخة بيانات لا تُعرض في HTML؛ حفظ العربية مباشرة يمنع تضخم السجلات بـ \\uXXXX.
    private static readonly JsonSerializerOptions BackupJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static bool SameEmployee(Employee a, Employee b) => a.Month == b.Month && a.Name == b.Name && a.Rank == b.Rank && a.Workplace == b.Workplace && a.Net == b.Net &&
        SameBytes(a.Items, b.Items) && SameBytes(a.Details, b.Details) && SameBytes(a.RawColumns, b.RawColumns);
    private static bool SameBytes(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);
    private static bool SameEmployees(IReadOnlyList<Employee> a, IReadOnlyList<Employee> b) => a.Count == b.Count && a.Zip(b).All(p => SameEmployee(p.First, p.Second));
    // السجلات المعروفة فقط: JSON مكافئ بترميز UTF-8 بدلاً من تهريب كل حرف عربي.
    // أي خصائص إضافية، عناوين مكررة كخصائص JSON، أو بنية غير متوقعة تبقى ببايتاتها الأصلية.
    private static byte[]? CompactRawColumns(byte[]? raw)
    {
        if (raw is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return raw;
            var props = root.EnumerateObject().ToArray();
            if (props.Length != 2 || props.Count(p => p.NameEquals("headers")) != 1 || props.Count(p => p.NameEquals("values")) != 1) return raw;
            var headers = root.GetProperty("headers"); var values = root.GetProperty("values");
            if (headers.ValueKind != JsonValueKind.Array || values.ValueKind != JsonValueKind.Array || headers.GetArrayLength() != values.GetArrayLength() ||
                headers.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String) || values.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String)) return raw;
            var compact = JsonSerializer.SerializeToUtf8Bytes(root, BackupJson);
            if (compact.Length >= raw.Length) return raw;
            using var check = JsonDocument.Parse(compact);
            if (!JsonElement.DeepEquals(root, check.RootElement)) return raw;
            return compact;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException) { return raw; }
    }
    private static bool SameRestoredEmployees(IReadOnlyList<Employee> expected, IReadOnlyList<Employee> actual) =>
        expected.Count == actual.Count && expected.Zip(actual).All(p =>
            SameEmployee(p.First, p.Second) || SameEmployee(p.First with { RawColumns = CompactRawColumns(p.First.RawColumns) }, p.Second));
    internal static byte[] Summary(IEnumerable<Employee> employees)
    {
        var result = new Dictionary<string, (int Count, decimal Total)>(StringComparer.Ordinal);
        foreach (var employee in employees)
        {
            if (employee.Items is null) continue;
            var items = JsonSerializer.Deserialize<Dictionary<string, decimal>>(employee.Items) ?? throw new InvalidDataException("بنود الراتب غير صالحة.");
            foreach (var (title, amount) in items) { var old = result.GetValueOrDefault(title); result[title] = (checked(old.Count + 1), checked(old.Total + amount)); }
        }
        return JsonSerializer.SerializeToUtf8Bytes(result.ToDictionary(p => p.Key, p => new { count = p.Value.Count, total = p.Value.Total }, StringComparer.Ordinal));
    }
    private static string BookEntry(Guid id) => $"books/{id.ToString().ToUpperInvariant()}.pdf";
    /// فهرس الكتب بصيغة الآيفون (OfficialBook): تُبقى حقول كل كتاب الأصلية (ومنها createdAt) وتُحدَّث الاسم والشهر والجهة والصفحات.
    private static byte[] BookIndex(Stream original, IReadOnlyList<ArchiveBook> books)
    {
        var old = new Dictionary<Guid, JsonObject>();
        if (JsonNode.Parse(original) is not JsonArray array) throw new InvalidDataException("فهرس الأرشيف غير صالح.");
        foreach (var node in array)
            if (node is JsonObject item && item["id"] is JsonValue value && value.TryGetValue<string>(out var text) && Guid.TryParse(text, out var id)) old[id] = item;
        var result = new JsonArray();
        foreach (var book in books)
        {
            var node = old.TryGetValue(book.Id, out var found) ? (JsonObject)found.DeepClone() : new JsonObject { ["id"] = book.Id.ToString().ToUpperInvariant() };
            node["title"] = book.Title; node["month"] = book.Month; node["pages"] = book.Pages;
            if (node["createdAt"] is null) node["createdAt"] = book.CreatedAt ?? LocalArchiveStore.IosNow();
            if (book.Entity is null) node.Remove("entity"); else node["entity"] = book.Entity;
            result.Add(node);
        }
        return Encoding.UTF8.GetBytes(result.ToJsonString());
    }
    private static byte[] LedgerPlist(IReadOnlyList<BankPeriod> periods) => BankReconciliation.LedgerPlist(periods);
    /// كل نسخة شاملة تُكتب من ويندوز تأخذ تاريخ إنشاء جديداً في manifest.json (ثواني الآيفون منذ ٢٠٠١ بتوقيت UTC، مثل JSONEncoder)،
    /// حتى لو لم تتغير الأعداد؛ وتبقى بقية حقول الفهرس كما هي بما فيها غير المعروفة، ولا تتغير تواريخ الكتب. createdAt للاختبار فقط.
    public static double IosSeconds(DateTimeOffset moment) => (moment.UtcDateTime - new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
    private static readonly string[] ManifestUpdated = ["createdAt", "accounts", "books", "months", "employees", "deductions", "version"];
    public static void Write(BackupSnapshot source, IReadOnlyList<BankPeriod> periods, string destination, SalaryTrustLedger? trust = null, DateTimeOffset? createdAt = null)
    {
        var created = IosSeconds(createdAt ?? DateTimeOffset.UtcNow);
        var target = Path.GetFullPath(destination); var directory = Path.GetDirectoryName(target) ?? throw new InvalidDataException("المجلد غير صالح.");
        var name = Path.GetFileNameWithoutExtension(target);
        if (!string.Equals(Path.GetExtension(target), ".zip", StringComparison.OrdinalIgnoreCase) || string.Equals(target, source.SourcePath, StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(directory, Path.GetDirectoryName(source.SourcePath), StringComparison.OrdinalIgnoreCase) && name.Length == 64 && name.All(c => char.IsAsciiHexDigit(c))))
            throw new InvalidDataException("وجهة النسخة غير صالحة.");
        var settings = source.SettingsPlists.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        settings["bankReconciliations"] = LedgerPlist(periods);
        if (trust is not null) { SalaryTrust.ValidateBooks(trust, source.Books); settings[SalaryTrust.SettingsKey] = SalaryTrust.Plist(trust); }
        var settingsJson = JsonSerializer.SerializeToUtf8Bytes(settings);
        var temp = Path.Combine(directory, ".salary-ledger-backup-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var original = BackupReader.Read(source.SourcePath);
            if (original.SourceDigest != source.SourceDigest) throw new InvalidDataException("تغيّرت نسخة المصدر؛ لم يُحفظ الملف.");
            var changedAccounts = !original.Accounts.SequenceEqual(source.Accounts);
            var changedEmployees = !SameEmployees(original.Employees, source.Employees);
            var changedDeductions = !original.Deductions.SequenceEqual(source.Deductions);
            var changedMonths = !original.Months.SequenceEqual(source.Months) || changedEmployees || changedDeductions;
            if (original.Months.Any(m => !source.Months.Any(n => n.Name == m.Name)) || source.Months.Select(m => m.Name).Distinct(StringComparer.Ordinal).Count() != source.Months.Count)
                throw new InvalidDataException("لا يمكن حذف شهر أو تكراره عند حفظ النسخة.");
            var localBooks = source.LocalBookFiles ?? new Dictionary<Guid, string>();
            var changedBooks = localBooks.Count > 0 || !original.Books.SequenceEqual(source.Books);
            if (changedBooks && (original.Version < 3 || source.Books.Select(b => b.Id).Distinct().Count() != source.Books.Count || localBooks.Keys.Any(id => !source.Books.Any(b => b.Id == id))
                || source.Books.Any(b => !localBooks.ContainsKey(b.Id) && !original.Books.Any(o => o.Id == b.Id && o.Pages == b.Pages))))
                throw new InvalidDataException("تعديلات الأرشيف لا تطابق النسخة؛ أعد فتح الأرشيف.");
            var droppedBooks = changedBooks ? original.Books.Where(o => localBooks.ContainsKey(o.Id) || !source.Books.Any(b => b.Id == o.Id)).Select(o => BookEntry(o.Id)).ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
            bool Rewritten(string name) => name == "settings.json" || (changedAccounts && name == "accounts.jsonl") || name == "manifest.json" ||
                (changedMonths && name == "months.jsonl") || (changedEmployees && name == "employees.jsonl") || (changedDeductions && name == "deductions.jsonl") || (changedBooks && name == "books/index.json");
            if (changedAccounts && (original.Version < 2 || source.Accounts.Count < original.Accounts.Count ||
                !original.Accounts.Select(a => a.Entity).SequenceEqual(source.Accounts.Take(original.Accounts.Count).Select(a => a.Entity))))
                throw new InvalidDataException("لا يمكن حفظ الحسابات في صيغة النسخة هذه أو مع تغيير أسمائها.");
            using var input = new FileStream(source.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (Convert.ToHexString(SHA256.HashData(input)) != source.SourceDigest) throw new InvalidDataException("تغيّرت نسخة المصدر؛ لم يُحفظ الملف.");
            input.Position = 0;
            using (var oldZip = new ZipArchive(input, ZipArchiveMode.Read, true))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var newZip = new ZipArchive(output, ZipArchiveMode.Create))
            {
                foreach (var oldEntry in oldZip.Entries)
                {
                    if (droppedBooks.Contains(oldEntry.FullName)) continue;
                    var newEntry = newZip.CreateEntry(oldEntry.FullName, CompressionLevel.Optimal);
                    newEntry.LastWriteTime = oldEntry.LastWriteTime; newEntry.ExternalAttributes = oldEntry.ExternalAttributes;
                    using var outStream = newEntry.Open();
                    if (oldEntry.FullName == "settings.json") outStream.Write(settingsJson);
                    else if (changedAccounts && oldEntry.FullName == "accounts.jsonl")
                    {
                        using var writer = new StreamWriter(outStream, new UTF8Encoding(false), 4096, true);
                        foreach (var account in source.Accounts) writer.WriteLine(JsonSerializer.Serialize(new { entity = account.Entity, iban = account.Iban, branch = account.Branch, swift = account.Swift, localAccount = account.LocalAccount }, BackupJson));
                    }
                    else if (changedMonths && oldEntry.FullName == "months.jsonl")
                    {
                        var existing = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
                        using (var oldRows = new StreamReader(oldEntry.Open(), Encoding.UTF8))
                            while (oldRows.ReadLine() is { } line) { var node = JsonNode.Parse(line)?.AsObject() ?? throw new InvalidDataException("بيانات الأشهر غير صالحة."); existing.Add(node["name"]?.GetValue<string>() ?? throw new InvalidDataException("اسم شهر غير صالح."), node); }
                        using var writer = new StreamWriter(outStream, new UTF8Encoding(false), 4096, true);
                        foreach (var month in source.Months)
                        {
                            if (!existing.TryGetValue(month.Name, out var node))
                                node = new JsonObject { ["name"] = month.Name, ["importedAt"] = (DateTime.UtcNow - new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds };
                            node["employeeCount"] = month.EmployeeCount; node["deductionCount"] = month.DeductionCount;
                            var currentEmployees = source.Employees.Where(e => e.Month == month.Name).ToArray();
                            var oldEmployees = original.Employees.Where(e => e.Month == month.Name).ToArray();
                            if (!SameEmployees(oldEmployees, currentEmployees)) node["summary"] = Convert.ToBase64String(Summary(currentEmployees));
                            writer.WriteLine(node.ToJsonString(BackupJson));
                        }
                    }
                    else if (changedEmployees && oldEntry.FullName == "employees.jsonl")
                    {
                        using var writer = new StreamWriter(outStream, new UTF8Encoding(false), 4096, true);
                        foreach (var e in source.Employees) writer.WriteLine(JsonSerializer.Serialize(new { month = e.Month, name = e.Name, rank = e.Rank, workplace = e.Workplace, net = e.Net, items = e.Items, details = e.Details, rawColumns = CompactRawColumns(e.RawColumns) }, BackupJson));
                    }
                    else if (changedDeductions && oldEntry.FullName == "deductions.jsonl")
                    {
                        using var writer = new StreamWriter(outStream, new UTF8Encoding(false), 4096, true);
                        foreach (var d in source.Deductions) writer.WriteLine(JsonSerializer.Serialize(new { month = d.Month, name = d.Name, title = d.Title, amount = d.Amount, note = d.Note, rank = d.Rank }, BackupJson));
                    }
                    else if (changedBooks && oldEntry.FullName == "books/index.json")
                    {
                        using var originalIndex = oldEntry.Open();
                        outStream.Write(BookIndex(originalIndex, source.Books));
                    }
                    else if (oldEntry.FullName == "manifest.json")
                    {
                        using var originalManifest = oldEntry.Open();
                        var manifest = JsonNode.Parse(originalManifest)?.AsObject() ?? throw new InvalidDataException("فهرس النسخة غير صالح.");
                        manifest["createdAt"] = created;
                        if (settings.ContainsKey(ContractOverrides.SettingsKey)) manifest["version"] = 6;
                        if (changedAccounts || changedMonths || changedBooks) manifest["accounts"] = source.Accounts.Count;
                        if (changedBooks) manifest["books"] = source.Books.Count;
                        if (changedMonths) { manifest["months"] = new JsonArray(source.Months.Select(m => (JsonNode?)JsonValue.Create(m.Name)).ToArray()); manifest["employees"] = source.Employees.Count; manifest["deductions"] = source.Deductions.Count; }
                        JsonSerializer.Serialize(outStream, manifest);
                    }
                    else { using var inStream = oldEntry.Open(); inStream.CopyTo(outStream); }
                }
                // ملفات الكتب المضافة أو المستبدلة في ويندوز.
                foreach (var (id, file) in localBooks)
                {
                    var bytes = File.ReadAllBytes(file);
                    if (bytes.Length < 5 || !bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8)) throw new InvalidDataException("ملف كتاب محلي غير صالح.");
                    var entry = newZip.CreateEntry(BookEntry(id), CompressionLevel.Optimal);
                    using var stream = entry.Open(); stream.Write(bytes);
                }
            }
            var restored = BackupReader.Read(temp);
            if (restored.Version != (settings.ContainsKey(ContractOverrides.SettingsKey) ? 6 : source.Version)) throw new InvalidDataException("تعذّر التحقق من إصدار النسخة.");
            if (!restored.Accounts.SequenceEqual(source.Accounts)) throw new InvalidDataException("تعذّر التحقق من حسابات الجهات في النسخة الجديدة.");
            if (!restored.Months.SequenceEqual(source.Months) || !SameRestoredEmployees(source.Employees, restored.Employees) || !restored.Deductions.SequenceEqual(source.Deductions))
                throw new InvalidDataException("تعذّر التحقق من بيانات الأشهر في النسخة الجديدة.");
            if (!restored.Books.Select(b => b with { CreatedAt = null }).SequenceEqual(source.Books.Select(b => b with { CreatedAt = null }))) throw new InvalidDataException("تعذّر التحقق من فهرس الأرشيف في النسخة الجديدة.");
            if (localBooks.Count > 0)
                using (var check = ZipFile.OpenRead(temp))
                    foreach (var (id, file) in localBooks)
                    {
                        using var stored = (check.GetEntry(BookEntry(id)) ?? throw new InvalidDataException("ملف كتاب مفقود في النسخة الجديدة.")).Open();
                        if (!SHA256.HashData(stored).AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(file)))) throw new InvalidDataException("تعذّر التحقق من ملف كتاب في النسخة الجديدة.");
                    }
            if (trust is not null && !SalaryTrust.Encode(SalaryTrust.ReadFromBackup(restored)).AsSpan().SequenceEqual(SalaryTrust.Encode(trust))) throw new InvalidDataException("تعذّر التحقق من دفتر الأمانات في النسخة الجديدة.");
            // الفهرس الجديد: تاريخ هذه النسخة، وبقية الحقول (ومنها ما لا يعرفه ويندوز) كما في المصدر.
            using (var oldZip = new ZipArchive(input, ZipArchiveMode.Read, true))
            using (var newZip = ZipFile.OpenRead(temp))
            {
                JsonObject Manifest(ZipArchive zip) { using var stream = (zip.GetEntry("manifest.json") ?? throw new InvalidDataException("فهرس النسخة مفقود.")).Open(); return JsonNode.Parse(stream)?.AsObject() ?? throw new InvalidDataException("فهرس النسخة غير صالح."); }
                var before = Manifest(oldZip); var after = Manifest(newZip);
                if (after["createdAt"] is not JsonValue value || !value.TryGetValue<double>(out var written) || Math.Abs(written - created) > 0.001 || restored.CreatedAt is not { } read || Math.Abs(read - created) > 0.001)
                    throw new InvalidDataException("تعذّر التحقق من تاريخ النسخة الجديدة.");
                var kept = before.Where(p => !ManifestUpdated.Contains(p.Key)).ToArray();
                if (after.Count(p => !ManifestUpdated.Contains(p.Key)) != kept.Length || kept.Any(p => !after.TryGetPropertyValue(p.Key, out var node) || !JsonNode.DeepEquals(p.Value, node)))
                    throw new InvalidDataException("تعذّر التحقق من فهرس النسخة الجديدة.");
            }
            var readPeriods = BankReconciliation.ReadFromBackup(restored);
            if (!LedgerPlist(readPeriods).AsSpan().SequenceEqual(LedgerPlist(periods))) throw new InvalidDataException("تعذّر التحقق من دفتر المطابقة في النسخة الجديدة.");
            using (var oldZip = new ZipArchive(input, ZipArchiveMode.Read, true))
            using (var newZip = ZipFile.OpenRead(temp))
            {
                foreach (var oldEntry in oldZip.Entries.Where(e => !Rewritten(e.FullName) && !droppedBooks.Contains(e.FullName)))
                {
                    var copy = newZip.GetEntry(oldEntry.FullName) ?? throw new InvalidDataException("ملف مفقود في النسخة الجديدة.");
                    using var a = oldEntry.Open(); using var b = copy.Open();
                    if (oldEntry.Length != copy.Length || !SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b))) throw new InvalidDataException("تعذّر التحقق من محتوى النسخة الجديدة.");
                }
            }
            input.Position = 0;
            if (Convert.ToHexString(SHA256.HashData(input)) != source.SourceDigest) throw new InvalidDataException("تغيّرت نسخة المصدر أثناء التصدير.");
            File.Move(temp, target, true);
        }
        finally { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
