using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SalaryDesktop.Core;
using SalaryLocal.Core;

public static class InteropTests
{
    private const string Month = "أيلول 2026";
    private const string BookId = "A0A0A0A0-B0B0-C0C0-D0D0-E0E0E0E0E0E0";
    private static int checks;

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "salary-interop-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { RoundTrips(root); AuxiliaryOnly(root); HistoricalAmounts(root); MoneyPrecision(root); FreshExport(root); Adversarial(root); Console.WriteLine($"iPhone interchange: {checks} assertions passed."); }
        finally { Directory.Delete(root, true); }
    }
    private static void RoundTrips(string root)
    {
        foreach (var version in new[] { 1, 2, 3, 4, 5, 6, 8, 9, 10 })
        {
            var source = Path.Combine(root, $"source-v{version}.zip"); var files = Fixture(version); WriteZip(source, files);
            var bytes = File.ReadAllBytes(source); var inspected = IphoneInteropService.Inspect(source);
            Check(inspected.Version == version && inspected.Employees == 2 && inspected.Staff == 1, "Inspect source counts v" + version);
            var workspace = IphoneInteropService.ImportNewWorkspace(source, Path.Combine(root, "workspaces"), inspected.Digest);
            using var store = new LocalPayrollStore(workspace);
            Check(store.GetSnapshot().Employees.Count == 2, "Imported payroll count v" + version);
            Check(store.GetSnapshot().StaffCount == 1, "Imported staff retained v" + version);
            Check(store.GetSnapshot().Accounts.Count == (version >= 2 ? 1 : 0), "Imported accounts v" + version);
            Check(store.GetSnapshot().Books.Count == (version >= 3 ? 1 : 0), "Imported books v" + version);
            // قارئ التقارير القديم لا يعرف 8–10؛ مرجع البيانات هنا fixture بصيغة 6، وليس ادعاء قبوله للصيغ الجديدة.
            var referencePath = Path.Combine(root, $"reference-v{version}.zip"); var reference = files.ToDictionary(p => p.Key, p => p.Value); Edit(reference, "manifest.json", n => n["version"] = Math.Min(version, 6)); WriteZip(referencePath, reference);
            var original = BackupReader.Read(referencePath); var imported = store.GetSnapshot();
            Parity(original, imported);
            var native = Path.Combine(root, $"native-v{version}.salarybackup");
            var nativeInfo = BackupService.Create(store, native);
            var nativePath = BackupService.RestoreAsNewWorkspace(native, Path.Combine(root, "restores"), nativeInfo.Digest);
            using var restored = new LocalPayrollStore(nativePath);
            Check(JsonSerializer.Serialize(store.GetHistory()) == JsonSerializer.Serialize(restored.GetHistory()), "Native full history preserved through bridge v" + version);
            Check(bytes.SequenceEqual(File.ReadAllBytes(source)), "iPhone source never rewritten v" + version);
            File.Delete(source);
            var exported = Path.Combine(root, $"export-v{version}.zip"); IphoneInteropService.Export(restored, exported);
            Check(IphoneInteropService.Inspect(exported).Employees == 2, "Export independently rereads v" + version);
            VerifyRetained(files, ReadZip(exported));
            var reimportPath = IphoneInteropService.ImportNewWorkspace(exported, Path.Combine(root, "reimports"), IphoneInteropService.Inspect(exported).Digest);
            using (var reimported = new LocalPayrollStore(reimportPath)) Parity(original, reimported.GetSnapshot());
            var exportBytes = File.ReadAllBytes(exported);
            Reject(() => IphoneInteropService.Export(restored, exported), "Refuse existing export target");
            Check(exportBytes.SequenceEqual(File.ReadAllBytes(exported)), "Existing export intact");
            var changed = imported.Employees.Select((e, i) => i == 0 ? e with { Net = e.Net + 1m } : e).ToArray();
            restored.Import(new(Month, changed, [], []), "synthetic-change.xlsx", new string('A', 64), restored.GetStatus().RevisionId, true);
            var lossy = Path.Combine(root, $"lossy-v{version}.zip");
            Reject(() => IphoneInteropService.Export(restored, lossy), "Changed row must not discard unknown fields");
            Check(!File.Exists(lossy), "Lossy export not published");
        }
    }
    private static void AuxiliaryOnly(string root)
    {
        var files = Fixture(); Edit(files, "manifest.json", n => { n["months"] = new JsonArray(); n["employees"] = 0; n["deductions"] = 0; });
        files["months.jsonl"] = []; files["employees.jsonl"] = []; files["deductions.jsonl"] = [];
        var source = Path.Combine(root, "auxiliary-only.zip"); WriteZip(source, files); var review = IphoneInteropService.Inspect(source);
        using var store = new LocalPayrollStore(IphoneInteropService.ImportNewWorkspace(source, Path.Combine(root, "auxiliary"), review.Digest));
        Check(store.GetMonths().Count == 0 && store.GetSnapshot().StaffCount == 1, "Auxiliary-only workspace is nonempty without months");
        var backup = Path.Combine(root, "auxiliary-only.salarybackup"); var native = BackupService.Create(store, backup);
        Check(BackupService.Inspect(backup, store).Relationship == BackupRelationship.Same, "Auxiliary-only revision comparison");
        using var restored = new LocalPayrollStore(BackupService.RestoreAsNewWorkspace(backup, Path.Combine(root, "auxiliary-restored"), native.Digest));
        var exported = Path.Combine(root, "auxiliary-export.zip"); IphoneInteropService.Export(restored, exported);
        VerifyRetained(files, ReadZip(exported));
    }
    private static void HistoricalAmounts(string root)
    {
        var files = Fixture();
        foreach (var (file, field) in new[] { ("employees.jsonl", "net"), ("deductions.jsonl", "amount") }) {
            var rows = Encoding.UTF8.GetString(files[file]).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => JsonNode.Parse(x)!).ToArray();
            rows[0][field] = null; rows[1][field] = -0.125m;
            files[file] = Bytes(string.Join("\n", rows.Select(n => n.ToJsonString())) + "\n");
        }
        var source = Path.Combine(root, "historical-null-negative.zip"); WriteZip(source, files);
        var inspected = IphoneInteropService.Inspect(source);
        Check(inspected.Warnings.Any(w => w.Contains("سالبة", StringComparison.Ordinal)), "Historical negative amounts visibly warned");
        var path = IphoneInteropService.ImportNewWorkspace(source, Path.Combine(root, "historical"), inspected.Digest);
        using var store = new LocalPayrollStore(path); var snapshot = store.GetSnapshot();
        Check(snapshot.Employees[0].Net is null && snapshot.Employees[1].Net == -0.125m, "Historical nullable and negative payroll not coerced");
        Check(snapshot.Deductions[0].Amount is null && snapshot.Deductions[1].Amount == -0.125m, "Historical nullable and negative deductions not coerced");
        var exported = Path.Combine(root, "historical-roundtrip.zip"); IphoneInteropService.Export(store, exported);
        VerifyRetained(files, ReadZip(exported));
        var backup = Path.Combine(root, "historical.salarybackup"); var review = BackupService.Create(store, backup);
        using var restored = new LocalPayrollStore(BackupService.RestoreAsNewWorkspace(backup, Path.Combine(root, "historical-native"), review.Digest));
        Check(JsonSerializer.Serialize(snapshot.Employees) == JsonSerializer.Serialize(restored.GetSnapshot().Employees), "Native backup retains historical amount semantics");
    }
    private static void MoneyPrecision(string root)
    {
        const decimal precise = 0.1234567890123456789012345678m;
        foreach (var field in new[] { "net", "deduction", "items", "items-json-overprecision", "items-duplicate", "summary" })
        {
            using var store = new LocalPayrollStore(Path.Combine(root, "precision-" + field));
            var items = field == "items-duplicate" ? Bytes("{\"synthetic\":1,\"synthetic\":2}") : field == "items-json-overprecision" ? Bytes("{\"synthetic\":0.100000000000000000000000000000000000001}") : field == "items" ? JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, decimal> { ["بند وهمي"] = precise }) : field == "summary" ? JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, decimal> { ["بند وهمي"] = 1000000000000000m }) : null;
            var employees = new List<Employee> { new(Month, "منتسب دقة وهمي", "موظف", "جهة وهمية", field == "net" ? precise : 100m, items, null, null) };
            if (field == "summary") employees.Add(new(Month, "منتسب جمع وهمي", "موظف", "جهة وهمية", 100m, JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, decimal> { ["بند وهمي"] = 0.01m }), null, null));
            var deductions = new[] { new Deduction(Month, "منتسب دقة وهمي", "جهة وهمية", field == "deduction" ? precise : 1m, "", "موظف") };
            store.Import(new(Month, employees, deductions, []), "precision-synthetic.xlsx", new string('C', 64), store.GetStatus().RevisionId, false);
            var before = store.GetSnapshot(); var revision = store.GetStatus().RevisionId;
            var native = Path.Combine(root, "precision-" + field + ".salarybackup"); var checkedNative = BackupService.Create(store, native);
            using (var restored = new LocalPayrollStore(BackupService.RestoreAsNewWorkspace(native, Path.Combine(root, "precision-native"), checkedNative.Digest))) {
                Check(JsonSerializer.Serialize(before.Employees) == JsonSerializer.Serialize(restored.GetSnapshot().Employees), "Native keeps exact employee precision " + field);
                Check(JsonSerializer.Serialize(before.Deductions) == JsonSerializer.Serialize(restored.GetSnapshot().Deductions), "Native keeps exact deduction precision " + field);
            }
            var target = Path.Combine(root, "precision-" + field + ".zip");
            Reject(() => IphoneInteropService.Export(store, target), "Reject iPhone precision loss " + field);
            Check(!File.Exists(target), "Precision rejection does not publish " + field);
            Check(revision == store.GetStatus().RevisionId && JsonSerializer.Serialize(before.Employees) == JsonSerializer.Serialize(store.GetSnapshot().Employees), "Precision rejection does not round native values " + field);
        }
        using var safe = new LocalPayrollStore(Path.Combine(root, "safe-decimals"));
        decimal[] amounts = [0m, 0.01m, 0.1m, 0.125m, 100.25m, 1234567.89m];
        var rows = amounts.Select((amount, i) => new Employee(Month, "منتسب عشري وهمي " + i, "موظف", "جهة وهمية", amount, JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, decimal> { ["بند وهمي " + i] = amount }), null, null)).ToArray();
        var deductionsSafe = amounts.Select((amount, i) => new Deduction(Month, rows[i].Name, "جهة وهمية", amount, "", "موظف")).ToArray();
        safe.Import(new(Month, rows, deductionsSafe, []), "safe-decimals.xlsx", new string('D', 64), safe.GetStatus().RevisionId, false);
        var output = Path.Combine(root, "safe-decimals.zip"); IphoneInteropService.Export(safe, output);
        var inspection = IphoneInteropService.Inspect(output);
        using var again = new LocalPayrollStore(IphoneInteropService.ImportNewWorkspace(output, Path.Combine(root, "safe-decimals-restored"), inspection.Digest));
        Check(JsonSerializer.Serialize(safe.GetSnapshot().Employees) == JsonSerializer.Serialize(again.GetSnapshot().Employees), "Common decimals export and reimport without rounding");
        Check(JsonSerializer.Serialize(safe.GetSnapshot().Deductions) == JsonSerializer.Serialize(again.GetSnapshot().Deductions), "Common deduction decimals export and reimport without rounding");
    }
    private static void FreshExport(string root)
    {
        using var store = new LocalPayrollStore(Path.Combine(root, "fresh-store"));
        store.Import(new(Month, [new(Month, "منتسب وهمي مستقل", "موظف", "جهة وهمية", 125.5m, null, null, null)], [], []), "synthetic.xlsx", new string('B', 64), store.GetStatus().RevisionId, false);
        var before = store.GetStatus(); var history = JsonSerializer.Serialize(store.GetHistory());
        var exported = Path.Combine(root, "fresh-export.zip"); IphoneInteropService.Export(store, exported);
        var snapshot = BackupReader.Read(exported);
        Check(snapshot.Employees.Single().Net == 125.5m, "Fresh local data exports exact decimal");
        var files = ReadZip(exported); var month = JsonNode.Parse(files["months.jsonl"])!;
        Check(month["importedAt"] is JsonValue time && time.TryGetValue<double>(out _), "Export includes Swift required month importedAt");
        Check(before.RevisionId == store.GetStatus().RevisionId && history == JsonSerializer.Serialize(store.GetHistory()), "Export never changes native history");
        var cancelled = Path.Combine(root, "cancel-export.zip");
        Cancel(() => IphoneInteropService.Export(store, cancelled, new CancellationToken(true)), "Cancel export");
        Check(!File.Exists(cancelled), "Cancelled export not published");
    }
    private static void Adversarial(string root)
    {
        var restoreRoot = Path.Combine(root, "invalid-restores"); Directory.CreateDirectory(restoreRoot);
        foreach (var (name, files) in InvalidFixtures())
        {
            var source = Path.Combine(root, name + ".zip"); WriteZip(source, files);
            Reject(() => IphoneInteropService.Inspect(source), "Reject inspect " + name);
            Reject(() => IphoneInteropService.ImportNewWorkspace(source, restoreRoot, Hash(File.ReadAllBytes(source))), "Reject import " + name);
            Check(!Directory.EnumerateFileSystemEntries(restoreRoot).Any(), "Rejected import leaves no workspace " + name);
        }
        foreach (var duplicate in new[] { "manifest.json", "employees.jsonl", "extensions/unknown.bin" })
        {
            var source = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip"); WriteZip(source, Fixture(), duplicate);
            Reject(() => IphoneInteropService.Inspect(source), "Duplicate archive entry " + duplicate);
        }
        var oversized = Path.Combine(root, "oversized.zip"); WriteZip(oversized, Fixture()); InflateDeclaredSize(oversized);
        Reject(() => IphoneInteropService.Inspect(oversized), "Reject oversized entry metadata without allocating gigabytes");
        var valid = Path.Combine(root, "cancel-source.zip"); WriteZip(valid, Fixture()); var review = IphoneInteropService.Inspect(valid);
        Cancel(() => IphoneInteropService.Inspect(valid, new CancellationToken(true)), "Cancel inspect");
        Cancel(() => IphoneInteropService.ImportNewWorkspace(valid, restoreRoot, review.Digest, new CancellationToken(true)), "Cancel import");
        Reject(() => IphoneInteropService.ImportNewWorkspace(valid, restoreRoot, ""), "Require reviewed digest");
        File.Delete(valid); var replacement = Fixture(); replacement["extensions/unknown.bin"] = new byte[] { 10 }; WriteZip(valid, replacement);
        Reject(() => IphoneInteropService.ImportNewWorkspace(valid, restoreRoot, review.Digest), "Reject changed valid file after review");
        Check(!Directory.EnumerateFileSystemEntries(restoreRoot).Any(), "Cancelled and changed imports publish no workspace");
    }
    private static void Parity(BackupSnapshot source, BackupSnapshot actual)
    {
        Check(JsonSerializer.Serialize(source.Employees) == JsonSerializer.Serialize(actual.Employees), "Known payroll and raw JSON byte fidelity");
        Check(JsonSerializer.Serialize(source.Deductions) == JsonSerializer.Serialize(actual.Deductions), "Known deduction fidelity");
        Check(JsonSerializer.Serialize(BankStatementCalculator.Calculate(source, Month)) == JsonSerializer.Serialize(BankStatementCalculator.Calculate(actual, Month)), "Bank calculation parity");
        Check(JsonSerializer.Serialize(FundReport.Summary(source, Month, "صندوق الشهداء")) == JsonSerializer.Serialize(FundReport.Summary(actual, Month, "صندوق الشهداء")), "Fund categories parity");
        Check(JsonSerializer.Serialize(FoodContractReport.Rows(source, Month, "شركة عراق الجوال")) == JsonSerializer.Serialize(FoodContractReport.Rows(actual, Month, "شركة عراق الجوال")), "Food contract parity");
        var health = HealthCareReport.Build(actual, Month, "الرعاية الصحية");
        Check(health.Irregular.Count == 1 && health.Irregular.Single().Amount == 3501m, "Irregular health contribution not rounded away");
        Check(JsonSerializer.Serialize(HealthCareReport.Build(source, Month, "الرعاية الصحية")) == JsonSerializer.Serialize(health), "Healthcare parity");
    }
    private static Dictionary<string, byte[]> ReadZip(string path)
    {
        using var zip = ZipFile.OpenRead(path); var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries) { using var s = entry.Open(); using var m = new MemoryStream(); s.CopyTo(m); result.Add(entry.FullName, m.ToArray()); } return result;
    }
    private static void VerifyRetained(Dictionary<string, byte[]> before, Dictionary<string, byte[]> after)
    {
        foreach (var (name, bytes) in before)
        {
            Check(after.ContainsKey(name), "Entry preserved " + name);
            if (name == "manifest.json") {
                var a = JsonNode.Parse(bytes)!; var b = JsonNode.Parse(after[name])!;
                Check(JsonNode.DeepEquals(a["futureManifest"], b["futureManifest"]), "Unknown manifest large numbers retain precision"); continue;
            }
            if (name.StartsWith("extensions/", StringComparison.Ordinal)) { Check(bytes.SequenceEqual(after[name]), "Unknown attachment byte identity " + name); continue; }
            if (name.EndsWith(".jsonl", StringComparison.Ordinal)) {
                var a = Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var b = Encoding.UTF8.GetString(after[name]).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                Check(a.Length == b.Length, "All rows preserved " + name);
                for (var i = 0; i < a.Length; i++) Check(JsonNode.DeepEquals(JsonNode.Parse(a[i]), JsonNode.Parse(b[i])), "Unknown row fields and precise JSON retained " + name);
            } else if (name.EndsWith(".json", StringComparison.Ordinal)) Check(JsonNode.DeepEquals(JsonNode.Parse(bytes), JsonNode.Parse(after[name])), "Unknown JSON fields retained " + name + "; before=" + Encoding.UTF8.GetString(bytes) + "; after=" + Encoding.UTF8.GetString(after[name]));
            else Check(bytes.SequenceEqual(after[name]), "Opaque bytes retained " + name);
        }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void Reject(Action action, string reason)
    { try { action(); } catch (Exception e) when (e is InvalidDataException or IOException) { checks++; return; } throw new Exception("Interop accepted invalid operation: " + reason); }
    private static void Cancel(Action action, string reason)
    { try { action(); } catch (OperationCanceledException) { checks++; return; } throw new Exception("Interop ignored cancellation: " + reason); }

    // إنشاء مستقل للصيغة المشتركة، لا يعيد استعمال كاتب التصدير قيد الاختبار.
    public static Dictionary<string, byte[]> Fixture(int version = 6)
    {
        var manifest = new JsonObject { ["version"] = version, ["createdAt"] = 700000000.25,
            ["months"] = new JsonArray(Month), ["employees"] = 2, ["deductions"] = 2, ["staff"] = 1,
            ["futureManifest"] = JsonNode.Parse("{\"large\":123456789012345678901234567890,\"fraction\":0.12345678901234567890123456789}") };
        if (version >= 2) manifest["accounts"] = 1;
        if (version >= 3) manifest["books"] = 1;
        var payroll = Enumerable.Range(0, 2).Select(i => new JsonObject {
            ["month"] = Month, ["name"] = "منتسب وهمي " + i, ["rank"] = i == 0 ? "عقود" : "موظف", ["workplace"] = "جهة وهمية",
            ["net"] = 100.125m + i,
            ["items"] = B64(new Dictionary<string, decimal> { ["صندوق الشهداء"] = 2000, ["الرعاية الصحية"] = i == 0 ? 3501 : 0, ["شركة عراق الجوال"] = 1000 }),
            ["details"] = B64(new Dictionary<string, string> { ["طريقة الدفع"] = i == 0 ? "كاش" : "بطاقة", ["الاسمي"] = "101", ["توقيفات تقاعدية"] = "2", ["مجموع استقطاعات المصارف"] = "7" }),
            ["rawColumns"] = B64(new { values = new[] { "0", "", "١٢٣٫٤٥", "حقل وهمي" }, headers = new[] { "مكرر", "مكرر", "", "غير معروف" }, extraRaw = new { preserve = true } }),
            ["futureEmployee"] = JsonNode.Parse("{\"large\":123456789012345678901234567890,\"array\":[null,false,\"محفوظ\"]}")
        }).ToArray();
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) {
            ["manifest.json"] = Bytes(manifest.ToJsonString()),
            ["months.jsonl"] = Line(new { name = Month, importedAt = 700000000.125, employeeCount = 2, deductionCount = 2, summary = (byte[]?)null, futureMonth = "محفوظ" }),
            ["employees.jsonl"] = Bytes(string.Join("\n", payroll.Select(p => p.ToJsonString())) + "\n"),
            ["deductions.jsonl"] = Concat(Line(new { month = Month, name = "منتسب وهمي 0", title = "جهة تنفيذ وهمية", amount = 7m, note = "إضبارة وهمية", rank = "عقود", futureDeduction = "محفوظ" }), Line(new { month = Month, name = "منتسب وهمي 1", title = "جهة تنفيذ وهمية", amount = 7m, note = "إضبارة وهمية", rank = "موظف", futureDeduction = "محفوظ أيضاً" })),
            ["staff.jsonl"] = Line(new { name = "منتسب وهمي 0", mother = "أم وهمية", stat = "TEST-ONLY", unified = "SYNTHETIC", rank = "عقود", status = "اختبار", workplace = "جهة وهمية", details = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["حقل ملاك"] = "محفوظ" }), futureStaff = "محفوظ" }),
            ["settings.json"] = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, byte[]> { ["futureSetting"] = PlistString("قيمة محفوظة & < >"), ["reportHeader"] = PlistString("عنوان كشف وهمي") }),
            ["extensions/unknown.bin"] = new byte[] { 0, 1, 255, 128, 42, 10 },
            ["extensions/unknown.json"] = Bytes("{\"future\":123456789012345678901234567890}")
        };
        if (version >= 2) files["accounts.jsonl"] = Line(new { entity = "جهة تنفيذ وهمية", iban = SyntheticIban(), branch = "فرع وهمي", swift = "TESTIQBAAAA", localAccount = version >= 5 ? "000000000000001" : null, futureAccount = "محفوظ" });
        if (version >= 3) {
            files["books/index.json"] = JsonSerializer.SerializeToUtf8Bytes(new[] { new { id = BookId, title = "كتاب وهمي", month = Month, pages = 1, entity = "جهة تنفيذ وهمية", createdAt = 700000000.125, futureBook = "محفوظ" } });
            files[$"books/{BookId}.pdf"] = Pdf();
        }
        if (version is 8 or 9 or 10)
        {
            var settings = JsonSerializer.Deserialize<Dictionary<string, byte[]>>(files["settings.json"])!;
            if (version == 8) settings["fundingExpenseApprovalsV1"] = PlistData(Bytes("{\"version\":1,\"records\":[]}"));
            if (version == 9) settings["customTransferTemplatesV1"] = PlistData(Bytes("{\"version\":1,\"templates\":[]}"));
            if (version == 10) {
                var template = new JsonObject { ["id"] = "11111111-2222-3333-4444-555555555555", ["title"] = "قالب وهمي", ["source"] = "none" };
                foreach (var key in new[] { "header", "number", "recipient", "subject", "body", "footer", "firstSigner", "secondSigner", "thirdSigner", "sourceName", "account" }) template[key] = "";
                settings["transferLetterCustomizations"] = PlistData(Bytes(new JsonObject { ["common"] = new JsonObject { ["letterTemplate"] = template }, ["individual"] = new JsonObject() }.ToJsonString()));
            }
            files["settings.json"] = JsonSerializer.SerializeToUtf8Bytes(settings);
        }
        return files;
    }
    private static string SyntheticIban()
    {
        var bban = "TEST000000000000001";
        var numeric = string.Concat((bban + "IQ00").Select(c => char.IsAsciiDigit(c) ? c.ToString() : (c - 'A' + 10).ToString(CultureInfo.InvariantCulture)));
        return "IQ" + (98 - BigInteger.Parse(numeric, CultureInfo.InvariantCulture) % 97).ToString("00", CultureInfo.InvariantCulture) + bban;
    }
    private static byte[] Pdf()
    {
        var text = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int>();
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R >>", "<< /Length 0 >>\nstream\n\nendstream"];
        for (var i = 0; i < objects.Length; i++) { offsets.Add(text.Length); text.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = text.Length; text.Append("xref\n0 5\n0000000000 65535 f \n"); foreach (var offset in offsets) text.Append(offset.ToString("0000000000", CultureInfo.InvariantCulture) + " 00000 n \n");
        text.Append($"trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"); return Encoding.ASCII.GetBytes(text.ToString());
    }
    public static IEnumerable<(string Name, Dictionary<string, byte[]> Files)> InvalidFixtures()
    {
        foreach (var version in new[] { 0, 7, 8, 9, 10, 99 }) { var f = Fixture(); Edit(f, "manifest.json", n => n["version"] = version); yield return ("unsupported-version-" + version, f); }
        { var f = Fixture(); Edit(f, "manifest.json", n => n.AsObject().Remove("createdAt")); yield return ("missing-manifest-date", f); }
        { var f = Fixture(); Edit(f, "manifest.json", n => n["createdAt"] = "not a date"); yield return ("invalid-manifest-date", f); }
        { var f = Fixture(); Edit(f, "months.jsonl", n => n.AsObject().Remove("importedAt")); yield return ("missing-month-date", f); }
        foreach (var version in new[] { 8, 9, 10 }) { var f = Fixture(version); Edit(f, "manifest.json", n => n["version"] = 6); yield return ("feature-downgrade-" + version, f); }
        { var f = Fixture(); f["employees.jsonl"] = Bytes(Encoding.UTF8.GetString(f["employees.jsonl"]).Replace("\"net\":100.125", "\"net\":100.125,\"net\":999", StringComparison.Ordinal)); yield return ("duplicate-json-amount", f); }
        { var f = Fixture(); f[$"books/{BookId}.pdf"] = Bytes("%PDF-1.4\n%%EOF"); yield return ("signature-only-pdf", f); }
        { var f = Fixture(); Edit(f, "books/index.json", n => n[0]!.AsObject().Remove("createdAt")); yield return ("missing-book-date", f); }
        { var f = Fixture(); Edit(f, "books/index.json", n => n[0]!["createdAt"] = "not a date"); yield return ("invalid-book-date", f); }
        { var f = Fixture(); Edit(f, "books/index.json", n => n[0]!["createdAt"] = 4.1e9); yield return ("out-of-range-book-date", f); }
        { var f = Fixture(); Edit(f, "books/index.json", n => n[0]!["pages"] = 2); yield return ("book-pdf-page-count-mismatch", f); }
        { var f = Fixture(); Edit(f, "books/index.json", n => n[0]!["pages"] = 4); yield return ("book-too-many-pages", f); }
        foreach (var invalid in new[] { Bytes("not a reference ZIP"), NestedZip("../escape.pdf"), NestedZip("manifest.json", duplicate: true) }) {
            var f = Fixture(); var settings = JsonSerializer.Deserialize<Dictionary<string, byte[]>>(f["settings.json"])!;
            settings["transferReferenceArchiveData"] = PlistData(invalid); f["settings.json"] = JsonSerializer.SerializeToUtf8Bytes(settings);
            yield return ("malformed-reference-" + Guid.NewGuid().ToString("N"), f);
        }
        foreach (var count in new[] { "employees", "deductions", "staff", "accounts", "books" }) { var f = Fixture(); Edit(f, "manifest.json", n => n[count] = 99); yield return ("wrong-count-" + count, f); }
        foreach (var name in new[] { "months.jsonl", "employees.jsonl", "deductions.jsonl", "staff.jsonl", "accounts.jsonl" }) {
            var f = Fixture(); f[name] = Concat(f[name], Bytes("{\"truncated\":")); yield return ("truncated-" + name, f);
            f = Fixture(); f[name] = Concat(f[name], Bytes("\n")); yield return ("blank-line-" + name, f);
        }
        foreach (var path in new[] { "../escape.bin", "/absolute.bin", "..\\escape.bin", "C:/escape.bin", "nested/../escape.bin" }) { var f = Fixture(); f[path] = new byte[] { 1 }; yield return ("unsafe-path-" + Guid.NewGuid().ToString("N"), f); }
        { var f = Fixture(); Edit(f, "months.jsonl", n => n["employeeCount"] = 1); yield return ("wrong-month-count", f); }
        { var f = Fixture(); f["employees.jsonl"] = Bytes(Encoding.UTF8.GetString(f["employees.jsonl"]).Replace("100.125", "9007199254740992", StringComparison.Ordinal)); yield return ("unsafe-amount", f); }
        { var f = Fixture(); f["employees.jsonl"] = Bytes(Encoding.UTF8.GetString(f["employees.jsonl"]).Replace("100.125", "0.123456789012345678901234567890123456789", StringComparison.Ordinal)); yield return ("unrepresentable-decimal", f); }
        { var f = Fixture(); f["settings.json"] = Bytes("{\"futureSetting\":\"not base64\"}"); yield return ("bad-settings-base64", f); }
        { var f = Fixture(); f["settings.json"] = Bytes("{\"futureSetting\":\"AA==\",\"futureSetting\":\"AQ==\"}"); yield return ("duplicate-setting", f); }
        { var f = Fixture(); f.Remove($"books/{BookId}.pdf"); yield return ("missing-book", f); }
        { var f = Fixture(); f[$"books/{BookId}.pdf"] = Bytes("not a PDF"); yield return ("invalid-book", f); }
    }
    public static void WriteZip(string path, IReadOnlyDictionary<string, byte[]> entries, string? duplicate = null)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var pair in entries) { using var s = zip.CreateEntry(pair.Key, CompressionLevel.Optimal).Open(); s.Write(pair.Value); }
        if (duplicate is not null) { using var s = zip.CreateEntry(duplicate).Open(); s.Write(entries[duplicate]); }
    }
    public static void InflateDeclaredSize(string path)
    {
        var bytes = File.ReadAllBytes(path); var offset = bytes.AsSpan().IndexOf(new byte[] { 0x50, 0x4b, 0x01, 0x02 });
        if (offset < 0) throw new Exception("Missing central directory in synthetic ZIP");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 24, 4), uint.MaxValue - 1); File.WriteAllBytes(path, bytes);
    }
    private static void Edit(Dictionary<string, byte[]> files, string name, Action<JsonNode> action)
    { var node = JsonNode.Parse(files[name])!; action(node); files[name] = Bytes(node.ToJsonString() + (name.EndsWith("jsonl", StringComparison.Ordinal) ? "\n" : "")); }
    private static byte[] NestedZip(string name, bool duplicate = false)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true)) {
            using (var entry = zip.CreateEntry(name).Open()) entry.Write(Bytes("{}"));
            if (duplicate) { using var entry = zip.CreateEntry(name).Open(); entry.Write(Bytes("{}")); }
        }
        return output.ToArray();
    }
    private static byte[] PlistData(byte[] bytes) => Bytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?><plist version=\"1.0\"><data>" + Convert.ToBase64String(bytes) + "</data></plist>");
    private static byte[] PlistString(string text) => Bytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?><!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\"><plist version=\"1.0\"><string>" + System.Security.SecurityElement.Escape(text) + "</string></plist>");
    private static string B64<T>(T value) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value));
    private static byte[] Line<T>(T value) => Concat(JsonSerializer.SerializeToUtf8Bytes(value), new byte[] { 10 });
    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    private static byte[] Concat(byte[] a, byte[] b) => a.Concat(b).ToArray();
    private static void Check(bool success, string reason) { checks++; if (!success) throw new Exception("Interop safety test failed: " + reason); }
}
