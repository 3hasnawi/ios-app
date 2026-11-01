using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Claunia.PropertyList;
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
    Console.WriteLine("Windows core checks passed with fictional data, including reconciliation validation.");
}
finally { Directory.Delete(root, true); }
