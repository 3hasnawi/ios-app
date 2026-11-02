using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using Claunia.PropertyList;

namespace SalaryDesktop.Core;

public static class ReconciliationBackupExport
{
    private static bool SameEmployee(Employee a, Employee b) => a.Month == b.Month && a.Name == b.Name && a.Rank == b.Rank && a.Workplace == b.Workplace && a.Net == b.Net &&
        SameBytes(a.Items, b.Items) && SameBytes(a.Details, b.Details) && SameBytes(a.RawColumns, b.RawColumns);
    private static bool SameBytes(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);
    private static bool SameEmployees(IReadOnlyList<Employee> a, IReadOnlyList<Employee> b) => a.Count == b.Count && a.Zip(b).All(p => SameEmployee(p.First, p.Second));
    private static byte[] Summary(IEnumerable<Employee> employees)
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
    private static double IosDate(DateOnly date)
    {
        var zone = TimeZoneInfo.TryFindSystemTimeZoneById("Asia/Baghdad", out var iraq) ? iraq :
            TimeZoneInfo.TryFindSystemTimeZoneById("Arab Standard Time", out var windows) ? windows : TimeZoneInfo.CreateCustomTimeZone("Iraq", TimeSpan.FromHours(3), "Iraq", "Iraq");
        var local = DateTime.SpecifyKind(date.ToDateTime(new TimeOnly(12, 0)), DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(local, zone);
        return (utc - new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
    }
    private static byte[] LedgerPlist(IReadOnlyList<BankPeriod> periods)
    {
        foreach (var period in periods) _ = BankReconciliation.Calculate(period);
        if (periods.Count > 5000 || periods.Select(p => p.Id).Distinct().Count() != periods.Count) throw new InvalidDataException("فترات المطابقة غير صالحة.");
        var body = new { version = 1, periods = periods.Select(p => new { id = p.Id, month = p.Month, start = IosDate(p.Start), end = IosDate(p.End), openingBalance = p.OpeningBalance,
            memberFirst = p.FirstMember, memberSecond = p.SecondMember, acceptedContractMissing = p.AcceptedContractMissing, acceptedContractAmount = p.AcceptedContractAmount,
            movements = p.Movements.Select(m => new { id = m.Id, date = IosDate(m.Date), description = m.Description, amount = m.Amount,
                direction = m.Direction == MovementDirection.Credit ? "credit" : "debit", sourceID = m.SourceId, sourceLineIDs = m.SourceLineIds,
                refundOf = m.RefundOf, isRefund = m.IsRefund, isFunding = m.IsFunding, matched = m.Matched }).ToArray() }).ToArray() };
        using var plist = new MemoryStream(); PropertyListParser.SaveAsBinary(new NSData(JsonSerializer.SerializeToUtf8Bytes(body)), plist); return plist.ToArray();
    }
    public static void Write(BackupSnapshot source, IReadOnlyList<BankPeriod> periods, string destination, SalaryTrustLedger? trust = null)
    {
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
            bool Rewritten(string name) => name == "settings.json" || (changedAccounts && name == "accounts.jsonl") || ((changedAccounts || changedMonths || changedBooks) && name == "manifest.json") ||
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
                        foreach (var account in source.Accounts) writer.WriteLine(JsonSerializer.Serialize(new { entity = account.Entity, iban = account.Iban, branch = account.Branch, swift = account.Swift, localAccount = account.LocalAccount }));
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
                            writer.WriteLine(node.ToJsonString());
                        }
                    }
                    else if (changedEmployees && oldEntry.FullName == "employees.jsonl")
                    {
                        using var writer = new StreamWriter(outStream, new UTF8Encoding(false), 4096, true);
                        foreach (var e in source.Employees) writer.WriteLine(JsonSerializer.Serialize(new { month = e.Month, name = e.Name, rank = e.Rank, workplace = e.Workplace, net = e.Net, items = e.Items, details = e.Details, rawColumns = e.RawColumns }));
                    }
                    else if (changedDeductions && oldEntry.FullName == "deductions.jsonl")
                    {
                        using var writer = new StreamWriter(outStream, new UTF8Encoding(false), 4096, true);
                        foreach (var d in source.Deductions) writer.WriteLine(JsonSerializer.Serialize(new { month = d.Month, name = d.Name, title = d.Title, amount = d.Amount, note = d.Note, rank = d.Rank }));
                    }
                    else if (changedBooks && oldEntry.FullName == "books/index.json")
                    {
                        using var originalIndex = oldEntry.Open();
                        outStream.Write(BookIndex(originalIndex, source.Books));
                    }
                    else if ((changedAccounts || changedMonths || changedBooks) && oldEntry.FullName == "manifest.json")
                    {
                        using var originalManifest = oldEntry.Open();
                        var manifest = JsonNode.Parse(originalManifest)?.AsObject() ?? throw new InvalidDataException("فهرس النسخة غير صالح.");
                        manifest["accounts"] = source.Accounts.Count;
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
            if (!restored.Accounts.SequenceEqual(source.Accounts)) throw new InvalidDataException("تعذّر التحقق من حسابات الجهات في النسخة الجديدة.");
            if (!restored.Months.SequenceEqual(source.Months) || !SameEmployees(restored.Employees, source.Employees) || !restored.Deductions.SequenceEqual(source.Deductions))
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
