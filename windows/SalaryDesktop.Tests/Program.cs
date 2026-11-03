using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Claunia.PropertyList;
using SkiaSharp;
using SalaryDesktop.Core;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
static void Entry(ZipArchive zip, string name, string text)
{
    var entry = zip.CreateEntry(name);
    using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
    writer.Write(text);
}
static string Lines<T>(IEnumerable<T> values) => string.Concat(values.Select(v => JsonSerializer.Serialize(v) + "\n"));
static void MakeAccountXlsx(string path, string iban, bool numeric = false, bool duplicate = false, bool missingName = false, string name = "جهة جديدة وهمية", string branch = "الرشيد فرع وهمي")
{
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    Entry(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
    Entry(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
    Entry(zip, "xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"حسابات تجريبية\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
    Entry(zip, "xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
    string Cell(string reference, string value) => $"<c r=\"{reference}\" t=\"inlineStr\"><is><t>{System.Security.SecurityElement.Escape(value)}</t></is></c>";
    var valueCell = numeric ? $"<c r=\"B2\"><v>{iban}</v></c>" : Cell("B2", iban);
    var row2 = $"<row r=\"2\">{Cell("A2", missingName ? "" : name)}{valueCell}{Cell("C2", branch)}{Cell("D2", "TESTIQBAAAA")}</row>";
    var row3 = duplicate ? $"<row r=\"3\">{Cell("A3", "جهة جديدة وهمية")}{Cell("B3", iban)}</row>" : "";
    Entry(zip, "xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\">{Cell("A1", "جهة التنفيذ")}{Cell("B1", "IBAN")}{Cell("C1", "الفرع")}{Cell("D1", "SWIFT")}</row>{row2}{row3}</sheetData></worksheet>");
}
static void MakeMonthlyXlsx(string path, string[] headers, string[][] rows)
{
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    Entry(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
    Entry(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
    Entry(zip, "xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"بيانات تجريبية\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
    Entry(zip, "xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
    string Row(string[] cells, int n) => $"<row r=\"{n}\">" + string.Concat(cells.Select((s, i) => $"<c r=\"{(char)('A' + i)}{n}\" t=\"inlineStr\"><is><t>{System.Security.SecurityElement.Escape(s)}</t></is></c>")) + "</row>";
    Entry(zip, "xl/worksheets/sheet1.xml", "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" + Row(headers, 1) + string.Concat(rows.Select((r, i) => Row(r, i + 2))) + "</sheetData></worksheet>");
}
static byte[] MakeReferenceArchive(string fontPath, string payer, string beneficiary, bool damaged = false)
{
    byte[] Pdf(int pages, string id)
    {
        using var file = new MemoryStream();
        using (var pdf = SKDocument.CreatePdf(file))
        using (var face = SKTypeface.FromFile(fontPath))
        using (var paint = new SKPaint { Typeface = face, TextSize = 10, Color = SKColors.Black })
        {
            for (int i = 0; i < pages; i++) { var canvas = pdf.BeginPage(595.3f, 841.9f); canvas.Clear(SKColors.White); if (i == 0 && id == "food_company") { canvas.DrawText(payer, 386, 364, paint); canvas.DrawText("(", 300, 385, paint); canvas.DrawText(beneficiary, 80, 405, paint); } else if (i == 0 && id == "contracts") { canvas.DrawText(payer, 127, 347, paint); canvas.DrawText("(", 300, 385, paint); canvas.DrawText(beneficiary, 80, 405, paint); } else if (i == 0 && id == "ramadi") { canvas.DrawText(payer, 387, 362, paint); canvas.DrawText(beneficiary, 335, 378, paint); }
                else if (i == 0 && id == "housing") { canvas.DrawText(payer, 212, 312, paint); canvas.DrawText(beneficiary[^15..], 141, 328, paint); }
                else if (i == 0 && id == "water") { canvas.DrawText(payer, 387, 346, paint); canvas.DrawText(beneficiary[^15..], 41, 362, paint); }
                else if (i == 0 && id == "rafidain_anbar") canvas.DrawText(payer, 162, 312, paint);
                else if (i == 0 && id.EndsWith("group", StringComparison.Ordinal)) canvas.DrawText(payer, 387, 350, paint); pdf.EndPage(); }
            pdf.Close();
        }
        return file.ToArray();
    }
    string[] ids = ["rafidain_group", "other_banks_group", "cash_salary", "food_company", "food_tax", "tax_social", "contracts", "water", "housing", "rafidain_anbar", "ramadi"];
    var records = ids.Select(id => { var pages = id.EndsWith("group", StringComparison.Ordinal) ? 2 : 1; var content = Pdf(pages, id); return (id, pages, content); }).ToArray();
    var manifest = new { version = 1, templates = records.Select((r, i) => new { id = r.id, path = $"templates/{r.id}.pdf", sha256 = damaged && i == 0 ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(r.content)).ToLowerInvariant(), pages = r.pages }).ToArray() };
    using var output = new MemoryStream();
    using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
    {
        Entry(zip, "manifest.json", JsonSerializer.Serialize(manifest));
        foreach (var record in records) { var entry = zip.CreateEntry($"templates/{record.id}.pdf"); using var stream = entry.Open(); stream.Write(record.content); }
    }
    return output.ToArray();
}
static void MakeBackup(string path, bool wrongCount = false, bool duplicate = false, bool omitPdf = false, bool omitRecentMonth = false, bool brokenLedger = false)
{
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    var id = Guid.Parse("a0a0a0a0-b0b0-c0c0-d0d0-e0e0e0e0e0e0");
    Entry(zip, "manifest.json", JsonSerializer.Serialize(new { version = 5, months = omitRecentMonth ? new[] { "أيلول 2026" } : new[] { "تشرين الاول 2026", "أيلول 2026" }, employees = wrongCount ? 3 : 2, deductions = 3, staff = 0, accounts = 1, books = 1, createdAt = 700000000.25, futureField = new { kept = true, big = 12345678901234567L } }));
    Entry(zip, "months.jsonl", Lines(omitRecentMonth ? new[] { new { name = "أيلول 2026", employeeCount = 2, deductionCount = 3 } } : new[] { new { name = "أيلول 2026", employeeCount = 2, deductionCount = 3 }, new { name = "تشرين الاول 2026", employeeCount = 0, deductionCount = 0 } }));
    Entry(zip, "employees.jsonl", Lines(new[] {
        new { month = "أيلول 2026", name = "أحمد وهمي", rank = "رتبة وهمية", workplace = "قسم وهمي", net = (decimal?)1250000m,
              items = (byte[]?)JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, decimal> { ["بند وهمي"] = 250m, ["استقطاع الجوال"] = 5000m }),
              details = (byte[]?)JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["الدرجة"] = "تجريبية", ["طريقة الدفع"] = "بطاقة", ["مجموع استقطاعات المصارف"] = "٢٬٥٠٠" }), rawColumns = (byte[]?)null },
        new { month = "أيلول 2026", name = "علي وهمي", rank = "رتبة وهمية", workplace = "قسم وهمي", net = (decimal?)null, items = (byte[]?)null, details = (byte[]?)null, rawColumns = (byte[]?)null }
    }));
    Entry(zip, "deductions.jsonl", Lines(new[] {
        new { month = "أيلول 2026", name = "أحمد وهمي", title = "جهة وهمية", amount = (decimal?)2500m, note = "", rank = "" },
        new { month = "أيلول 2026", name = "احمد وهمي", title = "جهة وهمية", amount = (decimal?)null, note = "", rank = "" },
        new { month = "أيلول 2026", name = "علي وهمي", title = "جهة وهمية", amount = (decimal?)null, note = "⟦حالة الاستقطاع:موقوف⟧", rank = "" }
    }));
    Entry(zip, "staff.jsonl", "");
    Entry(zip, "accounts.jsonl", Lines(new[] { new { entity = "جهة وهمية", iban = "IQ00TEST000000000000000", branch = "فرع تجريبي", swift = "TESTIQBAAAA", localAccount = (string?)null } }));
    var settings = new Dictionary<string, string> { ["bankStatementManualAmounts"] = "YnBsaXN0MDBPEFp7Itij2YrZhNmI2YQgMjAyNiI6IHsiYWNjdW11bGF0ZWRMZWF2ZSI6IDEwMDAsICJlbXBsb3llZVJld2FyZHMiOiAyMDAsICJzaW5nbGVMaXN0IjogMzAwfX0IAAAAAAAAAQEAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAGU=" };
    if (brokenLedger) { using var plist = new MemoryStream(); PropertyListParser.SaveAsBinary(new NSData(Encoding.UTF8.GetBytes("{\"version\":999,\"periods\":[]}")), plist); settings["bankReconciliations"] = Convert.ToBase64String(plist.ToArray()); }
    Entry(zip, "settings.json", JsonSerializer.Serialize(settings));
    Entry(zip, "books/index.json", JsonSerializer.Serialize(new[] { new { id, title = "كتاب وهمي", month = "أيلول 2026", pages = 1, entity = "جهة وهمية" } }));
    if (!omitPdf) Entry(zip, $"books/{id.ToString().ToUpperInvariant()}.pdf", "%PDF-1.4\n%%EOF");
    if (duplicate) Entry(zip, "manifest.json", "{}");
}
static void Reject(string path, string reason)
{
    try { _ = BackupReader.Read(path); }
    catch (InvalidDataException) { return; }
    throw new InvalidOperationException(reason);
}
var root = Path.Combine(Path.GetTempPath(), "salary-desktop-tests-" + Guid.NewGuid());
var sharedSamples = new[]
{
    "{\"headers\":[\"الاسم\",\"الاسم\",\"\"],\"values\":[\"أ\",\"\",\"س\"],\"future\":{\"keep\":7}}",
    "  { \"headers\" : [\"أ\\\"ب\", \"ملاحظة\"], \"values\": [\"\\u0627\", \"\"] } \n",
    "{\"headers\":[],\"values\":[]}",
    "{\"values\":[\"x\"],\"headers\":[\"a\"]}",
    "{\"future\":{\"headers\":[\"متداخل\"]},\"values\":[\"text \\\"headers\\\": [x]\"],\"headers\":[\"a\"]}",
};
foreach (var sample in sharedSamples)
{
    var bytes = Encoding.UTF8.GetBytes(sample);
    var parts = SharedPayrollColumns.Split(bytes) ?? throw new InvalidOperationException("supported shared header rejected");
    if (sample == sharedSamples[0]) Check(parts.SchemaId == "A4760FF0B92D6CA8158C9517118F6B68BC798793BF5DF760141CBB2966A60A48", "shared schema matches cross-platform hash vector");
    Check(SharedPayrollColumns.Join(parts.SchemaId, parts.Prefix, parts.Before, parts.Tail).AsSpan().SequenceEqual(bytes), "shared payroll columns round-trip exact bytes");
    try { _ = SharedPayrollColumns.Join(parts.SchemaId, [.. parts.Prefix, 32], parts.Before, parts.Tail); throw new InvalidOperationException("corrupt schema accepted"); }
    catch (InvalidDataException) { }
}
var ordered = SharedPayrollColumns.Split(Encoding.UTF8.GetBytes("{\"headers\":[\"a\"],\"values\":[\"x\"]}"))!;
var reordered = SharedPayrollColumns.Split(Encoding.UTF8.GetBytes("{\"values\":[\"x\"],\"headers\":[\"a\"]}"))!;
Check(ordered.SchemaId == reordered.SchemaId, "shared schema does not depend on JSON property order");
foreach (var sample in new[] { "not-json", "{\"headers\":[\"a\"],\"values\":[]}", "{\"headers\":[1],\"values\":[\"x\"]}" })
    Check(SharedPayrollColumns.Split(Encoding.UTF8.GetBytes(sample)) is null, "unsupported columns require exact original fallback");
string? sharedID = null;
for (var i = 0; i < 5000; i++)
{
    var raw = JsonSerializer.SerializeToUtf8Bytes(new { headers = new[] { "الاسم", "الاسم", "ملاحظة" }, values = new[] { $"منتسب {i}", "", "\"نص\\وهمي\"" }, future = new { id = i } });
    var parts = SharedPayrollColumns.Split(raw) ?? throw new InvalidOperationException("generated shared header rejected");
    sharedID ??= parts.SchemaId;
    Check(parts.SchemaId == sharedID && SharedPayrollColumns.Join(parts.SchemaId, parts.Prefix, parts.Before, parts.Tail).AsSpan().SequenceEqual(raw), "shared schema preserves generated record bytes");
}
var rowSchemas = new Dictionary<string, byte[]>(StringComparer.Ordinal);
SharedPayrollBackupRow.Packed? firstPacked = null;
byte[]? firstSharedRow = null;
for (var i = 0; i < 1000; i++)
{
    var columns = JsonSerializer.SerializeToUtf8Bytes(new { headers = new[] { "اسم المنتسب", "رقم العمل", "الملاحظات", "الملاحظات" }, values = new[] { $"موظف {i}", "123", "", "وهمي" }, future = new { keep = true } });
    var row = JsonSerializer.SerializeToUtf8Bytes(new { month = "أيلول 2026", name = $"موظف {i}", rawColumns = columns, future = new { id = i, money = 12345678901234567L } });
    var packed = SharedPayrollBackupRow.Pack(row, rowSchemas);
    Check(packed.SchemaId is not null && SharedPayrollBackupRow.Unpack(packed, rowSchemas).AsSpan().SequenceEqual(row), "packed payroll row keeps exact original bytes");
    if (firstPacked is null) { firstPacked = packed; firstSharedRow = row; }
}
Check(rowSchemas.Count == 1, "identical payroll headers are stored once");
if (Environment.GetEnvironmentVariable("SALARY_SHARED_FIXTURE_PATH") is { Length: > 0 } sharedFixture)
    File.WriteAllBytes(sharedFixture, JsonSerializer.SerializeToUtf8Bytes(new { Row = firstPacked, Schemas = rowSchemas, Original = firstSharedRow }));
var corruptRow = firstPacked! with { After = [.. firstPacked!.After!, 32] };
try { _ = SharedPayrollBackupRow.Unpack(corruptRow, rowSchemas); throw new InvalidOperationException("corrupt payroll row accepted"); }
catch (InvalidDataException) { }
try { _ = SharedPayrollBackupRow.Unpack(firstPacked!, new Dictionary<string, byte[]>()); throw new InvalidOperationException("missing payroll schema accepted"); }
catch (InvalidDataException) { }
var damagedSchemas = rowSchemas.ToDictionary(p => p.Key, p => p.Value.Concat(new byte[] { 32 }).ToArray(), StringComparer.Ordinal);
try { _ = SharedPayrollBackupRow.Unpack(firstPacked!, damagedSchemas); throw new InvalidOperationException("corrupted payroll schema accepted"); }
catch (InvalidDataException) { }
var unrecognizedRow = Encoding.UTF8.GetBytes("{\"rawColumns\":null,\"future\":\"محفوظ\"}");
var fallbackRow = SharedPayrollBackupRow.Pack(unrecognizedRow, rowSchemas);
Check(fallbackRow.SchemaId is null && SharedPayrollBackupRow.Unpack(fallbackRow, rowSchemas).AsSpan().SequenceEqual(unrecognizedRow), "unrecognized payroll row is retained verbatim");
var escapedColumns = Encoding.UTF8.GetBytes("{\"headers\":[\"࿿࿿࿿࿿࿿࿿࿿࿿࿿࿿\"],\"values\":[\"x\"]}");
var encodedEscapedColumns = Convert.ToBase64String(escapedColumns);
Check(encodedEscapedColumns.Contains('/') && encodedEscapedColumns.Contains('+'), "escape fixture includes base64 punctuation");
var escapedRow = Encoding.UTF8.GetBytes("{\"rawColumns\":\"" + encodedEscapedColumns.Replace("/", "\\/").Replace("+", "\\u002B") + "\",\"future\":true}");
var escapedPacked = SharedPayrollBackupRow.Pack(escapedRow, rowSchemas);
Check(escapedPacked.SchemaId is not null && SharedPayrollBackupRow.Unpack(escapedPacked, rowSchemas).AsSpan().SequenceEqual(escapedRow), "escaped Base64 JSON bytes round-trip exactly");
if (Environment.GetEnvironmentVariable("SALARY_ESCAPED_FIXTURE_PATH") is { Length: > 0 } escapedFixture)
    File.WriteAllBytes(escapedFixture, JsonSerializer.SerializeToUtf8Bytes(new { Row = escapedPacked, Schemas = rowSchemas, Original = escapedRow }));
Directory.CreateDirectory(root);
try
{
    string valid = Path.Combine(root, "valid.zip"), wrong = Path.Combine(root, "wrong.zip"), duplicate = Path.Combine(root, "duplicate.zip"), missing = Path.Combine(root, "missing.zip");
    MakeBackup(valid); MakeBackup(wrong, wrongCount: true); MakeBackup(duplicate, duplicate: true); MakeBackup(missing, omitPdf: true);
    var futureBackup = Path.Combine(root, "future-backup.zip"); MakeBackup(futureBackup);
    using (var zip = ZipFile.Open(futureBackup, ZipArchiveMode.Update))
    {
        var manifest = zip.GetEntry("manifest.json")!;
        using var reader = new StreamReader(manifest.Open(), Encoding.UTF8);
        var future = System.Text.Json.Nodes.JsonNode.Parse(reader.ReadToEnd())!.AsObject();
        reader.Close(); manifest.Delete(); future["version"] = 7;
        Entry(zip, "manifest.json", future.ToJsonString());
    }
    try { _ = BackupReader.Read(futureBackup); throw new InvalidOperationException("unknown future backup accepted"); }
    catch (InvalidDataException ex) { Check(ex.Message.Contains("الإصدار", StringComparison.Ordinal) || ex.Message.Contains("إصدار", StringComparison.Ordinal), "future backup version refused"); }
    var before = SHA256.HashData(File.ReadAllBytes(valid));
    var snapshot = BackupReader.Read(valid);
    Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(valid))), "backup was changed");
    Check(snapshot.Version == 5 && snapshot.Months[0].Name == "تشرين الاول 2026" && snapshot.Employees.Count == 2 && snapshot.Accounts.Count == 1 && snapshot.Books.Count == 1, "valid backup contents");
    Check(JsonSerializer.Deserialize<Dictionary<string, decimal>>(snapshot.Employees[0].Items!)!["بند وهمي"] == 250m &&
          JsonSerializer.Deserialize<Dictionary<string, string>>(snapshot.Employees[0].Details!)!["الدرجة"] == "تجريبية", "salary detail data preserved");
    var preview = Path.Combine(root, "preview.pdf");
    BackupReader.CopyBook(snapshot, snapshot.Books[0], preview);
    Check(File.ReadAllText(preview).StartsWith("%PDF-", StringComparison.Ordinal), "archive PDF copy");
    var existing = Path.Combine(root, "existing.pdf"); File.WriteAllText(existing, "existing");
    try { BackupReader.CopyBook(snapshot, snapshot.Books[0], existing); throw new InvalidOperationException("existing file overwritten"); }
    catch (IOException) { }
    Check(File.ReadAllText(existing) == "existing", "existing file preserved");
    var month = PayrollAnalysis.Summarize(snapshot, "أيلول 2026");
    var salaryXlsx = Path.Combine(root, "salary-fake.xlsx");
    MakeMonthlyXlsx(salaryXlsx, ["Name_m", "الرتبة", "NO_markaz", "صافي الراتب", "الرعاية الصحية", "طريقة الدفع", "الاسمي", "مجموع استقطاعات المصارف", "اسم الاستقطاع", "مبلغ الاستقطاع"],
        [["منتسب تجريبي", "شرطي", "قسم وهمي", "١٬٠٠٠", "50", "بطاقة", "700", "500", "شركة عراق الجوال", "100"]]);
    var salaryPreview = PayrollSpreadsheet.Read(salaryXlsx, "تشرين الثاني 2026");
    Check(salaryPreview.Problems.Count == 0 && salaryPreview.Employees.Single().Net == 1000m && JsonSerializer.Deserialize<Dictionary<string, decimal>>(salaryPreview.Employees.Single().Items!)!["شركة عراق الجوال"] == 100m,
        "payroll workbook retains salary and named deduction");
    var invalidSalary = Path.Combine(root, "invalid-salary.xlsx");
    MakeMonthlyXlsx(invalidSalary, ["الاسم", "صافي الراتب"], [["منتسب تجريبي", "1,2"]]);
    Check(PayrollSpreadsheet.Read(invalidSalary, "تشرين الثاني 2026").Problems.Count == 1, "malformed grouped amount is rejected instead of guessed");
    var deductionXlsx = Path.Combine(root, "deductions-fake.xlsx");
    MakeMonthlyXlsx(deductionXlsx, ["الاسم", "جهة التنفيذ", "المبلغ", "الملاحظات"], [["منتسب تجريبي", "جهة وهمية", "500", ""]]);
    var deductionPreview = PayrollSpreadsheet.Read(deductionXlsx, "تشرين الثاني 2026");
    Check(deductionPreview.Problems.Count == 0 && deductionPreview.Deductions.Single().Amount == 500m, "deduction workbook parsed for selected month");
    var inactiveXlsx = Path.Combine(root, "inactive-fake.xlsx");
    MakeMonthlyXlsx(inactiveXlsx, ["الاسم", "جهة التنفيذ", "المبلغ"], [["منتسب تجريبي", "جهة وهمية", "موقوف"]]);
    Check(PayrollSpreadsheet.Read(inactiveXlsx, "تشرين الثاني 2026").Deductions.Single() is { Amount: null } inactive && PayrollAnalysis.IsInactive(inactive), "inactive deduction status is retained without counting as amount");
    var annualXlsx = Path.Combine(root, "annual-fake.xlsx");
    MakeMonthlyXlsx(annualXlsx, ["الاسم", "جهة التنفيذ", "المبلغ", "الشهر"], [["منتسب تجريبي", "جهة وهمية", "500", "1"]]);
    Check(PayrollSpreadsheet.Read(annualXlsx, "تشرين الثاني 2026").Problems.Any(p => p.Message.Contains("متعدد الأشهر", StringComparison.Ordinal)), "annual deduction file is rejected rather than assigned to one month");
    // الاستيراد الموحّد: التعرّف على نوع الملف من أعمدته، والشهر من اسم الملف.
    Check(ImportDetector.Detect(salaryXlsx, snapshot).Kind == ImportKind.Payroll && ImportDetector.Detect(deductionXlsx, snapshot).Kind == ImportKind.Payroll &&
          ImportDetector.Detect(annualXlsx, snapshot).Kind == ImportKind.Annual && ImportDetector.Detect(invalidSalary, snapshot).Kind == ImportKind.Payroll, "smart import recognizes payroll, deduction and annual workbooks");
    Check(ImportDetector.Detect(Path.Combine(root, "x.zip"), snapshot).Kind == ImportKind.Backup && ImportDetector.Detect(Path.Combine(root, "notes.docx"), snapshot).Kind == ImportKind.Unknown, "smart import sorts backups and rejects unsupported files");
    Check(ImportDetector.SuggestedMonth(Path.Combine(root, "رواتب أيلول 2026.xlsx"), snapshot) == "أيلول 2026" && ImportDetector.SuggestedMonth(Path.Combine(root, "ملف.xlsx"), snapshot) is null, "smart import reads the month from the file name");
    Check(ImportDetector.Order([new ImportCandidate("b.xlsx", ImportKind.Payroll, "تشرين الأول 2026", ""), new ImportCandidate("a.zip", ImportKind.Backup, null, ""), new ImportCandidate("c.xlsx", ImportKind.Payroll, "أيلول 2026", "")]).Select(c => c.Path).SequenceEqual(["a.zip", "c.xlsx", "b.xlsx"]), "smart import opens the backup first then months in order");
    var payrollStore = new LocalPayrollImports(Path.Combine(root, "payroll-data")); var originalPayroll = payrollStore.Load(snapshot);
    var salaryState = payrollStore.Import(snapshot, originalPayroll, salaryPreview, false);
    var combinedPayroll = payrollStore.Import(snapshot, salaryState, deductionPreview, false);
    Check(combinedPayroll.Snapshot.Employees.Count == snapshot.Employees.Count + 1 && combinedPayroll.Snapshot.Deductions.Count == snapshot.Deductions.Count + 1 && payrollStore.Load(snapshot).Snapshot.Months.Count == snapshot.Months.Count + 1, "new month imports and persists both workbook types");
    var overlayCandidatePath = Path.Combine(root, "payroll-overlay-candidate.zip");
    SharedPayrollOverlay.Create(snapshot, combinedPayroll, overlayCandidatePath);
    var overlayRestoredPath = Path.Combine(root, "payroll-overlay-restored.zip");
    SharedPayrollCandidate.RestoreOriginal(overlayCandidatePath, overlayRestoredPath);
    var overlaySnapshot = BackupReader.Read(overlayRestoredPath);
    Check(overlaySnapshot.Months.SequenceEqual(combinedPayroll.Snapshot.Months) &&
          overlaySnapshot.Employees.Count == combinedPayroll.Snapshot.Employees.Count && overlaySnapshot.Deductions.SequenceEqual(combinedPayroll.Snapshot.Deductions) &&
          overlaySnapshot.Employees.Single(e => e.Month == salaryPreview.Month).RawColumns!.AsSpan().SequenceEqual(combinedPayroll.Snapshot.Employees.Single(e => e.Month == salaryPreview.Month).RawColumns!),
        "payroll overlay candidate includes pending imported months and retains their original columns");
    using (var beforeZip = ZipFile.OpenRead(snapshot.SourcePath))
    using (var afterZip = ZipFile.OpenRead(overlayRestoredPath))
    using (var beforeRows = beforeZip.GetEntry("employees.jsonl")!.Open())
    using (var afterRows = afterZip.GetEntry("employees.jsonl")!.Open())
    {
        using var firstBefore = new StreamReader(beforeRows, Encoding.UTF8); using var firstAfter = new StreamReader(afterRows, Encoding.UTF8);
        Check(firstBefore.ReadLine() == firstAfter.ReadLine(), "payroll overlay preserves original unrelated employee rows");
    }
    var staleOverlay = Path.Combine(root, "stale-overlay.zip");
    try { SharedPayrollOverlay.Create(snapshot with { SourceDigest = new string('A', 64) }, combinedPayroll, staleOverlay); throw new InvalidOperationException("stale source accepted"); }
    catch (InvalidDataException) { }
    Check(!File.Exists(staleOverlay), "stale source does not create a candidate");
    var overlayUnknownSource = Path.Combine(root, "overlay-unknown-source.zip"); File.Copy(valid, overlayUnknownSource);
    using (var zip = ZipFile.Open(overlayUnknownSource, ZipArchiveMode.Update))
    {
        var oldRows = zip.GetEntry("employees.jsonl")!;
        string[] lines;
        using (var reader = new StreamReader(oldRows.Open(), Encoding.UTF8)) lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        oldRows.Delete();
        var node = System.Text.Json.Nodes.JsonNode.Parse(lines[0])!.AsObject();
        node["futureEmployeeField"] = System.Text.Json.Nodes.JsonNode.Parse("{\"large\":9007199254740993,\"nested\":[\"محفوظ\"]}");
        lines[0] = node.ToJsonString();
        Entry(zip, "employees.jsonl", string.Join("\n", lines) + "\n");
        Entry(zip, "future-unknown.bin", "محتوى مستقبلي وهمي");
    }
    var unknownOverlaySource = BackupReader.Read(overlayUnknownSource);
    var unknownOverlayStore = new LocalPayrollImports(Path.Combine(root, "overlay-unknown-imports"));
    var unknownOverlayState = unknownOverlayStore.Import(unknownOverlaySource, unknownOverlayStore.Load(unknownOverlaySource), salaryPreview, false);
    var unknownOverlayCandidate = Path.Combine(root, "overlay-unknown-candidate.zip"); var unknownOverlayRestored = Path.Combine(root, "overlay-unknown-restored.zip");
    SharedPayrollOverlay.Create(unknownOverlaySource, unknownOverlayState, unknownOverlayCandidate);
    SharedPayrollCandidate.RestoreOriginal(unknownOverlayCandidate, unknownOverlayRestored);
    using (var originalZip = ZipFile.OpenRead(overlayUnknownSource))
    using (var restoredZip = ZipFile.OpenRead(unknownOverlayRestored))
    {
        using var originalRows = new StreamReader(originalZip.GetEntry("employees.jsonl")!.Open(), Encoding.UTF8);
        using var restoredRows = new StreamReader(restoredZip.GetEntry("employees.jsonl")!.Open(), Encoding.UTF8);
        Check(originalRows.ReadLine() == restoredRows.ReadLine(), "overlay preserves unknown employee fields and exact original bytes");
        using var unknownBefore = originalZip.GetEntry("future-unknown.bin")!.Open(); using var unknownAfter = restoredZip.GetEntry("future-unknown.bin")!.Open();
        Check(SHA256.HashData(unknownBefore).AsSpan().SequenceEqual(SHA256.HashData(unknownAfter)), "overlay retains unknown archive files");
        using var manifestReader = restoredZip.GetEntry("manifest.json")!.Open();
        using var restoredManifest = JsonDocument.Parse(manifestReader);
        Check(restoredManifest.RootElement.GetProperty("futureField").GetProperty("big").GetInt64() == 12345678901234567L, "overlay retains unknown manifest fields");
    }
    try { _ = payrollStore.Import(snapshot, originalPayroll, salaryPreview, true); throw new InvalidOperationException("stale payroll import accepted"); }
    catch (InvalidDataException) { }
    try { _ = payrollStore.Import(snapshot, combinedPayroll, salaryPreview, false); throw new InvalidOperationException("unreviewed salary replacement accepted"); }
    catch (InvalidDataException) { }
    Check(payrollStore.Load(snapshot).Revision == combinedPayroll.Revision, "stale and unreviewed imports preserve existing data");
    try { _ = payrollStore.Import(snapshot, combinedPayroll, PayrollSpreadsheet.Read(invalidSalary, "تشرين الثاني 2026"), true); throw new InvalidOperationException("invalid payroll import accepted"); }
    catch (InvalidDataException) { }
    Check(payrollStore.Load(snapshot).Revision == combinedPayroll.Revision, "invalid payroll import preserves previous data");
    // صيغة الإصدار السابق وبصمته تبقيان قابلة للقراءة دون تحويل أو استبدال الملف.
    var legacyRoot = Path.Combine(root, "legacy-payroll"); var legacyFolder = Path.Combine(legacyRoot, snapshot.SourceDigest); Directory.CreateDirectory(legacyFolder);
    var legacyBytes = JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, SourceDigest = snapshot.SourceDigest, Months = combinedPayroll.Imports });
    var legacyHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(legacyBytes));
    Check(legacyHash == combinedPayroll.Revision, "streamed payroll preserves the existing JSON representation and revision");
    File.WriteAllBytes(Path.Combine(legacyFolder, legacyHash + ".json"), legacyBytes);
    File.WriteAllText(Path.Combine(legacyFolder, "current.json"), JsonSerializer.Serialize(new { Version = 1, Revision = legacyHash }));
    var legacyPayrollStore = new LocalPayrollImports(legacyRoot); var legacyLoaded = legacyPayrollStore.Load(snapshot);
    Check(legacyLoaded.Revision == legacyHash && legacyLoaded.Snapshot.Deductions.Any(d => d.Month == deductionPreview.Month), "streaming reader accepts old payroll files");
    // ملف مؤقت من عملية انقطعت لا يغيّر المؤشر أو البيانات المعتمدة.
    File.WriteAllText(Path.Combine(legacyFolder, ".import-interrupted.tmp"), "{incomplete");
    Check(legacyPayrollStore.Load(snapshot).Revision == legacyHash, "interrupted temporary file is ignored");
    var legacyPath = Path.Combine(legacyFolder, legacyHash + ".json");
    File.AppendAllText(legacyPath, " ");
    try { _ = legacyPayrollStore.Import(snapshot, legacyLoaded, salaryPreview, true); throw new InvalidOperationException("corrupted payroll file accepted"); }
    catch (InvalidDataException) { }
    Check(File.ReadAllText(Path.Combine(legacyFolder, "current.json")).Contains(legacyHash, StringComparison.Ordinal), "corrupted source does not change payroll pointer");
    File.WriteAllBytes(legacyPath, legacyBytes);
    Check(legacyPayrollStore.Load(snapshot).Revision == legacyHash, "original payroll remains recoverable after failed import");
    // عدد كبير من سجلات تحتوي حقولاً ثنائية، ليتجاوز ناتج JSON المخزن عشرات الميغابايت.
    var largeRoot = Path.Combine(root, "large-payroll"); var largeStore = new LocalPayrollImports(largeRoot);
    var payload = new byte[2048]; new Random(123).NextBytes(payload);
    var largeRows = Enumerable.Range(0, 8000).Select(i => new Employee(salaryPreview.Month, $"منتسب وهمي {i}", "شرطي", "قسم وهمي", 1000m, payload, null, null)).ToArray();
    var largePreview = salaryPreview with { Employees = largeRows, Deductions = [] };
    var largeSaved = largeStore.Import(snapshot, largeStore.Load(snapshot), largePreview, false);
    var largeFile = Path.Combine(largeRoot, snapshot.SourceDigest, largeSaved.Revision + ".json");
    Check(new FileInfo(largeFile).Length > 20_000_000 && largeStore.Load(snapshot).Imports.Single().Employees!.Count == largeRows.Length,
        "large streamed payroll survives save and reload");
    var replacement = salaryPreview with { Employees = [salaryPreview.Employees.Single() with { Net = 1001m }] };
    var replacedPayroll = payrollStore.Import(snapshot, combinedPayroll, replacement, true);
    var replaceOverlay = Path.Combine(root, "replace-overlay-v7.zip");
    var replaceOverlayRestored = Path.Combine(root, "replace-overlay-restored.zip");
    SharedPayrollOverlay.Create(snapshot, replacedPayroll, replaceOverlay);
    SharedPayrollCandidate.RestoreOriginal(replaceOverlay, replaceOverlayRestored);
    var replaceOverlaySnapshot = BackupReader.Read(replaceOverlayRestored);
    Check(replaceOverlaySnapshot.Employees.Single(e => e.Month == replacement.Month).Net == 1001m &&
          replaceOverlaySnapshot.Deductions.Any(d => d.Month == replacement.Month) &&
          replaceOverlaySnapshot.Employees.Count == replacedPayroll.Snapshot.Employees.Count &&
          replaceOverlaySnapshot.Deductions.Count == replacedPayroll.Snapshot.Deductions.Count,
        "replacing a salary month in overlay keeps its separately imported deductions");
    Check(Directory.GetFiles(Path.Combine(root, "payroll-data", snapshot.SourceDigest), "*.json").Length >= 4 && replacedPayroll.Snapshot.Employees.Single(e => e.Month == replacement.Month).Net == 1001m && replacedPayroll.Snapshot.Deductions.Any(d => d.Month == replacement.Month), "reviewed salary replacement preserves deduction records and earlier versions");
    var salaryBackup = Path.Combine(root, "salary-import-backup.zip"); ReconciliationBackupExport.Write(combinedPayroll.Snapshot, [], salaryBackup);
    using (var archive = ZipFile.OpenRead(salaryBackup))
    using (var reader = new StreamReader(archive.GetEntry("employees.jsonl")!.Open(), Encoding.UTF8))
    {
        var rows = reader.ReadToEnd();
        Check(rows.Contains("منتسب تجريبي", StringComparison.Ordinal), "rewritten backup keeps Arabic names as compact UTF-8");
    }
    try { _ = BackupReader.Read(duplicate); throw new InvalidOperationException("duplicate ZIP entry accepted"); }
    catch (InvalidDataException ex) { Check(ex.Message.Contains("مكرر", StringComparison.Ordinal) && ex.Message.Contains("manifest.json", StringComparison.Ordinal), "duplicate backup entry is identified separately"); }
    // محاكاة فهرس ZIP يعلن حجماً كبيراً دون إنشاء ملف حجمه 1.5 غيغابايت.
    var oversizePath = Path.Combine(root, "oversize-metadata.zip");
    using (var zip = ZipFile.Open(oversizePath, ZipArchiveMode.Create)) Entry(zip, "employees.jsonl", "x");
    var oversizeBytes = File.ReadAllBytes(oversizePath);
    var central = oversizeBytes.AsSpan().IndexOf(new byte[] { 0x50, 0x4b, 0x01, 0x02 });
    Check(central >= 0, "ZIP central directory exists");
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(oversizeBytes.AsSpan(central + 24, 4), 1_500_000_001);
    File.WriteAllBytes(oversizePath, oversizeBytes);
    try { _ = BackupReader.Read(oversizePath); throw new InvalidOperationException("oversized ZIP entry accepted"); }
    catch (InvalidDataException ex) { Check(ex.Message.Contains("employees.jsonl", StringComparison.Ordinal) && ex.Message.Contains("بايت", StringComparison.Ordinal), "oversized entry is identified with its size"); }
    BackupDestination.Guard(salaryBackup, combinedPayroll.Snapshot); BackupDestination.Guard(Path.Combine(root, "not-yet.zip"), snapshot);
    try { BackupDestination.Guard(salaryBackup, snapshot); throw new InvalidOperationException("backup file overwritten by a copy missing a month"); }
    catch (InvalidDataException) { }
    var restoredSalary = BackupReader.Read(salaryBackup);
    var candidatePath = Path.Combine(root, "candidate-v7.zip");
    var beforeCandidate = SHA256.HashData(File.ReadAllBytes(salaryBackup));
    SharedPayrollCandidate.Create(salaryBackup, candidatePath);
    if (Environment.GetEnvironmentVariable("SALARY_CANDIDATE_FIXTURE_DIR") is { Length: > 0 } candidateFixtureDir)
    {
        Directory.CreateDirectory(candidateFixtureDir);
        File.Copy(candidatePath, Path.Combine(candidateFixtureDir, "candidate-v7.zip"), true);
        File.Copy(salaryBackup, Path.Combine(candidateFixtureDir, "candidate-source.zip"), true);
    }
    Check(SHA256.HashData(File.ReadAllBytes(salaryBackup)).AsSpan().SequenceEqual(beforeCandidate), "candidate generation preserves original backup");
    using (var originalZip = ZipFile.OpenRead(salaryBackup))
    using (var candidateZip = ZipFile.OpenRead(candidatePath))
    {
        using var schemasReader = new StreamReader(candidateZip.GetEntry("v7/payroll-schemas.jsonl")!.Open(), Encoding.UTF8);
        var candidateSchemas = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        while (schemasReader.ReadLine() is { } line)
        {
            using var schema = JsonDocument.Parse(line);
            candidateSchemas.Add(schema.RootElement.GetProperty("Id").GetString()!, schema.RootElement.GetProperty("Data").GetBytesFromBase64());
        }
        using var rows = new StreamReader(candidateZip.GetEntry("employees.jsonl")!.Open(), Encoding.UTF8);
        using var recovered = new MemoryStream();
        while (rows.ReadLine() is { } line)
        {
            var row = JsonSerializer.Deserialize<SharedPayrollBackupRow.Packed>(line)!;
            recovered.Write(SharedPayrollBackupRow.Unpack(row, candidateSchemas));
        }
        using var originalRows = originalZip.GetEntry("employees.jsonl")!.Open();
        Check(SHA256.HashData(recovered.ToArray()).AsSpan().SequenceEqual(SHA256.HashData(originalRows)), "candidate restores every employee byte");
        Check(candidateSchemas.Count > 0, "candidate stored shared headers");
    }
    try { SharedPayrollCandidate.Create(salaryBackup, candidatePath); throw new InvalidOperationException("candidate overwrote existing file"); }
    catch (InvalidDataException) { }
    Check(SHA256.HashData(File.ReadAllBytes(salaryBackup)).AsSpan().SequenceEqual(beforeCandidate), "candidate overwrite refusal preserves source");
    try { _ = BackupReader.Read(candidatePath); throw new InvalidOperationException("old reader accepted candidate format"); }
    catch (InvalidDataException) { }
    var unpackedPath = Path.Combine(root, "candidate-restored-original.zip");
    SharedPayrollCandidate.RestoreOriginal(candidatePath, unpackedPath);
    using (var beforeZip = ZipFile.OpenRead(salaryBackup))
    using (var afterZip = ZipFile.OpenRead(unpackedPath))
    {
        Check(beforeZip.Entries.Count == afterZip.Entries.Count, "restored candidate retains all archive entries");
        foreach (var originalEntry in beforeZip.Entries)
        {
            var restoredEntry = afterZip.GetEntry(originalEntry.FullName) ?? throw new InvalidOperationException("restored entry missing");
            using var a = originalEntry.Open(); using var b = restoredEntry.Open();
            Check(SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b)), "candidate round-trip preserves each entry");
        }
    }
    Check(BackupReader.Read(unpackedPath).Employees.Count == combinedPayroll.Snapshot.Employees.Count, "restored legacy backup opens normally");
    var overlayActions = new Dictionary<string, Action<Stream>>(StringComparer.Ordinal);
    overlayActions["manifest.json"] = output =>
    {
        using var zip = ZipFile.OpenRead(salaryBackup); using var input = zip.GetEntry("manifest.json")!.Open();
        var node = System.Text.Json.Nodes.JsonNode.Parse(input)!.AsObject();
        node["employees"] = node["employees"]!.GetValue<int>() + 1;
        node["deductions"] = node["deductions"]!.GetValue<int>() + 1;
        JsonSerializer.Serialize(output, node);
    };
    overlayActions["months.jsonl"] = output =>
    {
        using var zip = ZipFile.OpenRead(salaryBackup); using var input = zip.GetEntry("months.jsonl")!.Open();
        using var reader = new StreamReader(input, Encoding.UTF8); using var writer = new StreamWriter(output, new UTF8Encoding(false), 4096, true);
        while (reader.ReadLine() is { } line)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(line)!.AsObject();
            if (node["name"]!.GetValue<string>() == salaryPreview.Month)
            {
                node["employeeCount"] = node["employeeCount"]!.GetValue<int>() + 1;
                node["deductionCount"] = node["deductionCount"]!.GetValue<int>() + 1;
            }
            writer.WriteLine(node.ToJsonString());
        }
    };
    overlayActions["employees.jsonl"] = output =>
    {
        using var zip = ZipFile.OpenRead(salaryBackup); using var input = zip.GetEntry("employees.jsonl")!.Open(); input.CopyTo(output);
        var raw = JsonSerializer.SerializeToUtf8Bytes(new { headers = new[] { "الاسم", "الصافي", "ملاحظة" }, values = new[] { "إضافة وهمية", "700", "" } });
        using var writer = new StreamWriter(output, new UTF8Encoding(false), 4096, true);
        writer.WriteLine(JsonSerializer.Serialize(new { month = salaryPreview.Month, name = "إضافة وهمية", rank = "شرطي", workplace = "قسم وهمي", net = 700, items = (byte[]?)null, details = (byte[]?)null, rawColumns = raw }));
    };
    overlayActions["deductions.jsonl"] = output =>
    {
        using var zip = ZipFile.OpenRead(salaryBackup); using var input = zip.GetEntry("deductions.jsonl")!.Open(); input.CopyTo(output);
        using var writer = new StreamWriter(output, new UTF8Encoding(false), 4096, true);
        writer.WriteLine(JsonSerializer.Serialize(new { month = salaryPreview.Month, name = "إضافة وهمية", title = "جهة وهمية", amount = 30, note = "", rank = "شرطي" }));
    };
    var mergedCandidate = Path.Combine(root, "merged-candidate.zip");
    SharedPayrollCandidate.CreateMerged(salaryBackup, mergedCandidate, overlayActions);
    var mergedRestored = Path.Combine(root, "merged-restored.zip"); SharedPayrollCandidate.RestoreOriginal(mergedCandidate, mergedRestored);
    var mergedSnapshot = BackupReader.Read(mergedRestored);
    Check(mergedSnapshot.Employees.Any(e => e.Month == salaryPreview.Month && e.Name == "إضافة وهمية" && e.Net == 700m) &&
          mergedSnapshot.Deductions.Any(d => d.Month == salaryPreview.Month && d.Name == "إضافة وهمية" && d.Amount == 30m) &&
          mergedSnapshot.Employees.Count == combinedPayroll.Snapshot.Employees.Count + 1 && mergedSnapshot.Deductions.Count == combinedPayroll.Snapshot.Deductions.Count + 1,
        "candidate merges explicit fictional payroll and deduction changes before compacting");
    var failedMerge = Path.Combine(root, "failed-merge.zip");
    try { SharedPayrollCandidate.CreateMerged(salaryBackup, failedMerge, new Dictionary<string, Action<Stream>> { ["employees.jsonl"] = s => { s.WriteByte(65); throw new IOException("simulated interruption"); } }); throw new InvalidOperationException("interrupted merge accepted"); }
    catch (IOException) { }
    Check(!File.Exists(failedMerge) && !Directory.EnumerateFiles(root, ".salary-merge-*.tmp").Any() && SHA256.HashData(File.ReadAllBytes(salaryBackup)).AsSpan().SequenceEqual(beforeCandidate),
        "interrupted merge preserves source and cleans temporary output");
    if (Environment.GetEnvironmentVariable("SALARY_SWIFT_CANDIDATE_PATH") is { Length: > 0 } swiftCandidate &&
        Environment.GetEnvironmentVariable("SALARY_SWIFT_SOURCE_PATH") is { Length: > 0 } swiftSource)
    {
        var swiftRestored = Path.Combine(root, "swift-candidate-restored.zip");
        SharedPayrollCandidate.RestoreOriginal(swiftCandidate, swiftRestored);
        using var sourceZip = ZipFile.OpenRead(swiftSource); using var restoredZip = ZipFile.OpenRead(swiftRestored);
        Check(sourceZip.Entries.Count == restoredZip.Entries.Count, "Swift candidate keeps every original archive entry");
        foreach (var sourceEntry in sourceZip.Entries)
        {
            var other = restoredZip.GetEntry(sourceEntry.FullName) ?? throw new InvalidOperationException("Swift candidate entry missing");
            using var a = sourceEntry.Open(); using var b = other.Open();
            Check(SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b)), "Swift candidate round-trip preserves entry bytes");
        }
        Console.WriteLine("C# restored Swift-produced candidate archive entries exactly.");
    }
    var corruptCandidate = Path.Combine(root, "candidate-corrupt.zip"); File.Copy(candidatePath, corruptCandidate);
    using (var zip = ZipFile.Open(corruptCandidate, ZipArchiveMode.Update))
    {
        zip.GetEntry("v7/payroll-schemas.jsonl")!.Delete();
        Entry(zip, "v7/payroll-schemas.jsonl", "{\"Id\":\"BAD\",\"Data\":\"YQ==\"}\n");
    }
    var failedRestore = Path.Combine(root, "candidate-failed-restore.zip");
    try { SharedPayrollCandidate.RestoreOriginal(corruptCandidate, failedRestore); throw new InvalidOperationException("corrupt candidate restored"); }
    catch (InvalidDataException) { }
    Check(!File.Exists(failedRestore) && !Directory.EnumerateFiles(root, ".salary-unpack-*.tmp").Any(), "corrupt candidate leaves no output or temporary file");
    var largeCandidateSource = Path.Combine(root, "large-candidate-source.zip");
    var longHeaders = Enumerable.Range(0, 100).Select(i => $"عمود رواتب واستقطاعات وهمي رقم {i}").ToArray();
    using (var archive = ZipFile.Open(largeCandidateSource, ZipArchiveMode.Create))
    {
        Entry(archive, "manifest.json", "{\"version\":5,\"future\":{\"kept\":true}}");
        Entry(archive, "future-unknown.bin", "محتوى وهمي محفوظ");
        var candidateEmployees = archive.CreateEntry("employees.jsonl", CompressionLevel.Optimal);
        using var writer = new StreamWriter(candidateEmployees.Open(), new UTF8Encoding(false));
        for (var i = 0; i < 3000; i++)
        {
            var values = Enumerable.Range(0, 100).Select(n => n == 0 ? $"منتسب وهمي {i}" : "").ToArray();
            var raw = JsonSerializer.SerializeToUtf8Bytes(new { headers = longHeaders, values });
            writer.WriteLine(JsonSerializer.Serialize(new { name = $"منتسب وهمي {i}", rawColumns = raw, future = new { id = i } }));
        }
    }
    var largeCandidatePath = Path.Combine(root, "large-candidate-v7.zip");
    SharedPayrollCandidate.Create(largeCandidateSource, largeCandidatePath);
    using (var sourceZip = ZipFile.OpenRead(largeCandidateSource))
    using (var compactZip = ZipFile.OpenRead(largeCandidatePath))
    {
        var originalSize = sourceZip.GetEntry("employees.jsonl")!.Length;
        var compactSize = compactZip.GetEntry("employees.jsonl")!.Length + compactZip.GetEntry("v7/payroll-schemas.jsonl")!.Length;
        Check(compactSize < originalSize * 0.7, "candidate reduces repeated headers on a larger fictional dataset");
        Console.WriteLine($"Fictional candidate: {originalSize:N0} to {compactSize:N0} uncompressed bytes across 3000 rows.");
    }
    var invalidCandidatePath = Path.Combine(root, "candidate-duplicate.zip");
    try { SharedPayrollCandidate.Create(duplicate, invalidCandidatePath); throw new InvalidOperationException("duplicate source accepted for candidate"); }
    catch (InvalidDataException) { }
    Check(!File.Exists(invalidCandidatePath) && !Directory.EnumerateFiles(root, ".salary-compact-*.tmp").Any(), "failed candidate leaves no destination or temporary file");
    var interruptedSource = Path.Combine(root, "candidate-interrupted-source.zip");
    using (var zip = ZipFile.Open(interruptedSource, ZipArchiveMode.Create))
    {
        Entry(zip, "manifest.json", "{\"version\":5}");
        var employeeFile = zip.CreateEntry("employees.jsonl", CompressionLevel.Optimal);
        using var output = employeeFile.Open(); output.Write("{}\n"u8);
        var chunk = Enumerable.Repeat((byte)'x', 1024 * 1024).ToArray();
        for (var i = 0; i < 17; i++) output.Write(chunk);
    }
    var interruptedHash = SHA256.HashData(File.ReadAllBytes(interruptedSource));
    var interruptedTarget = Path.Combine(root, "candidate-interrupted-output.zip");
    try { SharedPayrollCandidate.Create(interruptedSource, interruptedTarget); throw new InvalidOperationException("oversized record accepted"); }
    catch (InvalidDataException) { }
    Check(!File.Exists(interruptedTarget) && !Directory.EnumerateFiles(root, ".salary-compact-*.tmp").Any() && SHA256.HashData(File.ReadAllBytes(interruptedSource)).AsSpan().SequenceEqual(interruptedHash),
        "failure after starting candidate write preserves source and removes incomplete output");
    if (Environment.GetEnvironmentVariable("SALARY_RUN_LARGE_CANDIDATE") == "1")
    {
        var stressSource = Path.Combine(root, "stress-source.zip"); var stressCandidate = Path.Combine(root, "stress-candidate.zip");
        using (var stressZip = ZipFile.Open(stressSource, ZipArchiveMode.Create))
        {
            Entry(stressZip, "manifest.json", "{\"version\":5,\"future\":{\"kept\":true}}");
            var stressEntry = stressZip.CreateEntry("employees.jsonl", CompressionLevel.Optimal);
            using var writer = new StreamWriter(stressEntry.Open(), new UTF8Encoding(false));
            for (var i = 0; i < 102_000; i++)
            {
                var values = Enumerable.Range(0, 100).Select(n => n == 0 ? $"منتسب وهمي {i}" : "").ToArray();
                var raw = JsonSerializer.SerializeToUtf8Bytes(new { headers = longHeaders, values });
                writer.WriteLine(JsonSerializer.Serialize(new { name = $"منتسب وهمي {i}", rawColumns = raw, future = new { id = i } }));
            }
        }
        SharedPayrollCandidate.Create(stressSource, stressCandidate);
        using var inputZip = ZipFile.OpenRead(stressSource); using var outputZip = ZipFile.OpenRead(stressCandidate);
        var stressBefore = inputZip.GetEntry("employees.jsonl")!.Length;
        var stressAfter = outputZip.GetEntry("employees.jsonl")!.Length + outputZip.GetEntry("v7/payroll-schemas.jsonl")!.Length;
        Check(stressBefore > 2_274_462_462 && stressAfter < stressBefore * 0.7, "large candidate checks a workload above the reported size");
        Console.WriteLine($"Large fictional candidate: {stressBefore:N0} to {stressAfter:N0} uncompressed bytes; peak working set {System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64:N0} bytes.");
    }
    var rawBefore = combinedPayroll.Snapshot.Employees.Single(e => e.Month == salaryPreview.Month).RawColumns!;
    var rawAfter = restoredSalary.Employees.Single(e => e.Month == salaryPreview.Month).RawColumns!;
    using (var beforeJson = JsonDocument.Parse(rawBefore))
    using (var afterJson = JsonDocument.Parse(rawAfter))
        Check(rawAfter.Length < rawBefore.Length && JsonElement.DeepEquals(beforeJson.RootElement, afterJson.RootElement), "backup compacts original columns without changing any values or headers");
    var unknownRaw = JsonSerializer.SerializeToUtf8Bytes(new { headers = new[] { "الاسم", "الاسم", "" }, values = new[] { "اختبار", "", "س" }, future = new { note = "يبقى كما هو" } });
    var unknownSource = combinedPayroll.Snapshot with { Employees = combinedPayroll.Snapshot.Employees.Select(e => e.Month == salaryPreview.Month ? e with { RawColumns = unknownRaw } : e).ToArray() };
    var unknownPath = Path.Combine(root, "unknown-columns-backup.zip"); ReconciliationBackupExport.Write(unknownSource, [], unknownPath);
    Check(BackupReader.Read(unknownPath).Employees.Single(e => e.Month == salaryPreview.Month).RawColumns!.AsSpan().SequenceEqual(unknownRaw), "unknown original-column fields preserve their exact bytes");
    Check(restoredSalary.Months.Any(m => m.Name == "تشرين الثاني 2026" && m.EmployeeCount == 1 && m.DeductionCount == 1) &&
          restoredSalary.Employees.Single(e => e.Month == "تشرين الثاني 2026").Net == 1000m && restoredSalary.Deductions.Single(d => d.Month == "تشرين الثاني 2026").Amount == 500m,
        "imported payroll and deductions restore from backup");
    using (var zip = ZipFile.OpenRead(salaryBackup))
    using (var reader = new StreamReader(zip.GetEntry("months.jsonl")!.Open()))
    {
        var nodes = new List<JsonElement>(); while (reader.ReadLine() is { } line) nodes.Add(JsonSerializer.Deserialize<JsonElement>(line));
        var summary = nodes.Single(n => n.GetProperty("name").GetString() == "تشرين الثاني 2026").GetProperty("summary").GetBytesFromBase64();
        Check(JsonSerializer.Deserialize<JsonElement>(summary).GetProperty("شركة عراق الجوال").GetProperty("total").GetDecimal() == 100m, "backup updates payroll summary for imported month");
    }
    var priorMonthPreview = salaryPreview with { Month = "أيلول 2026", Employees = [salaryPreview.Employees.Single() with { Month = "أيلول 2026" }] };
    try { _ = payrollStore.Import(snapshot, replacedPayroll, priorMonthPreview, false); throw new InvalidOperationException("existing month replaced without review"); }
    catch (InvalidDataException) { }
    var replacedExisting = payrollStore.Import(snapshot, replacedPayroll, priorMonthPreview, true);
    Check(replacedExisting.Snapshot.Employees.Count(e => e.Month == "أيلول 2026") == 1 && replacedExisting.Snapshot.Deductions.Count(d => d.Month == "أيلول 2026") == snapshot.Deductions.Count(d => d.Month == "أيلول 2026"), "reviewed existing month replacement preserves deductions");
    var replacedBackup = Path.Combine(root, "replaced-payroll.zip"); ReconciliationBackupExport.Write(replacedExisting.Snapshot, [], replacedBackup);
    Check(BackupReader.Read(replacedBackup).Employees.Count(e => e.Month == "أيلول 2026") == 1 && snapshot.Employees.Count(e => e.Month == "أيلول 2026") == 2, "replacement backup preserves source and records");
    Check(month.Employees == 2 && month.NetTotal == 1250000m && month.MissingNet == 1 && month.Deductions == 2 && month.MissingAmounts == 1, "monthly totals and missing values");
    var bank = BankStatementCalculator.Calculate(snapshot, "أيلول 2026");
    // الاحتساب محفوظ لنفس البيانات والشهر، وأي تعديل (نسخة بيانات جديدة) يعيد الحساب.
    Check(ReferenceEquals(bank, BankStatementCalculator.Calculate(snapshot, "أيلول 2026")) && !ReferenceEquals(bank, BankStatementCalculator.Calculate(snapshot with { }, "أيلول 2026")) &&
          BankStatementCalculator.Calculate(snapshot with { }, "أيلول 2026").BankTotal == bank.BankTotal, "bank statement result is reused for the same data and recomputed for changed data");
    Check(ReferenceEquals(TransferPreparation.Prepare(snapshot, "أيلول 2026"), TransferPreparation.Prepare(snapshot, "أيلول 2026")), "transfer preparation is reused when returning to the page");
    Check(bank.Card == 1250000m && bank.Cash == 0m && bank.BankDeductions == 2500m && bank.Breakdown["شركة عراق الجوال"] == 5000m, "bank payroll sources");
    Check(bank.Manual.Total == 1500m && bank.BankTotal == 1259000m && !bank.ExecutionMatched && bank.MissingExecutionAmounts == 1, "bank manual amounts and reconciliation");
    var emptySettings = snapshot with { SettingsPlists = new Dictionary<string, byte[]>() };
    var funding = new BankMovement(Guid.NewGuid(), new DateOnly(2026, 9, 27), "تمويل تجريبي", 1000, MovementDirection.Credit, IsFunding: true);
    var refund = new BankMovement(Guid.NewGuid(), new DateOnly(2026, 10, 10), "وارد مسترجع تجريبي", 200, MovementDirection.Credit, IsRefund: true);
    var repay = new BankMovement(Guid.NewGuid(), new DateOnly(2026, 10, 27), "إعادة المسترجع", 150, MovementDirection.Debit, RefundOf: refund.Id, Matched: true);
    var spending = new BankMovement(Guid.NewGuid(), new DateOnly(2026, 10, 27), "مصروف تجريبي", 1050, MovementDirection.Debit, SourceId: "source", SourceLineIds: ["line"]);
    var period = new BankPeriod(Guid.NewGuid(), "أيلول 2026", funding.Date, spending.Date, 0, "", "", null, null, [funding, refund, repay, spending]);
    var overlaySettings = combinedPayroll.Snapshot.SettingsPlists.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    using (var futureSetting = new MemoryStream())
    {
        PropertyListParser.SaveAsBinary(new NSData(Encoding.UTF8.GetBytes("قيمة وهمية محفوظة")), futureSetting);
        overlaySettings["futureSetting"] = futureSetting.ToArray();
    }
    var overlayActive = combinedPayroll.Snapshot with
    {
        Accounts = combinedPayroll.Snapshot.Accounts.Append(new ExecutionAccount("جهة وهمية مضافة", "IQ00TEST", "فرع وهمي", "", null)).ToArray(),
        SettingsPlists = overlaySettings
    };
    var settingsCandidate = Path.Combine(root, "settings-overlay-v7.zip"); var settingsRestored = Path.Combine(root, "settings-overlay-restored.zip");
    SharedPayrollOverlay.CreateIncludingSettings(snapshot, combinedPayroll, overlayActive, [period], new SalaryTrustLedger(1, [], true), settingsCandidate);
    SharedPayrollCandidate.RestoreOriginal(settingsCandidate, settingsRestored);
    var settingsRead = BackupReader.Read(settingsRestored);
    Check(settingsRead.Accounts.Any(a => a.Entity == "جهة وهمية مضافة") &&
          settingsRead.SettingsPlists["futureSetting"].AsSpan().SequenceEqual(overlaySettings["futureSetting"]) &&
          BankReconciliation.ReadFromBackup(settingsRead).Single().Id == period.Id && SalaryTrust.ReadFromBackup(settingsRead).OpeningImported,
        "settings overlay preserves accounts, unknown setting, reconciliation and trust");
    var rejectedArchiveCandidate = Path.Combine(root, "rejected-archive-overlay.zip");
    var missingBook = new ArchiveBook(Guid.NewGuid(), "كتاب وهمي مفقود", salaryPreview.Month, 1, null);
    try { SharedPayrollOverlay.CreateIncludingSettings(snapshot, combinedPayroll, overlayActive with { Books = [missingBook] }, [period], null, rejectedArchiveCandidate); throw new InvalidOperationException("archive change silently ignored"); }
    catch (InvalidDataException) { }
    Check(!File.Exists(rejectedArchiveCandidate), "unsupported archive changes block the candidate");
    var newBook = new ArchiveBook(Guid.NewGuid(), "كتاب وهمي مضاف", salaryPreview.Month, 1, "جهة وهمية");
    var oldBook = snapshot.Books.Single();
    var replacedPdf = Path.Combine(root, "replaced-local-book.pdf"); var addedPdf = Path.Combine(root, "added-local-book.pdf");
    File.WriteAllText(replacedPdf, "%PDF-1.4\nreplaced fictional"); File.WriteAllText(addedPdf, "%PDF-1.4\nadded fictional");
    var archiveActive = overlayActive with
    {
        Books = [oldBook with { Title = "عنوان معدل وهمي" }, newBook],
        LocalBookFiles = new Dictionary<Guid, string> { [oldBook.Id] = replacedPdf, [newBook.Id] = addedPdf }
    };
    var archiveCandidate = Path.Combine(root, "archive-overlay-v7.zip"); var archiveRestored = Path.Combine(root, "archive-overlay-restored.zip");
    SharedPayrollOverlay.CreateIncludingSettings(snapshot, combinedPayroll, archiveActive, [period], new SalaryTrustLedger(1, [], true), archiveCandidate);
    SharedPayrollCandidate.RestoreOriginal(archiveCandidate, archiveRestored);
    var overlayArchiveRead = BackupReader.Read(archiveRestored);
    Check(overlayArchiveRead.Books.Count == 2 && overlayArchiveRead.Books.Single(b => b.Id == oldBook.Id).Title == "عنوان معدل وهمي" && overlayArchiveRead.Books.Any(b => b.Id == newBook.Id),
        "archive overlay preserves existing book identity and includes added book");
    var oldCopy = Path.Combine(root, "overlay-replaced-copy.pdf"); var newCopy = Path.Combine(root, "overlay-added-copy.pdf");
    BackupReader.CopyBook(overlayArchiveRead, overlayArchiveRead.Books.Single(b => b.Id == oldBook.Id), oldCopy);
    BackupReader.CopyBook(overlayArchiveRead, overlayArchiveRead.Books.Single(b => b.Id == newBook.Id), newCopy);
    Check(File.ReadAllBytes(oldCopy).AsSpan().SequenceEqual(File.ReadAllBytes(replacedPdf)) && File.ReadAllBytes(newCopy).AsSpan().SequenceEqual(File.ReadAllBytes(addedPdf)),
        "archive overlay retains exact added and replacement PDF bytes");
    var deletionCandidate = Path.Combine(root, "archive-deletion-v7.zip"); var deletionRestored = Path.Combine(root, "archive-deletion-restored.zip");
    SharedPayrollOverlay.CreateIncludingSettings(snapshot, combinedPayroll, overlayActive with { Books = [] }, [period], null, deletionCandidate);
    SharedPayrollCandidate.RestoreOriginal(deletionCandidate, deletionRestored);
    var deletionRead = BackupReader.Read(deletionRestored);
    using (var deletionZip = ZipFile.OpenRead(deletionRestored))
        Check(deletionRead.Books.Count == 0 && !deletionZip.Entries.Any(e => e.FullName.EndsWith(".pdf", StringComparison.Ordinal)),
            "explicitly removed book is absent from the candidate and restored archive");
    var failedBookCandidate = Path.Combine(root, "missing-book-file-v7.zip");
    var missingBookActive = overlayActive with { Books = [.. snapshot.Books, missingBook], LocalBookFiles = new Dictionary<Guid, string> { [missingBook.Id] = Path.Combine(root, "missing-book.pdf") } };
    try { SharedPayrollOverlay.CreateIncludingSettings(snapshot, combinedPayroll, missingBookActive, [period], null, failedBookCandidate); throw new InvalidOperationException("missing local book accepted"); }
    catch (IOException) { }
    Check(!File.Exists(failedBookCandidate) && !Directory.EnumerateFiles(root, ".salary-merge-*.tmp").Any(), "missing archive file prevents incomplete backup");
    var totals = BankReconciliation.Calculate(period);
    var font = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf") : "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
    var testPdf = ReconciliationPdf.Generate(period with { FirstMember = "عضو تجريبي أول", SecondMember = "عضو تجريبي ثان" }, font);
    Check(testPdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8) && testPdf.Length > 1000, "A4 reconciliation PDF draft created");
    if (Environment.GetEnvironmentVariable("SALARY_TEST_PDF") is { Length: > 0 } pdfPath) File.WriteAllBytes(pdfPath, testPdf);
    var longPeriod = period with { Movements = Enumerable.Range(0, 75).Select(i => new BankMovement(Guid.NewGuid(), period.Start, "حركة اختبارية ذات بيان طويل للتأكد من ظهور النص بوضوح في الكشف الورقي", 100, MovementDirection.Debit)).ToArray() };
    var longPdf = ReconciliationPdf.Generate(longPeriod, font);
    var wideAmountPeriod = period with { Movements = [funding with { Amount = 123456789012345, Description = string.Join(" ", Enumerable.Repeat("بيان طويل لاختبار التفاف النص", 5)) }] };
    using (var wideDocument = UglyToad.PdfPig.PdfDocument.Open(ReconciliationPdf.Generate(wideAmountPeriod, font)))
    {
        var text = string.Join(" ", wideDocument.GetPages().SelectMany(page => page.GetWords()).Select(word => word.Text));
        Check(text.Contains("123,456,789,012,345", StringComparison.Ordinal), "reconciliation table retains large amounts and wrapped descriptions");
    }
    using (var longDocument = UglyToad.PdfPig.PdfDocument.Open(longPdf))
    {
        var longPages = longDocument.GetPages().ToArray();
        Check(longPages.Length > 1 && longPages.All(p => Math.Abs(p.Width - 595) < 1 && Math.Abs(p.Height - 842) < 1),
            $"long reconciliation generates multiple A4 pages ({longPages.Length} pages, {string.Join(", ", longPages.Select(p => $"{p.Width:0}x{p.Height:0}"))})");
    }
    if (Environment.GetEnvironmentVariable("SALARY_TEST_LONG_PDF") is { Length: > 0 } longPath) File.WriteAllBytes(longPath, longPdf);
    var pdfOutput = Path.Combine(root, "statement.pdf"); File.WriteAllText(pdfOutput, "original");
    try { ReconciliationPdf.SaveDraft(period with { Movements = [] }, font, pdfOutput); throw new InvalidOperationException("empty statement accepted"); }
    catch (InvalidDataException) { }
    Check(File.ReadAllText(pdfOutput) == "original", "failed PDF generation preserves existing file");
    Check(totals.Credits == 1200 && totals.Funding == 1000 && totals.Debits == 1200 && totals.Balance == 0 && totals.OutstandingRefunds.Single().Remaining == 50, "reconciliation totals and outstanding refunds");
    Check(totals.Lines[^2].Movement.Id == repay.Id && totals.Lines[^1].Movement.Id == spending.Id, "same-day movements preserve insertion order");
    Check(!BankReconciliation.Replace(repay, repay with { Amount = 151 }).Matched && BankReconciliation.Replace(repay, repay).Matched, "changes clear reconciliation confirmation");
    try { _ = BankReconciliation.Calculate(period with { Movements = [funding, refund, repay with { Amount = 201 }] }); throw new InvalidOperationException("excess refund accepted"); }
    catch (InvalidDataException) { }
    try { _ = BankReconciliation.Calculate(period with { Movements = [funding, spending, spending with { Id = Guid.NewGuid(), SourceId = "different" }] }); throw new InvalidOperationException("duplicate source line accepted"); }
    catch (InvalidDataException) { }
    try { _ = BankReconciliation.Calculate(period with { Movements = [funding, spending with { Date = period.End.AddDays(1) }] }); throw new InvalidOperationException("out-of-period movement accepted"); }
    catch (InvalidDataException) { }
    static double IosDate(DateTimeOffset instant) => (instant - new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalSeconds;
    var ledgerJson = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, periods = new[] { new { id = period.Id, month = period.Month,
        start = IosDate(new DateTimeOffset(2026, 9, 26, 21, 30, 0, TimeSpan.Zero)), end = IosDate(new DateTimeOffset(2026, 10, 26, 21, 30, 0, TimeSpan.Zero)),
        openingBalance = 0, memberFirst = "", memberSecond = "", movements = new[] { new { id = funding.Id, date = IosDate(new DateTimeOffset(2026, 9, 26, 21, 30, 0, TimeSpan.Zero)), description = funding.Description, amount = funding.Amount, direction = "credit", isFunding = true, matched = false } } } } });
    using var ledgerPlist = new MemoryStream();
    PropertyListParser.SaveAsBinary(new NSData(ledgerJson), ledgerPlist);
    var ledgerSnapshot = snapshot with { SettingsPlists = new Dictionary<string, byte[]> { ["bankReconciliations"] = ledgerPlist.ToArray() } };
    var restoredPeriod = BankReconciliation.ReadFromBackup(ledgerSnapshot).Single();
    Check(restoredPeriod.Start == new DateOnly(2026, 9, 27) && restoredPeriod.End == new DateOnly(2026, 10, 27) && BankReconciliation.Calculate(restoredPeriod).Funding == 1000, "restore iOS ledger using Baghdad calendar dates");
    Check(MonthNames.Previous("تشرين الأول ٢٠٢٦") == 202609 && MonthNames.Previous("كانون الثاني 2026") == 202512 && MonthNames.Previous("غير معروف") is null && MonthNames.SortKey("أيلول 2026") == 202609, "previous calendar month selection and year boundary");
    Check(BankReconciliation.ReadFromBackup(emptySettings).Count == 0, "absent iOS reconciliation ledger");
    var ledgers = new LocalReconciliationStore(Path.Combine(root, "ledgers"));
    var initialLedger = ledgers.Load(ledgerSnapshot);
    Check(initialLedger.Revision is null && initialLedger.Periods.Single().Id == period.Id, "local ledger starts from iPhone backup");
    var savedLedger = ledgers.Save(ledgerSnapshot, initialLedger, [period]);
    Check(savedLedger.Revision is not null && ledgers.Load(ledgerSnapshot).Periods.Single().Movements.Count == 4, "local ledger is restored with movements");
    try { _ = ledgers.Save(ledgerSnapshot, initialLedger, [period with { OpeningBalance = 1 }]); throw new InvalidOperationException("stale ledger accepted"); }
    catch (InvalidDataException) { }
    Check(ledgers.Load(ledgerSnapshot).Periods.Single().OpeningBalance == 0, "stale writer preserves local ledger");
    try { _ = ledgers.Save(ledgerSnapshot, savedLedger, [period with { Movements = [funding, refund, repay with { Amount = 201 }] }]); throw new InvalidOperationException("invalid ledger saved"); }
    catch (InvalidDataException) { }
    Check(ledgers.Load(ledgerSnapshot).Revision == savedLedger.Revision, "invalid ledger preserves previous state");
    var exportedLedgerBackup = Path.Combine(root, "reconciliation-backup.zip");
    // تاريخ النسخة: كل نسخة شاملة من ويندوز تأخذ وقت إنشائها (ثواني ٢٠٠١ UTC)، وتبقى حقول الفهرس الأخرى ومصدرها كما هي.
    var exportMoment = new DateTimeOffset(2026, 11, 2, 23, 30, 15, TimeSpan.FromHours(3));   // بعد منتصف ليل UTC بنصف ساعة بتوقيت بغداد
    ReconciliationBackupExport.Write(ledgerSnapshot, savedLedger.Periods, exportedLedgerBackup, createdAt: exportMoment);
    var exportedLedger = BackupReader.Read(exportedLedgerBackup);
    System.Text.Json.Nodes.JsonObject ManifestOf(string path) { using var zip = ZipFile.OpenRead(path); using var stream = zip.GetEntry("manifest.json")!.Open(); return System.Text.Json.Nodes.JsonNode.Parse(stream)!.AsObject(); }
    var sourceManifest = ManifestOf(ledgerSnapshot.SourcePath); var exportedManifest = ManifestOf(exportedLedgerBackup);
    Check(ledgerSnapshot.Created == new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(700000000.25) && sourceManifest["createdAt"]!.GetValue<double>() == 700000000.25, "backup creation date read from the iPhone manifest");
    Check(Math.Abs(exportedManifest["createdAt"]!.GetValue<double>() - ReconciliationBackupExport.IosSeconds(exportMoment)) < 0.001 && exportedLedger.Created == exportMoment
        && exportedLedger.Created!.Value.UtcDateTime.Day == 2 && exportedLedger.Created.Value.ToOffset(TimeSpan.FromHours(3)).Hour == 23, "Windows backup records its own creation time in UTC seconds since 2001");
    Check(System.Text.Json.Nodes.JsonNode.DeepEquals(sourceManifest["futureField"], exportedManifest["futureField"]) && exportedManifest["futureField"]!["big"]!.ToJsonString() == "12345678901234567"
        && exportedManifest["version"]!.GetValue<int>() == sourceManifest["version"]!.GetValue<int>() && exportedManifest.Count == sourceManifest.Count, "backup manifest keeps unknown fields while updating the date");
    Check(BackupReader.CreatedAt(exportedLedgerBackup) == exportMoment, "backup date readable from the manifest alone");
    var unchangedBackup = Path.Combine(root, "unchanged-backup.zip"); var beforeUnchanged = DateTimeOffset.UtcNow.AddSeconds(-1);
    ReconciliationBackupExport.Write(snapshot, [], unchangedBackup);
    var unchangedCreated = BackupReader.Read(unchangedBackup).Created;
    Check(unchangedCreated is { } fresh && fresh >= beforeUnchanged && fresh <= DateTimeOffset.UtcNow.AddSeconds(1) && ManifestOf(unchangedBackup)["months"]!.ToJsonString() == ManifestOf(snapshot.SourcePath)["months"]!.ToJsonString(), "backup without data changes still gets a new creation date");
    var roundTripPeriod = BankReconciliation.ReadFromBackup(exportedLedger).Single();
    Check(roundTripPeriod.Id == period.Id && roundTripPeriod.Movements.Count == 4 && roundTripPeriod.Movements[2].RefundOf == refund.Id && roundTripPeriod.End == period.End && BankReconciliation.Calculate(roundTripPeriod).Balance == 0, "reconciliation backup preserves dates, links and totals");
    Check(exportedLedger.Employees.Count == snapshot.Employees.Count && exportedLedger.Books.Count == snapshot.Books.Count && before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(valid))), "reconciliation backup preserves source data and original file");
    try { ReconciliationBackupExport.Write(ledgerSnapshot, savedLedger.Periods, ledgerSnapshot.SourcePath); throw new InvalidOperationException("backup source overwritten"); }
    catch (InvalidDataException) { }
    var previousExport = SHA256.HashData(File.ReadAllBytes(exportedLedgerBackup));
    try { ReconciliationBackupExport.Write(ledgerSnapshot, [period with { Movements = [funding, refund, repay with { Amount = 201 }] }], exportedLedgerBackup); throw new InvalidOperationException("invalid exported ledger accepted"); }
    catch (InvalidDataException) { }
    Check(previousExport.SequenceEqual(SHA256.HashData(File.ReadAllBytes(exportedLedgerBackup))), "failed reconciliation export preserves existing file");
    // حقول الآيفون التي لا يستخدمها ويندوز (ومنها بيانات اللجنة) تبقى كما هي، والاعتماد يُحفظ ويقفل الفترة.
    var iphoneFields = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, periods = new[] { new { id = period.Id, month = period.Month,
        start = IosDate(new DateTimeOffset(2026, 9, 26, 21, 30, 0, TimeSpan.Zero)), end = IosDate(new DateTimeOffset(2026, 10, 26, 21, 30, 0, TimeSpan.Zero)),
        openingBalance = 0, memberFirst = "عضو وهمي أول", memberSecond = "عضو وهمي ثان", firstRank = "رتبة وهمية", firstPosition = "منصب وهمي", firstDate = " / 10 / 2026",
        secondRank = "رتبة ثانية وهمية", secondPosition = "منصب ثان وهمي", signatureDefaultsInitialized = true, futureField = "قيمة مستقبلية",
        movements = new[] { new { id = funding.Id, date = IosDate(new DateTimeOffset(2026, 9, 26, 21, 30, 0, TimeSpan.Zero)), description = funding.Description, amount = funding.Amount, direction = "credit", isFunding = true, matched = true, futureMovement = 7 } } } } });
    using var iphonePlist = new MemoryStream(); PropertyListParser.SaveAsBinary(new NSData(iphoneFields), iphonePlist);
    var iphoneLedgerSnapshot = snapshot with { SettingsPlists = new Dictionary<string, byte[]> { ["bankReconciliations"] = iphonePlist.ToArray() } };
    var iphonePeriod = BankReconciliation.ReadFromBackup(iphoneLedgerSnapshot).Single();
    Check(iphonePeriod.Committee is { FirstRank: "رتبة وهمية", SecondPosition: "منصب ثان وهمي", DefaultsInitialized: true } && iphonePeriod.Extra!.Contains("futureField", StringComparison.Ordinal) &&
          iphonePeriod.Movements[0].Extra!.Contains("futureMovement", StringComparison.Ordinal) && !iphonePeriod.IsConfirmed, "iPhone committee and unknown ledger fields are read");
    var signatureLines = BankReconciliation.SignatureLines(iphonePeriod, true);
    Check(signatureLines[0] == "رتبة وهمية" && signatureLines[1] == "عضو وهمي أول" && signatureLines[3] == " / ١٠ / ٢٠٢٦" && BankReconciliation.SignatureLines(iphonePeriod, false)[3] == " / ١٠ / ٢٠٢٦", "committee signature lines follow the iPhone format");
    var confirmedPeriod = BankReconciliation.Confirm(iphonePeriod, new DateTime(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc));
    Check(confirmedPeriod.IsConfirmed && BankReconciliation.ConfirmedTime(confirmedPeriod)?.Hour == 12, "confirmation time is stored and shown in Baghdad time");
    var fidelityBackup = Path.Combine(root, "reconciliation-fidelity.zip");
    ReconciliationBackupExport.Write(iphoneLedgerSnapshot, [confirmedPeriod], fidelityBackup);
    var fidelityRead = BackupReader.Read(fidelityBackup);
    var fidelityPeriod = BankReconciliation.ReadFromBackup(fidelityRead).Single();
    using (var fidelityJson = JsonDocument.Parse(((NSData)PropertyListParser.Parse(fidelityRead.SettingsPlists["bankReconciliations"])).Bytes))
    {
        var stored = fidelityJson.RootElement.GetProperty("periods")[0];
        Check(stored.GetProperty("firstRank").GetString() == "رتبة وهمية" && stored.GetProperty("futureField").GetString() == "قيمة مستقبلية" && stored.GetProperty("confirmedAt").GetDouble() > 0 &&
              stored.GetProperty("movements")[0].GetProperty("futureMovement").GetInt32() == 7 && !stored.TryGetProperty("secondDate", out _), "backup keeps iPhone fields and adds confirmedAt");
    }
    Check(BankReconciliation.Same(fidelityPeriod, confirmedPeriod), "confirmed period survives the shared backup unchanged");
    var lockStore = new LocalReconciliationStore(Path.Combine(root, "ledger-lock"));
    var lockStart = lockStore.Load(iphoneLedgerSnapshot);
    var lockConfirmed = lockStore.Save(iphoneLedgerSnapshot, lockStart, [confirmedPeriod]);
    try { _ = lockStore.Save(iphoneLedgerSnapshot, lockConfirmed, [confirmedPeriod with { OpeningBalance = 5 }]); throw new InvalidOperationException("confirmed period edited"); }
    catch (InvalidDataException) { }
    try { _ = lockStore.Save(iphoneLedgerSnapshot, lockConfirmed, []); throw new InvalidOperationException("confirmed period deleted"); }
    catch (InvalidDataException) { }
    try { _ = lockStore.Save(iphoneLedgerSnapshot, lockConfirmed, [BankReconciliation.Unconfirm(confirmedPeriod) with { OpeningBalance = 7 }]); throw new InvalidOperationException("unconfirm with edits accepted"); }
    catch (InvalidDataException) { }
    using (var bigNumbers = JsonDocument.Parse("{\"futureNumber\":9007199254740993,\"futureFraction\":123456789.123456789}"))
    {
        var precise = confirmedPeriod with { Extra = System.Text.Json.Nodes.JsonNode.Parse(bigNumbers.RootElement.GetRawText())!.ToJsonString() };
        var preciseRead = BankReconciliation.ReadFromBackup(iphoneLedgerSnapshot with { SettingsPlists = new Dictionary<string, byte[]> { ["bankReconciliations"] = BankReconciliation.LedgerPlist([precise]) } }).Single();
        Check(preciseRead.Extra!.Contains("9007199254740993", StringComparison.Ordinal) && preciseRead.Extra.Contains("123456789.123456789", StringComparison.Ordinal), "unknown ledger numbers keep their exact digits");
    }
    var lockReopened = lockStore.Save(iphoneLedgerSnapshot, lockConfirmed, [BankReconciliation.Unconfirm(confirmedPeriod)]);
    var lockEdited = lockStore.Save(iphoneLedgerSnapshot, lockReopened, [BankReconciliation.Unconfirm(confirmedPeriod) with { OpeningBalance = 5 }]);
    Check(lockEdited.Periods.Single().OpeningBalance == 5 && !lockEdited.Periods.Single().IsConfirmed, "unconfirming a period allows editing again");
    // المراجعة: الفترة الكاملة (رصيد صفر، تمويل في أول يوم، كل الحركات مؤشرة) بلا تنبيهات مانعة.
    var completePeriod = period with { Movements = [funding with { Matched = true }, spending with { Amount = 1000, Matched = true, SourceId = null, SourceLineIds = null }] };
    var completeReview = ReconciliationReview.Prepare(completePeriod, null, null, new BankAccountDetails("حساب وهمي", "فرع وهمي", "123", "IQ00TEST"));
    var openReview = ReconciliationReview.Prepare(period, null, null, null);
    Check(completeReview.Complete && completeReview.Unmatched == 0 && !openReview.Complete && openReview.Unmatched > 0 &&
          ReconciliationReview.Prepare(completePeriod, null, null, new BankAccountDetails("", "", "", "")).Complete, "reconciliation review alerts and completeness");
    // كشف المطابقة بالشكل المعتمد: بيانات الحساب والجدول والمجموع والتوقيع.
    var statementFont = File.ReadAllBytes(font);
    var reconStatement = ReconciliationPdf.Statement(confirmedPeriod with { OpeningBalance = 0 }, new BankAccountDetails("حساب رواتب وهمي", "فرع وهمي", "1234567", "IQ00TEST0000000000001"), true, new ReportAssets(statementFont, statementFont, null));
    using (var reconPdf = UglyToad.PdfPig.PdfDocument.Open(reconStatement))
    {
        var words = string.Join(" ", reconPdf.GetPages().SelectMany(pg => pg.GetWords()).Select(w => w.Text));
        Check(reconPdf.NumberOfPages == 1 && words.Contains("IQD", StringComparison.Ordinal) && words.Contains("IQ00TEST0000000000001", StringComparison.Ordinal) && words.Contains("1,000", StringComparison.Ordinal) && !words.Contains("1234567", StringComparison.Ordinal), "approved reconciliation statement contains account, currency and amounts without the local account");
    }
    if (Environment.GetEnvironmentVariable("SALARY_TEST_STATEMENT_PDF") is { Length: > 0 } statementPath) { Directory.CreateDirectory(Path.GetDirectoryName(statementPath)!); File.WriteAllBytes(statementPath, reconStatement); }
    Check(BankStatementCalculator.Manual(emptySettings, "أيلول 2026").Total == 0m, "absent manual amounts");
    var brokenSettings = snapshot with { SettingsPlists = new Dictionary<string, byte[]> { ["bankStatementManualAmounts"] = [1, 2, 3] } };
    try { _ = BankStatementCalculator.Manual(brokenSettings, "أيلول 2026"); throw new InvalidOperationException("invalid manual settings accepted"); }
    catch (InvalidDataException) { }
    var contractPerson = new Employee("أيلول 2026", "موظف افتراضي", "عقود", "قسم تجريبي", 500m,
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, decimal> { ["الرعاية الصحية"] = 20m, ["استقطاع الجوال"] = 100m }),
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["طريقة الدفع"] = "كاش", ["الاسمي"] = "١٬٠٠١", ["توقيفات تقاعدية"] = "75", ["مجموع استقطاعات المصارف"] = "2500" }), null);
    var contractSnapshot = snapshot with { Employees = new[] { contractPerson } };
    var contractBank = BankStatementCalculator.Calculate(contractSnapshot, "أيلول 2026");
    Check(contractBank.Cash == 500m && contractBank.ContractNominalShare == 121m && contractBank.ContractContribution == 196m &&
          contractBank.Breakdown["الرعاية الصحية"] == 20m && contractBank.Breakdown["شركة عراق الجوال"] == 100m, "contract rounding and deduction sources");
    Check((contractBank with { ContractNominal = 1000m }).ContractNominalShare == 120m &&
          (contractBank with { ContractNominal = 1001m }).ContractNominalShare == 121m &&
          (contractBank with { ContractNominal = 1004m }).ContractNominalShare == 121m &&
          (contractBank with { ContractNominal = 1005m }).ContractNominalShare == 121m &&
          (contractBank with { ContractNominal = 1_000_000_000_001m }).ContractNominalShare == 120_000_000_001m,
          "contract nominal share rounds upward once after the total, while whole amounts stay unchanged");
    var manualContract = new ContractOverride(205m, contractBank.ContractNominal, contractBank.ContractRetirement, contractBank.ContractCount, contractBank.MissingContractNominal);
    var contractPlist = ContractOverrides.Set(null, "أيلول 2026", manualContract);
    var contractWithManual = contractSnapshot with { SettingsPlists = new Dictionary<string, byte[]>(contractSnapshot.SettingsPlists) { [ContractOverrides.SettingsKey] = contractPlist } };
    Check(ContractOverrides.Read(contractWithManual, "أيلول 2026") == manualContract && manualContract.Matches(contractBank) && !manualContract.Matches(contractBank with { ContractNominal = 1002m }), "manual contract final roundtrip and changed basis detection");
    var calculatedManual = BankStatementCalculator.Calculate(contractWithManual, "أيلول 2026");
    Check(calculatedManual.ContractContribution == 205m && calculatedManual.BankTotal == contractBank.BankTotal + 9m, "manual final contract amount enters the bank total exactly once");
    var staleManual = calculatedManual with { ContractNominal = 1002m };
    Check(staleManual.ContractOverrideStale && staleManual.ContractContribution == staleManual.CalculatedContractContribution, "changed payroll basis marks manual amount stale");
    var contractBackup = Path.Combine(root, "contract-override-v6.zip");
    var backupWithContract = snapshot with { SettingsPlists = new Dictionary<string, byte[]>(snapshot.SettingsPlists) { [ContractOverrides.SettingsKey] = contractPlist } };
    ReconciliationBackupExport.Write(backupWithContract, [], contractBackup);
    var restoredContract = BackupReader.Read(contractBackup);
    Check(restoredContract.Version == 6 && ContractOverrides.Read(restoredContract, "أيلول 2026") == manualContract, "manual amount roundtrips in a protected v6 backup");
    var futureContractJson = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["أيلول 2026"] = new { total = 205, nominal = 1001, retirement = 75, count = 1, missing = 0, future = "محفوظ" } });
    using var futureContractStream = new MemoryStream(); PropertyListParser.SaveAsBinary(new NSData(futureContractJson), futureContractStream);
    var futureContractEdited = ContractOverrides.Set(futureContractStream.ToArray(), "أيلول 2026", manualContract with { Total = 210 });
    var futureContractData = (NSData)PropertyListParser.Parse(futureContractEdited);
    Check(JsonDocument.Parse(futureContractData.Bytes).RootElement.GetProperty("أيلول 2026").GetProperty("future").GetString() == "محفوظ", "contract edit preserves future fields");
    var removedContract = ContractOverrides.Set(contractPlist, "أيلول 2026", null);
    Check(ContractOverrides.Read(contractWithManual with { SettingsPlists = new Dictionary<string, byte[]> { [ContractOverrides.SettingsKey] = removedContract } }, "أيلول 2026") is null, "manual contract override can be removed");
    try { _ = ContractOverrides.Set(null, "أيلول 2026", manualContract with { Total = 1.5m }); throw new InvalidOperationException("fractional override accepted"); }
    catch (InvalidDataException) { }
    var statusColumns = JsonSerializer.SerializeToUtf8Bytes(new { headers = new[] { "صافي الراتب", "الملاحظات4" }, values = new[] { "500", "حالة تجريبية" } });
    var statusCash = CashSalaryListPreparation.Prepare(contractSnapshot with { Employees = [contractPerson with { RawColumns = statusColumns }] }, "أيلول 2026");
    Check(statusCash.Rows.Single().CaseStatus == "حالة تجريبية", "cash status comes from notes immediately after net salary");
    var cashList = CashSalaryListPreparation.Prepare(contractSnapshot with { Employees = [contractPerson, contractPerson with { Name = "صفر تجريبي", Net = 0m }] }, "أيلول 2026");
    Check(cashList.Total == 500 && cashList.Rows.Count == 1 && cashList.Rows[0].Name == contractPerson.Name, "cash list excludes zero and matches net total");
    try { _ = CashSalaryListPreparation.Prepare(contractSnapshot with { Employees = [contractPerson with { Workplace = "" }] }, "أيلول 2026"); throw new InvalidOperationException("incomplete cash person accepted"); }
    catch (InvalidDataException) { }
    string[] rawHeaders = ["مجموع استقطاعات المصارف", "صندوق الشهداء", "الرعاية الصحية", "النشاط الرياضي", "الضريبة", "الحماية الاجتماعية", "شركة عراق الجوال", "الاسمي", "توقيفات تقاعدية"];
    var rawColumns = JsonSerializer.SerializeToUtf8Bytes(new { headers = rawHeaders, values = rawHeaders.Select(_ => "").ToArray() });
    var contractRawColumns = JsonSerializer.SerializeToUtf8Bytes(new { headers = rawHeaders, values = rawHeaders.Select(_ => "").ToArray() });
    var completeContractSource = snapshot with { Employees = [contractPerson with { RawColumns = contractRawColumns }] };
    var contractStore = new LocalTransferLinks(Path.Combine(root, "contract-adjustment"));
    var contractInitial = contractStore.Load(completeContractSource);
    var contractBasis = BankStatementCalculator.Calculate(contractInitial.Snapshot, "أيلول 2026");
    var contractSaved = contractStore.SaveContractOverride(completeContractSource, contractInitial, "أيلول 2026", contractBasis, 205m);
    Check(BankStatementCalculator.Calculate(contractSaved.Snapshot, "أيلول 2026").ContractContribution == 205m && contractStore.Load(completeContractSource).Revision == contractSaved.Revision, "contract adjustment saves and reloads through the atomic local store");
    var changedContractSource = completeContractSource with { Employees = [contractPerson with { RawColumns = contractRawColumns, Details = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["طريقة الدفع"] = "كاش", ["الاسمي"] = "1002", ["توقيفات تقاعدية"] = "75", ["مجموع استقطاعات المصارف"] = "2500" }) }] };
    Check(BankStatementCalculator.Calculate(contractStore.Load(changedContractSource).Snapshot, "أيلول 2026").ContractOverrideStale, "reimported payroll makes the saved override stale");
    try { _ = contractStore.SaveContractOverride(changedContractSource, contractSaved, "أيلول 2026", contractBasis, 210m); throw new InvalidOperationException("stale basis saved"); }
    catch (InvalidDataException) { }
    Check(contractStore.Load(completeContractSource).Revision == contractSaved.Revision, "stale contract save changes no data");
    var contractReset = contractStore.SaveContractOverride(completeContractSource, contractSaved, "أيلول 2026", BankStatementCalculator.Calculate(contractSaved.Snapshot, "أيلول 2026"), null);
    Check(BankStatementCalculator.Calculate(contractReset.Snapshot, "أيلول 2026").ContractContribution == contractBasis.CalculatedContractContribution, "reset returns to automatic contract calculation");
    var incompleteContract = contractPerson with {
        Details = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["طريقة الدفع"] = "كاش", ["توقيفات تقاعدية"] = "75", ["مجموع استقطاعات المصارف"] = "2500" }), RawColumns = rawColumns };
    var incompleteBank = BankStatementCalculator.Calculate(snapshot with { Employees = new[] { incompleteContract } }, "أيلول 2026");
    Check(incompleteBank.MissingContractNominal == 1 && incompleteBank.Warnings.Count > 0 && incompleteBank.TransferWarnings.Count == 0, "missing contract nominal remains explicit without blocking group source fields");
    var invalidRaw = JsonSerializer.SerializeToUtf8Bytes(new { headers = rawHeaders, values = new[] { "", "", "", "", "", "", "غير رقم", "", "" } });
    var invalidBank = BankStatementCalculator.Calculate(snapshot with { Employees = new[] { incompleteContract with { RawColumns = invalidRaw } } }, "أيلول 2026");
    Check(invalidBank.TransferWarnings.Any(w => w.Contains("غير صالحة", StringComparison.Ordinal)), "invalid original payroll source blocks transfer");
    const string fakePayer = "IQ06TEST123000000000001", fakeReceiver = "IQ76TEST123000000000002";
    var referencesArchive = MakeReferenceArchive(font, fakePayer, fakeReceiver);
    var references = LetterReferencePack.ReadArchive(referencesArchive);
    Check(references.Count == 11 && LetterReferencePack.IndividualAccount(references, "food_company", fakePayer) == fakeReceiver, "reference pack validates PDF and extracts the checked beneficiary");
    Check(LetterReferencePack.IndividualAccount(references, "rafidain_group", fakePayer) is null, "group payer matches reference without guessing beneficiary");
    try { _ = LetterReferencePack.IndividualAccount(references, "food_company", fakeReceiver); throw new InvalidOperationException("mismatched payer accepted"); }
    catch (InvalidDataException) { }
    try { _ = LetterReferencePack.ReadArchive(MakeReferenceArchive(font, fakePayer, fakeReceiver, damaged: true)); throw new InvalidOperationException("damaged reference accepted"); }
    catch (InvalidDataException) { }
    var foodPlan = IndividualLetterPreparation.Payroll("food_company", incompleteBank with { Breakdown = new Dictionary<string, decimal>(incompleteBank.Breakdown) { ["شركة عراق الجوال"] = 100000 } }, "دافع وهمي", fakePayer, references);
    Check(foodPlan.Total == 95254 && foodPlan.BeneficiaryAccount == fakeReceiver, "food company amount and checked beneficiary");
    try { _ = IndividualLetterPreparation.Payroll("contracts", incompleteBank, "دافع وهمي", fakePayer, references); throw new InvalidOperationException("missing nominal accepted without review"); }
    catch (InvalidDataException) { }
    var acceptedContract = IndividualLetterPreparation.Payroll("contracts", incompleteBank, "دافع وهمي", fakePayer, references, true);
    Check(acceptedContract.Total == 75 && acceptedContract.Notices.Count == 1, "explicit contract warning review remains recorded");
    var manualLetter = IndividualLetterPreparation.Payroll("contracts", calculatedManual with { TransferWarnings = [] }, "دافع وهمي", fakePayer, references);
    Check(manualLetter.Total == 205 && manualLetter.Lines.Sum(x => x.Amount) == 205 && manualLetter.Lines.Single(x => x.Title == "التعديل اليدوي").Amount == 9, "contract letter shows the same manual final and explicit adjustment");
    var lowerOverride = manualContract with { Total = 180m };
    var lowerLetter = IndividualLetterPreparation.Payroll("contracts", calculatedManual with { TransferWarnings = [], ManualContractOverride = lowerOverride }, "دافع وهمي", fakePayer, references);
    Check(lowerLetter.Total == 180 && lowerLetter.Lines.Single(x => x.Title == "التعديل اليدوي").Amount == -16, "negative contract adjustment remains explicit");
    try { _ = IndividualLetterPreparation.Payroll("contracts", staleManual with { TransferWarnings = [] }, "دافع وهمي", fakePayer, references); throw new InvalidOperationException("stale contract adjustment used in letter"); }
    catch (InvalidDataException) { }
    var anbarAccount = new ExecutionAccount("جهة وهمية", fakeReceiver, "الرافدين فرع الانبار", "", null);
    var entity = new TransferEntry("deduction|جهة وهمية", "أيلول 2026", "جهة وهمية", 2500m, 1, 0, anbarAccount, TransferGroup.Anbar, null, false);
    var entityTransfer = new TransferSnapshot("أيلول 2026", "دافع وهمي", fakePayer, [entity], []);
    var personRow = new Deduction("أيلول 2026", "اسم تجريبي", "جهة وهمية", 2500m, "", "رتبة تجريبية");
    var entitySource = snapshot with { Deductions = [personRow] };
    foreach (var id in new[] { "ramadi", "housing", "water" })
    {
        var plan = IndividualEntityPreparation.Prepare(entitySource, entityTransfer, id, entity.Id, references);
        Check(plan.Total == 2500 && plan.People == 1 && plan.Rows.Single().Name == personRow.Name, "individual entity matches source for " + id);
    }
    try { _ = IndividualEntityPreparation.Prepare(entitySource with { Deductions = [personRow with { Amount = 2499m }] }, entityTransfer, "ramadi", entity.Id, references); throw new InvalidOperationException("mismatched entity rows accepted"); }
    catch (InvalidDataException) { }
    try { _ = IndividualEntityPreparation.Prepare(entitySource with { Deductions = [personRow with { Rank = "" }] }, entityTransfer, "water", entity.Id, references); throw new InvalidOperationException("missing water rank accepted"); }
    catch (InvalidDataException) { }
    var directTitle = "مصرف الرافدين/فرع الانبار";
    var directEntity = entity with { Id = "deduction|" + directTitle, Title = directTitle, Account = null };
    var direct = IndividualEntityPreparation.Prepare(entitySource with { Deductions = [personRow with { Title = directTitle }] }, entityTransfer with { Entries = [directEntity] }, "rafidain_anbar", directEntity.Id, references);
    Check(direct.BeneficiaryAccount is null && direct.Total == 2500, "direct Anbar bank book uses the matched deduction");
    var nameLists = NameLists.Prepare(entitySource, entityTransfer);
    Check(nameLists.Single(l => l.Id == entity.Id).Rows.Single().Amount == 2500 && nameLists.Single(l => l.Id == entity.Id).Rows.Single().Number == 1, "entity name list matches deductions and includes sequence");
    var generatedNames = NameListPdf.Generate(nameLists.Single(l => l.Id == entity.Id), File.ReadAllBytes(font));
    Check(generatedNames.AsSpan(0, 5).SequenceEqual("%PDF-"u8), "independent entity names PDF verified");
    if (Environment.GetEnvironmentVariable("SALARY_TEST_NAMES_PDF") is { Length: > 0 } namesPath) File.WriteAllBytes(namesPath, generatedNames);
    var manyNames = nameLists.Single(l => l.Id == entity.Id) with { Rows = Enumerable.Range(1, 100).Select(n => new NameListRow(n, "اسم تجريبي", "عقود", "قسم افتراضي", 2500)).ToArray(), Total = 250000 };
    using (var pagesDocument = UglyToad.PdfPig.PdfDocument.Open(NameListPdf.Generate(manyNames, File.ReadAllBytes(font)))) Check(pagesDocument.NumberOfPages > 1, "long name list continues on separate pages");
    try { _ = NameListPdf.Generate(manyNames with { Total = 1 }, File.ReadAllBytes(font)); throw new InvalidOperationException("invalid list total accepted"); }
    catch (InvalidDataException) { }
    var badLists = NameLists.Prepare(entitySource with { Deductions = [personRow with { Amount = 2499m }] }, entityTransfer);
    Check(badLists.Single(l => l.Id == entity.Id).Problem is not null && badLists.Single(l => l.Id == entity.Id).Rows.Count == 0, "mismatched name list is not displayed as valid");
    using var individualSettingsPlist = new MemoryStream();
    PropertyListParser.SaveAsBinary(new NSData(JsonSerializer.SerializeToUtf8Bytes(new { common = new { header = "ترويسة عامة", fixedNumber = "ح/1", committeeFirst = "عضو عام", foodContractReference = "عقد تجريبي" }, individual = new Dictionary<string, object> { ["food_company"] = new { committeeFirst = "عضو خاص" } } })), individualSettingsPlist);
    var individualSettingsSnapshot = emptySettings with { SettingsPlists = new Dictionary<string, byte[]> { ["transferLetterCustomizations"] = individualSettingsPlist.ToArray() } };
    Check(GroupLetterSettingsReader.Read(individualSettingsSnapshot, "food_company").First == "عضو خاص" && GroupLetterSettingsReader.Read(individualSettingsSnapshot, "food_tax").First == "عضو عام" && GroupLetterSettingsReader.FoodContract(individualSettingsSnapshot) == "عقد تجريبي", "individual letter settings inherit and preserve contract reference");
    const string fakeAlternate = "IQ49TEST123000000000003";
    Check(TransferPreparation.ValidIban(fakeAlternate), "synthetic alternate IBAN valid");
    byte[] CustomAccount(string id, string origin, string chosen, string prior, string reference)
    {
        using var buffer = new MemoryStream();
        PropertyListParser.SaveAsBinary(new NSData(JsonSerializer.SerializeToUtf8Bytes(new { common = new { }, individual = new Dictionary<string, object> { [id] = new { beneficiaryAccount = chosen, beneficiarySource = origin, beneficiaryOriginal = prior, beneficiaryReference = reference } } })), buffer); return buffer.ToArray();
    }
    var payrollCustom = emptySettings with { SettingsPlists = new Dictionary<string, byte[]> { ["transferLetterCustomizations"] = CustomAccount("food_company", "food_company", fakeAlternate, fakeReceiver, fakeReceiver) } };
    var customFood = IndividualLetterPreparation.Payroll("food_company", incompleteBank with { Breakdown = new Dictionary<string, decimal>(incompleteBank.Breakdown) { ["شركة عراق الجوال"] = 100000 } }, "دافع وهمي", fakePayer, references, customization: payrollCustom);
    Check(customFood.BeneficiaryAccount == fakeAlternate, "pinned payroll beneficiary override is honored");
    var entityCustom = entitySource with { SettingsPlists = new Dictionary<string, byte[]> { ["transferLetterCustomizations"] = CustomAccount("ramadi", entity.Title, fakeAlternate, fakeReceiver, fakeReceiver) } };
    try { _ = IndividualEntityPreparation.Prepare(entityCustom, entityTransfer, "ramadi", entity.Id, references); throw new InvalidOperationException("book account changed before account list"); }
    catch (InvalidDataException) { }
    var updatedEntity = entity with { Account = anbarAccount with { Iban = fakeAlternate } };
    var updatedPlan = IndividualEntityPreparation.Prepare(entityCustom, entityTransfer with { Entries = [updatedEntity] }, "ramadi", updatedEntity.Id, references);
    Check(updatedPlan.BeneficiaryAccount == fakeAlternate, "updated entity account and pinned override agree");
    var staleReference = entitySource with { SettingsPlists = new Dictionary<string, byte[]> { ["transferLetterCustomizations"] = CustomAccount("ramadi", entity.Title, fakeAlternate, fakeReceiver, fakePayer) } };
    try { _ = IndividualEntityPreparation.Prepare(staleReference, entityTransfer with { Entries = [updatedEntity] }, "ramadi", updatedEntity.Id, references); throw new InvalidOperationException("stale account reference accepted"); }
    catch (InvalidDataException) { }
    var individualSettings = new GroupLetterSettings("ترويسة تجريبية\nقسم حسابات وهمي", "ح/1/22/", "عضو تجريبي أول", "عضو تجريبي ثان", "عضو تجريبي ثالث", "نسخة تجريبية", null, 10, 0, null);
    var individualFont = File.ReadAllBytes(font); var individualLogo = File.ReadAllBytes(Path.GetFullPath("SalaryApp/Assets.xcassets/IraqEmblem.imageset/IraqEmblem.png"));
    IndividualLetterPlan SampleIndividual(string id, params IndividualLetterLine[] values) { var sum = values.Sum(v => v.Amount); return new(id, "أيلول 2026", "دافع وهمي", fakePayer, fakeReceiver, sum, ArabicAmountWords.Dinars(sum), values, null, null, []); }
    var singlePlans = new[] {
        foodPlan, SampleIndividual("food_tax", new IndividualLetterLine("ضريبة عقد الإطعام", 1746)),
        SampleIndividual("tax_social", new("الحماية الاجتماعية", 300), new("ضريبة الدخل", 200)),
        acceptedContract, SampleIndividual("cash_salary", new IndividualLetterLine("رواتب الكاش", 500)) };
    foreach (var individual in singlePlans)
    {
        var pdf = IndividualPayrollPdf.Generate(individual, individualSettings, individualFont, individualFont, individualLogo, "٣ / ٢٠٢٢", individual.Id == "cash_salary" ? cashList : null);
        Check(pdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8), "individual payroll PDF checked: " + individual.Id);
        if (Environment.GetEnvironmentVariable("SALARY_TEST_INDIVIDUAL_DIR") is { Length: > 0 } dir) { Directory.CreateDirectory(dir); File.WriteAllBytes(Path.Combine(dir, individual.Id + ".pdf"), pdf); }
        if (Environment.GetEnvironmentVariable("SALARY_TEST_MAJALLA") is { Length: > 0 } sakkalPath) {
            var sakkalPdf = IndividualPayrollPdf.Generate(individual, individualSettings, File.ReadAllBytes(sakkalPath), individualFont, individualLogo, "٣ / ٢٠٢٢", individual.Id == "cash_salary" ? cashList : null);
            if (Environment.GetEnvironmentVariable("SALARY_TEST_INDIVIDUAL_DIR") is { Length: > 0 } sakkalDir) File.WriteAllBytes(Path.Combine(sakkalDir, individual.Id + "-sakkal.pdf"), sakkalPdf);
        }
    }
    try { _ = IndividualPayrollPdf.Generate(foodPlan, individualSettings, individualFont, individualFont, individualLogo, null); throw new InvalidOperationException("food contract reference omitted"); }
    catch (InvalidDataException) { }
    try { _ = IndividualPayrollPdf.Generate(singlePlans.Last(), individualSettings, individualFont, individualFont, individualLogo, null, cashList with { Total = 501 }); throw new InvalidOperationException("incorrect cash list accepted"); }
    catch (InvalidDataException) { }
    foreach (var entityId in new[] { "ramadi", "housing", "water", "rafidain_anbar" })
    {
        var entityPlan = entityId == "rafidain_anbar" ? direct : IndividualEntityPreparation.Prepare(entitySource, entityTransfer, entityId, entity.Id, references);
        var pdf = IndividualEntityPdf.Generate(entityPlan, individualSettings, individualFont, individualLogo);
        Check(pdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8), "individual entity PDF checked: " + entityId);
        if (Environment.GetEnvironmentVariable("SALARY_TEST_INDIVIDUAL_DIR") is { Length: > 0 } entityDir) { Directory.CreateDirectory(entityDir); File.WriteAllBytes(Path.Combine(entityDir, entityId + ".pdf"), pdf); }
        if (Environment.GetEnvironmentVariable("SALARY_TEST_MAJALLA") is { Length: > 0 } sakkalFile)
        {
            var sakkalPdf = IndividualEntityPdf.Generate(entityPlan, individualSettings, File.ReadAllBytes(sakkalFile), individualLogo);
            if (Environment.GetEnvironmentVariable("SALARY_TEST_INDIVIDUAL_DIR") is { Length: > 0 } saveDir) File.WriteAllBytes(Path.Combine(saveDir, entityId + "-sakkal.pdf"), sakkalPdf);
        }
    }
    byte[] Plist(NSObject value) { using var buffer = new MemoryStream(); PropertyListParser.SaveAsBinary(value, buffer); return buffer.ToArray(); }
    var bookSource = snapshot with { Employees = [incompleteContract], Deductions = [personRow], Accounts = [anbarAccount], Months = snapshot.Months.Select(m => m.Name == "أيلول 2026" ? new PayrollMonth(m.Name, 1, 1) : m).ToArray(), SettingsPlists = new Dictionary<string, byte[]> {
        ["bankStatementAccountName"] = Plist(new NSString("دافع وهمي")), ["bankStatementIBAN"] = Plist(new NSString(fakePayer)), ["transferReferenceArchiveData"] = Plist(new NSData(referencesArchive)) } };
    var bookLinkStore = new LocalTransferLinks(Path.Combine(root, "individual-book-links")); var originalBookState = bookLinkStore.Load(bookSource);
    var preparedBookTransfer = TransferPreparation.Prepare(originalBookState.Snapshot, "أيلول 2026"); var preparedEntity = preparedBookTransfer.Entries.Single(e => e.Title == "جهة وهمية");
    var savedBookLink = bookLinkStore.SaveBookLink(bookSource, originalBookState, preparedEntity, "ramadi");
    Check(IndividualLetterLinks.Resolve(bookLinkStore.Load(bookSource).Snapshot, "ramadi", preparedBookTransfer.Entries)?.Id == preparedEntity.Id, "individual book choice persists across reopening");
    try { _ = bookLinkStore.SaveBookLink(bookSource, originalBookState, preparedEntity, "ramadi"); throw new InvalidOperationException("stale book link accepted"); }
    catch (InvalidDataException) { }
    var fakeSettings = new GroupLetterSettings("ترويسة تجريبية", "ح/1", "عضو أول", "عضو ثان", "عضو ثالث", "", null, null, 0, null);
    var afterBookSettings = bookLinkStore.SaveGroupSettings(bookSource, savedBookLink, TransferGroup.Rafidain, fakeSettings, false);
    Check(IndividualLetterLinks.Resolve(afterBookSettings.Snapshot, "ramadi", preparedBookTransfer.Entries)?.Id == preparedEntity.Id, "book choice survives unrelated settings changes");
    var changedCandidate = preparedEntity with { Account = preparedEntity.Account! with { Iban = fakePayer } };
    Check(IndividualLetterLinks.Resolve(afterBookSettings.Snapshot, "ramadi", [changedCandidate]) is null, "changed account does not silently reuse a book choice");
    var savedBookBackup = Path.Combine(root, "individual-links-backup.zip"); ReconciliationBackupExport.Write(afterBookSettings.Snapshot, [], savedBookBackup);
    Check(IndividualLetterLinks.Resolve(BackupReader.Read(savedBookBackup), "ramadi", preparedBookTransfer.Entries)?.Id == preparedEntity.Id, "individual book choice survives backup export");
    var modifiedBody = IndividualLetterBody.Default("food_company").Replace("نرجو تحويل", "نرجو التفضل بتحويل", StringComparison.Ordinal);
    Check(IndividualPayrollPdf.Generate(foodPlan, individualSettings with { BodyTemplate = modifiedBody }, individualFont, individualFont, individualLogo, "٣ / ٢٠٢٢").Length > 0, "custom individual body retains mandatory placeholders");
    try { IndividualLetterBody.Validate("food_company", modifiedBody.Replace("{{حساب المستفيد}}", "", StringComparison.Ordinal)); throw new InvalidOperationException("custom body without beneficiary accepted"); }
    catch (InvalidDataException) { }
    try { IndividualLetterBody.Validate("water", IndividualLetterBody.Default("water").Replace("{{الاسم}}", "{{الاسم}} {{الاسم}}", StringComparison.Ordinal)); throw new InvalidOperationException("duplicate body placeholder accepted"); }
    catch (InvalidDataException) { }
    var bodySettings = fakeSettings with { BodyTemplate = IndividualLetterBody.Default("ramadi").Replace("نرجو تحويل", "نرجو التفضل بتحويل", StringComparison.Ordinal) };
    var afterBody = bookLinkStore.SaveIndividualSettings(bookSource, afterBookSettings, "ramadi", bodySettings, false, new HashSet<string> { "bodyTemplate" });
    Check(GroupLetterSettingsReader.Read(afterBody.Snapshot, "ramadi").BodyTemplate == bodySettings.BodyTemplate && IndividualLetterLinks.Resolve(afterBody.Snapshot, "ramadi", preparedBookTransfer.Entries)?.Id == preparedEntity.Id, "individual body saves without losing entity link");
    try { _ = bookLinkStore.SaveIndividualSettings(bookSource, afterBody, "ramadi", bodySettings with { BodyTemplate = "نص ناقص" }, false, new HashSet<string> { "bodyTemplate" }); throw new InvalidOperationException("invalid individual body saved"); }
    catch (InvalidDataException) { }
    Check(bookLinkStore.Load(bookSource).Revision == afterBody.Revision, "invalid body does not overwrite saved customization");
    var foodRefSettings = bodySettings with { FoodContractReference = "٣ / ٢٠٢٢" };
    var afterFood = bookLinkStore.SaveIndividualSettings(bookSource, afterBody, "food_company", foodRefSettings, false, new HashSet<string> { "foodContractReference" });
    Check(GroupLetterSettingsReader.FoodContract(afterFood.Snapshot) == "٣ / ٢٠٢٢" && GroupLetterSettingsReader.Read(afterFood.Snapshot, "ramadi").BodyTemplate == bodySettings.BodyTemplate, "food contract reference is common and preserves individual body");
    var foodBackup = Path.Combine(root, "individual-food-settings-backup.zip"); ReconciliationBackupExport.Write(afterFood.Snapshot, [], foodBackup);
    Check(GroupLetterSettingsReader.FoodContract(BackupReader.Read(foodBackup)) == "٣ / ٢٠٢٢", "food contract reference survives backup export");
    try { _ = bookLinkStore.SaveIndividualSettings(bookSource, afterFood, "ramadi", bodySettings, true, new HashSet<string> { "bodyTemplate" }); throw new InvalidOperationException("individual body applied to all books"); }
    catch (InvalidDataException) { }
    var reviewMonth = "تشرين الاول 2026"; var reviewOld = contractPerson with { Name = "اسم مقارنة وهمي", Month = "أيلول 2026", Net = 1000, Rank = "ملازم اول", Workplace = "قسم أول" };
    var reviewNew = reviewOld with { Month = reviewMonth, Net = 1200, Rank = "نقيب", Workplace = "قسم ثان" };
    var duplicateOld = reviewOld with { Name = "اسم مكرر وهمي" }; var duplicateNew = duplicateOld with { Month = reviewMonth };
    var reviewSource = snapshot with { Employees = [reviewOld, duplicateOld, reviewNew, duplicateNew, duplicateNew] };
    byte[] IbanDetails(string iban) => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["IBAN"] = iban });
    var ibanPeople = new[] {
        reviewNew with { Name = "موظف أ", Details = IbanDetails("iq06 test 1230 0000 0000 001") },
        reviewNew with { Name = "موظف ب", Details = IbanDetails(fakePayer) },
        reviewNew with { Name = "موظف ج", Details = IbanDetails(fakeReceiver) },
        reviewNew with { Name = "موظف د", Details = IbanDetails("IQ00TEST000000000000000") },
        reviewNew with { Name = "موظف هـ", Details = null },
        reviewOld with { Name = "شهر آخر", Details = IbanDetails(fakeReceiver) }
    };
    var ibanAudit = IbanAudit.Prepare(reviewSource with { Employees = ibanPeople }, reviewMonth);
    Check(ibanAudit.Rows.Count == 5 && ibanAudit.Rows.Count(x => x.Status == IbanAuditStatus.Duplicate) == 2 && ibanAudit.Rows.Count(x => x.Status == IbanAuditStatus.Valid) == 1 && ibanAudit.Rows.Count(x => x.Status == IbanAuditStatus.Invalid) == 1 && ibanAudit.Rows.Count(x => x.Status == IbanAuditStatus.Missing) == 1, "IBAN audit normalizes accounts, detects shared accounts and restricts its results to selected month");
    Check(!IbanAudit.Valid("IQABTEST123000000000001") && !IbanAudit.Valid("IQ06TES1123000000000001") && !IbanAudit.Valid("IQ06TEST12300000000000A") && IbanAudit.Valid(fakeReceiver), "IBAN audit checks Iraqi structure and checksum");
    var comparisonRows = new[] {
        new Deduction("أيلول 2026", "موظف وهمي أ", "جهة ألف", 10m, "١٢", "موظف"),
        new Deduction("أيلول 2026", "موظف وهمي أ", "جهة ألف", 5m, "١٢", "موظف"),
        new Deduction(reviewMonth, "موظف وهمي أ", "جهة ألف", 20m, "12", "موظف"),
        new Deduction("أيلول 2026", "موظف وهمي ب", "جهة باء", 15m, "", "موظف"),
        new Deduction(reviewMonth, "موظف وهمي ج", "جهة جيم", 12m, "", "موظف"),
        new Deduction("أيلول 2026", "موظف وهمي د", "جهة دال", 20m, "", "موظف"),
        new Deduction(reviewMonth, "موظف وهمي د", "جهة دال", 10m, "", "موظف"),
        new Deduction("أيلول 2026", "موظف وهمي هـ", "جهة هاء", 9m, "", "موظف"),
        new Deduction(reviewMonth, "موظف وهمي هـ", "جهة هاء", 9m, "", "موظف")
    };
    var comparisonSource = reviewSource with { Deductions = comparisonRows };
    var comparison = DeductionComparison.Compare(comparisonSource, "أيلول 2026", reviewMonth);
    Check(new DeductionChange("اختبار", "جهة", "", 500.125m, 501.375m, DeductionChangeKind.Increased).BeforeText == "500.125", "deduction comparison must not round monetary values");
    Check(comparison.Entries.Count == 5 && comparison.Entries.Single(x => x.Name == "موظف وهمي أ") is { Before: 15m, After: 20m, Kind: DeductionChangeKind.Increased } && Enum.GetValues<DeductionChangeKind>().All(k => comparison.Entries.Any(x => x.Kind == k)), "deduction comparison groups duplicates and covers all change states");
    try { DeductionComparison.Compare(comparisonSource with { Deductions = [comparisonRows[0]] }, "أيلول 2026", reviewMonth); throw new InvalidOperationException("empty comparison month accepted without confirmation"); } catch (InvalidDataException) { }
    Check(DeductionComparison.Compare(comparisonSource with { Deductions = [comparisonRows[0]] }, "أيلول 2026", reviewMonth, true).Entries.Single().Kind == DeductionChangeKind.Stopped, "confirmed empty comparison month");
    try { DeductionComparison.Compare(comparisonSource with { Deductions = [comparisonRows[0], comparisonRows[2] with { Amount = null }] }, "أيلول 2026", reviewMonth); throw new InvalidOperationException("missing amount accepted in comparison"); } catch (InvalidDataException) { }
    try { DeductionComparison.Compare(comparisonSource, reviewMonth, "أيلول 2026"); throw new InvalidOperationException("reverse month comparison accepted"); } catch (InvalidDataException) { }
    var preciseComparison = new DeductionChange("اسم وهمي", "جهة وهمية", "١٢", 500.125m, 1234567890123456m, DeductionChangeKind.Increased);
    var compareColumns = new[] { "الاسم", "الجهة", "الإضبارة", "السابق", "الحالي", "الفرق", "الحالة" }.Select(t => new ReportColumn(t, 15, ReportColumnKind.Text)).ToArray();
    string ExactComparisonAmount(decimal n) => n.ToString("0.############################", System.Globalization.CultureInfo.InvariantCulture);
    var comparisonSheet = new ReportSheet("تجريبي", "مقارنة تجريبية", compareColumns, [new[] { preciseComparison.Name, preciseComparison.Entity, preciseComparison.Note, ExactComparisonAmount(preciseComparison.Before), ExactComparisonAmount(preciseComparison.After), ExactComparisonAmount(preciseComparison.Difference), preciseComparison.Status }], [], false);
    var comparisonExcel = DeductionComparisonReport.Excel(comparisonSheet, [preciseComparison]);
    using (var compareZip = new ZipArchive(new MemoryStream(comparisonExcel), ZipArchiveMode.Read))
    {
        using var worksheet = new StreamReader(compareZip.GetEntry("xl/worksheets/sheet1.xml")!.Open()); var xml = worksheet.ReadToEnd();
        using var styles = new StreamReader(compareZip.GetEntry("xl/styles.xml")!.Open()); var styleXml = styles.ReadToEnd();
        Check(xml.Contains("<c r=\"D4\" s=\"6\"><v>500.125</v></c>") && xml.Contains("r=\"E4\" s=\"4\" t=\"inlineStr\"") && xml.Contains("1234567890123456") && styleXml.Contains("#,##0.############################"), "comparison Excel preserves decimal display and stores high-precision values as text");
    }
    try { DeductionComparisonReport.Excel(comparisonSheet, [preciseComparison with { Before = 501m }]); throw new InvalidOperationException("comparison export accepted mismatched data"); } catch (InvalidDataException) { }
    var referenceChanged = comparisonSource with { Deductions = [comparisonRows[0], comparisonRows[2] with { Amount = 10m, Note = "إضبارة جديدة" }] };
    var referenceComparison = DeductionComparison.Compare(referenceChanged, "أيلول 2026", reviewMonth);
    Check(referenceComparison.People.Single().Kind == DeductionChangeKind.Unchanged && referenceComparison.Entries.Count == 2 && referenceComparison.Entries.Any(x => x.Kind == DeductionChangeKind.Stopped) && referenceComparison.Entries.Any(x => x.Kind == DeductionChangeKind.Started), "changed case reference must not appear as a stopped employee");
    var zeroRecord = comparisonSource with { Deductions = [comparisonRows[0], comparisonRows[2] with { Amount = 0m }] };
    Check(DeductionComparison.Compare(zeroRecord, "أيلول 2026", reviewMonth).Entries.Single().Kind == DeductionChangeKind.Stopped, "zero deductions must not count as active amounts");
    try { DeductionComparison.Compare(comparisonSource with { Deductions = [comparisonRows[0], comparisonRows[2] with { Amount = -1m }] }, "أيلول 2026", reviewMonth); throw new InvalidOperationException("negative amount accepted in comparison"); } catch (InvalidDataException) { }
    var review = SalaryReview.Prepare(reviewSource, reviewMonth);
    Check(review.PreviousMonth == "أيلول 2026" && review.Rows.Any(r => r.Kind == "تغيّر الصافي" && r.Name == reviewNew.Name && r.Before == 1000 && r.After == 1200) && review.Rows.Any(r => r.Kind == "تغيّرت الرتبة") && review.Rows.Any(r => r.Kind == "تغيّر محل العمل"), "salary review compares unique employees to previous month");
    Check(review.ExcludedAmbiguous == 1 && review.Rows.Any(r => r.Kind == "أسماء مكررة") && !review.Rows.Any(r => r.Kind == "جديد في الرواتب" && r.Name == duplicateNew.Name), "ambiguous duplicate names never cause automatic comparison");
    var noPrevious = SalaryReview.Prepare(reviewSource with { Months = reviewSource.Months.Where(m => m.Name == reviewMonth).ToArray(), Employees = [reviewNew] }, reviewMonth);
    Check(noPrevious.PreviousMonth is null && noPrevious.Notes.Any(n => n.Contains("السابق", StringComparison.Ordinal)), "missing prior month is explicit");
    var auditEmployees = Enumerable.Range(1, 10).Select(n => contractPerson with { Name = "اسم فحص " + n, Month = reviewMonth, Net = n == 1 ? 499m : 500m,
        Items = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, decimal> { ["الرعاية الصحية"] = n == 1 ? 3501m : 3500m }),
        Details = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["طريقة الدفع"] = "كاش", ["الاستحقاق"] = "١٬٠٠٠", ["مجموع الاستقطاعات"] = "٥٠٠", ["مجموع استقطاعات المصارف"] = "٢٬٥٠٠", ["الرقم الوظيفي"] = n <= 2 ? "١٢٣" : n.ToString(System.Globalization.CultureInfo.InvariantCulture) }) }).ToArray();
    var detailedAudit = SalaryReview.Prepare(snapshot with { Employees = auditEmployees, Deductions = [personRow with { Month = reviewMonth }] }, reviewMonth);
    Check(detailedAudit.Rows.Any(r => r.Kind == "حساب الصافي" && r.Before == 500 && r.After == 499) && detailedAudit.Rows.Any(r => r.Kind == "الرعاية الصحية") && detailedAudit.Rows.Count(r => r.Kind == "رقم مكرر") == 2, "salary review checks arithmetic, healthcare, and repeated identifiers");
    Check(detailedAudit.Rows.Any(r => r.Kind == "استقطاع بلا راتب") && detailedAudit.Rows.Any(r => r.Kind == "مجموع الاستقطاعات"), "salary review checks deduction orphans and totals");
    // التشخيص: التكرار حالة واحدة لكل رقم، وفرق الصافي الصغير يُشخّص تقريباً، ولكل حالة سبب.
    Check(detailedAudit.Issues.Count(i => i.Kind == "رقم مكرر") == 1 && detailedAudit.Issues.Single(i => i.Kind == "رقم مكرر") is { People.Count: 2 } repeated && repeated.Title.Contains("123", StringComparison.Ordinal)
        && detailedAudit.Issues.Any(i => i.Kind == "حساب الصافي" && i.Diagnosis.Contains("تقريب", StringComparison.Ordinal)) && detailedAudit.Issues.All(i => i.Diagnosis.Length > 0),
        "salary review groups repeated numbers into one diagnosed case");
    Check(review.Issues.Any(i => i.Kind == "تغيّر الصافي" && i.People.Contains(reviewNew.Name)) && review.Issues.Any(i => i.Kind == "أسماء مكررة" && i.Columns.Count == 2), "salary review diagnoses changes and duplicate rows");
    Check(SalaryReviewDiagnosis.IbanProblem("IQ00TEST000000000000000").Contains("رقم التحقق", StringComparison.Ordinal) && SalaryReviewDiagnosis.IbanProblem("IQ12").Contains("4 خانة", StringComparison.Ordinal), "IBAN problems are explained");
    var afterAccount = bookLinkStore.SaveIndividualSettings(bookSource, afterFood, "ramadi", bodySettings, false, new HashSet<string> { "beneficiaryAccount" }, "أيلول 2026", preparedEntity.Id, fakeAlternate);
    Check(IndividualAccountCustomization.Read(afterAccount.Snapshot, "ramadi")?.Value == fakeAlternate, "individual account customization saved with its reference");
    var beforeUpdate = TransferPreparation.Prepare(afterAccount.Snapshot, "أيلول 2026");
    try { _ = IndividualEntityPreparation.Prepare(afterAccount.Snapshot, beforeUpdate, "ramadi", preparedEntity.Id, references); throw new InvalidOperationException("changed book account accepted before account list updated"); }
    catch (InvalidDataException) { }
    var changedAccountState = bookLinkStore.SaveAccount(bookSource, afterAccount, anbarAccount, anbarAccount with { Iban = fakeAlternate });
    var afterUpdate = TransferPreparation.Prepare(changedAccountState.Snapshot, "أيلول 2026");
    Check(IndividualEntityPreparation.Prepare(changedAccountState.Snapshot, afterUpdate, "ramadi", preparedEntity.Id, references).BeneficiaryAccount == fakeAlternate, "updated list unlocks chosen book account");
    var individualAccountBackup = Path.Combine(root, "individual-account-backup.zip"); ReconciliationBackupExport.Write(changedAccountState.Snapshot, [], individualAccountBackup);
    Check(IndividualAccountCustomization.Read(BackupReader.Read(individualAccountBackup), "ramadi")?.Value == fakeAlternate, "book account customization survives backup");
    try { _ = bookLinkStore.SaveIndividualSettings(bookSource, changedAccountState, "ramadi", bodySettings, false, new HashSet<string> { "beneficiaryAccount", "bodyTemplate" }, "أيلول 2026", preparedEntity.Id, fakePayer); throw new InvalidOperationException("payer accepted as beneficiary"); }
    catch (InvalidDataException) { }
    Check(bookLinkStore.Load(bookSource).Revision == changedAccountState.Revision, "invalid account cannot partially save other fields");
    var alternateOrderStore = new LocalTransferLinks(Path.Combine(root, "individual-account-updated-first"));
    var alternateInitial = alternateOrderStore.Load(bookSource);
    var listUpdatedFirst = alternateOrderStore.SaveAccount(bookSource, alternateInitial, anbarAccount, anbarAccount with { Iban = fakeAlternate });
    var linkedAfterUpdate = alternateOrderStore.SaveIndividualSettings(bookSource, listUpdatedFirst, "ramadi", fakeSettings, false, new HashSet<string> { "beneficiaryAccount" }, "أيلول 2026", preparedEntity.Id, fakeAlternate);
    var transferAfterFirst = TransferPreparation.Prepare(linkedAfterUpdate.Snapshot, "أيلول 2026");
    Check(IndividualEntityPreparation.Prepare(linkedAfterUpdate.Snapshot, transferAfterFirst, "ramadi", preparedEntity.Id, references).BeneficiaryAccount == fakeAlternate, "book customization works when the account list was updated first");
    try { _ = alternateOrderStore.SaveIndividualSettings(bookSource, linkedAfterUpdate, "ramadi", fakeSettings, true, new HashSet<string> { "beneficiaryAccount" }, "أيلول 2026", preparedEntity.Id, fakeAlternate); throw new InvalidOperationException("beneficiary account applied to all books"); }
    catch (InvalidDataException) { }
    var payrollAccountStore = new LocalTransferLinks(Path.Combine(root, "payroll-account-custom"));
    var payrollAccountInitial = payrollAccountStore.Load(bookSource);
    var payrollAccountSaved = payrollAccountStore.SaveIndividualSettings(bookSource, payrollAccountInitial, "food_company", fakeSettings, false, new HashSet<string> { "beneficiaryAccount" }, "أيلول 2026", null, fakeAlternate);
    var payrollAccountPlan = IndividualLetterPreparation.Payroll("food_company", incompleteBank with { Breakdown = new Dictionary<string, decimal>(incompleteBank.Breakdown) { ["شركة عراق الجوال"] = 100000 } }, "دافع وهمي", fakePayer, references, customization: payrollAccountSaved.Snapshot);
    Check(payrollAccountPlan.BeneficiaryAccount == fakeAlternate && payrollAccountSaved.Snapshot.Accounts.SequenceEqual(bookSource.Accounts), "payroll beneficiary customization does not alter saved transfer accounts");
    Check(IndividualPayrollPdf.Generate(payrollAccountPlan, individualSettings, individualFont, individualFont, individualLogo, "٣ / ٢٠٢٢").Length > 0, "custom payroll beneficiary appears in verified PDF");
    var annualPath = Path.Combine(root, "annual-2024.xlsx");
    string[] annualHeaders = ["الرتبة", "الاسم الكامل", "المبلغ", "جهة التنفيذ", "رقم وتاريخ الاضبارة", "شهر"];
    string[][] annualRows = [ ["موظف", "اسم وهمي 1", "١٠٠", "جهة وهمية", "", "كانون الثاني"], ["موظف", "اسم وهمي 2", "موقوف", "جهة وهمية", "", "شباط"], ["موظف", "اسم وهمي 3", "٥٠", "جهة وهمية", "", "شباط"], ["موظف", "اسم وهمي 4", "هارب", "جهة وهمية", "", "كانون الأول"] ];
    MakeMonthlyXlsx(annualPath, annualHeaders, annualRows);
    var annualPreview = AnnualDeductionSpreadsheet.Read(annualPath, snapshot);
    Check(annualPreview.Problems.Count == 0 && annualPreview.Months.Count == 3 && annualPreview.Months.Sum(p => p.Deductions.Count) == 4 && annualPreview.Months.SelectMany(p => p.Deductions).Count(PayrollAnalysis.IsInactive) == 2, "annual deductions split by month and preserve inactive statuses");
    var annualStore = new LocalPayrollImports(Path.Combine(root, "annual-imports")); var annualInitial = annualStore.Load(snapshot);
    var annualSaved = annualStore.ImportBatch(snapshot, annualInitial, annualPreview.Months, false);
    Check(annualSaved.Snapshot.Deductions.Count == snapshot.Deductions.Count + 4 && annualSaved.Snapshot.Months.Count == snapshot.Months.Count + 3, "annual months save in a single revision");
    var annualBackupPath = Path.Combine(root, "annual-import-backup.zip"); ReconciliationBackupExport.Write(annualSaved.Snapshot, [], annualBackupPath);
    var restoredAnnual = BackupReader.Read(annualBackupPath);
    Check(restoredAnnual.Deductions.Count == annualSaved.Snapshot.Deductions.Count && restoredAnnual.Months.Count == annualSaved.Snapshot.Months.Count && restoredAnnual.Deductions.Count(PayrollAnalysis.IsInactive) == annualSaved.Snapshot.Deductions.Count(PayrollAnalysis.IsInactive), "annual imports and inactive statuses survive backup export");
    try { _ = annualStore.ImportBatch(snapshot, annualSaved, annualPreview.Months, false); throw new InvalidOperationException("existing annual deductions replaced without review"); }
    catch (InvalidDataException) { }
    Check(annualStore.Load(snapshot).Revision == annualSaved.Revision, "unapproved annual replacement preserves every month");
    var invalidAnnualPath = Path.Combine(root, "annual-invalid-2024.xlsx"); MakeMonthlyXlsx(invalidAnnualPath, annualHeaders, annualRows.Concat([["موظف", "اسم وهمي", "حالة غامضة", "جهة وهمية", "", "آذار"]]).ToArray());
    var invalidAnnual = AnnualDeductionSpreadsheet.Read(invalidAnnualPath, snapshot);
    Check(invalidAnnual.Problems.Count > 0, "invalid annual row reported");
    try { _ = annualStore.ImportBatch(snapshot, annualSaved, invalidAnnual.Months, true); throw new InvalidOperationException("partial invalid annual import accepted"); }
    catch (InvalidDataException) { }
    Check(annualStore.Load(snapshot).Revision == annualSaved.Revision, "invalid annual file preserves all previously saved months");
    var conflictAnnualPath = Path.Combine(root, "annual-conflict-2024.xlsx"); MakeMonthlyXlsx(conflictAnnualPath, annualHeaders, [["موظف", "اسم وهمي", "١٠", "جهة وهمية", "", "شباط 2025"]]);
    Check(AnnualDeductionSpreadsheet.Read(conflictAnnualPath, snapshot).Problems.Count > 0, "conflicting annual year is rejected");
    var noYearAnnualPath = Path.Combine(root, "annual-no-year.xlsx"); MakeMonthlyXlsx(noYearAnnualPath, annualHeaders, annualRows);
    Check(AnnualDeductionSpreadsheet.Read(noYearAnnualPath, snapshot).Problems.Count > 0, "annual file without a definite year is rejected");
    if (Environment.GetEnvironmentVariable("SALARY_TEST_ANNUAL_DIR") is { Length: > 0 } annualFixtureDir)
    {
        var fixture = AnnualDeductionSpreadsheet.Read(Path.Combine(annualFixtureDir, "annual-fixture.xlsx"), snapshot);
        Check(fixture.Problems.Count == 0 && fixture.Months.Count == 3 && fixture.Months.Sum(p => p.Deductions.Count) == 5, "existing annual fixture separates three months");
        Check(AnnualDeductionSpreadsheet.Read(Path.Combine(annualFixtureDir, "annual-invalid.xlsx"), snapshot).Problems.Count > 0 && AnnualDeductionSpreadsheet.Read(Path.Combine(annualFixtureDir, "annual-2025.xlsx"), snapshot).Problems.Count > 0, "existing annual fixtures reject invalid status and conflicting years");
    }

    var staffBackup = Path.Combine(root, "staff-fixture.zip"); MakeBackup(staffBackup);
    using (var archive = ZipFile.Open(staffBackup, ZipArchiveMode.Update))
    {
        archive.GetEntry("manifest.json")!.Delete();
        Entry(archive, "manifest.json", JsonSerializer.Serialize(new { version = 5, months = new[] { "تشرين الاول 2026", "أيلول 2026" }, employees = 2, deductions = 3, staff = 2, accounts = 1, books = 1 }));
        archive.GetEntry("staff.jsonl")!.Delete();
        var sections = JsonSerializer.SerializeToUtf8Bytes(new[] { new { title = "قسم تجريبي", fields = new[] { new { label = "حقل وهمي", value = "قيمة وهمية" } } } });
        Entry(archive, "staff.jsonl", Lines(new[] { new { name = "موظف ملاك وهمي", mother = "أم وهمية", stat = "10", unified = "20", rank = "موظف", status = "مستمر", workplace = "قسم وهمي", details = sections }, new { name = "موظف آخر وهمي", mother = "", stat = "11", unified = "21", rank = "موظف", status = "متقاعد", workplace = "قسم وهمي", details = sections } }));
    }
    var staffSource = BackupReader.Read(staffBackup); var staffMembers = StaffRecords.Read(staffSource);
    Check(staffMembers.Count == 2 && staffMembers[0].Unified == "20" && StaffRecords.Sections(staffMembers[0]).Single().Fields.Single().Value == "قيمة وهمية", "staff data and structured details are read from verified backup");
    using (var changed = new FileStream(staffBackup, FileMode.Append, FileAccess.Write, FileShare.None)) changed.WriteByte(0);
    try { _ = StaffRecords.Read(staffSource); throw new InvalidOperationException("changed backup used for staff"); }
    catch (InvalidDataException) { }
    var roster = new[] {
        new StaffMember("اسم ملاك وهمي", "", "", "20", "ملازم", "مستمر", "قسم وهمي", null),
        new StaffMember("ملاك بلا راتب وهمي", "", "", "40", "موظف", "مستمر", "قسم وهمي", null),
        new StaffMember("متقاعد وهمي", "", "", "41", "موظف", "متقاعد", "قسم وهمي", null),
        new StaffMember("اسم ملتبس وهمي", "", "", "42", "موظف", "مستمر", "قسم وهمي", null),
        new StaffMember("اسم ملتبس وهمي", "", "", "43", "موظف", "مستمر", "قسم وهمي", null) };
    var rosterPayroll = new[] {
        contractPerson with { Month = reviewMonth, Name = "اسم راتب وهمي", Rank = "نقيب", Details = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["طريقة الدفع"] = "كاش", ["الرقم الموحد"] = "٢٠" }) },
        contractPerson with { Month = reviewMonth, Name = "راتب بلا ملاك وهمي", Details = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["طريقة الدفع"] = "كاش" }) },
        contractPerson with { Month = reviewMonth, Name = "اسم ملتبس وهمي", Details = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["طريقة الدفع"] = "كاش" }) } };
    var rosterAudit = SalaryReview.Prepare(snapshot with { StaffCount = roster.Length, Employees = rosterPayroll }, reviewMonth, roster);
    Check(rosterAudit.Rows.Any(r => r.Kind == "رتبة مختلفة عن الملاك" && r.Name == "اسم راتب وهمي") && rosterAudit.Rows.Any(r => r.Kind == "راتب بلا ملاك" && r.Name == "راتب بلا ملاك وهمي") && rosterAudit.Rows.Any(r => r.Kind == "ملاك بلا راتب" && r.Name == "ملاك بلا راتب وهمي"), "staff audit matches unique unified identifiers and reports missing records");
    Check(!rosterAudit.Rows.Any(r => r.Kind == "ملاك بلا راتب" && (r.Name == "متقاعد وهمي" || r.Name == "اسم ملتبس وهمي")) && rosterAudit.Notes.Any(n => n.Contains("غير محسوم", StringComparison.Ordinal)), "retired and ambiguous staff are not reported as missing salaries");
    try { _ = SalaryReview.Prepare(snapshot with { StaffCount = 1, Employees = rosterPayroll }, reviewMonth, roster); throw new InvalidOperationException("incomplete staff dataset accepted"); }
    catch (InvalidDataException) { }
    if (Environment.GetEnvironmentVariable("SALARY_TEST_STAFF_LARGE") == "1")
    {
        const int count = 42_000;
        var largeEmployees = Enumerable.Range(1, count).Select(n => contractPerson with { Month = reviewMonth, Name = "منتسب تجريبي " + n, Rank = "موظف", Details = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["طريقة الدفع"] = "كاش", ["الرقم الموحد"] = n.ToString(System.Globalization.CultureInfo.InvariantCulture) }) }).ToArray();
        var largeStaff = Enumerable.Range(1, count).Select(n => new StaffMember("منتسب تجريبي " + n, "", "", n.ToString(System.Globalization.CultureInfo.InvariantCulture), "موظف", "مستمر", "قسم تجريبي", null)).ToArray();
        var result = SalaryReview.Prepare(snapshot with { StaffCount = count, Employees = largeEmployees, Deductions = [] }, reviewMonth, largeStaff);
        Check(!result.Rows.Any(r => r.Kind is "راتب بلا ملاك" or "ملاك بلا راتب" or "رتبة مختلفة عن الملاك") && result.EmployeeCount == count, "large staff and payroll comparison matches all synthetic records");
        Console.WriteLine("Synthetic staff review: 42000 records matched.");
    }
    var bankDetailState = bookLinkStore.SaveBankDetails(bookSource, changedAccountState, "أيلول 2026", "دافع تجريبي محدّث", fakePayer, new BankManualAmounts(1000, 2000, 3000));
    var bankOtherMonth = bookLinkStore.SaveBankDetails(bookSource, bankDetailState, "تشرين الاول 2026", "دافع تجريبي محدّث", fakePayer, new BankManualAmounts(4, 5, 6));
    Check(BankStatementCalculator.Manual(bankOtherMonth.Snapshot, "أيلول 2026").Total == 6000 && BankStatementCalculator.Manual(bankOtherMonth.Snapshot, "تشرين الاول 2026").Total == 15, "bank manual values stay separate by month");
    Check(TransferPreparation.Prepare(bankOtherMonth.Snapshot, "أيلول 2026").Payer == "دافع تجريبي محدّث" && IndividualAccountCustomization.Read(bankOtherMonth.Snapshot, "ramadi")?.Value == fakeAlternate, "bank settings preserve individual account links and customization");
    var bankUpdatedBackup = Path.Combine(root, "bank-details-backup.zip"); ReconciliationBackupExport.Write(bankOtherMonth.Snapshot, [], bankUpdatedBackup);
    var restoredBankDetails = BackupReader.Read(bankUpdatedBackup);
    Check(BankStatementCalculator.Manual(restoredBankDetails, "أيلول 2026").Total == 6000 && TransferPreparation.Prepare(restoredBankDetails, "أيلول 2026").Payer == "دافع تجريبي محدّث", "bank settings survive backup export");
    try { _ = bookLinkStore.SaveBankDetails(bookSource, bankOtherMonth, "أيلول 2026", "دافع وهمي", fakePayer, new BankManualAmounts(-1, 0, 0)); throw new InvalidOperationException("negative manual amount accepted"); }
    catch (InvalidDataException) { }
    Check(bookLinkStore.Load(bookSource).Revision == bankOtherMonth.Revision, "invalid bank settings preserve prior state");
    var bankAccountState = bookLinkStore.SaveBankDetails(bookSource, bankOtherMonth, "أيلول 2026", "دافع تجريبي محدّث", fakePayer, new BankManualAmounts(1000, 2000, 3000), "فرع وهمي", "123456789");
    var savedAccount = TransferPreparation.Account(bankAccountState.Snapshot);
    var afterOtherSave = bookLinkStore.SaveBankDetails(bookSource, bankAccountState, "تشرين الاول 2026", "دافع تجريبي محدّث", fakePayer, new BankManualAmounts(4, 5, 6));
    Check(savedAccount is { Branch: "فرع وهمي", LocalAccount: "123456789" } && TransferPreparation.Account(afterOtherSave.Snapshot).Branch == "فرع وهمي" && TransferPreparation.Account(bookSource).Branch == "الأنبار", "bank branch and local account are saved and kept by later saves");
    var bankAccountBackup = Path.Combine(root, "bank-account-backup.zip"); ReconciliationBackupExport.Write(afterOtherSave.Snapshot, [], bankAccountBackup);
    Check(TransferPreparation.Account(BackupReader.Read(bankAccountBackup)) is { Branch: "فرع وهمي", LocalAccount: "123456789" }, "bank branch and local account survive backup export");
    try { _ = bookLinkStore.SaveBankDetails(bookSource, afterOtherSave, "أيلول 2026", "دافع وهمي", fakePayer, new BankManualAmounts(0, 0, 0), "فرع", "12ab"); throw new InvalidOperationException("non-numeric local account accepted"); }
    catch (InvalidDataException) { }
    var itemEmployees = new[] { contractPerson, contractPerson with { Name = "شخص آخر وهمي", Items = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, decimal> { ["الرعاية الصحية"] = 30m }) } };
    var itemSource = snapshot with { Employees = itemEmployees }; var itemGroups = SalaryItemReports.Groups(itemSource, "أيلول 2026"); var healthGroup = itemGroups.Single(g => g.Title == "الرعاية الصحية");
    Check(healthGroup.People == 2 && healthGroup.Total == 50 && SalaryItemReports.Rows(itemSource, "أيلول 2026", healthGroup).Sum(r => r.Amount) == 50, "salary item report matches contributing employees and totals");
    try { _ = SalaryItemReports.Rows(itemSource, "أيلول 2026", healthGroup with { Total = 51 }); throw new InvalidOperationException("incorrect item report summary accepted"); }
    catch (InvalidDataException) { }
    try { _ = SalaryItemReports.Groups(itemSource with { Employees = [contractPerson with { Items = Encoding.UTF8.GetBytes("{") }] }, "أيلول 2026"); throw new InvalidOperationException("invalid payroll items accepted"); }
    catch (InvalidDataException) { }
    var initialPath = Path.Combine(root, "new-dataset.zip");
    var initialPreview = new PayrollImportPreview("أيلول 2026", [contractPerson], [personRow], []);
    var initialSnapshot = InitialDataset.Create(initialPreview, initialPath);
    Check(initialSnapshot.Months.Count == 1 && initialSnapshot.Employees.Count == 1 && initialSnapshot.Deductions.Count == 1 && initialSnapshot.Accounts.Count == 0, "initial dataset contains only reviewed records");
    using (var initialZip = ZipFile.OpenRead(initialPath))
    {
        using var monthReader = new StreamReader(initialZip.GetEntry("months.jsonl")!.Open(), Encoding.UTF8);
        using var monthJson = JsonDocument.Parse(monthReader.ReadLine()!);
        Check(monthJson.RootElement.TryGetProperty("importedAt", out var importedAt) && importedAt.ValueKind == JsonValueKind.Number && monthJson.RootElement.TryGetProperty("summary", out var summaryValue) && summaryValue.ValueKind == JsonValueKind.String, "initial backup includes iPhone month date and item summary");
    }
    var standaloneStore = new LocalSnapshotStore(Path.Combine(root, "new-dataset-local")); var standalone = standaloneStore.Import(initialPath, expectedDigest: initialSnapshot.SourceDigest, requireEmptyStore: true);
    var initialBackup = Path.Combine(root, "new-dataset-export.zip"); ReconciliationBackupExport.Write(standalone, [], initialBackup);
    Check(BackupReader.Read(initialBackup).Employees.Count == 1, "initial dataset can be exported as a verified backup");
    try { _ = standaloneStore.Import(initialPath, requireEmptyStore: true); throw new InvalidOperationException("initial creation overwrote an existing local store"); }
    catch (InvalidDataException) { }
    try { _ = InitialDataset.Create(new PayrollImportPreview("أيلول 2026", [], [], []), Path.Combine(root, "empty-dataset.zip")); throw new InvalidOperationException("empty initial dataset accepted"); }
    catch (InvalidDataException) { }
    var initialHash = SHA256.HashData(File.ReadAllBytes(initialPath));
    try { _ = InitialDataset.Create(initialPreview, initialPath); throw new InvalidOperationException("initial dataset overwrote existing file"); }
    catch (IOException) { }
    Check(initialHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(initialPath))), "existing initial dataset is preserved");
    var referenceFolder = Path.Combine(root, "reference-folder"); Directory.CreateDirectory(referenceFolder);
    using (var referenceZip = new ZipArchive(new MemoryStream(referencesArchive), ZipArchiveMode.Read))
        foreach (var file in referenceZip.Entries.Where(e => e.FullName.StartsWith("templates/", StringComparison.Ordinal)))
        { using var input = file.Open(); using var output = File.Create(Path.Combine(referenceFolder, Path.GetFileName(file.FullName))); input.CopyTo(output); }
    var rebuiltReferences = ReferenceArchiveBuilder.Create(referenceFolder);
    Check(LetterReferencePack.ReadArchive(rebuiltReferences).Count == 11, "reference folder packs and validates every required PDF");
    var standaloneLinks = new LocalTransferLinks(Path.Combine(root, "standalone-reference-links")); var beforeReferences = standaloneLinks.Load(standalone);
    var withReferences = standaloneLinks.SaveReferences(standalone, beforeReferences, rebuiltReferences);
    var afterReferencesAndBank = standaloneLinks.SaveBankDetails(standalone, withReferences, "أيلول 2026", "دافع وهمي", fakePayer, new BankManualAmounts(1, 2, 3));
    Check(LetterReferencePack.Read(afterReferencesAndBank.Snapshot).Count == 11 && BankStatementCalculator.Manual(afterReferencesAndBank.Snapshot, "أيلول 2026").Total == 6, "imported references survive bank settings change");
    var referenceBackup = Path.Combine(root, "reference-import-backup.zip"); ReconciliationBackupExport.Write(afterReferencesAndBank.Snapshot, [], referenceBackup);
    Check(LetterReferencePack.Read(BackupReader.Read(referenceBackup)).Count == 11, "imported references survive backup export");
    File.Copy(Path.Combine(referenceFolder, "rafidain_group.pdf"), Path.Combine(referenceFolder, "مجموعة مصارف الرافدين.pdf"));
    try { _ = ReferenceArchiveBuilder.Create(referenceFolder); throw new InvalidOperationException("ambiguous reference files accepted"); }
    catch (InvalidDataException) { }
    if (Environment.GetEnvironmentVariable("SALARY_TEST_REFERENCE_DIR") is { Length: > 0 } privateReferenceDir)
    {
        var packed = ReferenceArchiveBuilder.Create(privateReferenceDir);
        Check(LetterReferencePack.ReadArchive(packed).Count == 12, "private local reference pack validates all required PDFs and numeral reference");
        Console.WriteLine("Local reference validation: 12 PDFs accepted.");
    }
    var issuanceSource = bookSource with { Employees = [incompleteContract with { Items = null }], Accounts = [anbarAccount with { Branch = "الرافدين فرع تجريبي", Swift = "TESTIQBAAAA" }] };
    var issuanceStore = new LocalTransferLinks(Path.Combine(root, "issuance-review")); var issuanceInitial = issuanceStore.Load(issuanceSource);
    var issuanceTransfer = TransferPreparation.Prepare(issuanceInitial.Snapshot, "أيلول 2026");
    var issuanceReview = GroupIssuance.Review(issuanceInitial.Snapshot, issuanceTransfer, TransferGroup.Rafidain);
    Check(issuanceReview.Lines.Count == 1 && issuanceReview.Changes.Count == 0 && issuanceReview.BaselineMonth is null, "first group issuance prepares one checked entity");
    var issuanceSaved = issuanceStore.ConfirmGroup(issuanceSource, issuanceInitial, issuanceReview, "مراجعة تجريبية");
    var sameReview = GroupIssuance.Review(issuanceSaved.Snapshot, TransferPreparation.Prepare(issuanceSaved.Snapshot, "أيلول 2026"), TransferGroup.Rafidain);
    Check(sameReview.Changes.Count == 0 && sameReview.BaselineMonth == "أيلول 2026", "unchanged group matches its saved confirmation");
    var changedNamesSource = issuanceSaved.Snapshot with { Deductions = [personRow with { Name = "اسم مختلف وهمي" }] };
    var changedNames = GroupIssuance.Review(changedNamesSource, TransferPreparation.Prepare(changedNamesSource, "أيلول 2026"), TransferGroup.Rafidain);
    Check(changedNames.Changes.Any(c => c.Contains("الأسماء", StringComparison.Ordinal)), "changed names are flagged even when total and count stay unchanged");
    var increasedEmployee = incompleteContract with { Items = null, Details = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["طريقة الدفع"] = "كاش", ["توقيفات تقاعدية"] = "75", ["مجموع استقطاعات المصارف"] = "3000" }) };
    var increasedSource = issuanceSaved.Snapshot with { Employees = [increasedEmployee], Deductions = [personRow with { Amount = 3000m }] };
    var increasedReview = GroupIssuance.Review(increasedSource, TransferPreparation.Prepare(increasedSource, "أيلول 2026"), TransferGroup.Rafidain);
    Check(increasedReview.Changes.Any(c => c.Contains("2,500", StringComparison.Ordinal) && c.Contains("3,000", StringComparison.Ordinal)), "group issuance describes old and new amounts");
    var rawLedger = (NSData)PropertyListParser.Parse(issuanceSaved.Snapshot.SettingsPlists[GroupIssuance.SettingsKey]);
    var ledgerNode = System.Text.Json.Nodes.JsonNode.Parse(rawLedger.Bytes)!.AsObject();
    var firstLine = ledgerNode["records"]![0]!["lines"]![0]!.AsObject(); firstLine.Remove("windowsDetailHash"); firstLine["detailHash"] = "iphone-fingerprint";
    using var oldLedgerPlist = new MemoryStream(); PropertyListParser.SaveAsBinary(new NSData(JsonSerializer.SerializeToUtf8Bytes(ledgerNode)), oldLedgerPlist);
    var oldSettings = issuanceSaved.Snapshot.SettingsPlists.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal); oldSettings[GroupIssuance.SettingsKey] = oldLedgerPlist.ToArray();
    var iosLedgerSource = issuanceSaved.Snapshot with { SettingsPlists = oldSettings };
    var iosReview = GroupIssuance.Review(iosLedgerSource, TransferPreparation.Prepare(iosLedgerSource, "أيلول 2026"), TransferGroup.Rafidain);
    Check(iosReview.Changes.Any(c => c.Contains("بصمة الأسماء", StringComparison.Ordinal)), "iPhone-only fingerprint triggers explicit review on Windows");
    Check(!Encoding.UTF8.GetString(rawLedger.Bytes).Contains(personRow.Name, StringComparison.Ordinal), "issuance ledger stores no employee names");
    try { _ = issuanceStore.ConfirmGroup(issuanceSource, issuanceInitial, issuanceReview, "قديم"); throw new InvalidOperationException("stale group confirmation accepted"); }
    catch (InvalidDataException) { }
    var issuanceBackupPath = Path.Combine(root, "issuance-backup.zip"); ReconciliationBackupExport.Write(issuanceSaved.Snapshot, [], issuanceBackupPath);
    var issuanceRestored = BackupReader.Read(issuanceBackupPath); GroupIssuance.Validate(issuanceRestored);
    Check(GroupIssuance.Review(issuanceRestored, TransferPreparation.Prepare(issuanceRestored, "أيلول 2026"), TransferGroup.Rafidain).Changes.Count == 0, "group issuance and details fingerprint survive backup export");
    var composedLinksStore = new LocalTransferLinks(Path.Combine(root, "composed-links"));
    var composedInitial = composedLinksStore.Load(combinedPayroll.Snapshot);
    var fakeUpdatedAccount = composedInitial.Snapshot.Accounts.Single() with { Iban = fakeReceiver, Branch = "الرافدين فرع تجريبي", Swift = "TESTIQBAAAA" };
    var composedLinks = composedLinksStore.SaveAccount(combinedPayroll.Snapshot, composedInitial, composedInitial.Snapshot.Accounts.Single(), fakeUpdatedAccount);
    var composedBackupPath = Path.Combine(root, "composed-backup.zip"); ReconciliationBackupExport.Write(composedLinks.Snapshot, [period], composedBackupPath);
    var composed = BackupReader.Read(composedBackupPath);
    Check(composed.Employees.Any(e => e.Month == "تشرين الثاني 2026" && e.Net == 1000m) && composed.Deductions.Any(d => d.Month == "تشرين الثاني 2026" && d.Amount == 500m) &&
          composed.Accounts.Single().Iban == fakeReceiver && BankReconciliation.ReadFromBackup(composed).Single().Id == period.Id, "backup combines imported month, edited account and reconciliation");
    var xlsx = Path.Combine(root, "accounts.xlsx"); MakeAccountXlsx(xlsx, fakeReceiver);
    var accountPreview = AccountSpreadsheet.Read(xlsx);
    Check(accountPreview.Problems.Count == 0 && accountPreview.Accounts.Single().Iban == fakeReceiver, "text IBAN spreadsheet read");
    Check(ImportDetector.Detect(xlsx, snapshot).Kind == ImportKind.Accounts, "smart import recognizes the accounts workbook");
    var numericXlsx = Path.Combine(root, "numeric.xlsx"); MakeAccountXlsx(numericXlsx, "123456789012345", numeric: true);
    Check(AccountSpreadsheet.Read(numericXlsx).Problems.Single().Message.Contains("كنص", StringComparison.Ordinal), "numeric account cell is not guessed");
    var localXlsx = Path.Combine(root, "local.xlsx"); MakeAccountXlsx(localXlsx, "123456789012345", numeric: true, name: "صندوق الإسكان فرع الأنبار", branch: "الرافدين فرع الأنبار");
    Check(AccountSpreadsheet.Read(localXlsx).Accounts.Single() is { Iban: "", LocalAccount: "123456789012345" }, "exact 15 digit numeric local account allowed for approved Anbar entity");
    var shortLocalXlsx = Path.Combine(root, "local-short.xlsx"); MakeAccountXlsx(shortLocalXlsx, "12345678901234", numeric: true, name: "صندوق الإسكان فرع الأنبار", branch: "الرافدين فرع الأنبار");
    Check(AccountSpreadsheet.Read(shortLocalXlsx).Problems.Count == 1, "short numeric local account does not receive guessed leading zero");
    var duplicateXlsx = Path.Combine(root, "duplicate-accounts.xlsx"); MakeAccountXlsx(duplicateXlsx, fakeReceiver, duplicate: true);
    Check(AccountSpreadsheet.Read(duplicateXlsx).Problems.Any(p => p.Message.Contains("مكرر", StringComparison.Ordinal)), "duplicate account rows reported");
    var missingNameXlsx = Path.Combine(root, "missing-name.xlsx"); MakeAccountXlsx(missingNameXlsx, fakeReceiver, missingName: true);
    Check(AccountSpreadsheet.Read(missingNameXlsx).Problems.Any(p => p.Message.Contains("دون اسم", StringComparison.Ordinal)), "account with missing entity is not silently skipped");
    var importStore = new LocalTransferLinks(Path.Combine(root, "xlsx-import")); var beforeImport = importStore.Load(snapshot);
    var importedAccounts = importStore.ImportAccounts(snapshot, beforeImport, accountPreview);
    Check(importedAccounts.Snapshot.Accounts.Count == snapshot.Accounts.Count + 1 && importStore.Load(snapshot).Snapshot.Accounts.Last().Iban == fakeReceiver, "spreadsheet accounts merge and persist");
    var importedLinkEntry = TransferPreparation.Prepare(importedAccounts.Snapshot, "أيلول 2026").Entries.First(e => e.Title == "جهة وهمية");
    var importedLinked = importStore.SaveLink(snapshot, importedAccounts, importedLinkEntry, importedAccounts.Snapshot.Accounts.Last());
    Check(TransferPreparation.Prepare(importedLinked.Snapshot, "أيلول 2026").Entries.First(e => e.Title == "جهة وهمية") is { Group: TransferGroup.OtherBanks, Account.Iban: fakeReceiver }, "imported account links to deduction and changes group");
    try { _ = importStore.ImportAccounts(snapshot, importedLinked, AccountSpreadsheet.Read(numericXlsx)); throw new InvalidOperationException("invalid account import accepted"); }
    catch (InvalidDataException) { }
    Check(importStore.Load(snapshot).Revision == importedLinked.Revision, "failed spreadsheet import preserves accounts");
    var importBackupPath = Path.Combine(root, "imported-accounts.zip"); ReconciliationBackupExport.Write(importedLinked.Snapshot, [], importBackupPath);
    var importRestored = BackupReader.Read(importBackupPath);
    Check(importRestored.Accounts.Last().Iban == fakeReceiver && importRestored.Accounts.Count == snapshot.Accounts.Count + 1 &&
          TransferPreparation.Prepare(importRestored, "أيلول 2026").Entries.First(e => e.Title == "جهة وهمية").Account?.Iban == fakeReceiver, "imported account and link survive backup export");
    var transferAccount = new ExecutionAccount("جهة تحويل وهمية", fakeReceiver, "مصرف الرافدين فرع تجريبي", "TESTIQBAAAA", null);
    Check(TransferPreparation.ValidIban(fakePayer) && TransferPreparation.ValidIban(fakeReceiver) && !TransferPreparation.ValidIban("IQ00") && TransferPreparation.ValidSwift("TESTIQBAAAA"), "transfer IBAN and BIC checks");
    Check(TransferPreparation.Classify(transferAccount) == TransferGroup.Rafidain &&
          TransferPreparation.Classify(transferAccount with { Branch = "الرافدين فرع الانبار" }) == TransferGroup.Anbar &&
          TransferPreparation.Classify(transferAccount with { Branch = "الرشيد فرع وهمي" }) == TransferGroup.OtherBanks, "transfer bank classification");
    var entry = new TransferEntry("deduction|وهمي", "أيلول 2026", "جهة تحويل وهمية", 2500m, 1, 0, transferAccount, TransferGroup.Rafidain, null, false);
    var transfer = new TransferSnapshot("أيلول 2026", "دافع وهمي", fakePayer, new[] { entry }, Array.Empty<string>());
    var bytes = TransferPreparation.MakeCsv(transfer, TransferGroup.Rafidain, new DateOnly(2026, 10, 31));
    var csv = Encoding.UTF8.GetString(bytes);
    Check(ArabicAmountWords.Dinars(1) == "دينار واحد" && ArabicAmountWords.Dinars(2) == "ديناران" && ArabicAmountWords.Dinars(3) == "ثلاثة دنانير" &&
          ArabicAmountWords.Dinars(1000) == "ألف دينار" && ArabicAmountWords.Dinars(1_000_002) == "مليون وديناران", "Arabic amount words preserve unit and scale forms");
    var groupLetter = GroupLetterPreparation.Prepare(transfer, TransferGroup.Rafidain, new DateOnly(2026, 10, 31));
    Check(groupLetter.Total == 2500 && groupLetter.Lines.Single().Iban == fakeReceiver && groupLetter.Csv.AsSpan().SequenceEqual(bytes), "group letter plan matches validated transfer CSV");
    var letterSettings = new GroupLetterSettings("ترويسة تجريبية\nقسم حسابات وهمي", "ح/1/22/", "عضو تجريبي أول", "عضو تجريبي ثان", "عضو تجريبي ثالث", "نسخة تجريبية", null, 10, 0, null);
    var logoPath = Path.GetFullPath("SalaryApp/Assets.xcassets/IraqEmblem.imageset/IraqEmblem.png");
    var fontBytes = File.ReadAllBytes(font); var logoBytes = File.ReadAllBytes(logoPath);
    try { GroupLetterFont.Validate(fontBytes); throw new InvalidOperationException("wrong letter font accepted"); }
    catch (InvalidDataException) { }
    Check(IndividualPayrollPdf.Generate(manualLetter, letterSettings, fontBytes, fontBytes, logoBytes, null).Length > 0 &&
          IndividualPayrollPdf.Generate(lowerLetter, letterSettings, fontBytes, fontBytes, logoBytes, null).Length > 0,
          "positive and negative contract adjustments generate verifiable letters");
    var groupPdf = GroupLetterPdf.Generate(groupLetter, letterSettings, fontBytes, fontBytes, logoBytes);
    Check(groupPdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8), "group PDF generated with validated table");
    var footerExample = "نسخة منه الى:\n. (" + new string(' ', 106) + ") للتفضل بالاطلاع وإعلامنا الاستلام مع التقدير.\n. مستلم (مذكور)";
    var expandedFooter = GroupLetterPdf.FooterWritingSpace(footerExample);
    Check(expandedFooter.Contains("(" + new string('\u00A0', 50) + ")", StringComparison.Ordinal) && expandedFooter.Contains("(مذكور)", StringComparison.Ordinal) && GroupLetterPdf.FooterWritingSpace(expandedFooter) == expandedFooter,
        "group footer reserves the empty writing space without changing filled parentheses");
    var footerPdf = GroupLetterPdf.Generate(groupLetter, letterSettings with { Footer = footerExample.Replace("\n. مستلم (مذكور)", "", StringComparison.Ordinal) }, fontBytes, fontBytes, logoBytes);
    using (var footerDocument = UglyToad.PdfPig.PdfDocument.Open(footerPdf))
    {
        var brackets = footerDocument.GetPage(1).Letters.Where(l => (l.Value == "(" || l.Value == ")") && l.GlyphRectangle.Bottom < 110).ToArray();
        Check(brackets.Length == 2 && brackets.Max(l => l.GlyphRectangle.Left) - brackets.Min(l => l.GlyphRectangle.Left) > 90 &&
              Math.Abs(brackets[0].GlyphRectangle.Bottom - brackets[1].GlyphRectangle.Bottom) < 1, "printed group footer has a long handwriting gap on the same line");
    }
    var namedEntry = entry with { Title = "صندوق شهداء الشرطة (تبرعات)", Account = transferAccount with { Branch = "مصرف الرافدين فرع 12" } };
    var namedPlan = GroupLetterPreparation.Prepare(transfer with { Entries = [namedEntry] }, TransferGroup.Rafidain, new DateOnly(2026, 10, 31));
    Check(GroupLetterPdf.Generate(namedPlan, letterSettings, fontBytes, fontBytes, logoBytes).Length > 0, "Arabic name with parentheses and branch number remains verifiable");
    var changedBodyMonthPdf = GroupLetterPdf.Generate(groupLetter, letterSettings with { BodyMonth = "كانون الأول 2025", OutgoingMonth = 11 }, fontBytes, fontBytes, logoBytes);
    using (var monthDocument = UglyToad.PdfPig.PdfDocument.Open(changedBodyMonthPdf))
    {
        var tableText = TransferPreparation.NormalizeAccount(string.Concat(monthDocument.GetPage(2).Letters.Select(l => l.Value)));
        Check(tableText.Contains("2026", StringComparison.Ordinal) && !tableText.Contains("2025", StringComparison.Ordinal), "changing body month does not change group table month");
    }
    var groupOutput = Path.Combine(root, "group-letter.pdf"); File.WriteAllText(groupOutput, "existing");
    try { GroupLetterPdf.SaveDraft(groupLetter, letterSettings with { First = "" }, fontBytes, fontBytes, logoBytes, groupOutput); throw new InvalidOperationException("invalid letter overwrote file"); }
    catch (InvalidDataException) { }
    Check(File.ReadAllText(groupOutput) == "existing", "failed group PDF preserves existing file");
    var changedLetter = groupLetter with { Lines = [groupLetter.Lines.Single() with { Amount = 2501 }], Total = 2501, Words = ArabicAmountWords.Dinars(2501) };
    try { _ = GroupLetterPdf.Generate(changedLetter, letterSettings, fontBytes, fontBytes, logoBytes); throw new InvalidOperationException("letter with mismatched CSV accepted"); }
    catch (InvalidDataException) { }
    var mismatchedCsv = groupLetter with { Csv = Encoding.UTF8.GetBytes(csv.Replace("|2500|", "|2501|", StringComparison.Ordinal)) };
    try { _ = GroupLetterPdf.Generate(mismatchedCsv, letterSettings, fontBytes, fontBytes, logoBytes); throw new InvalidOperationException("letter with changed CSV accepted"); }
    catch (InvalidDataException) { }
    var manyEntries = Enumerable.Range(1, 40).Select(i => entry with { Id = "fake-" + i, Title = "جهة وهمية " + i, Beneficiary = "جهة وهمية " + i }).ToArray();
    var manyPlan = GroupLetterPreparation.Prepare(transfer with { Entries = manyEntries }, TransferGroup.Rafidain, new DateOnly(2026, 10, 31));
    var manyPdf = GroupLetterPdf.Generate(manyPlan, letterSettings, fontBytes, fontBytes, logoBytes);
    Check(System.Text.RegularExpressions.Regex.Matches(Encoding.Latin1.GetString(manyPdf), @"/Type\s*/Page\b").Count == 2, "group letter and many-row table fit on exactly two pages");
    var tooManyEntries = Enumerable.Range(1, 250).Select(i => entry with { Id = "fake-" + i, Title = "جهة وهمية " + i, Beneficiary = "جهة وهمية " + i }).ToArray();
    var tooManyPlan = GroupLetterPreparation.Prepare(transfer with { Entries = tooManyEntries }, TransferGroup.Rafidain, new DateOnly(2026, 10, 31));
    try { _ = GroupLetterPdf.Generate(tooManyPlan, letterSettings, fontBytes, fontBytes, logoBytes); throw new InvalidOperationException("overflowing group table accepted"); }
    catch (InvalidDataException) { }
    var settingsStore = new LocalTransferLinks(Path.Combine(root, "letter-settings"));
    var settingsInitial = settingsStore.Load(snapshot);
    var commonSettings = settingsStore.SaveGroupSettings(snapshot, settingsInitial, TransferGroup.Rafidain, letterSettings, true);
    Check(GroupLetterSettingsReader.Read(commonSettings.Snapshot, TransferGroup.OtherBanks).First == letterSettings.First, "apply letter settings to all books");
    var otherSettings = letterSettings with { First = "اسم آخر تجريبي", Header = "ترويسة أخرى تجريبية" };
    var specificSettings = settingsStore.SaveGroupSettings(snapshot, commonSettings, TransferGroup.OtherBanks, otherSettings, false);
    Check(GroupLetterSettingsReader.Read(specificSettings.Snapshot, TransferGroup.Rafidain).First == letterSettings.First && GroupLetterSettingsReader.Read(specificSettings.Snapshot, TransferGroup.OtherBanks).First == otherSettings.First, "specific letter settings preserve other group");
    var selectionUpdate = settingsStore.SaveGroupSettings(snapshot, specificSettings, TransferGroup.Rafidain, letterSettings with { First = "عضو جديد تجريبي" }, true, new HashSet<string> { "committeeFirst" });
    Check(GroupLetterSettingsReader.Read(selectionUpdate.Snapshot, TransferGroup.OtherBanks).First == "عضو جديد تجريبي" && GroupLetterSettingsReader.Read(selectionUpdate.Snapshot, TransferGroup.OtherBanks).Header == otherSettings.Header, "apply only changed field across books");
    try { _ = settingsStore.SaveGroupSettings(snapshot, selectionUpdate, TransferGroup.Rafidain, letterSettings with { BodyTemplate = "نص ناقص" }, false, new HashSet<string> { "bodyTemplate" }); throw new InvalidOperationException("invalid letter body accepted"); }
    catch (InvalidDataException) { }
    try { _ = settingsStore.SaveGroupSettings(snapshot, selectionUpdate, TransferGroup.Rafidain, letterSettings with { BodyTemplate = GroupLetterPdf.DefaultBodyTemplate }, true, new HashSet<string> { "bodyTemplate" }); throw new InvalidOperationException("group body applied to all individual letters"); }
    catch (InvalidDataException) { }
    var settingsBackupPath = Path.Combine(root, "letter-settings-backup.zip"); ReconciliationBackupExport.Write(specificSettings.Snapshot, [], settingsBackupPath);
    Check(GroupLetterSettingsReader.Read(BackupReader.Read(settingsBackupPath), TransferGroup.OtherBanks).First == otherSettings.First, "letter settings included in backup");
    if (Environment.GetEnvironmentVariable("SALARY_TEST_GROUP_PDF") is { Length: > 0 } groupPath) File.WriteAllBytes(groupPath, groupPdf);
    if (Environment.GetEnvironmentVariable("SALARY_TEST_MAJALLA") is { Length: > 0 } majallaPath)
    {
        var majalla = File.ReadAllBytes(majallaPath); GroupLetterFont.Validate(majalla);
        Check(GroupLetterPdf.Generate(manyPlan, letterSettings, majalla, fontBytes, logoBytes).Length > 1000, "Sakkal group letter with 40 verified table rows");
        var customFont = settingsStore.SaveGroupFont(snapshot, selectionUpdate, majalla);
        Check(GroupLetterFont.Read(settingsStore.Load(snapshot).Snapshot)!.AsSpan().SequenceEqual(majalla), "Sakkal font persists with settings");
        var afterFontSettings = settingsStore.SaveGroupSettings(snapshot, customFont, TransferGroup.Rafidain, letterSettings with { First = "عضو خط تجريبي" }, false, new HashSet<string> { "committeeFirst" });
        var fontBackup = Path.Combine(root, "font-backup.zip"); ReconciliationBackupExport.Write(afterFontSettings.Snapshot, [], fontBackup);
        Check(GroupLetterFont.Read(BackupReader.Read(fontBackup))!.AsSpan().SequenceEqual(majalla), "Sakkal font is preserved after settings change and backup");
        if (Environment.GetEnvironmentVariable("SALARY_TEST_MAJALLA_PDF") is { Length: > 0 } majallaPdf) File.WriteAllBytes(majallaPdf, GroupLetterPdf.Generate(groupLetter, GroupLetterSettingsReader.Read(customFont.Snapshot, TransferGroup.Rafidain), majalla, fontBytes, logoBytes));
    }
    try { _ = GroupLetterPreparation.Prepare(transfer with { Entries = [entry with { MissingAmounts = 1 }] }, TransferGroup.Rafidain, new DateOnly(2026, 10, 31)); throw new InvalidOperationException("invalid group letter accepted"); }
    catch (InvalidDataException) { }
    Check(!bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) && !csv.EndsWith('\n') &&
          csv.Contains("20261031300001|20261031|دافع وهمي|IQ06TEST123000000000001|2500|IQD|TESTIQBAAAA|جهة تحويل وهمية|IQ76TEST123000000000002|استقطاعات أيلول 2026|SLEV", StringComparison.Ordinal), "Rafidain CSV format");
    var localDirectory = Path.Combine(root, "local-data");
    var local = new LocalSnapshotStore(localDirectory);
    Check(local.LoadCurrent() is null, "empty local store");
    var localSnapshot = local.Import(valid);
    Check(localSnapshot.SourcePath != snapshot.SourcePath && local.LoadCurrent()?.SourceDigest == snapshot.SourceDigest, "local validated import");
    try { _ = local.Import(valid, expectedDigest: new string('A', 64)); throw new InvalidOperationException("changed reviewed backup accepted"); }
    catch (InvalidDataException) { }
    Check(local.LoadCurrent()?.SourceDigest == snapshot.SourceDigest, "mismatched reviewed backup preserves current pointer");
    try { _ = local.Import(wrong); throw new InvalidOperationException("invalid local import accepted"); }
    catch (InvalidDataException) { }
    Check(local.LoadCurrent()?.SourceDigest == snapshot.SourceDigest && before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(valid))), "failed local import preserves previous data and source");
    var brokenLedgerBackup = Path.Combine(root, "broken-ledger.zip"); MakeBackup(brokenLedgerBackup, brokenLedger: true);
    try { _ = local.Import(brokenLedgerBackup); throw new InvalidOperationException("incompatible ledger accepted"); }
    catch (InvalidDataException) { }
    Check(local.LoadCurrent()?.SourceDigest == snapshot.SourceDigest, "incompatible ledger preserves previous local snapshot");
    using (var held = new FileStream(Path.Combine(localDirectory, ".store.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        try { _ = local.Import(valid); throw new InvalidOperationException("concurrent store write accepted"); }
        catch (IOException) { }
    }
    Check(local.LoadCurrent()?.SourceDigest == snapshot.SourceDigest, "concurrent store update preserves current snapshot");
    var older = Path.Combine(root, "older.zip"); MakeBackup(older, omitRecentMonth: true);
    try { _ = local.Import(older); throw new InvalidOperationException("missing month replaced local backup"); }
    catch (InvalidDataException) { }
    Check(local.LoadCurrent()?.SourceDigest == snapshot.SourceDigest, "missing month preserves current snapshot");
    var newer = Path.Combine(root, "newer.zip"); File.Copy(valid, newer);
    using (var changedCopy = new FileStream(newer, FileMode.Append, FileAccess.Write)) changedCopy.WriteByte(0);
    var newerSnapshot = local.Import(newer);
    Check(newerSnapshot.SourceDigest != snapshot.SourceDigest && File.Exists(localSnapshot.SourcePath), "local import preserves previous snapshot");
    try { _ = local.Import(valid, new CancellationToken(true)); throw new InvalidOperationException("cancelled local import accepted"); }
    catch (OperationCanceledException) { }
    Check(local.LoadCurrent()?.SourceDigest == newerSnapshot.SourceDigest, "cancelled local import preserves current snapshot");
    var exportedBackup = Path.Combine(root, "exported.zip");
    LocalSnapshotStore.ExportOriginal(localSnapshot, exportedBackup);
    Check(SHA256.HashData(File.ReadAllBytes(exportedBackup)).SequenceEqual(before), "backup export preserves full original bytes");
    try { LocalSnapshotStore.ExportOriginal(localSnapshot, localSnapshot.SourcePath); throw new InvalidOperationException("local source overwritten"); }
    catch (InvalidDataException) { }
    var transferOutput = Path.Combine(root, "transfer.csv");
    File.WriteAllText(transferOutput, "existing");
    try { TransferFileSave.Write(snapshot, transfer with { SourceWarnings = new[] { "تحذير وهمي" } }, TransferGroup.Rafidain, new DateOnly(2026, 10, 31), transferOutput); throw new InvalidOperationException("unsafe transfer saved"); }
    catch (InvalidDataException) { }
    Check(File.ReadAllText(transferOutput) == "existing", "failed transfer preserves existing file");
    TransferFileSave.Write(snapshot, transfer, TransferGroup.Rafidain, new DateOnly(2026, 10, 31), transferOutput);
    Check(File.ReadAllBytes(transferOutput).AsSpan().SequenceEqual(bytes), "validated transfer file saved");
    var changedCsv = Encoding.UTF8.GetBytes(csv.Replace("|2500|", "|2501|", StringComparison.Ordinal));
    try { TransferPreparation.VerifyCsv(changedCsv, transfer, TransferGroup.Rafidain, new DateOnly(2026, 10, 31)); throw new InvalidOperationException("altered transfer file accepted"); }
    catch (InvalidDataException) { }
    var transferSettings = new Dictionary<string, byte[]> {
        ["bankStatementAccountName"] = Convert.FromBase64String("YnBsaXN0MDBpBi8GJwZBBjkAIAZIBkcGRQZKCAAAAAAAAAEBAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAAb"),
        ["bankStatementIBAN"] = Convert.FromBase64String("YnBsaXN0MDBfEBdJUTA2VEVTVDEyMzAwMDAwMDAwMDAwMQgAAAAAAAABAQAAAAAAAAABAAAAAAAAAAAAAAAAAAAAIg=="),
        ["transferPayrollAccountLinks"] = Convert.FromBase64String("YnBsaXN0MDBPEDN7ImRlZHVjdGlvbnzYrNmH2Ycg2YjZh9mF2YrZhyI6Itit2LPYp9ioINmI2YfZhdmKIn0IAAAAAAAAAQEAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAD4=")
    };
    var linkedSnapshot = snapshot with { Accounts = new[] { transferAccount with { Entity = "حساب وهمي" } }, SettingsPlists = transferSettings };
    var prepared = TransferPreparation.Prepare(linkedSnapshot, "أيلول 2026");
    Check(prepared.Payer == "دافع وهمي" && prepared.PayerAccount == fakePayer && prepared.Entries.First(e => e.Title == "جهة وهمية").Account?.Entity == "حساب وهمي", "restore payer and account alias from binary property lists");
    var fullyMatchedSnapshot = snapshot with {
        Employees = new[] { contractPerson with { Items = null, RawColumns = rawColumns } },
        Deductions = new[] { snapshot.Deductions[0] },
        Accounts = new[] { transferAccount with { Entity = "حساب وهمي" } }, SettingsPlists = transferSettings
    };
    var fullyPrepared = TransferPreparation.Prepare(fullyMatchedSnapshot, "أيلول 2026");
    Check(fullyPrepared.SourceWarnings.Count == 0 && fullyPrepared.Entries.Count == 1 && fullyPrepared.Entries[0].Group == TransferGroup.Rafidain,
        "complete source snapshot resolves grouped transfer");
    var alternativeAccount = transferAccount with { Entity = "حساب بديل وهمي", Branch = "مصرف الرشيد فرع وهمي" };
    var sourceForLinks = fullyMatchedSnapshot with { Accounts = [fullyMatchedSnapshot.Accounts[0], alternativeAccount] };
    var linkStore = new LocalTransferLinks(Path.Combine(root, "links"));
    var initialLinks = linkStore.Load(sourceForLinks);
    var linkEntry = TransferPreparation.Prepare(initialLinks.Snapshot, "أيلول 2026").Entries.Single();
    var updatedLinks = linkStore.SaveLink(sourceForLinks, initialLinks, linkEntry, alternativeAccount);
    Check(TransferPreparation.Prepare(updatedLinks.Snapshot, "أيلول 2026").Entries.Single().Group == TransferGroup.OtherBanks &&
          TransferPreparation.Prepare(linkStore.Load(sourceForLinks).Snapshot, "أيلول 2026").Entries.Single().Account?.Entity == alternativeAccount.Entity,
          "saved alias updates transfer group and survives reload");
    try { _ = linkStore.SaveLink(sourceForLinks, initialLinks, linkEntry, alternativeAccount); throw new InvalidOperationException("stale link update accepted"); }
    catch (InvalidDataException) { }
    Check(linkStore.Load(sourceForLinks).Revision == updatedLinks.Revision && TransferPreparation.Prepare(sourceForLinks, "أيلول 2026").Entries.Single().Group == TransferGroup.Rafidain, "stale update preserves link and original backup");
    var selectedEntry = TransferPreparation.Prepare(updatedLinks.Snapshot, "أيلول 2026").Entries.Single();
    var categorized = linkStore.SaveCategory(sourceForLinks, updatedLinks, selectedEntry, TransferGroup.Rafidain);
    var mismatchedEntry = TransferPreparation.Prepare(categorized.Snapshot, "أيلول 2026").Entries.Single();
    Check(mismatchedEntry.Group == TransferGroup.Rafidain && TransferPreparation.Issues(mismatchedEntry).Any(i => i.Contains("المجموعة", StringComparison.Ordinal)) && linkStore.Load(sourceForLinks).Categories.Count == 1,
        "saved bank classification warns when it conflicts with account");
    try { _ = linkStore.SaveCategory(sourceForLinks, updatedLinks, selectedEntry, TransferGroup.Anbar); throw new InvalidOperationException("stale category update accepted"); }
    catch (InvalidDataException) { }
    Check(linkStore.Load(sourceForLinks).Revision == categorized.Revision, "stale category update preserves saved classification");
    var legacyDir = Path.Combine(root, "legacy-links"); Directory.CreateDirectory(legacyDir);
    using var categoryPlist = new MemoryStream();
    PropertyListParser.SaveAsBinary(new NSData(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["مفتاح وهمي"] = "anbar" })), categoryPlist);
    var legacySettings = sourceForLinks.SettingsPlists.ToDictionary(p => p.Key, p => p.Value);
    legacySettings["transferEntityCategories"] = categoryPlist.ToArray();
    var legacySource = sourceForLinks with { SettingsPlists = legacySettings };
    File.WriteAllText(Path.Combine(legacyDir, legacySource.SourceDigest + ".json"), JsonSerializer.Serialize(new { Version = 1, legacySource.SourceDigest, Links = updatedLinks.Links }));
    var legacyStore = new LocalTransferLinks(legacyDir); var legacy = legacyStore.Load(legacySource);
    var legacyEntry = TransferPreparation.Prepare(legacy.Snapshot, "أيلول 2026").Entries.Single();
    var migrated = legacyStore.SaveLink(legacySource, legacy, legacyEntry, alternativeAccount);
    Check(migrated.Categories.GetValueOrDefault("مفتاح وهمي") == "anbar" && legacyStore.Load(legacySource).Categories.GetValueOrDefault("مفتاح وهمي") == "anbar", "legacy links preserve classifications during migration");
    var payrollLinkSource = sourceForLinks with { Employees = [contractPerson with { RawColumns = rawColumns }] };
    var payrollLinkStore = new LocalTransferLinks(Path.Combine(root, "payroll-links"));
    var payrollLinks = payrollLinkStore.Load(payrollLinkSource);
    var healthEntry = TransferPreparation.Prepare(payrollLinks.Snapshot, "أيلول 2026").Entries.Single(e => e.Id == "payroll|health");
    var healthLinked = payrollLinkStore.SaveLink(payrollLinkSource, payrollLinks, healthEntry, alternativeAccount);
    Check(TransferPreparation.Prepare(healthLinked.Snapshot, "أيلول 2026").Entries.Single(e => e.Id == "payroll|health").Account?.Entity == alternativeAccount.Entity && healthLinked.Links.ContainsKey("health"), "payroll recipient links to saved account");
    var linkedBackupPath = Path.Combine(root, "linked-backup.zip");
    ReconciliationBackupExport.Write(updatedLinks.Snapshot with { Accounts = snapshot.Accounts }, [], linkedBackupPath);
    var exportedLinksPlist = (NSData)PropertyListParser.Parse(BackupReader.Read(linkedBackupPath).SettingsPlists["transferPayrollAccountLinks"]);
    Check(JsonSerializer.Deserialize<Dictionary<string, string>>(exportedLinksPlist.Bytes)!["deduction|جهه وهميه"] == PayrollAnalysis.Normalize(alternativeAccount.Entity), "backup includes changed account aliases");
    var realLinkStore = new LocalTransferLinks(Path.Combine(root, "real-fixture-links"));
    var realLinkInitial = realLinkStore.Load(snapshot);
    var actualFixtureEntry = TransferPreparation.Prepare(snapshot, "أيلول 2026").Entries.First(e => e.Title == "جهة وهمية");
    var actualFixtureLink = realLinkStore.SaveLink(snapshot, realLinkInitial, actualFixtureEntry, snapshot.Accounts.Single());
    var linkedFixtureEntry = TransferPreparation.Prepare(actualFixtureLink.Snapshot, "أيلول 2026").Entries.First(e => e.Title == "جهة وهمية");
    var classifiedFixture = realLinkStore.SaveCategory(snapshot, actualFixtureLink, linkedFixtureEntry, TransferGroup.Rafidain);
    var completeBackupPath = Path.Combine(root, "complete-fixture.zip");
    ReconciliationBackupExport.Write(classifiedFixture.Snapshot, [period], completeBackupPath);
    var completeRestored = BackupReader.Read(completeBackupPath);
    Check(TransferPreparation.Prepare(completeRestored, "أيلول 2026").Entries.First(e => e.Title == "جهة وهمية") is { Group: TransferGroup.Rafidain, Account.Entity: "جهة وهمية" } &&
          BankReconciliation.ReadFromBackup(completeRestored).Single().Id == period.Id && completeRestored.Employees.Count == snapshot.Employees.Count,
          "backup restores account alias, classification and reconciliation with original records");
    var accountStore = new LocalTransferLinks(Path.Combine(root, "account-edit")); var initialAccountState = accountStore.Load(snapshot);
    var changedAccount = snapshot.Accounts.Single() with { Iban = fakeReceiver, Swift = "TESTIQBAAAA", Branch = "الرافدين فرع وهمي" };
    var editedAccountState = accountStore.SaveAccount(snapshot, initialAccountState, snapshot.Accounts.Single(), changedAccount);
    Check(accountStore.Load(snapshot).Snapshot.Accounts.Single().Iban == fakeReceiver && snapshot.Accounts.Single().Iban != fakeReceiver, "account edits persist locally without changing backup");
    try { _ = accountStore.SaveAccount(snapshot, initialAccountState, snapshot.Accounts.Single(), snapshot.Accounts.Single() with { Branch = "فرع قديم وهمي" }); throw new InvalidOperationException("stale inline edit accepted"); }
    catch (InvalidDataException) { }
    Check(accountStore.Load(snapshot).Revision == editedAccountState.Revision && accountStore.Load(snapshot).Snapshot.Accounts.Single() == changedAccount, "stale edit cannot overwrite a newer account or partially change it");
    Check(TransferPreparation.Prepare(editedAccountState.Snapshot, "أيلول 2026").Entries.First(e => e.Title == "جهة وهمية") is { Group: TransferGroup.Rafidain, Account.Iban: fakeReceiver }, "edited account updates transfer classification");
    try { _ = accountStore.SaveAccount(snapshot, editedAccountState, changedAccount, changedAccount with { Iban = "IQ00" }); throw new InvalidOperationException("invalid account accepted"); }
    catch (InvalidDataException) { }
    Check(accountStore.Load(snapshot).Revision == editedAccountState.Revision, "invalid account edit preserves previous state");
    try { _ = accountStore.SaveAccount(snapshot, editedAccountState, changedAccount, changedAccount with { Swift = "غير صالح" }); throw new InvalidOperationException("invalid swift accepted"); }
    catch (InvalidDataException) { }
    var accountBackupPath = Path.Combine(root, "account-edited.zip");
    ReconciliationBackupExport.Write(editedAccountState.Snapshot, [period], accountBackupPath);
    var accountBackup = BackupReader.Read(accountBackupPath);
    Check(accountBackup.Accounts.Single() == changedAccount && accountBackup.Employees.Count == snapshot.Employees.Count && BankReconciliation.ReadFromBackup(accountBackup).Single().Id == period.Id, "edited account and reconciliation restore from backup");
    var suggestions = ReconciliationSuggestions.Prepare(fullyMatchedSnapshot, "أيلول 2026");
    var groupSuggestion = suggestions.Items.Single(s => s.Id.EndsWith("|group|rafidain", StringComparison.Ordinal));
    Check(groupSuggestion.Amount == 2500 && groupSuggestion.SourceLineIds.Single() == "deductions|أيلول 2026|false|جهة وهمية|execution" && suggestions.Items.Any(s => s.Id.EndsWith("|cash", StringComparison.Ordinal) && s.Amount == 500), "suggestions from matched payroll and group sources");
    var withSuggestion = ReconciliationSuggestions.Add(period with { Movements = [funding] }, groupSuggestion, period.End);
    Check(withSuggestion.Movements[^1].SourceId == groupSuggestion.Id && !withSuggestion.Movements[^1].Matched && ReconciliationSuggestions.Changed(withSuggestion, suggestions).Count == 0, "suggested transfer requires manual confirmation");
    try { _ = ReconciliationSuggestions.Add(withSuggestion, groupSuggestion, period.End); throw new InvalidOperationException("duplicate suggestion accepted"); }
    catch (InvalidDataException) { }
    try { _ = ReconciliationSuggestions.Add(withSuggestion, groupSuggestion with { Id = "different" }, period.End); throw new InvalidOperationException("overlapping suggestion source accepted"); }
    catch (InvalidDataException) { }
    try { _ = ReconciliationSuggestions.Add(period, groupSuggestion, period.End.AddDays(1)); throw new InvalidOperationException("out-of-period suggestion accepted"); }
    catch (InvalidDataException) { }
    var changedSuggestions = suggestions with { Items = suggestions.Items.Select(s => s.Id == groupSuggestion.Id ? s with { Amount = s.Amount + 1 } : s).ToArray() };
    Check(ReconciliationSuggestions.Changed(withSuggestion, changedSuggestions).Count == 1, "changed source is reported");
    Check(ReconciliationSuggestions.Changed(withSuggestion, suggestions with { Items = [] }).Count == 1, "missing source is reported");
    var incompleteSuggestions = ReconciliationSuggestions.Prepare(snapshot, "أيلول 2026");
    Check(!incompleteSuggestions.Items.Any(s => s.Id.EndsWith("|group|rafidain", StringComparison.Ordinal) || s.Id.EndsWith("|card", StringComparison.Ordinal)) && incompleteSuggestions.Warnings.Count > 0, "incomplete payroll and transfer are not suggested");
    var preparedCsv = TransferPreparation.MakeCsv(fullyPrepared, TransferGroup.Rafidain, new DateOnly(2026, 10, 31));
    Check(Encoding.UTF8.GetString(preparedCsv).Contains("20261031300001|20261031|دافع وهمي", StringComparison.Ordinal), "source-to-transfer integration");
    var otherTransfer = transfer with { Entries = new[] { entry with { Group = TransferGroup.OtherBanks, Account = transferAccount with { Branch = "الرشيد فرع وهمي" } } } };
    var otherCsv = Encoding.UTF8.GetString(TransferPreparation.MakeCsv(otherTransfer, TransferGroup.OtherBanks, new DateOnly(2026, 10, 31)));
    Check(otherCsv.Contains("20261031200001|20261031", StringComparison.Ordinal), "other banks CSV reference");
    try { _ = TransferPreparation.MakeCsv(transfer with { Entries = new[] { entry with { MissingAmounts = 1 } } }, TransferGroup.Rafidain, new DateOnly(2026, 10, 31)); throw new InvalidOperationException("missing transfer amount accepted"); }
    catch (InvalidDataException) { }
    try { _ = TransferPreparation.MakeCsv(transfer with { Entries = new[] { entry, entry with { Id = "other", Group = TransferGroup.Unresolved } } }, TransferGroup.Rafidain, new DateOnly(2026, 10, 31)); throw new InvalidOperationException("unclassified transfer accepted"); }
    catch (InvalidDataException) { }
    var groups = PayrollAnalysis.Groups(snapshot, "أيلول 2026");
    Check(groups.Count == 1 && groups[0].People == 1 && groups[0].Total == 2500m && groups[0].MissingAmounts == 1, "deduction grouping and inactive status");
    Check(PayrollAnalysis.Normalize("أحمد كَريم") == PayrollAnalysis.Normalize("احمد كريم"), "Arabic search normalization");
    using (var editedFile = new FileStream(valid, FileMode.Append, FileAccess.Write)) editedFile.WriteByte(0);
    try { TransferFileSave.Write(snapshot, transfer, TransferGroup.Rafidain, new DateOnly(2026, 10, 31), transferOutput); throw new InvalidOperationException("changed transfer source accepted"); }
    catch (InvalidDataException) { }
    Check(File.ReadAllBytes(transferOutput).AsSpan().SequenceEqual(bytes), "changed source preserves previous transfer");
    try { BackupReader.CopyBook(snapshot, snapshot.Books[0], Path.Combine(root, "changed.pdf")); throw new InvalidOperationException("changed backup accepted"); }
    catch (InvalidDataException) { }
    Reject(wrong, "count mismatch accepted"); Reject(duplicate, "duplicate entry accepted"); Reject(missing, "missing archive document accepted");
    {
        // الكشوف المعتمدة وملف المنتسب ببيانات وهمية.
        string[] reportRanks = ["ملازم", "موظف د3", "شرطي", "عقود", "نقيب"];
        var reportEmployees = new List<Employee>();
        foreach (var reportMonth in new[] { "أيلول 2026", "آب 2026" })
            for (int i = 0; i < 30; i++)
            {
                var items = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, decimal> { ["صندوق الشهداء"] = i % 5 == 3 ? 2000 : 4000, ["الرعاية الصحية"] = i == 7 ? 5000 : 3500 * (1 + i % 3), ["شركة عراق الجوال"] = i % 2 == 0 ? 25000 : 50000 });
                var details = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["الاسمي"] = "500000", ["الاستحقاق"] = reportMonth == "آب 2026" ? "790000" : "800000", ["مجموع الاستقطاعات"] = "100000", ["IBAN"] = "IQ00TEST000000000000000", ["طريقة الدفع"] = "بطاقة" });
                reportEmployees.Add(new Employee(reportMonth, $"منتسب وهمي {i}", reportRanks[i % 5], "قسم وهمي", (reportMonth == "آب 2026" ? 690000m : 700000m) + i, items, details, null));
            }
        var reportDeductions = Enumerable.Range(0, 12).Select(i => new Deduction("أيلول 2026", $"منتسب وهمي {i}", i % 2 == 0 ? "جهة وهمية أولى" : "جهة وهمية ثانية", 10000m + i * 500, $"اضبارة {i}", "")).ToList();
        reportDeductions.Add(new Deduction("أيلول 2026", "منتسب وهمي 20", "جهة وهمية أولى", null, "⟦حالة الاستقطاع:موقوف⟧", ""));
        var reportSnapshot = new BackupSnapshot(Path.Combine(root, "fictional-report.zip"), new string('A', 64), 5,
            [new PayrollMonth("أيلول 2026", 30, reportDeductions.Count), new PayrollMonth("آب 2026", 30, 0)], reportEmployees, reportDeductions, 0, [], [], new Dictionary<string, byte[]>());
        var reportMonthSummary = PayrollAnalysis.Summarize(reportSnapshot, "أيلول 2026");
        Check(reportMonthSummary.Deductions == 12 && reportMonthSummary.DeductionTotal == 153000m && reportMonthSummary.Entities == 2, "home deduction total and entity count exclude inactive entries");
        var entityReport = StandardReports.EntityDeductions(reportSnapshot, "أيلول 2026", "جهة وهمية أولى");
        Check(entityReport.Rows.Count == 6 && entityReport.Total == reportDeductions.Where(d => d.Title == "جهة وهمية أولى" && d.Amount is not null).Sum(d => d.Amount!.Value) && entityReport.Rows[0][1] == "ملازم" && entityReport.Title.Contains("٢٠٢٦", StringComparison.Ordinal), "entityReport list excludes inactive entries and takes payroll rank");
        var entityXlsx = Path.Combine(root, "entity.xlsx"); ReportFiles.Save(XlsxReport.Generate(entityReport), entityXlsx, ".xlsx");
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        using (var stream = File.OpenRead(entityXlsx)) using (var reader = ExcelDataReader.ExcelReaderFactory.CreateOpenXmlReader(stream))
        {
            reader.Read(); Check(Convert.ToString(reader.GetValue(0)) == ReportSheet.DefaultHeader, "Excel header");
            reader.Read(); Check(Convert.ToString(reader.GetValue(0)) == entityReport.Title, "Excel title");
            reader.Read(); Check(Convert.ToString(reader.GetValue(2)) == "الاسم الكامل", "Excel columns");
            reader.Read(); Check(Convert.ToDouble(reader.GetValue(0)) == 1 && Convert.ToString(reader.GetValue(2)) == "منتسب وهمي 0" && Convert.ToDecimal(reader.GetValue(3)) == 10000m, "Excel first row");
        }
        var reportAssets = ReportAssets.Load(Path.GetFullPath("SalaryApp/Assets.xcassets/WatermarkLogo.imageset/WatermarkLogo.png"));
        var stamp = new DateTime(2026, 11, 1, 10, 30, 0);
        var entityPdf = ReportPdf.Generate([entityReport], reportAssets, stamp);
        using (var read = UglyToad.PdfPig.PdfDocument.Open(entityPdf)) Check(read.NumberOfPages == 1 && read.GetPage(1).Width < read.GetPage(1).Height, "entity PDF pages use portrait A4");
        var longEntityRows = Enumerable.Range(0, 60).Select(i => new[] { $"{i + 1}", "موظف", $"منتسب وهمي طويل الاسم للتأكد من وضوح الاسم الكامل رقم {i}", "", "ملاحظة وهمية طويلة للتحقق من التفاف رقم وتاريخ الاضبارة" }).ToArray();
        var longEntity = (entityReport with { Rows = longEntityRows, Amounts = Enumerable.Repeat(1000m, longEntityRows.Length).ToArray() }).Fitted();
        Check(!longEntity.Landscape && longEntity.Columns.All(c => c.Title != "جهة التنفيذ"), "entity export stays portrait without repeated entity column");
        using (var read = UglyToad.PdfPig.PdfDocument.Open(ReportPdf.Generate([longEntity], reportAssets, stamp)))
            Check(read.NumberOfPages > 1 && Enumerable.Range(1, read.NumberOfPages).All(i => read.GetPage(i).Width < read.GetPage(i).Height), "long entity PDF wraps across portrait pages");
        using (var zip = new ZipArchive(new MemoryStream(XlsxReport.Generate(longEntity)), ZipArchiveMode.Read))
        using (var xml = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()))
        {
            var sheetXml = xml.ReadToEnd();
            Check(sheetXml.Contains("orientation=\"portrait\"") && sheetXml.Contains("منتسب وهمي طويل الاسم") && !sheetXml.Contains("جهة التنفيذ"), "entity Excel retains full names and portrait setup");
        }
        var longSheet = new ReportSheet(ReportSheet.DefaultHeader, "كشف وهمي طويل", [new("ت", 5, ReportColumnKind.Serial), new("الاسم الكامل", 34, ReportColumnKind.Text), new("المبلغ", 14, ReportColumnKind.Amount)],
            Enumerable.Range(0, 120).Select(i => new[] { "", $"منتسب وهمي طويل الاسم جداً لاختبار الاختصار رقم {i}", "" }).ToArray(), Enumerable.Range(0, 120).Select(i => 1000m * i).ToArray()).Fitted();
        using (var read = UglyToad.PdfPig.PdfDocument.Open(ReportPdf.Generate([longSheet, entityReport], reportAssets, stamp))) Check(read.NumberOfPages >= 4, "multi-page PDF with several sheets");
        var fund = FundReport.Summary(reportSnapshot, "أيلول 2026", "صندوق الشهداء");
        Check(fund[0].Count == 12 && fund[1].Count == 6 && fund[2].Count == 6 && fund[3].Count == 6 && fund[3].Total == 12000m, "fund categories by rank");
        Check(FundReport.Excel(fund, FundReport.Title(reportSnapshot, "أيلول 2026")).Length > 500 && FundReport.Pdf(fund, FundReport.Title(reportSnapshot, "أيلول 2026"), reportAssets, stamp).Length > 500, "fund files");
        var food = FoodContractReport.Rows(reportSnapshot, "أيلول 2026", "شركة عراق الجوال");
        var split = new FoodContractReport.Split(food.Sum(r => r.Total));
        Check(food.Count == 2 && split.Total == 1125000m && split.Fund == 33750m && split.Tax == Math.Round((split.Total - split.Fund) * 0.018m, MidpointRounding.AwayFromZero) && split.Company == split.Total - split.Fund - split.Tax, "food contract split");
        Check(FoodContractReport.Excel(food, FoodContractReport.Title(reportSnapshot, "أيلول 2026")).Length > 500 && FoodContractReport.Pdf(food, FoodContractReport.Title(reportSnapshot, "أيلول 2026"), reportAssets, stamp).Length > 500, "food files");
        var health = HealthCareReport.Build(reportSnapshot, "أيلول 2026", "الرعاية الصحية");
        Check(health.Subscribers.Count == 30 && health.Irregular.Count == 1 && health.Others.Count == 0 && HealthCareReport.FamilyText(5000) == "1.43" && RankOrder.Rank(health.Subscribers[0].Rank) <= RankOrder.Rank(health.Subscribers[^1].Rank), "health lists, irregular amount and rank order");
        Check(HealthCareReport.Excel(health, "عنوان وهمي", "عنوان وهمي آخر").Length > 500 && HealthCareReport.Pdf(health, "عنوان وهمي", "عنوان وهمي آخر", reportAssets, stamp).Length > 500, "health files");
        var person = PersonProfiles.Build(reportSnapshot, "منتسب  وهمي 2", null);
        Check(person.Months.Count == 2 && person.Months[0].Month == "أيلول 2026" && person.Months[0].Deductions.Count == 1, "person profile months");
        var slip = PersonProfiles.Payslip(person.Months[0]);
        Check(slip.Net == 700002m && slip.Gross?.Amount == 800000m && slip.Allowances.Any(l => l.Title == "الاسمي") && slip.EntityDeductions.Count == 1 && slip.Info.Any(l => l.Title == "IBAN"), "payslip sections");
        var changes = PersonProfiles.Changes(person, "أيلول 2026");
        Check(changes.Any(c => c.Title == "صافي الراتب" && c.Difference == "+10,000") && changes.Any(c => c.Title.StartsWith("استقطاع جديد", StringComparison.Ordinal)), "month changes");
        Check(PersonProfiles.SearchIndex(reportSnapshot, null).Count == 30, "search index keeps one entry per person");
        var payslipPdf = PayslipPdf.Generate(ReportSheet.DefaultHeader, person.Name, "ملازم", "قسم وهمي", "أيلول 2026", slip, reportAssets, stamp);
        using (var read = UglyToad.PdfPig.PdfDocument.Open(payslipPdf)) Check(read.NumberOfPages == 1, "payslip PDF pages");
        var salaryReport = SalaryReports.Make(person.Months[0], false, false);
        Check(salaryReport.Net == 700002m && salaryReport.EntitlementTotal == 800000m && salaryReport.Entitlements.Count == 16 && salaryReport.Deductions.Count == 16 && salaryReport.Banks.Count == 1 && !salaryReport.MissingDetails, "salary report sections follow the iPhone order");
        var hiddenReport = SalaryReports.Make(person.Months[0], true, true);
        Check(hiddenReport.Entitlements.All(l => l.Value != 0) && hiddenReport.Info.First(i => i.Label == "IBAN").Value.StartsWith("•••• ", StringComparison.Ordinal), "salary report hides zero items and personal numbers");
        var salaryReportPdf = SalaryReportPdf.Generate([salaryReport], reportAssets, null, stamp);
        using (var read = UglyToad.PdfPig.PdfDocument.Open(salaryReportPdf)) Check(read.NumberOfPages == 1, "salary report PDF is one A4 page");
        try { _ = SalaryReports.Make(person.Months[0] with { Employees = [] }, false, false); throw new InvalidOperationException("salary report without payroll accepted"); }
        catch (InvalidDataException) { }
        // كشف استقطاعات موظف لجهة واحدة عبر الأشهر (بيانات وهمية): شهر بقيد، شهر بلا ملف استقطاعات، وشهر بحالة «موقوف».
        var statementDeductions = new List<Deduction>
        {
            new("تموز 2026", "منتسب تجريبي", "جهة أ", 15000m, "", "عريف"), new("تموز 2026", "شخص آخر", "جهة أ", 1000m, "", ""),
            new("أيلول 2026", "منتسب  تجريبي", "جهة أ", null, "⟦حالة الاستقطاع:موقوف⟧", ""), new("أيلول 2026", "منتسب تجريبي", "جهة ب", 2000m, "", ""),
        };
        var statementSnapshot = new BackupSnapshot(Path.Combine(root, "fictional-statement.zip"), new string('B', 64), 5,
            [new PayrollMonth("تموز 2026", 1, 2), new PayrollMonth("آب 2026", 1, 0), new PayrollMonth("أيلول 2026", 1, 2)],
            [new Employee("أيلول 2026", "منتسب تجريبي", "عريف", "قسم وهمي", 500000m, null, null, null)], statementDeductions, 0, [], [], new Dictionary<string, byte[]>());
        var statementPerson = EmployeeDeductions.Search(EmployeeDeductions.People(statementSnapshot), "منتسب تجريبي").Single();
        Check(EmployeeDeductions.Entities(statementSnapshot, statementPerson.Key).SequenceEqual(["جهة أ", "جهة ب"]), "deduction statement entities");
        Check(EmployeeDeductions.AvailableMonths(statementSnapshot, statementPerson.Key, "جهة أ").Keys.SequenceEqual([202607, 202609]), "deduction statement available months");
        var statement = EmployeeDeductions.Statement(statementSnapshot, statementPerson, "جهة أ", 202607, 202609);
        Check(statement.Rows.Count == 3 && statement.Rows[0].Amount == 15000m && statement.Rows[1].IsUnavailable && statement.Rows[2].Kind == DeductionPeriodKind.Status && statement.Rows[2].Statuses![0] == "موقوف"
            && statement.Total == 15000m && statement.Incomplete && statement.Rank == "عريف", "deduction statement rows, statuses and unavailable months");
        var statementPdf = EmployeeDeductionPdf.Generate(statement, reportAssets, null, stamp);
        using (var read = UglyToad.PdfPig.PdfDocument.Open(statementPdf)) Check(read.NumberOfPages == 1, "deduction statement PDF is one page for one year");
        var longStatement = EmployeeDeductions.Statement(statementSnapshot, statementPerson, "جهة أ", 202301, 202609);
        using (var read = UglyToad.PdfPig.PdfDocument.Open(EmployeeDeductionPdf.Generate(longStatement, reportAssets, null, stamp))) Check(read.NumberOfPages == 2, "deduction statement fits three years per page");
        if (Environment.GetEnvironmentVariable("SALARY_TEST_INDIVIDUAL_DIR") is { Length: > 0 } statementDir)
        { Directory.CreateDirectory(statementDir); File.WriteAllBytes(Path.Combine(statementDir, "employee-deductions.pdf"), EmployeeDeductionPdf.Generate(longStatement, reportAssets, File.ReadAllBytes(Path.GetFullPath("SalaryApp/Assets.xcassets/HomeLogo.imageset/HomeLogo.png")), stamp)); }
        try { _ = EmployeeDeductions.Statement(statementSnapshot with { Deductions = [.. statementDeductions, new Deduction("تموز 2026", "منتسب تجريبي", "جهة أ", -5m, "", "")] }, statementPerson, "جهة أ", 202607, 202609); throw new InvalidOperationException("negative deduction accepted"); }
        catch (InvalidDataException) { }
        var payrollRows = PayrollRows.Build(reportEmployees.Where(e => e.Month == "أيلول 2026"));
        Check(payrollRows.Length == 30 && payrollRows[0].Payment == "بطاقة" && payrollRows[0].Gross == 800000m && payrollRows[0].TotalDeductions == 100000m && payrollRows[0].Iban.StartsWith("IQ00", StringComparison.Ordinal), "payroll rows read detail fields");
        var brokenRow = PayrollRows.Row(reportEmployees[0] with { Details = Encoding.UTF8.GetBytes("{broken") });
        Check(brokenRow.Payment.Length == 0 && brokenRow.Net == reportEmployees[0].Net, "broken details keep the payroll row");
        var shownTable = TableReport.Build(ReportSheet.DefaultHeader, "جدول وهمي", ["الاسم", "المبلغ", "الملاحظة"], 1, [["أ", "", "x"], ["ب", "", ""], ["ج", "", "موقوف"]], [1000m, 2500.5m, null]);
        Check(shownTable.Columns.Count == 4 && shownTable.Total == 3500.5m && shownTable.Rows[2][2] == "—" && shownTable.Rows[1][0] == "2", "displayed table report");
        var shownXlsx = Path.Combine(root, "shown.xlsx"); ReportFiles.Save(XlsxReport.Generate(shownTable), shownXlsx, ".xlsx");
        using (var stream = File.OpenRead(shownXlsx)) using (var reader = ExcelDataReader.ExcelReaderFactory.CreateOpenXmlReader(stream))
        {
            reader.Read(); reader.Read(); reader.Read(); reader.Read(); Check(Convert.ToDecimal(reader.GetValue(2)) == 1000m, "table export amount cell");
            reader.Read(); reader.Read(); Check(Convert.ToString(reader.GetValue(2)) == "—", "missing amount shown as dash");
            reader.Read(); Check(Convert.ToDecimal(reader.GetValue(2)) == 3500.5m, "table export total excludes missing amount");
        }
        using (var read = UglyToad.PdfPig.PdfDocument.Open(ReportPdf.Generate([shownTable], reportAssets, stamp))) Check(read.NumberOfPages == 1, "table export PDF");
        try { _ = TableReport.Build(ReportSheet.DefaultHeader, "فارغ", ["الاسم"], -1, [], []); throw new InvalidOperationException("empty table export accepted"); }
        catch (InvalidDataException) { }
        var overview = HomeOverviews.Build(reportSnapshot, "أيلول 2026", null);
        Check(overview.Categories.Select(c => c.Count).SequenceEqual([12, 6, 6, 6]) && overview.PreviousMonth == "آب 2026" && overview.NetChange == 300000m && overview.PeopleChange == 0 && overview.AlertError is null, "home overview categories and comparison");
        Check(overview.Alerts.Any(a => a.Kind == "تغيّر الصافي" && a.Count == 30), "home overview groups review notes");
        var copiesRoot = Path.Combine(root, "copies"); Directory.CreateDirectory(copiesRoot);
        var copyDigests = Enumerable.Range(0, 7).Select(i => new string((char)('0' + i), 64)).ToArray();
        for (int i = 0; i < copyDigests.Length; i++) { var file = Path.Combine(copiesRoot, copyDigests[i] + ".zip"); File.WriteAllBytes(file, new byte[100 + i]); File.SetLastWriteTimeUtc(file, new DateTime(2026, 1, 1).AddDays(i)); }
        File.WriteAllText(Path.Combine(copiesRoot, "current.json"), JsonSerializer.Serialize(new { Version = 1, Digest = copyDigests[0] }));
        var copyStore = new LocalSnapshotStore(copiesRoot);
        Check(copyStore.Copies().Count == 7 && copyStore.Copies()[0].Current && copyStore.Copies()[1].Digest == copyDigests[6], "local copies listed newest first with the open copy");
        var pruned = copyStore.Prune(5, digest => digest == copyDigests[1]);
        Check(pruned.Deleted == 1 && pruned.Protected == 1 && pruned.Freed == 102 && !File.Exists(Path.Combine(copiesRoot, copyDigests[2] + ".zip")) && File.Exists(Path.Combine(copiesRoot, copyDigests[0] + ".zip")) && copyStore.Copies().Count == 6, "pruning keeps the open copy, the newest five and protected copies");
        // سيرفر النسخ المحلي: قواعد العنوان والرمز مثل الآيفون، والرفع والتنزيل مع رد وهمي بلا شبكة.
        Check(BackupServer.Url("http://192.168.1.10:8765")?.ToString() == "http://192.168.1.10:8765/" && BackupServer.Url("http://10.0.0.5:8765/") is not null && BackupServer.Url("http://172.20.1.1:8765") is not null, "private server addresses accepted");
        Check(new[] { "https://192.168.1.10:8765", "http://8.8.8.8:8765", "http://172.32.0.1:8765", "http://192.168.1.10:8765/x", "http://u:p@192.168.1.10:8765", "http://nas.local:8765", "http://192.168.1.10:8765?a=1", "" }.All(a => BackupServer.Url(a) is null), "public, encrypted, named or path server addresses rejected");
        Check(BackupServer.ValidToken(new string('a', 32)) && !BackupServer.ValidToken(new string('a', 31)) && !BackupServer.ValidToken(new string('a', 31) + " ") && !BackupServer.ValidToken(new string('ب', 40)), "server token rules");
        var serverFile = Path.Combine(root, "server-upload.zip"); File.WriteAllBytes(serverFile, Encoding.UTF8.GetBytes("fictional backup bytes"));
        var serverDigest = BackupServer.Sha256(serverFile); var serverBase = BackupServer.Url("http://192.168.1.10:8765")!;
        HttpRequestMessage? sent = null;
        using (var http = new HttpClient(new StubHandler(r => { sent = r; return new HttpResponseMessage(System.Net.HttpStatusCode.Created) { Content = new StringContent($"{{\"id\":\"backup-current.zip\",\"size\":22,\"sha256\":\"{serverDigest}\"}}") }; })))
            Check(await BackupServer.Upload(http, serverBase, new string('k', 40), serverFile) == 22 && sent!.RequestUri!.ToString() == "http://192.168.1.10:8765/v1/backups"
                && sent.Headers.Authorization?.ToString() == "Bearer " + new string('k', 40) && sent.Headers.GetValues("X-Content-SHA256").Single() == serverDigest, "server upload sends token and checksum and reads the confirmation");
        foreach (var (status, body) in new[] { (System.Net.HttpStatusCode.Created, "{\"size\":22,\"sha256\":\"0000\"}"), (System.Net.HttpStatusCode.BadRequest, "{}"), (System.Net.HttpStatusCode.Unauthorized, "{}") })
        {
            using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(status) { Content = new StringContent(body) }));
            try { _ = await BackupServer.Upload(http, serverBase, new string('k', 40), serverFile); throw new InvalidOperationException("unconfirmed server upload accepted: " + status); }
            catch (InvalidDataException) { }
        }
        var restored = Path.Combine(root, "server-restore.zip");
        using (var http = new HttpClient(new StubHandler(r => r.RequestUri!.AbsolutePath == "/v1/backups/latest" ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(serverFile)) } : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound))))
            await BackupServer.DownloadLatest(http, serverBase, new string('k', 40), restored);
        Check(BackupServer.Sha256(restored) == serverDigest, "server restore downloads the latest backup");
        using (var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"service\":\"salary-backup\",\"backup_count\":1,\"backup_bytes\":22,\"disk_free\":1000,\"backups\":[{\"id\":\"backup-current.zip\",\"size\":22,\"created_at\":\"2026-11-01T10:00:00+00:00\"}]}") })))
        {
            var serverStatus = await BackupServer.Status(http, serverBase, new string('k', 40));
            Check(serverStatus.Count == 1 && serverStatus.Bytes == 22 && serverStatus.DiskFree == 1000 && serverStatus.Latest?.Year == 2026 && !serverStatus.BackupFormatGuard, "legacy server status is not assumed to protect backup format");
        }
        using (var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"service\":\"salary-backup\",\"backup_count\":0,\"backup_bytes\":0,\"disk_free\":1000,\"backups\":[],\"backup_format_guard\":true}") })))
            Check((await BackupServer.Status(http, serverBase, new string('k', 40))).BackupFormatGuard, "server explicitly advertises backup format guard");
        var fictionalDevice = Guid.NewGuid().ToString("D"); var fictionalUpload = Guid.NewGuid().ToString("D");
        using (var http = new HttpClient(new StubHandler(r =>
        {
            Check(r.Headers.GetValues("X-Backup-Device-ID").Single() == fictionalDevice &&
                  Encoding.UTF8.GetString(Convert.FromBase64String(r.Headers.GetValues("X-Backup-Device-Name").Single())) == "حاسوب وهمي" &&
                  r.Headers.GetValues("X-Backup-Device-Platform").Single() == "windows", "server upload device identity");
            return new HttpResponseMessage(System.Net.HttpStatusCode.Created) { Content = new StringContent(JsonSerializer.Serialize(new { size = 22, sha256 = serverDigest, upload_id = fictionalUpload, uploaded_at = "2026-11-03T10:00:00+00:00" })) };
        })))
        {
            var receipt = await BackupServer.UploadWithDevice(http, serverBase, new string('k', 40), serverFile, fictionalDevice, "حاسوب وهمي");
            Check(receipt.UploadId == fictionalUpload && receipt.UploadedAt?.Year == 2026, "upload receipt identifies this exact upload");
        }
        var statusJson = JsonSerializer.Serialize(new { service = "salary-backup", backup_count = 1, backup_bytes = 22, disk_free = 1000,
            backups = new[] { new { created_at = "2025-01-01T10:00:00+00:00", uploaded_at = "2026-11-03T10:00:00+00:00", upload_id = fictionalUpload, device = new { id = fictionalDevice, name = "حاسوب وهمي" } } } });
        using (var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(statusJson) })))
        {
            var serverStatus = await BackupServer.Status(http, serverBase, new string('k', 40));
            Check(serverStatus.Latest?.Year == 2026 && serverStatus.UploadId == fictionalUpload && serverStatus.DeviceId == fictionalDevice && serverStatus.DeviceName == "حاسوب وهمي", "server upload time and device identity");
        }
        Console.WriteLine("Approved report exports and member profile checked with fictional data.");
    }
    // نسخة مصدر جديدة: ملف valid.zip عُدّل عمداً في اختبار سابق.
    var trustSourcePath = Path.Combine(root, "trust-source.zip"); MakeBackup(trustSourcePath); var trustSnapshot = BackupReader.Read(trustSourcePath);
    // الأمانات: رصيد، صرف جزئي، ملاحظات القيد والدفعة، ومنع تكرار الاسم/الشهر أو تجاوز المبلغ.
    var trustEntry = new SalaryTrustEntry(Guid.NewGuid().ToString(), "منتسب وهمي", "رتبة وهمية", "قسم وهمي", 2026, 10, 1000,
        "حالة تجريبية", "ملاحظة قيد وهمية", "", false, false,
        [new SalaryTrustPayment(Guid.NewGuid().ToString(), 300, "كتاب وهمي", "2026-11-02", "ملاحظة صرف وهمية")]);
    var reviewEntry = new SalaryTrustEntry(Guid.NewGuid().ToString(), "منتسب ثان وهمي", "رتبة", "قسم", 2026, 9, 500,
        "حالة", "", "قطع علاقة تقاعد", false, true, []);
    var trustLedger = new SalaryTrustLedger(1, [trustEntry, reviewEntry], true);
    var oldLedger = System.Text.Json.Nodes.JsonNode.Parse(SalaryTrust.Encode(trustLedger))!.AsObject();
    foreach (var node in oldLedger["entries"]!.AsArray()) node!.AsObject().Remove("bookIds");
    Check(SalaryTrust.Decode(Encoding.UTF8.GetBytes(oldLedger.ToJsonString())).Entries.All(e => e.BookIds is null), "older ledgers without document links remain readable");
    var trustDecoded = SalaryTrust.Decode(SalaryTrust.Encode(trustLedger));
    Check(trustDecoded.Entries.Count == 2 && SalaryTrust.Remaining(trustDecoded.Entries[0]) == 700 &&
          trustDecoded.Entries[0].CaseStatus == "حالة تجريبية" && trustDecoded.Entries[0].Note == "ملاحظة قيد وهمية" &&
          trustDecoded.Entries[0].Payments[0].Note == "ملاحظة صرف وهمية" && trustDecoded.Entries[1].ReviewPending, "trust ledger round trip and partial payment");
    try { SalaryTrust.Validate(trustLedger with { Entries = [trustEntry, trustEntry with { Id = Guid.NewGuid().ToString() }] }); throw new InvalidOperationException("duplicate trust entry accepted"); }
    catch (InvalidDataException) { }
    try { SalaryTrust.Validate(trustLedger with { Entries = [trustEntry with { Payments = [new SalaryTrustPayment(Guid.NewGuid().ToString(), 1001, "كتاب", "2026-11-02", "")] }] }); throw new InvalidOperationException("overpayment accepted"); }
    catch (InvalidDataException) { }
    try { SalaryTrust.Validate(trustLedger with { Entries = [trustEntry with { Payments = [new SalaryTrustPayment(Guid.NewGuid().ToString(), 10, "كتاب", "2026-02-30", "")] }] }); throw new InvalidOperationException("invalid book date accepted"); }
    catch (InvalidDataException) { }
    var trustXlsx = Path.Combine(root, "trust-fictional.xlsx");
    MakeMonthlyXlsx(trustXlsx, ["ت", "الرتبة", "الاسم الكامل", "صافي الراتب", "ملاحظات4", "الفوج او المديرية", "الشهر", "السنة", "الحالة", "الملاحظات"],
        [["1", "رتبة", "منتسب أول وهمي", "1000", "حالة من الملف", "قسم", "أيلول", "2026", "", "ملاحظة قيد"],
         ["2", "رتبة", "منتسب ثان وهمي", "2000", "حالة أخرى", "قسم", "آب", "2026", "صرف", ""],
         ["3", "رتبة", "منتسب ثالث وهمي", "3000", "", "قسم", "تموز", "2026", "قطع علاقة تقاعد", ""]]);
    var openingTrust = SalaryTrustSpreadsheet.Read(trustXlsx);
    Check(openingTrust.Entries.Count == 3 && openingTrust.Entries[0].CaseStatus == "حالة من الملف" && openingTrust.Entries[0].Note == "ملاحظة قيد" &&
        openingTrust.Entries[1].LegacyPaid && openingTrust.Entries[2].ReviewPending, "opening trust spreadsheet and source status");
    var fictionalBook = trustSnapshot.Books.Single();
    var idsToLink = new HashSet<string>([trustEntry.Id, reviewEntry.Id], StringComparer.Ordinal);
    var linkedTrust = SalaryTrust.Link(trustLedger, fictionalBook.Id, idsToLink);
    SalaryTrust.ValidateBooks(linkedTrust, trustSnapshot.Books);
    Check(linkedTrust.Entries.All(e => e.BookIds?.Count == 1) && SalaryTrust.Remaining(linkedTrust.Entries[0]) == SalaryTrust.Remaining(trustEntry), "group linking does not create a payment");
    var linkedAgain = SalaryTrust.Link(linkedTrust, fictionalBook.Id, idsToLink);
    Check(linkedAgain.Entries.All(e => e.BookIds?.Count == 1), "relinking the same book does not duplicate it");
    try { _ = SalaryTrust.Link(trustLedger, fictionalBook.Id, new HashSet<string> { Guid.NewGuid().ToString() }); throw new InvalidOperationException("linking missing entry accepted"); }
    catch (InvalidDataException) { }
    try { SalaryTrust.ValidateBooks(linkedTrust, []); throw new InvalidOperationException("missing linked book accepted"); }
    catch (InvalidDataException) { }
    var trustBackup = Path.Combine(root, "trust-fictional.zip");
    ReconciliationBackupExport.Write(trustSnapshot, [], trustBackup, linkedTrust);
    var trustRead = SalaryTrust.ReadFromBackup(BackupReader.Read(trustBackup));
    Check(trustRead.Entries.Count == 2 && trustRead.Entries[0].Payments[0].Note == "ملاحظة صرف وهمية" &&
          Guid.Parse(trustRead.Entries[0].BookIds!.Single()) == fictionalBook.Id, "trust ledger and archived document link survive shared backup");
    var trustAssets = ReportAssets.Load(Path.GetFullPath("SalaryApp/Assets.xcassets/WatermarkLogo.imageset/WatermarkLogo.png"));
    var trustStamp = new DateTime(2026, 11, 2, 9, 0, 0);
    // رقم كتاب الصرف وتاريخه اختياريان ومستقلان كما في الآيفون؛ التاريخ غير الفارغ يجب أن يكون صحيحاً.
    SalaryTrustPayment Pay(long amount, string number, string date) => new(Guid.NewGuid().ToString(), amount, number, date, "");
    foreach (var (number, date) in new[] { ("", ""), ("كتاب 7", ""), ("", "2026-10-05"), ("كتاب 8", "2026-10-06") })
        SalaryTrust.Validate(trustLedger with { Entries = [trustEntry with { Payments = [Pay(100, number, date), Pay(200, "", "")] }] });
    foreach (var date in new[] { "2026-13-01", "26-10-05", "2026-1-5", "1899-12-31", "2026/10/05" })
    {
        try { SalaryTrust.Validate(trustLedger with { Entries = [trustEntry with { Payments = [Pay(10, "", date)] }] }); throw new InvalidOperationException("invalid optional book date accepted: " + date); }
        catch (InvalidDataException) { }
    }
    try { SalaryTrust.Validate(trustLedger with { Entries = [trustEntry with { Payments = [Pay(600, "", ""), Pay(500, "", "")] }] }); throw new InvalidOperationException("partial payments above the deposit accepted"); }
    catch (InvalidDataException) { }
    // دفتر بصيغة الآيفون: معرفات بأحرف كبيرة، بلا bookIds، ودفعة بلا رقم ولا تاريخ.
    var iphoneId = Guid.NewGuid().ToString().ToUpperInvariant(); var iphonePayment = Guid.NewGuid().ToString().ToUpperInvariant();
    var iphoneLedger = SalaryTrust.Decode(Encoding.UTF8.GetBytes("{\"entries\":[{\"amount\":900,\"caseStatus\":\"\",\"id\":\"" + iphoneId + "\",\"importedStatus\":\"\",\"legacyPaid\":false,\"month\":10,\"name\":\"منتسب آيفون وهمي\",\"note\":\"\",\"payments\":[{\"amount\":400,\"bookDate\":\"\",\"bookNumber\":\"\",\"id\":\"" + iphonePayment + "\",\"note\":\"\"}],\"rank\":\"\",\"reviewPending\":false,\"workplace\":\"\",\"year\":2026}],\"openingImported\":true,\"version\":1}"));
    Check(iphoneLedger.Entries.Single().Payments.Single().BookNumber == "" && SalaryTrust.Remaining(iphoneLedger.Entries.Single()) == 500 && iphoneLedger.Entries.Single().BookIds is null, "iPhone ledger with empty book number and date opens on Windows");
    var iphoneBackup = Path.Combine(root, "trust-iphone-format.zip");
    ReconciliationBackupExport.Write(trustSnapshot, [], iphoneBackup, iphoneLedger);
    Check(SalaryTrust.ReadFromBackup(BackupReader.Read(iphoneBackup)).Entries.Single().Payments.Single().BookDate == "", "empty book fields survive the shared backup");
    // الأرشيف في ويندوز: إضافة كتاب من صور (مثل الماسح) أو PDF، تعديل البيانات، الاستبدال والحذف، ثم دخولها النسخة الاحتياطية بصيغة الآيفون.
    var archiveSource = Path.Combine(root, "archive-fictional.zip"); MakeBackup(archiveSource);
    var archiveRaw = BackupReader.Read(archiveSource);
    var archiveStore = new LocalArchiveStore(Path.Combine(root, "archive-store"));
    byte[] FictionalPage(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height); using var canvas = new SKCanvas(bitmap); canvas.Clear(SKColors.White);
        using var ink = new SKPaint { Color = SKColors.Black }; canvas.DrawRect(width / 10f, height / 10f, width / 2f, height / 20f, ink);
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90); return data.ToArray();
    }
    var scanned = ArchivePdf.FromImages([new ArchiveImage(FictionalPage(1240, 1754)), new ArchiveImage(FictionalPage(1754, 1240), 1)]);
    Check(ArchivePdf.Pages(scanned) == 2, "scanned pages become a two-page archive PDF");
    try { _ = ArchivePdf.FromImages(Enumerable.Range(0, 4).Select(_ => new ArchiveImage(FictionalPage(200, 300))).ToArray()); throw new InvalidOperationException("four scanned pages accepted"); }
    catch (InvalidDataException) { }
    try { _ = ArchivePdf.Pages(Encoding.ASCII.GetBytes("not a pdf")); throw new InvalidOperationException("non-PDF accepted"); }
    catch (InvalidDataException) { }
    var archiveEmpty = archiveStore.Load(archiveRaw);
    Check(archiveEmpty.Revision is null && archiveEmpty.Books.Count == 1 && ReferenceEquals(archiveStore.Apply(archiveRaw), archiveRaw), "archive without Windows changes uses the backup as-is");
    try { _ = archiveStore.Add(archiveRaw, archiveEmpty, "   ", "أيلول 2026", null, scanned); throw new InvalidOperationException("untitled book accepted"); }
    catch (InvalidDataException) { }
    var archiveAdded = archiveStore.Add(archiveRaw, archiveEmpty, "كتاب ممسوح وهمي", "أيلول 2026", "جهة وهمية للأرشيف", scanned);
    var addedBook = archiveAdded.Books[0];
    Check(archiveAdded.Books.Count == 2 && addedBook.Pages == 2 && addedBook.CreatedAt is > 0 && archiveAdded.Files.ContainsKey(addedBook.Id), "scanned book added to the local archive");
    try { _ = archiveStore.Edit(archiveRaw, archiveEmpty, addedBook.Id, "اسم آخر", "أيلول 2026", null); throw new InvalidOperationException("stale archive revision accepted"); }
    catch (InvalidDataException) { }
    var originalBook = archiveRaw.Books[0];
    var archiveEdited = archiveStore.Edit(archiveRaw, archiveAdded, originalBook.Id, "كتاب وهمي بعد التعديل", "تشرين الاول 2026", "");
    var singlePage = ArchivePdf.FromImages([new ArchiveImage(FictionalPage(600, 800))]);
    var archiveReplaced = archiveStore.Replace(archiveRaw, archiveEdited, addedBook.Id, singlePage);
    Check(archiveReplaced.Books.Single(b => b.Id == addedBook.Id).Pages == 1 && archiveReplaced.Books.Single(b => b.Id == originalBook.Id) is { Title: "كتاب وهمي بعد التعديل", Month: "تشرين الاول 2026", Entity: null }, "archive edit and file replacement");
    var archiveApplied = archiveStore.Apply(archiveRaw);
    var previewCopy = Path.Combine(root, "archive-local-copy.pdf");
    BackupReader.CopyBook(archiveApplied, archiveApplied.Books.Single(b => b.Id == addedBook.Id), previewCopy);
    Check(File.ReadAllBytes(previewCopy).AsSpan().SequenceEqual(singlePage), "local archive book opens from the Windows copy");
    var archiveBackup = Path.Combine(root, "archive-shared.zip");
    ReconciliationBackupExport.Write(archiveApplied, [], archiveBackup);
    var archiveRead = BackupReader.Read(archiveBackup);
    Check(archiveRead.Books.Count == 2 && archiveRead.Books[0].Id == addedBook.Id && archiveRead.Books[0].Entity == "جهة وهمية للأرشيف" && archiveRead.Books[1].Title == "كتاب وهمي بعد التعديل" && archiveRead.Books[1].Entity is null, "archive changes enter the shared backup index");
    using (var archiveZip = ZipFile.OpenRead(archiveBackup))
    {
        var index = JsonDocument.Parse(archiveZip.GetEntry("books/index.json")!.Open()).RootElement;
        Check(index[0].GetProperty("id").GetString() == addedBook.Id.ToString().ToUpperInvariant() && index[0].GetProperty("createdAt").GetDouble() > 0 && !index[1].TryGetProperty("entity", out _), "archive index keeps the iPhone format");
        Check(JsonDocument.Parse(archiveZip.GetEntry("manifest.json")!.Open()).RootElement.GetProperty("books").GetInt32() == 2, "manifest counts archive books");
    }
    var sharedCopy = Path.Combine(root, "archive-shared-copy.pdf");
    BackupReader.CopyBook(archiveRead, archiveRead.Books[0], sharedCopy);
    Check(File.ReadAllBytes(sharedCopy).AsSpan().SequenceEqual(singlePage), "replaced book file stored in the shared backup");
    var archiveDeleted = archiveStore.Delete(archiveRaw, archiveStore.Load(archiveRaw), originalBook.Id);
    var deletedBackup = Path.Combine(root, "archive-deleted.zip");
    ReconciliationBackupExport.Write(archiveStore.Apply(archiveRaw), [], deletedBackup);
    using (var deletedZip = ZipFile.OpenRead(deletedBackup))
        Check(archiveDeleted.Books.Count == 1 && BackupReader.Read(deletedBackup).Books.Single().Id == addedBook.Id && deletedZip.GetEntry($"books/{originalBook.Id.ToString().ToUpperInvariant()}.pdf") is null, "deleted book removed from the shared backup");
    // المرشحات والمتبقي المؤكد والكشف (مطابق للآيفون).
    var paidEntry = trustEntry with { Id = Guid.NewGuid().ToString(), Name = "منتسب مصروف وهمي", Payments = [Pay(1000, "", "")] };
    var unpaidEntry = trustEntry with { Id = Guid.NewGuid().ToString(), Name = "منتسب غير مصروف وهمي", Payments = [] };
    var filterLedger = new SalaryTrustLedger(1, [trustEntry, reviewEntry, paidEntry, unpaidEntry], true);
    int Matches(SalaryTrustFilter filter) => filterLedger.Entries.Count(e => SalaryTrust.Includes(filter, e));
    Check(Matches(SalaryTrustFilter.All) == 4 && Matches(SalaryTrustFilter.Review) == 1 && Matches(SalaryTrustFilter.Paid) == 1 && Matches(SalaryTrustFilter.Partial) == 1 &&
          Matches(SalaryTrustFilter.Unpaid) == 1 && Matches(SalaryTrustFilter.Unfinished) == 2 && SalaryTrust.Outstanding(filterLedger.Entries) == 1700, "trust filters and confirmed outstanding exclude review entries");
    var trustSheet = SalaryTrustReports.Sheet(filterLedger, filterLedger.Entries, ReportSheet.DefaultHeader, "كل الأشهر", "كل الحالات", "");
    Check(trustSheet.Rows.Count == 5 && trustSheet.Rows[^1][1] == "المتبقي المؤكد" && trustSheet.Rows[^1][7] == "1,700" && trustSheet.Rows[^1][3].StartsWith("للمراجعة", StringComparison.Ordinal) && trustSheet.Rows[1][7] == "—", "trust report rows and confirmed outstanding line");
    var trustPdf = SalaryTrustReports.Pdf(trustSheet, trustAssets, trustStamp);
    using (var read = UglyToad.PdfPig.PdfDocument.Open(trustPdf)) Check(read.NumberOfPages == 1, "trust report PDF");
    var trustExcel = Path.Combine(root, "trust-report.xlsx"); ReportFiles.Save(SalaryTrustReports.Excel(trustSheet, filterLedger.Entries), trustExcel, ".xlsx");
    using (var stream = File.OpenRead(trustExcel)) using (var reader = ExcelDataReader.ExcelReaderFactory.CreateOpenXmlReader(stream))
    {
        reader.Read(); reader.Read(); reader.Read(); reader.Read();
        Check(Convert.ToDouble(reader.GetValue(5)) == 1000 && Convert.ToDouble(reader.GetValue(7)) == 700, "trust Excel keeps amounts as numbers");
    }
    try { _ = SalaryTrustReports.Sheet(filterLedger, [trustEntry with { Amount = 5 }], ReportSheet.DefaultHeader, "", "", ""); throw new InvalidOperationException("stale trust report accepted"); }
    catch (InvalidDataException) { }
    // PDF متعدد الأسطر والصفحات (مثل SalaryTrustPDF في الآيفون): الاسم والحالة الطويلان يلتفان دون اقتطاع، والمبالغ تُطابق من الملف.
    var longStatus = filterLedger with { Entries = [unpaidEntry with { Name = "منتسب وهمي باسم طويل جداً يتجاوز عرض خلية الاسم في الكشف ولا يُقتطع", CaseStatus = string.Concat(Enumerable.Repeat("حالة طويلة جداً متعددة الأسطر ", 12)) }] };
    using (var read = UglyToad.PdfPig.PdfDocument.Open(SalaryTrustReports.Pdf(SalaryTrustReports.Sheet(longStatus, longStatus.Entries, ReportSheet.DefaultHeader, "", "", ""), trustAssets, trustStamp)))
        Check(read.NumberOfPages == 1, "long trust name and status wrap instead of being truncated");
    var manyTrust = new SalaryTrustLedger(1, Enumerable.Range(0, 120).Select(i => unpaidEntry with { Id = Guid.NewGuid().ToString(), Name = $"منتسب وهمي رقم {i}", Amount = 1000 + i, CaseStatus = i % 7 == 0 ? string.Concat(Enumerable.Repeat("حالة متعددة الأسطر ", 6)) : "" }).ToArray(), true);
    var manyShown = SalaryTrustReports.Filtered(manyTrust.Entries, 0, SalaryTrustFilter.All, "");
    using (var read = UglyToad.PdfPig.PdfDocument.Open(SalaryTrustReports.Pdf(SalaryTrustReports.Sheet(manyTrust, manyShown, ReportSheet.DefaultHeader, "كل الأشهر", "كل الحالات", ""), trustAssets, trustStamp)))
        Check(read.NumberOfPages > 3, "many trust entries split across PDF pages");
    var tallTrust = filterLedger with { Entries = [unpaidEntry with { CaseStatus = new string('ح', 1990) }] };
    try { _ = SalaryTrustReports.Pdf(SalaryTrustReports.Sheet(tallTrust, tallTrust.Entries, ReportSheet.DefaultHeader, "", "", ""), trustAssets, trustStamp); throw new InvalidOperationException("entry taller than a PDF page accepted"); }
    catch (InvalidDataException) { }
    // المرشحات متقاطعة: الشهر وحالة الصرف والبحث بالاسم معاً، والترتيب كالآيفون.
    var crossed = SalaryTrustReports.Filtered(filterLedger.Entries, 202610, SalaryTrustFilter.Unfinished, "غير مصروف");
    Check(crossed.Count == 1 && crossed[0].Id == unpaidEntry.Id && SalaryTrustReports.Filtered(filterLedger.Entries, 202609, SalaryTrustFilter.All, "").Single().Id == reviewEntry.Id, "trust filters intersect month, status and name search");
    // الملف الافتتاحي: مبالغ بأرقام عربية وصف غير صالح يوقف الاستيراد كله.
    var arabicDigits = Path.Combine(root, "trust-arabic-digits.xlsx");
    MakeMonthlyXlsx(arabicDigits, ["الاسم الكامل", "صافي الراتب", "الشهر", "السنة", "الحالة"], [["منتسب وهمي", "١٬٠٠٠", "أيلول", "2026", ""]]);
    Check(SalaryTrustSpreadsheet.Read(arabicDigits).Entries.Single().Amount == 1000, "opening trust amounts with Arabic digits");
    var badOpening = Path.Combine(root, "trust-bad.xlsx");
    MakeMonthlyXlsx(badOpening, ["الاسم الكامل", "صافي الراتب", "الشهر", "السنة", "الحالة"], [["منتسب وهمي", "1000", "أيلول", "2026", ""], ["منتسب آخر", "1000", "تشرين الأول", "2026", ""]]);
    try { _ = SalaryTrustSpreadsheet.Read(badOpening); throw new InvalidOperationException("opening entry after the cutoff accepted"); }
    catch (InvalidDataException) { }
    // دفتر الجهاز وكاش الأشهر بعد الافتتاح: إضافة مرة واحدة، ورفض تغيّر الحالة أو حفظ قديم.
    var cashMonth = "تشرين الأول 2026";
    var cashSource = trustSnapshot with { Months = trustSnapshot.Months.Append(new PayrollMonth(cashMonth, 1, 0)).ToArray(), Employees = [contractPerson with { Month = cashMonth, RawColumns = statusColumns }] };
    var localTrust = new LocalSalaryTrustStore(Path.Combine(root, "salary-trust"));
    var emptyTrust = localTrust.Load(cashSource);
    var savedTrust = localTrust.Save(cashSource, emptyTrust, openingTrust);
    var withCash = localTrust.AddCash(cashSource, cashMonth);
    Check(withCash.Ledger.Entries.Count == 4 && withCash.Ledger.Entries[^1].Amount == 500 && withCash.Ledger.Entries[^1].CaseStatus == "حالة تجريبية" && withCash.Ledger.Entries[^1].Month == 10, "cash salaries after the opening are added as deposits");
    Check(localTrust.AddCash(cashSource, cashMonth).Revision == withCash.Revision, "adding the same cash month twice changes nothing");
    var changedStatus = cashSource with { Employees = [contractPerson with { Month = cashMonth, RawColumns = JsonSerializer.SerializeToUtf8Bytes(new { headers = new[] { "صافي الراتب", "الملاحظات4" }, values = new[] { "500", "حالة أخرى" } }) }] };
    try { _ = localTrust.AddCash(changedStatus, cashMonth); throw new InvalidOperationException("changed cash status accepted"); }
    catch (InvalidDataException) { }
    try { _ = localTrust.Save(cashSource, savedTrust, openingTrust); throw new InvalidOperationException("stale trust save accepted"); }
    catch (InvalidDataException) { }
    Console.WriteLine("Salary trust ledger checked with fictional data.");
    Console.WriteLine("Windows core checks passed with fictional data, including reconciliation validation.");
}
finally { Directory.Delete(root, true); }

sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
}
