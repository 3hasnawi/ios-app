using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using SalaryDesktop.Core;

namespace SalaryLocal.Core;

public sealed record IphoneInspection(int Version, int Months, int Employees, int Deductions, int Staff, int Accounts, int Books, string Digest, IReadOnlyList<string> Warnings);

/// تبادل ملفات فقط، لا اتصال بالآيفون ولا تعديل لملف المصدر. الاستيراد دائماً إلى مساحة مستقلة.
public static class IphoneInteropService
{
    private const string MetadataKey = "interop/metadata.json";
    private const string ExtraPrefix = "interop/extra/";
    private const int MaximumLine = 2_000_000;
    private const long MaximumArchive = 4L * 1024 * 1024 * 1024;
    private const decimal MaximumAmount = 9_007_199_254_740_991m;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = 64 };
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private sealed record Extras(int Count, IReadOnlyList<JsonObject> Values);
    private sealed record Metadata(int Version, JsonObject Manifest, IReadOnlyDictionary<string, JsonObject> Months, IReadOnlyDictionary<string, string> PayrollHashes, IReadOnlyDictionary<string, Dictionary<string, Extras>> ExtraFields);
    private sealed record Parsed(BackupSnapshot Snapshot, IReadOnlyList<StaffMember> Staff, IReadOnlyDictionary<string, byte[]> Attachments, Metadata Metadata);
    private static readonly HashSet<string> KnownEntries = new(StringComparer.Ordinal) { "manifest.json", "months.jsonl", "employees.jsonl", "deductions.jsonl", "staff.jsonl", "accounts.jsonl", "settings.json", "books/index.json" };
    private static InvalidDataException Invalid(string reason) => new("نسخة الآيفون غير صالحة: " + reason + " لم تُغيّر البيانات.");
    private static string Hash(ReadOnlySpan<byte> value) => Convert.ToHexString(SHA256.HashData(value));
    private static string Fingerprint<T>(T value) => Hash(JsonSerializer.SerializeToUtf8Bytes(value, Json));
    private static string PayrollHash(IEnumerable<Employee> rows) => Fingerprint(rows.ToArray());
    private static JsonObject Node<T>(T value) { var node = JsonSerializer.SerializeToNode(value, Json)!.AsObject(); if (value is ArchiveBook book) node["id"] = book.Id.ToString().ToUpperInvariant(); return node; }
    private static bool SafeName(string name) => name.Length is > 0 and <= 240 && !name.StartsWith('/') && !name.Contains('\\') && !name.Contains(':') && !name.Any(char.IsControl) && name.Split('/').All(p => p.Length > 0 && p is not "." and not "..");
    private static void DuplicateGuard(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in element.EnumerateObject()) { if (!names.Add(p.Name)) throw Invalid("حقول JSON مكررة"); DuplicateGuard(p.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) DuplicateGuard(child);
    }
    private static JsonObject ParseObject(string text)
    {
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 }); DuplicateGuard(doc.RootElement);
        return JsonNode.Parse(text)?.AsObject() ?? throw Invalid("سجل ليس كائناً");
    }
    private static byte[] Bytes(ZipArchiveEntry entry, long limit, CancellationToken cancel)
    {
        if (entry.Length < 0 || entry.Length > limit || entry.Length > int.MaxValue) throw Invalid("حجم ملف داخلي غير مدعوم");
        using var input = entry.Open(); using var output = new MemoryStream(); var buffer = new byte[65536]; long length = 0; int read;
        while ((read = input.Read(buffer)) > 0) { cancel.ThrowIfCancellationRequested(); length += read; if (length > limit || length > entry.Length) throw Invalid("حجم داخلي مخالف للفهرس"); output.Write(buffer, 0, read); }
        if (length != entry.Length) throw Invalid("ملف داخلي مبتور"); return output.ToArray();
    }
    private static IEnumerable<JsonObject> Lines(ZipArchiveEntry entry, CancellationToken cancel)
    {
        using var stream = entry.Open(); using var reader = new StreamReader(stream, Utf8, false, 65536); var buffer = new char[8192]; var line = new StringBuilder(); int read;
        while ((read = reader.Read(buffer)) > 0)
        {
            cancel.ThrowIfCancellationRequested();
            for (var i = 0; i < read; i++)
            {
                char c = buffer[i];
                if (c == '\n') { if (line.Length > 0 && line[^1] == '\r') line.Length--; if (line.Length == 0) throw Invalid("سطر فارغ"); yield return ParseObject(line.ToString()); line.Clear(); }
                else { if (line.Length >= MaximumLine) throw Invalid("سطر أكبر من الحد المدعوم"); line.Append(c); }
            }
        }
        if (line.Length > 0) { if (line[^1] == '\r') line.Length--; if (line.Length == 0) throw Invalid("سطر فارغ"); yield return ParseObject(line.ToString()); }
    }
    private static string Text(JsonObject obj, string key, bool allowEmpty = true)
    {
        if (obj[key] is not JsonValue value || !value.TryGetValue<string>(out var text) || text is null || (!allowEmpty && string.IsNullOrWhiteSpace(text))) throw Invalid("الحقل " + key);
        return text;
    }
    private static int Count(JsonObject obj, string key, int maximum = 500_000)
    { if (obj[key] is not JsonValue value || !value.TryGetValue<int>(out var number) || number < 0 || number > maximum) throw Invalid("العدد " + key); return number; }
    private static decimal? Amount(JsonObject obj, string key)
    {
        if (obj[key] is null) return null;
        if (obj[key] is not JsonValue number || !number.TryGetValue<decimal>(out var value) || value < -MaximumAmount || value > MaximumAmount) throw Invalid("المبلغ " + key);
        // لا نقبل تقريباً صامتاً من كسور ذات دقة أعلى من decimal.
        var literal = obj[key]!.ToJsonString(); if (literal.Length > 256) throw Invalid("صيغة مبلغ أطول من الحد المدعوم");
        if (!ExactDecimal(literal, value)) throw Invalid("دقة مبلغ أعلى من الحد المدعوم");
        return value;
    }
    private static bool ExactDecimal(string literal, decimal value)
    {
        static (System.Numerics.BigInteger number, int exponent) Parts(string text)
        {
            var split = text.Split(['e', 'E']); int exponent = split.Length == 2 ? int.Parse(split[1], CultureInfo.InvariantCulture) : 0;
            int dot = split[0].IndexOf('.'); if (dot >= 0) exponent -= split[0].Length - dot - 1;
            var n = System.Numerics.BigInteger.Parse(split[0].Replace(".", ""), CultureInfo.InvariantCulture);
            if (n == 0) return (0, 0); while (n % 10 == 0) { n /= 10; exponent++; } return (n, exponent);
        }
        try { return Parts(literal) == Parts(value.ToString(CultureInfo.InvariantCulture)); } catch (Exception ex) when (ex is FormatException or OverflowException) { return false; }
    }
    private static byte[]? Data(JsonObject obj, string key, bool required = false)
    {
        if (obj[key] is null) { if (required) throw Invalid("الحقل " + key); return null; }
        try { var bytes = Convert.FromBase64String(Text(obj, key)); if (bytes.Length > 12 * 1024 * 1024) throw Invalid("حقل بيانات كبير"); return bytes; }
        catch (FormatException) { throw Invalid("ترميز بيانات " + key); }
    }
    private static Parsed Read(string path, CancellationToken cancel)
    {
        try { return ReadCore(path, cancel); }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or ArgumentException or OverflowException or DecoderFallbackException) { throw new InvalidDataException("تعذّر قراءة نسخة الآيفون دون فقد. لم تُغيّر البيانات.", ex); }
    }
    private static Parsed ReadCore(string path, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested(); using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > MaximumArchive) throw Invalid("حجم الأرشيف غير مدعوم");
        var digest = Convert.ToHexString(SHA256.HashData(file)); file.Position = 0; using var zip = new ZipArchive(file, ZipArchiveMode.Read, true);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal); var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        if (zip.Entries.Count > 100_000) throw Invalid("عدد الملفات كبير");
        foreach (var e in zip.Entries)
        {
            cancel.ThrowIfCancellationRequested(); if (!SafeName(e.FullName) || !folded.Add(e.FullName) || !entries.TryAdd(e.FullName, e) || e.Length > 1_500_000_000 || (total = checked(total + e.Length)) > MaximumArchive) throw Invalid("مسار أو حجم أو اسم داخلي مكرر");
        }
        ZipArchiveEntry Required(string key) => entries.GetValueOrDefault(key) ?? throw Invalid("الملف مفقود: " + key);
        JsonObject Object(string key, int max = 1_000_000) => ParseObject(Utf8.GetString(Bytes(Required(key), max, cancel)));
        var manifest = Object("manifest.json"); int version = Count(manifest, "version", 10);
        if (version is < 1 or 7) throw Invalid("الإصدار غير مدعوم");
        if (manifest["createdAt"] is not JsonValue createdValue || !createdValue.TryGetValue<double>(out var createdSeconds) || BackupSnapshot.IosDate(createdSeconds) is null) throw Invalid("تاريخ النسخة");
        if (version == 1 && (entries.ContainsKey("accounts.jsonl") || manifest["accounts"] is not null && Count(manifest, "accounts") != 0) || version < 3 && (entries.Keys.Any(k => k.StartsWith("books/", StringComparison.Ordinal)) || manifest["books"] is not null && Count(manifest, "books") != 0)) throw Invalid("ملفات حسابات أو كتب لا تطابق إصدار النسخة");
        int expectedE = Count(manifest, "employees", 1_000_000), expectedD = Count(manifest, "deductions", 2_000_000), expectedS = Count(manifest, "staff"), expectedA = version >= 2 ? Count(manifest, "accounts") : 0, expectedB = version >= 3 ? Count(manifest, "books", 50_000) : 0;
        if (manifest["months"] is not JsonArray monthsArray || monthsArray.Count > 4000) throw Invalid("فهرس الأشهر");
        var names = monthsArray.Select(n => n?.GetValue<string>() ?? throw Invalid("اسم الشهر")).ToArray();
        if (names.Any(string.IsNullOrWhiteSpace) || names.Distinct(StringComparer.Ordinal).Count() != names.Length) throw Invalid("أسماء الأشهر مكررة");
        if (names.Length + expectedE + expectedD + expectedS + expectedA + expectedB == 0) throw Invalid("نسخة فارغة");
        var months = new List<PayrollMonth>(); var monthNodes = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var row in Lines(Required("months.jsonl"), cancel))
        {
            var name = Text(row, "name", false); if (!monthNodes.TryAdd(name, row) || months.Count >= names.Length) throw Invalid("شهر مكرر أو زائد");
            if (row["importedAt"] is not JsonValue time || !time.TryGetValue<double>(out var seconds) || BackupSnapshot.IosDate(seconds) is null) throw Invalid("تاريخ الشهر");
            _ = Data(row, "summary"); months.Add(new(name, Count(row, "employeeCount", 1_000_000), Count(row, "deductionCount", 2_000_000)));
        }
        if (!monthNodes.Keys.Order().SequenceEqual(names.Order())) throw Invalid("أسماء الأشهر تخالف الفهرس");
        var extraFields = new Dictionary<string, Dictionary<string, Extras>>(StringComparer.Ordinal);
        List<T> ReadRows<T>(string entry, int expected, Func<JsonObject, T> decode)
        {
            var result = new List<T>(); var extras = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
            foreach (var row in Lines(Required(entry), cancel))
            {
                if (result.Count >= expected) throw Invalid("عدد سجلات زائد: " + entry); var item = decode(row); result.Add(item); var key = Fingerprint(item); var extra = (JsonObject)row.DeepClone();
                foreach (var field in Node(item)) extra.Remove(field.Key);
                if (!extras.TryGetValue(key, out var list)) extras[key] = list = []; list.Add(extra);
            }
            if (result.Count != expected) throw Invalid("عدد السجلات لا يطابق الفهرس: " + entry);
            extraFields[entry] = extras.Where(p => p.Value.Any(n => n.Count > 0)).ToDictionary(p => p.Key, p => new Extras(p.Value.Count, p.Value), StringComparer.Ordinal); return result;
        }
        var employees = ReadRows("employees.jsonl", expectedE, r => new Employee(Text(r, "month", false), Text(r, "name", false), Text(r, "rank"), Text(r, "workplace"), Amount(r, "net"), Data(r, "items"), Data(r, "details"), Data(r, "rawColumns")));
        var deductions = ReadRows("deductions.jsonl", expectedD, r => new Deduction(Text(r, "month", false), Text(r, "name", false), Text(r, "title", false), Amount(r, "amount"), Text(r, "note"), Text(r, "rank")));
        var staff = ReadRows("staff.jsonl", expectedS, r => new StaffMember(Text(r, "name", false), Text(r, "mother"), Text(r, "stat"), Text(r, "unified"), Text(r, "rank"), Text(r, "status"), Text(r, "workplace"), Data(r, "details", true)));
        var accounts = version >= 2 ? ReadRows("accounts.jsonl", expectedA, r => new ExecutionAccount(Text(r, "entity", false), Text(r, "iban"), Text(r, "branch"), Text(r, "swift"), r["localAccount"] is null ? null : Text(r, "localAccount"))) : [];
        foreach (var month in months)
            if (employees.Count(e => e.Month == month.Name) != month.EmployeeCount || deductions.Count(d => d.Month == month.Name) != month.DeductionCount) throw Invalid("مجموع سجلات الشهر لا يطابق فهرسه");
        if (employees.Any(e => !monthNodes.ContainsKey(e.Month)) || deductions.Any(d => !monthNodes.ContainsKey(d.Month))) throw Invalid("سجل خارج الأشهر");
        var settings = new Dictionary<string, byte[]>(StringComparer.Ordinal); foreach (var p in Object("settings.json", 100_000_000)) { if (p.Value is null) throw Invalid("إعداد فارغ"); settings[p.Key] = Convert.FromBase64String(p.Value.GetValue<string>()); }
        IphoneFormatGuard.Validate(version, settings);
        var books = new List<ArchiveBook>(); var attachments = new Dictionary<string, byte[]>(StringComparer.Ordinal); long attachmentBytes = 0;
        void Attach(string key, byte[] value) { if ((attachmentBytes = checked(attachmentBytes + value.Length)) > 512_000_000) throw Invalid("المرفقات تتجاوز الحد المحلي 512 ميغابايت"); attachments.Add(key, value); }
        if (version >= 3)
        {
            var data = Bytes(Required("books/index.json"), 50_000_000, cancel); using var doc = JsonDocument.Parse(data); DuplicateGuard(doc.RootElement);
            var array = JsonNode.Parse(data)?.AsArray() ?? throw Invalid("فهرس الكتب"); if (array.Count != expectedB) throw Invalid("عدد الكتب"); var extra = new Dictionary<string, Extras>(StringComparer.Ordinal);
            foreach (var item in array)
            {
                var row = item?.AsObject() ?? throw Invalid("قيد كتاب"); var book = row.Deserialize<ArchiveBook>(Json) ?? throw Invalid("قيد كتاب");
                if (row["createdAt"] is not JsonValue created || !created.TryGetValue<double>(out var date) || BackupSnapshot.IosDate(date) is null) throw Invalid("تاريخ الكتاب");
                if (book.Id == Guid.Empty || string.IsNullOrWhiteSpace(book.Title) || string.IsNullOrWhiteSpace(book.Month) || book.Pages is < 1 or > 3 || books.Any(b => b.Id == book.Id)) throw Invalid("بيانات كتاب");
                var key = $"books/{book.Id.ToString().ToUpperInvariant()}.pdf"; var pdf = Bytes(Required(key), 100_000_000, cancel); if (ArchivePdf.Pages(pdf) != book.Pages) throw Invalid("صفحات كتاب PDF تخالف الفهرس"); Attach(key, pdf); books.Add(book);
                var unknown = (JsonObject)row.DeepClone(); foreach (var field in Node(book)) unknown.Remove(field.Key); if (unknown.Count > 0) extra[Fingerprint(book)] = new(1, [unknown]);
            }
            extraFields["books/index.json"] = extra;
        }
        foreach (var e in entries.Values.Where(e => !KnownEntries.Contains(e.FullName) && !attachments.ContainsKey(e.FullName))) Attach(ExtraPrefix + e.FullName, Bytes(e, 100_000_000, cancel));
        var metadata = new Metadata(version, manifest, monthNodes, months.ToDictionary(m => m.Name, m => PayrollHash(employees.Where(e => e.Month == m.Name)), StringComparer.Ordinal), extraFields);
        Attach(MetadataKey, JsonSerializer.SerializeToUtf8Bytes(metadata, Json));
        var snapshot = new BackupSnapshot(Path.GetFullPath(path), digest, version, months, employees, deductions, staff.Count, accounts, books, settings) { CreatedAt = createdSeconds };
        if (settings.ContainsKey(SalaryTrust.SettingsKey)) SalaryTrust.ValidateBooks(SalaryTrust.ReadFromBackup(snapshot), books);
        file.Position = 0; if (Convert.ToHexString(SHA256.HashData(file)) != digest) throw Invalid("تغيّر الملف أثناء القراءة");
        return new(snapshot, staff, attachments, metadata);
    }
    public static IphoneInspection Inspect(string path, CancellationToken cancellation = default)
    {
        var parsed = Read(path, cancellation); var s = parsed.Snapshot;
        var warnings = new List<string> { "التبادل ملفي فقط؛ لم يُختبر الاسترجاع الفعلي على الآيفون من هذه النسخة.", "نسخة الآيفون تنقل الحالة الحالية ولا تنقل تاريخ إصدارات SQLite. احفظ .salarybackup أيضاً.", "الملفات غير مشفرة. لا استبدال ولا دمج تلقائي بين الأجهزة." };
        if (s.Employees.Any(e => e.Net is null or < 0) || s.Deductions.Any(d => d.Amount < 0 || d.Amount is null && !PayrollAnalysis.IsInactive(d))) warnings.Add("توجد مبالغ قديمة فارغة أو سالبة، ستُحفظ كما هي وتحتاج تدقيقاً قبل الاعتماد.");
        if (parsed.Metadata.ExtraFields.Values.Any(v => v.Count > 0)) warnings.Add("توجد حقول إضافية محفوظة؛ سيُرفض التصدير إذا تعذّر ربطها بسجلاتها بعد تعديلها.");
        return new(s.Version, s.Months.Count, s.Employees.Count, s.Deductions.Count, s.StaffCount, s.Accounts.Count, s.Books.Count, s.SourceDigest, warnings);
    }
    public static string ImportNewWorkspace(string path, string workspacesRoot, string expectedDigest, CancellationToken cancellation = default)
    {
        var parsed = Read(path, cancellation); if (parsed.Snapshot.SourceDigest != expectedDigest) throw Invalid("تغيّرت النسخة منذ المعاينة");
        Directory.CreateDirectory(workspacesRoot); var staging = Path.Combine(workspacesRoot, ".iphone-" + Guid.NewGuid().ToString("N")); var destination = Path.Combine(workspacesRoot, Guid.NewGuid().ToString("N"));
        try
        {
            using (var store = new LocalPayrollStore(staging)) { store.ImportDataset(parsed.Snapshot, parsed.Staff, parsed.Attachments, store.GetStatus().RevisionId); }
            cancellation.ThrowIfCancellationRequested(); LocalPayrollStore.ValidateDatabase(Path.Combine(staging, "payroll.db")); Directory.Move(staging, destination); return destination;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    public static void Export(LocalPayrollStore store, string destination, CancellationToken cancellation = default)
    {
        var target = Path.GetFullPath(destination); if (!string.Equals(Path.GetExtension(target), ".zip", StringComparison.OrdinalIgnoreCase)) throw Invalid("اختر امتداد zip");
        if (File.Exists(target)) throw new IOException("لن تُستبدل نسخة موجودة؛ اختر اسماً جديداً.");
        cancellation.ThrowIfCancellationRequested(); var revision = store.GetStatus().RevisionId; var snapshot = store.GetSnapshot(); var staff = store.GetStaff(); var keys = store.GetAttachments();
        Metadata? metadata = keys.Contains(MetadataKey) ? JsonSerializer.Deserialize<Metadata>(store.ReadAttachment(MetadataKey), Json) ?? throw Invalid("بيانات التبادل المحلية") : null;
        var version = IphoneFormatGuard.ExportVersion(snapshot.SettingsPlists, metadata?.Version ?? 5);
        foreach (var e in snapshot.Employees) { IphoneMoney(e.Net); if (e.Items is not null) foreach (var amount in ReadItems(e.Items).Values) IphoneMoney(amount); }
        foreach (var d in snapshot.Deductions) IphoneMoney(d.Amount);
        if (snapshot.Months.Count + snapshot.StaffCount + snapshot.Accounts.Count + snapshot.Books.Count == 0) throw Invalid("مساحة فارغة");
        var temp = Path.Combine(Path.GetDirectoryName(target)!, ".iphone-export-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.WriteThrough))
            {
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create, true))
                {
                    var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    Stream Entry(string name) { cancellation.ThrowIfCancellationRequested(); if (!SafeName(name) || !written.Add(name)) throw Invalid("اسم تصدير متعارض"); return zip.CreateEntry(name, CompressionLevel.Optimal).Open(); }
                    void Write(string name, byte[] bytes) { using var output = Entry(name); output.Write(bytes); }
                    void Rows<T>(string name, IReadOnlyList<T> rows, bool array = false)
                    {
                        var extras = metadata?.ExtraFields.GetValueOrDefault(name) ?? new Dictionary<string, Extras>(); var counts = rows.GroupBy(Fingerprint).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
                        if (extras.Any(p => counts.GetValueOrDefault(p.Key) != p.Value.Count)) throw Invalid("تغيّر سجل ذو حقول إضافية في " + name + "؛ احتفظ بالنسخة المحلية ولا تُسقط الحقول");
                        var positions = new Dictionary<string, int>(StringComparer.Ordinal); using var output = Entry(name); using var writer = new StreamWriter(output, Utf8, 65536); if (array) writer.Write('['); bool first = true;
                        foreach (var row in rows)
                        {
                            cancellation.ThrowIfCancellationRequested(); var node = Node(row); var hash = Fingerprint(row);
                            if (extras.TryGetValue(hash, out var extra)) { int index = positions.GetValueOrDefault(hash); foreach (var p in extra.Values[index]) { if (node.ContainsKey(p.Key)) throw Invalid("تعارض حقل إضافي"); node[p.Key] = p.Value?.DeepClone(); } positions[hash] = index + 1; }
                            if (array && !first) writer.Write(','); writer.Write(node.ToJsonString(Json)); if (!array) writer.Write('\n'); first = false;
                        }
                        if (array) writer.Write(']');
                    }
                    var now = (DateTimeOffset.UtcNow - new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalSeconds;
                    var manifest = metadata is null ? new JsonObject() : (JsonObject)metadata.Manifest.DeepClone(); manifest["version"] = version; manifest["createdAt"] = now; manifest["months"] = new JsonArray(snapshot.Months.Select(m => (JsonNode?)JsonValue.Create(m.Name)).ToArray()); manifest["employees"] = snapshot.Employees.Count; manifest["deductions"] = snapshot.Deductions.Count; manifest["staff"] = staff.Count; manifest["accounts"] = snapshot.Accounts.Count; manifest["books"] = snapshot.Books.Count;
                    Write("manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, Json)); Write("settings.json", JsonSerializer.SerializeToUtf8Bytes(snapshot.SettingsPlists, Json));
                    using (var stream = Entry("months.jsonl")) using (var writer = new StreamWriter(stream, Utf8, 65536))
                    {
                        foreach (var month in snapshot.Months)
                        {
                            var node = metadata?.Months.GetValueOrDefault(month.Name)?.DeepClone().AsObject() ?? new JsonObject { ["name"] = month.Name, ["importedAt"] = now };
                            node["employeeCount"] = month.EmployeeCount; node["deductionCount"] = month.DeductionCount;
                            var employees = snapshot.Employees.Where(e => e.Month == month.Name).ToArray();
                            if (metadata?.PayrollHashes.GetValueOrDefault(month.Name) != PayrollHash(employees)) node["summary"] = Convert.ToBase64String(Summary(employees));
                            writer.WriteLine(node.ToJsonString(Json));
                        }
                    }
                    Rows("employees.jsonl", snapshot.Employees); Rows("deductions.jsonl", snapshot.Deductions); Rows("staff.jsonl", staff); Rows("accounts.jsonl", snapshot.Accounts); Rows("books/index.json", snapshot.Books, true);
                    foreach (var book in snapshot.Books) { var key = $"books/{book.Id.ToString().ToUpperInvariant()}.pdf"; using var output = Entry(key); store.CopyAttachment(key, output); }
                    foreach (var key in keys.Where(k => k.StartsWith(ExtraPrefix, StringComparison.Ordinal))) { using var output = Entry(key[ExtraPrefix.Length..]); store.CopyAttachment(key, output); }
                }
                file.Flush(true);
            }
            cancellation.ThrowIfCancellationRequested(); var checkedCopy = Read(temp, cancellation);
            if (!SameSnapshot(snapshot, checkedCopy.Snapshot) || Fingerprint(staff) != Fingerprint(checkedCopy.Staff)) throw Invalid("فشل التحقق من تطابق التصدير");
            if (metadata is not null)
            {
                if (!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(metadata.ExtraFields.Where(p => p.Value.Count > 0).ToDictionary(), Json), JsonSerializer.SerializeToNode(checkedCopy.Metadata.ExtraFields.Where(p => p.Value.Count > 0).ToDictionary(), Json))) throw Invalid("فشل تطابق حقول السجلات الإضافية");
                var changedManifestKeys = new HashSet<string>(["version", "createdAt", "months", "employees", "deductions", "staff", "accounts", "books"], StringComparer.Ordinal);
                foreach (var p in metadata.Manifest.Where(p => !changedManifestKeys.Contains(p.Key)))
                    if (!JsonNode.DeepEquals(p.Value, checkedCopy.Metadata.Manifest[p.Key])) throw Invalid("فشل تطابق حقل فهرس إضافي");
                foreach (var month in metadata.Months)
                {
                    if (!checkedCopy.Metadata.Months.TryGetValue(month.Key, out var actual)) throw Invalid("شهر مفقود في التصدير");
                    foreach (var p in month.Value.Where(p => p.Key is not ("employeeCount" or "deductionCount" or "summary")))
                        if (!JsonNode.DeepEquals(p.Value, actual[p.Key])) throw Invalid("فشل تطابق بيانات الشهر");
                }
            }
            foreach (var key in keys.Where(k => k != MetadataKey))
            {
                if (!key.StartsWith(ExtraPrefix, StringComparison.Ordinal) && !snapshot.Books.Any(b => key == $"books/{b.Id.ToString().ToUpperInvariant()}.pdf")) throw Invalid("مرفق محلي لا يملك مسار تبادل معتمداً: " + key);
                if (!checkedCopy.Attachments.TryGetValue(key, out var copied) || Hash(store.ReadAttachment(key)) != Hash(copied)) throw Invalid("فشل تطابق مرفق التصدير");
            }
            if (store.GetStatus().RevisionId != revision) throw Invalid("تغيّرت مساحة العمل أثناء التصدير");
            File.Move(temp, target, false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static bool SameSnapshot(BackupSnapshot a, BackupSnapshot b) => Fingerprint(a.Months) == Fingerprint(b.Months) && Fingerprint(a.Employees) == Fingerprint(b.Employees) && Fingerprint(a.Deductions) == Fingerprint(b.Deductions) && Fingerprint(a.Accounts) == Fingerprint(b.Accounts) && Fingerprint(a.Books) == Fingerprint(b.Books) && a.SettingsPlists.Count == b.SettingsPlists.Count && a.SettingsPlists.All(p => b.SettingsPlists.TryGetValue(p.Key, out var value) && p.Value.AsSpan().SequenceEqual(value));
    private static IReadOnlyDictionary<string, decimal> ReadItems(byte[] bytes)
    {
        try
        {
            var node = ParseObject(Utf8.GetString(bytes)); var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var item in node) result.Add(item.Key, Amount(node, item.Key) ?? throw Invalid("بند راتب بلا مبلغ"));
            return result;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or ArgumentException) { throw new InvalidDataException("بنود الراتب لا يمكن تصديرها إلى الآيفون دون فقد.", ex); }
    }
    private static void IphoneMoney(decimal? amount)
    {
        if (amount is not { } value) return;
        var text = ((double)value).ToString("R", CultureInfo.InvariantCulture);
        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var restored) || value != restored) throw Invalid("مبلغ بدقة لا يحفظها نظام أرقام الآيفون؛ استخدم .salarybackup دون تقريب المبلغ");
    }
    private static byte[] Summary(IEnumerable<Employee> employees)
    {
        var summary = new Dictionary<string, (int Count, decimal Total)>(StringComparer.Ordinal);
        foreach (var e in employees)
        {
            if (e.Items is null) continue; var items = ReadItems(e.Items);
            foreach (var (key, value) in items) { var old = summary.GetValueOrDefault(key); summary[key] = (checked(old.Count + 1), checked(old.Total + value)); }
        }
        foreach (var item in summary.Values) IphoneMoney(item.Total);
        return JsonSerializer.SerializeToUtf8Bytes(summary.ToDictionary(p => p.Key, p => new { count = p.Value.Count, total = p.Value.Total }), Json);
    }
}
