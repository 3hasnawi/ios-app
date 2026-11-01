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
    Entry(zip, "manifest.json", JsonSerializer.Serialize(new { version = 5, months = omitRecentMonth ? new[] { "أيلول 2026" } : new[] { "تشرين الاول 2026", "أيلول 2026" }, employees = wrongCount ? 3 : 2, deductions = 3, staff = 0, accounts = 1, books = 1 }));
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
Directory.CreateDirectory(root);
try
{
    string valid = Path.Combine(root, "valid.zip"), wrong = Path.Combine(root, "wrong.zip"), duplicate = Path.Combine(root, "duplicate.zip"), missing = Path.Combine(root, "missing.zip");
    MakeBackup(valid); MakeBackup(wrong, wrongCount: true); MakeBackup(duplicate, duplicate: true); MakeBackup(missing, omitPdf: true);
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
    try { _ = payrollStore.Import(snapshot, originalPayroll, salaryPreview, true); throw new InvalidOperationException("stale payroll import accepted"); }
    catch (InvalidDataException) { }
    try { _ = payrollStore.Import(snapshot, combinedPayroll, salaryPreview, false); throw new InvalidOperationException("unreviewed salary replacement accepted"); }
    catch (InvalidDataException) { }
    Check(payrollStore.Load(snapshot).Revision == combinedPayroll.Revision, "stale and unreviewed imports preserve existing data");
    try { _ = payrollStore.Import(snapshot, combinedPayroll, PayrollSpreadsheet.Read(invalidSalary, "تشرين الثاني 2026"), true); throw new InvalidOperationException("invalid payroll import accepted"); }
    catch (InvalidDataException) { }
    Check(payrollStore.Load(snapshot).Revision == combinedPayroll.Revision, "invalid payroll import preserves previous data");
    var replacement = salaryPreview with { Employees = [salaryPreview.Employees.Single() with { Net = 1001m }] };
    var replacedPayroll = payrollStore.Import(snapshot, combinedPayroll, replacement, true);
    Check(Directory.GetFiles(Path.Combine(root, "payroll-data", snapshot.SourceDigest), "*.json").Length >= 4 && replacedPayroll.Snapshot.Employees.Single(e => e.Month == replacement.Month).Net == 1001m && replacedPayroll.Snapshot.Deductions.Any(d => d.Month == replacement.Month), "reviewed salary replacement preserves deduction records and earlier versions");
    var salaryBackup = Path.Combine(root, "salary-import-backup.zip"); ReconciliationBackupExport.Write(combinedPayroll.Snapshot, [], salaryBackup);
    BackupDestination.Guard(salaryBackup, combinedPayroll.Snapshot); BackupDestination.Guard(Path.Combine(root, "not-yet.zip"), snapshot);
    try { BackupDestination.Guard(salaryBackup, snapshot); throw new InvalidOperationException("backup file overwritten by a copy missing a month"); }
    catch (InvalidDataException) { }
    var restoredSalary = BackupReader.Read(salaryBackup);
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
    var totals = BankReconciliation.Calculate(period);
    var font = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf") : "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
    var testPdf = ReconciliationPdf.Generate(period with { FirstMember = "عضو تجريبي أول", SecondMember = "عضو تجريبي ثان" }, font);
    Check(testPdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8) && testPdf.Length > 1000, "A4 reconciliation PDF draft created");
    if (Environment.GetEnvironmentVariable("SALARY_TEST_PDF") is { Length: > 0 } pdfPath) File.WriteAllBytes(pdfPath, testPdf);
    var longPeriod = period with { Movements = Enumerable.Range(0, 75).Select(i => new BankMovement(Guid.NewGuid(), period.Start, "حركة اختبارية ذات بيان طويل للتأكد من ظهور النص بوضوح في الكشف الورقي", 100, MovementDirection.Debit)).ToArray() };
    var longPdf = ReconciliationPdf.Generate(longPeriod, font);
    var pdfStructure = Encoding.Latin1.GetString(longPdf);
    Check(System.Text.RegularExpressions.Regex.Matches(pdfStructure, @"/Type\s*/Page\b").Count > 1 && pdfStructure.Contains("/MediaBox [0 0 595 842]", StringComparison.Ordinal), "long reconciliation generates multiple A4 pages");
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
    ReconciliationBackupExport.Write(ledgerSnapshot, savedLedger.Periods, exportedLedgerBackup);
    var exportedLedger = BackupReader.Read(exportedLedgerBackup);
    var roundTripPeriod = BankReconciliation.ReadFromBackup(exportedLedger).Single();
    Check(roundTripPeriod.Id == period.Id && roundTripPeriod.Movements.Count == 4 && roundTripPeriod.Movements[2].RefundOf == refund.Id && roundTripPeriod.End == period.End && BankReconciliation.Calculate(roundTripPeriod).Balance == 0, "reconciliation backup preserves dates, links and totals");
    Check(exportedLedger.Employees.Count == snapshot.Employees.Count && exportedLedger.Books.Count == snapshot.Books.Count && before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(valid))), "reconciliation backup preserves source data and original file");
    try { ReconciliationBackupExport.Write(ledgerSnapshot, savedLedger.Periods, ledgerSnapshot.SourcePath); throw new InvalidOperationException("backup source overwritten"); }
    catch (InvalidDataException) { }
    var previousExport = SHA256.HashData(File.ReadAllBytes(exportedLedgerBackup));
    try { ReconciliationBackupExport.Write(ledgerSnapshot, [period with { Movements = [funding, refund, repay with { Amount = 201 }] }], exportedLedgerBackup); throw new InvalidOperationException("invalid exported ledger accepted"); }
    catch (InvalidDataException) { }
    Check(previousExport.SequenceEqual(SHA256.HashData(File.ReadAllBytes(exportedLedgerBackup))), "failed reconciliation export preserves existing file");
    Check(BankStatementCalculator.Manual(emptySettings, "أيلول 2026").Total == 0m, "absent manual amounts");
    var brokenSettings = snapshot with { SettingsPlists = new Dictionary<string, byte[]> { ["bankStatementManualAmounts"] = [1, 2, 3] } };
    try { _ = BankStatementCalculator.Manual(brokenSettings, "أيلول 2026"); throw new InvalidOperationException("invalid manual settings accepted"); }
    catch (InvalidDataException) { }
    var contractPerson = new Employee("أيلول 2026", "موظف افتراضي", "عقود", "قسم تجريبي", 500m,
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, decimal> { ["الرعاية الصحية"] = 20m, ["استقطاع الجوال"] = 100m }),
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["طريقة الدفع"] = "كاش", ["الاسمي"] = "١٬٠٠١", ["توقيفات تقاعدية"] = "75", ["مجموع استقطاعات المصارف"] = "2500" }), null);
    var contractSnapshot = snapshot with { Employees = new[] { contractPerson } };
    var contractBank = BankStatementCalculator.Calculate(contractSnapshot, "أيلول 2026");
    Check(contractBank.Cash == 500m && contractBank.ContractNominalShare == 120m && contractBank.ContractContribution == 195m &&
          contractBank.Breakdown["الرعاية الصحية"] == 20m && contractBank.Breakdown["شركة عراق الجوال"] == 100m, "contract rounding and deduction sources");
    var statusColumns = JsonSerializer.SerializeToUtf8Bytes(new { headers = new[] { "صافي الراتب", "الملاحظات4" }, values = new[] { "500", "حالة تجريبية" } });
    var statusCash = CashSalaryListPreparation.Prepare(contractSnapshot with { Employees = [contractPerson with { RawColumns = statusColumns }] }, "أيلول 2026");
    Check(statusCash.Rows.Single().CaseStatus == "حالة تجريبية", "cash status comes from notes immediately after net salary");
    var cashList = CashSalaryListPreparation.Prepare(contractSnapshot with { Employees = [contractPerson, contractPerson with { Name = "صفر تجريبي", Net = 0m }] }, "أيلول 2026");
    Check(cashList.Total == 500 && cashList.Rows.Count == 1 && cashList.Rows[0].Name == contractPerson.Name, "cash list excludes zero and matches net total");
    try { _ = CashSalaryListPreparation.Prepare(contractSnapshot with { Employees = [contractPerson with { Workplace = "" }] }, "أيلول 2026"); throw new InvalidOperationException("incomplete cash person accepted"); }
    catch (InvalidDataException) { }
    string[] rawHeaders = ["مجموع استقطاعات المصارف", "صندوق الشهداء", "الرعاية الصحية", "النشاط الرياضي", "الضريبة", "الحماية الاجتماعية", "شركة عراق الجوال", "الاسمي", "توقيفات تقاعدية"];
    var rawColumns = JsonSerializer.SerializeToUtf8Bytes(new { headers = rawHeaders, values = rawHeaders.Select(_ => "").ToArray() });
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
    var groupPdf = GroupLetterPdf.Generate(groupLetter, letterSettings, fontBytes, fontBytes, logoBytes);
    Check(groupPdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8), "group PDF generated with validated table");
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
        using (var read = UglyToad.PdfPig.PdfDocument.Open(entityPdf)) Check(read.NumberOfPages == 1, "entity PDF pages");
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
            Check(serverStatus.Count == 1 && serverStatus.Bytes == 22 && serverStatus.DiskFree == 1000 && serverStatus.Latest?.Year == 2026, "server status for connection test");
        }
        Console.WriteLine("Approved report exports and member profile checked with fictional data.");
    }
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
    var fictionalBook = snapshot.Books.Single();
    var idsToLink = new HashSet<string>([trustEntry.Id, reviewEntry.Id], StringComparer.Ordinal);
    var linkedTrust = SalaryTrust.Link(trustLedger, fictionalBook.Id, idsToLink);
    SalaryTrust.ValidateBooks(linkedTrust, snapshot.Books);
    Check(linkedTrust.Entries.All(e => e.BookIds?.Count == 1) && SalaryTrust.Remaining(linkedTrust.Entries[0]) == SalaryTrust.Remaining(trustEntry), "group linking does not create a payment");
    var linkedAgain = SalaryTrust.Link(linkedTrust, fictionalBook.Id, idsToLink);
    Check(linkedAgain.Entries.All(e => e.BookIds?.Count == 1), "relinking the same book does not duplicate it");
    try { _ = SalaryTrust.Link(trustLedger, fictionalBook.Id, new HashSet<string> { Guid.NewGuid().ToString() }); throw new InvalidOperationException("linking missing entry accepted"); }
    catch (InvalidDataException) { }
    try { SalaryTrust.ValidateBooks(linkedTrust, []); throw new InvalidOperationException("missing linked book accepted"); }
    catch (InvalidDataException) { }
    var trustBackup = Path.Combine(root, "trust-fictional.zip");
    ReconciliationBackupExport.Write(snapshot, [], trustBackup, linkedTrust);
    var trustRead = SalaryTrust.ReadFromBackup(BackupReader.Read(trustBackup));
    Check(trustRead.Entries.Count == 2 && trustRead.Entries[0].Payments[0].Note == "ملاحظة صرف وهمية" &&
          Guid.Parse(trustRead.Entries[0].BookIds!.Single()) == fictionalBook.Id, "trust ledger and archived document link survive shared backup");
    Console.WriteLine("Salary trust ledger checked with fictional data.");
    Console.WriteLine("Windows core checks passed with fictional data, including reconciliation validation.");
}
finally { Directory.Delete(root, true); }

sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
}
