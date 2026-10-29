using SalaryWindows;
using System.Text.Json;
static string Enc(object x) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(x));
var month = "2026-10";
var people = new List<Employee> {
  new(month,"اسم وهمي","عقود","قسم",1000,Enc(new Dictionary<string,double>{{"الاسمي",101},{"توقيفات تقاعدية",7},{"مجموع استقطاعات المصارف",80},{"الضريبة",3}}),Enc(new Dictionary<string,string>{{"طريقة الدفع","بطاقة"}})),
  new(month,"اسم ثان","عقد","قسم",500,Enc(new Dictionary<string,double>{{"الاسمي",1000}}),Enc(new Dictionary<string,string>{{"طريقة الدفع","كاش"}}))
};
var data = new BackupData(new Manifest(4,[month],2,1,0,0,0),people,[new(month,"اسم وهمي","جهة وهمية",80,null,null)],[],[],[],new(){{"bankStatementManualAmounts", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "plist.txt"))}}, "unused");
var b = BankCalculator.Calculate(data, month); var m = ManualAmounts.ForMonth(data,month);
if (b.Cash != 500 || b.Card != 1000 || b.Contract != 19 || b.BankDeductions != 80 || !b.Matched || b.Subtotal != 1602 || m != (100,200,300)) throw new Exception($"bad bank: {b} manual: {m}");
if (PayrollMath.Category("عقد") != 3 || PayrollMath.Category("عقود") != 3 || PayrollMath.Category("ملازم أول") != 0 || PayrollMath.ParseNumber("١٬٢٣٤٫٥") != 1234.5) throw new Exception("normalization failed");
Console.WriteLine("Core fixture passed: payroll classification, bank totals, rounding, matching and per-month manual amounts.");

var outFile = Path.Combine(Path.GetTempPath(), "salary-test-" + Guid.NewGuid().ToString("N") + ".xlsx");
try {
    ExcelExport.Entity(outFile, data, month, "جهة وهمية");
    using var zip = System.IO.Compression.ZipFile.OpenRead(outFile);
    using var sheet = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
    var xml = System.Xml.Linq.XDocument.Load(sheet);
    XNamespaceCheck(xml);
} finally { File.Delete(outFile); }
static void XNamespaceCheck(System.Xml.Linq.XDocument xml) {
    System.Xml.Linq.XNamespace n = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    if (xml.Descendants(n + "sheetView").First().Attribute("rightToLeft")?.Value != "1" || xml.Descendants(n + "row").Count() != 5) throw new Exception("invalid sheet");
}
Console.WriteLine("Excel fixture passed: right-to-left sheet and data rows.");
var archive = Path.Combine(Path.GetTempPath(), "salary-fixture-" + Guid.NewGuid().ToString("N") + ".zip");
try
{
    using (var zip = System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Create))
    {
        void Write(string name, string text) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(text); }
        Write("manifest.json", "{\"version\":4,\"months\":[\"2026-10\"],\"employees\":1,\"deductions\":0,\"staff\":0,\"accounts\":0,\"books\":0}");
        Write("settings.json", "{}"); Write("months.jsonl", "{\"name\":\"2026-10\"}\n");
        Write("employees.jsonl", "{\"month\":\"2026-10\",\"name\":\"اسم وهمي\",\"rank\":\"عقود\",\"workplace\":\"قسم\",\"net\":100}\n");
        Write("deductions.jsonl", ""); Write("staff.jsonl", ""); Write("accounts.jsonl", ""); Write("books/index.json", "[]");
    }
    var loaded = BackupReader.Read(archive);
    if (loaded.Employees.Count != 1 || loaded.Employees[0].Net != 100 || loaded.Manifest.Months[0] != "2026-10") throw new Exception("bad backup read");
    using (var zip = System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Update))
    { zip.GetEntry("employees.jsonl")!.Delete(); using var writer = new StreamWriter(zip.CreateEntry("employees.jsonl").Open()); }
    try { BackupReader.Read(archive); throw new Exception("accepted mismatched counts"); }
    catch (InvalidDataException) { }
}
finally { File.Delete(archive); }
Console.WriteLine("Backup fixture passed: read valid archive and reject inconsistent record counts.");
var older = "2026-09"; var newer = "2026-10";
var changesData = data with {
    Manifest = data.Manifest with { Months = [newer, older] },
    Deductions = [
        new(older,"أحمد وهمي","جهة أ",100,"123",null), new(older,"أحمد وهمي","جهة أ",50,"123",null),
        new(newer,"احمد وهمي","جهة أ",200,"123",null),
        new(older,"خارج وهمي","جهة ب",90,"3",null), new(newer,"جديد وهمي","جهة ب",20,"4",null),
        new(newer,"مكرر وهمي","جهة ج",10,"5",null), new(newer,"مكرر وهمي","جهة ج",10,"5",null)
    ]
};
var comparison = DeductionComparison.Compare(changesData, older, newer);
if (comparison.Entries.Count(x => x.Kind == "زاد") != 1 || comparison.Entries.Count(x => x.Kind == "استقطاع متوقف") != 1 || comparison.Entries.Count(x => x.Kind == "استقطاع جديد") != 2 || DeductionComparison.Duplicates(changesData, newer).Count != 1) throw new Exception("deduction comparison failed");
var invalid = changesData with { Deductions = changesData.Deductions.Append(new(newer,"اسم","جهة",null,null,null)).ToList() };
try { DeductionComparison.Compare(invalid, older, newer); throw new Exception("invalid deduction accepted"); } catch (InvalidDataException) { }
Console.WriteLine("Deduction comparison fixture passed: aggregation, changes, duplicates and invalid amounts.");
var payrollFile = Path.Combine(Path.GetTempPath(), "salary-payroll-" + Guid.NewGuid().ToString("N") + ".xlsx");
try {
    ExcelExport.Payroll(payrollFile, data, month, "الاسمي");
    using var zip = System.IO.Compression.ZipFile.OpenRead(payrollFile);
    using var sheet = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
    var xml = System.Xml.Linq.XDocument.Load(sheet);
    System.Xml.Linq.XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    if (xml.Descendants(ns + "row").Count() != 6 || !xml.Descendants(ns + "v").Any(v => v.Value == "1101")) throw new Exception("payroll report totals failed");
} finally { File.Delete(payrollFile); }
Console.WriteLine("Payroll export fixture passed: rows and totals.");
var pdfOutput = Environment.GetEnvironmentVariable("SALARY_PDF_TEST_PATH") ?? Path.Combine(Path.GetTempPath(), "salary-pdf-" + Guid.NewGuid().ToString("N") + ".pdf");
try {
    var lines = Enumerable.Range(1, 120).Select(i => new PdfExport.Line($"موظف تجريبي رقم {i}", "موظف", i.ToString(), "قسم الحسابات")).ToList();
    var font = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf") : "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
    PdfExport.Write(pdfOutput, "تشرين الأول 2026", "كشف تجريبي", lines, font, total: 7260);
    var bytes = File.ReadAllBytes(pdfOutput);
    if (bytes.Length < 2000 || System.Text.Encoding.ASCII.GetString(bytes, 0, 5) != "%PDF-") throw new Exception("invalid PDF");
} finally { if (Environment.GetEnvironmentVariable("SALARY_PDF_TEST_PATH") == null) File.Delete(pdfOutput); }
Console.WriteLine("PDF fixture passed: 120 rows exported.");
var protectedFile = Path.Combine(Path.GetTempPath(), "salary-protected-" + Guid.NewGuid().ToString("N") + ".pdf");
try {
    File.WriteAllText(protectedFile, "existing");
    var longLine = new PdfExport.Line(string.Join(" ", Enumerable.Repeat("اسم", 10000)), "", "1", "");
    var testFont = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf") : "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
    try { PdfExport.Write(protectedFile, "2026", "اختبار", [longLine], testFont); throw new Exception("oversized line was accepted"); }
    catch (InvalidDataException) { }
    if (File.ReadAllText(protectedFile) != "existing") throw new Exception("existing export was damaged");
} finally { File.Delete(protectedFile); }
Console.WriteLine("PDF failure fixture passed: oversized row rejected without replacing existing file.");
var salaryOld = new Employee(older, "أحمد وهمي", "ملازم اول", "قسم أ", 1000, Enc(new Dictionary<string,double>{{"الاسمي",800}}), Enc(new Dictionary<string,string>{{"IBAN","IQ123456789012345678901"}}));
var salaryNew = new Employee(newer, "احمد وهمي", "ملازم 1", "قسم ب", 1100, Enc(new Dictionary<string,double>{{"الاسمي",900}}), Enc(new Dictionary<string,string>{{"IBAN","IQ123456789012345678999"}}));
var salaryData = changesData with { Employees = [salaryOld, salaryNew, new(older,"خارج وهمي","","",5,null,null), new(newer,"جديد وهمي","","",5,null,null)] };
var sc = SalaryComparison.Compare(salaryData, older, newer);
if (sc.Changes.Count(x => x.Kind == "جدد") != 1 || sc.Changes.Count(x => x.Kind == "خارجون") != 1 || sc.Changes.Count(x => x.Kind == "تغير الصافي") != 1 || sc.Changes.Count(x => x.Kind == "تغير الرتبة") != 0 || sc.Changes.Count(x => x.Kind == "تغير محل العمل") != 1 || sc.Changes.Count(x => x.Kind == "تغير الحساب المصرفي") != 1) throw new Exception("salary comparison failed");
var ambiguous = salaryData with { Employees = salaryData.Employees.Append(salaryOld).ToList() };
if (SalaryComparison.Compare(ambiguous, older, newer).Changes.Any(x => x.Name == "احمد وهمي")) throw new Exception("ambiguous name compared");
Console.WriteLine("Salary comparison fixture passed: changes, equivalent ranks and duplicate-name protection.");
var staffSections = new List<StaffSection> {
    new("المعلومات الشخصية", [new("اسم الام الكامل","أم تجريبية"),new("الحالة الزوجية","متزوج"),new("اسم الزوجة/ الزوج","زوجة تجريبية")]),
    new("التعيين والمباشرة", [new("رقم الامر","123"),new("تاريخ الامر","2020"),new("تاريخ مباشرة بالخدمة لاول مرة","2021")]),
    new("الترقية", [new("رقم امر الترقية","44"),new("تاريخ الترقية","2024")]),
    new("التحصيل الدراسي", [new("التحصيل الدراسي","بكالوريوس")]), new("الملاحظات", [new("الملاحظات","ملاحظة وهمية")]),
    new("السجل", [new("تاريخ التحديث","2026")])
};
var staffReport = StaffReportData.From(new Staff("اسم وهمي","ام تجريبية","1","2","رتبة","مستمر","قسم",Enc(staffSections)));
var fields = staffReport.Sections.SelectMany(x => x.Fields).ToDictionary(x => x.Label, x => x.Value);
foreach (var name in new[] { "اسم الأم", "الحالة الزوجية", "اسم الزوجة / الزوج", "التحصيل الدراسي", "رقم أمر التعيين", "تاريخ أمر التعيين", "أول مباشرة بالخدمة", "رقم أمر الترقية", "تاريخ الترقية", "تاريخ تحديث السجل", "الملاحظات" })
    if (!fields.ContainsKey(name)) throw new Exception("missing staff field: " + name);
if (fields.ContainsKey("المنصب")) throw new Exception("missing field was fabricated");
Console.WriteLine("Staff report fixture passed: requested fields retained and absent position omitted.");
var staffPdf = Environment.GetEnvironmentVariable("SALARY_STAFF_TEST_PATH") ?? Path.Combine(Path.GetTempPath(), "salary-staff-" + Guid.NewGuid().ToString("N") + ".pdf");
try {
    var font = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf") : "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
    StaffReportPdf.Write(staffPdf, staffReport, font, "WindowsApp/Resources/HomeLogo.png");
    if (!File.Exists(staffPdf) || new FileInfo(staffPdf).Length < 2000) throw new Exception("invalid staff pdf");
    var original = File.ReadAllBytes(staffPdf);
    var huge = staffReport with { Sections = [new StaffSection("السجل والملاحظات", [new("الملاحظات", string.Join(" ", Enumerable.Repeat("بيانات طويلة", 5000)))])] };
    try { StaffReportPdf.Write(staffPdf, huge, font); throw new Exception("long staff report accepted"); } catch (InvalidDataException) { }
    if (!File.ReadAllBytes(staffPdf).SequenceEqual(original)) throw new Exception("previous staff PDF overwritten on failure");
} finally { if (Environment.GetEnvironmentVariable("SALARY_STAFF_TEST_PATH") == null) File.Delete(staffPdf); }
Console.WriteLine("Staff PDF fixture passed: one report and overflow rejection without replacement.");
var checkPeople = new List<Employee> {
    new(newer,"شخص وهمي أ","عقود","قسم",100,Enc(new Dictionary<string,double>{{"الرعاية الصحية",3600}}),Enc(new Dictionary<string,string>{{"IBAN","IQ1"},{"الرقم الوظيفي","00123"}})),
    new(newer,"شخص وهمي ب","موظف","قسم",200,Enc(new Dictionary<string,double>{{"الرعاية الصحية",3500}}),Enc(new Dictionary<string,string>{{"IBAN","IQ1"},{"الرقم الوظيفي","123"}}))
};
var checks = SalaryChecks.Run(data with { Employees = checkPeople }, newer);
if (checks.Single(x => x.Title == "فحص IBAN").Lines.Count != 4 || checks.Single(x => x.Title == "تكرار الرقم الوظيفي").Lines.Count != 1 || checks.Single(x => x.Title == "فحص الرعاية الصحية").Lines.Count != 1) throw new Exception("salary checks failed");
if (!checks.Single(x => x.Title == "فحص الحساب").Explanation.Contains("غير متاح")) throw new Exception("missing calculation was reported as checked");
Console.WriteLine("Salary checks fixture passed: invalid/shared IBAN, IDs, health and unavailable calculation.");
var staffMatchingData = data with {
  Employees = [new(newer,"راتب فقط","عقود","قسم",1,null,Enc(new Dictionary<string,string>())), new(newer,"موظف مشترك","ملازم 1","قسم",1,null,Enc(new Dictionary<string,string>{{"الرقم الموحد","123"}}))],
  Staff = [new("موظف مشترك","","","00123","ملازم اول","مستمر","قسم",null), new("ملاك فقط","","","","موظف","مستمر","قسم",null), new("متقاعد وهمي","","","","موظف","متقاعد","قسم",null)]
};
var matching = StaffMatching.Run(staffMatchingData, newer);
if (matching.Single(x => x.Title == "لهم راتب وليسوا في الملاك").Lines.Count != 1 || matching.Single(x => x.Title == "في الملاك بلا راتب").Lines.Count != 1 || matching.Single(x => x.Title == "الرتبة مختلفة عن الملاك").Lines.Count != 0) throw new Exception("staff matching failed");
var ambiguousMatch = staffMatchingData with { Staff = staffMatchingData.Staff.Append(staffMatchingData.Staff[0]).ToList() };
if (StaffMatching.Run(ambiguousMatch, newer).Single(x => x.Title == "مطابقة ملاك غير حاسمة").Lines.Count != 1) throw new Exception("ambiguous staff match accepted");
Console.WriteLine("Staff matching fixture passed: unified number, inactive status and ambiguous matches.");
var auditPath = Path.Combine(Path.GetTempPath(), "salary-audit-" + Guid.NewGuid().ToString("N") + ".xlsx");
try {
    var auditLines = Enumerable.Range(1, 650).Select(i => "حالة وهمية " + i).ToList();
    auditLines[20] = "=SUM(1,1)";
    ExcelExport.Audit(auditPath, "الشهر الجديد / الشهر السابق", "حالات التدقيق", auditLines);
    using var zip = System.IO.Compression.ZipFile.OpenRead(auditPath);
    using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
    var document = System.Xml.Linq.XDocument.Load(stream);
    System.Xml.Linq.XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    if (document.Descendants(ns + "row").Count() != 653 || !document.Descendants(ns + "t").Any(x => x.Value == "حالة وهمية 650") || document.Descendants(ns + "f").Any()) throw new Exception("audit export lost rows or treated text as formulas");
} finally { File.Delete(auditPath); }
Console.WriteLine("Audit Excel fixture passed: all 650 results exported as text.");
var specialEmployees = new List<Employee> {
    new(newer,"مشترك وهمي","موظف","قسم",100,Enc(new Dictionary<string,double>{{"الرعاية الصحية",7000},{"صندوق الشهداء",4000},{"شركة عراق الجوال",10000}}),null),
    new(newer,"غير مشترك وهمي","شرطي","قسم",100,Enc(new Dictionary<string,double>{{"الرعاية الصحية",0},{"شركة عراق الجوال",20000}}),null),
    new(newer,"عقد مستبعد وهمي","عقود","قسم",100,Enc(new Dictionary<string,double>{{"الرعاية الصحية",0},{"صندوق الشهداء",2000}}),null),
    new(newer,"مبلغ يحتاج مراجعة","موظف","قسم",100,Enc(new Dictionary<string,double>{{"الرعاية الصحية",3600}}),null)
};
var specialData = data with { Employees = specialEmployees };
var health = SpecialReports.Build(specialData, newer, "الرعاية الصحية")!;
if (!health.Lines.Contains("المشتركون: 2") || !health.Lines.Contains("غير المشتركين: 1") || !health.Lines.Any(x => x.Contains("يحتاج مراجعة")) || health.Lines.Any(x => x.StartsWith("غير مشترك") && x.Contains("عقد مستبعد"))) throw new Exception("health report failed");
var fundReport = SpecialReports.Build(specialData, newer, "صندوق الشهداء")!;
if (!fundReport.Lines.Any(x => x.Contains("موظفون") && x.Contains("4,000")) || !fundReport.Lines.Any(x => x.Contains("عقود") && x.Contains("2,000"))) throw new Exception("martyrs report failed");
var foodReport = SpecialReports.Build(specialData, newer, "شركة عراق الجوال")!;
if (!foodReport.Lines.Any(x => x.Contains("900")) || !foodReport.Lines.Any(x => x.Contains("524")) || !foodReport.Lines.Any(x => x.Contains("28,576"))) throw new Exception("food contract split failed");
Console.WriteLine("Special reports fixture passed: health exclusions, fund categories and food split rounding.");
var missingPath = Path.Combine(Path.GetTempPath(), "salary-missing-" + Guid.NewGuid().ToString("N") + ".xlsx");
try {
    var missing = data with { Deductions = [new(month,"اسم وهمي","جهة وهمية",null,null,null)] };
    ExcelExport.Entity(missingPath, missing, month, "جهة وهمية");
    using var zip = System.IO.Compression.ZipFile.OpenRead(missingPath);
    using var sheet = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
    var doc = System.Xml.Linq.XDocument.Load(sheet);
    System.Xml.Linq.XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    if (!doc.Descendants(ns + "t").Any(x => x.Value.Contains("المجموع غير متاح"))) throw new Exception("empty amount shown as valid total");
} finally { File.Delete(missingPath); }
Console.WriteLine("Missing amount fixture passed: no misleading total.");
var inspectExcel = Environment.GetEnvironmentVariable("SALARY_EXCEL_TEST_PATH");
if (!string.IsNullOrEmpty(inspectExcel)) ExcelExport.Audit(inspectExcel, "الشهر السابق / الشهر الحالي", "نتائج وهمية", Enumerable.Range(1, 650).Select(i => "حالة وهمية " + i).ToList());
var customHeader = data with { Settings = new() { ["reportHeader"] = "YnBsaXN0MDBtBkIGSgYnBi8GKQAgBioGLAYxBkoGKAZKBikIAAAAAAAAAQEAAAAAAAAAAQAAAAAAAAAAAAAAAAAAACM=" } };
if (BackupSettings.ReportHeader(customHeader) != "قيادة تجريبية") throw new Exception("unicode report header failed");
var asciiHeader = data with { Settings = new() { ["reportHeader"] = "YnBsaXN0MDBeRXhhbXBsZSBoZWFkZXIIAAAAAAAAAQEAAAAAAAAAAQAAAAAAAAAAAAAAAAAAABc=" } };
if (BackupSettings.ReportHeader(asciiHeader) != "Example header") throw new Exception("ASCII report header failed");
var officialPath = Environment.GetEnvironmentVariable("SALARY_OFFICIAL_EXCEL_PATH");
if (!string.IsNullOrEmpty(officialPath)) ExcelExport.Entity(officialPath, customHeader, month, "جهة وهمية");
Console.WriteLine("Report header fixture passed for Arabic and ASCII.");
var largePath = Path.Combine(Path.GetTempPath(), "salary-large-" + Guid.NewGuid().ToString("N") + ".xlsx");
try {
    var many = data with { Deductions = Enumerable.Range(1, 2000).Select(i => new Deduction(month, "اسم وهمي " + i, "جهة وهمية", 1.25, "مرجع " + i, "موظف")).ToList() };
    ExcelExport.Entity(largePath, many, month, "جهة وهمية");
    using var zip = System.IO.Compression.ZipFile.OpenRead(largePath); using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
    var doc = System.Xml.Linq.XDocument.Load(stream); System.Xml.Linq.XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    if (doc.Descendants(ns + "row").Count() != 2004 || !doc.Descendants(ns + "f").Any(x => x.Value == "SUM(D4:D2003)") || !doc.Descendants(ns + "v").Any(x => x.Value == "2500")) throw new Exception("large official Excel failed");
} finally { File.Delete(largePath); }
Console.WriteLine("Official Excel fixture passed: all 2000 rows and decimal total.");
var specialDir = Environment.GetEnvironmentVariable("SALARY_SPECIAL_TEST_DIR") ?? Path.Combine(Path.GetTempPath(), "salary-special-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(specialDir);
try {
    foreach (var (file,title) in new[] { ("health.xlsx","الرعاية الصحية"), ("fund.xlsx","صندوق الشهداء"), ("food.xlsx","شركة عراق الجوال") })
    {
        var outpath = Path.Combine(specialDir,file); SpecialExcel.Write(outpath,specialData,newer,title);
        using var zip = System.IO.Compression.ZipFile.OpenRead(outpath);
        if (zip.GetEntry("xl/worksheets/sheet1.xml") == null || (file == "health.xlsx" && zip.GetEntry("xl/worksheets/sheet2.xml") == null)) throw new Exception("missing special sheet");
    }
} finally { if (Environment.GetEnvironmentVariable("SALARY_SPECIAL_TEST_DIR") == null) Directory.Delete(specialDir,true); }
Console.WriteLine("Special Excel fixture passed: health, fund and food packages generated.");
var specialPdfDir = Environment.GetEnvironmentVariable("SALARY_SPECIAL_PDF_TEST_DIR") ?? Path.Combine(Path.GetTempPath(), "salary-special-pdf-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(specialPdfDir);
try {
    var font=OperatingSystem.IsWindows()?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts),"arial.ttf"):"/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
    foreach (var (file,title) in new[] { ("health.pdf","الرعاية الصحية"), ("fund.pdf","صندوق الشهداء"), ("food.pdf","شركة عراق الجوال") })
    {
        var output=Path.Combine(specialPdfDir,file); SpecialPdf.Write(output,specialData,newer,title,font);
        var bytes=File.ReadAllBytes(output); if(bytes.Length<2000 || System.Text.Encoding.ASCII.GetString(bytes,0,5)!="%PDF-") throw new Exception("invalid special PDF");
    }
} finally { if(Environment.GetEnvironmentVariable("SALARY_SPECIAL_PDF_TEST_DIR")==null) Directory.Delete(specialPdfDir,true); }
Console.WriteLine("Special PDF fixture passed for health, fund and food.");
var largeSpecialPath = Environment.GetEnvironmentVariable("SALARY_LARGE_HEALTH_PDF") ?? Path.Combine(Path.GetTempPath(), "salary-health-" + Guid.NewGuid().ToString("N") + ".pdf");
try {
    var employees = Enumerable.Range(1,150).Select(i => new Employee(newer,"مشترك وهمي رقم "+i,"موظف","قسم",100,Enc(new Dictionary<string,double>{{"الرعاية الصحية",3500}}),null)).ToList();
    employees.Add(new(newer,"غير مشترك وحيد","شرطي","قسم",100,Enc(new Dictionary<string,double>{{"الرعاية الصحية",0}}),null));
    var fixture = specialData with { Employees = employees };
    var font=OperatingSystem.IsWindows()?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts),"arial.ttf"):"/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
    SpecialPdf.Write(largeSpecialPath,fixture,newer,"الرعاية الصحية",font);
    if(new FileInfo(largeSpecialPath).Length<5000) throw new Exception("large health PDF missing");
} finally { if(Environment.GetEnvironmentVariable("SALARY_LARGE_HEALTH_PDF")==null) File.Delete(largeSpecialPath); }
Console.WriteLine("Large health PDF fixture passed: 150 subscribers and an unsubscribed employee.");
var savedSpecial = Path.Combine(Path.GetTempPath(), "special-preserve-" + Guid.NewGuid().ToString("N"));
try {
    File.WriteAllText(savedSpecial,"old-file");
    var huge = specialData with { Employees = [new(newer,string.Join(" ",Enumerable.Repeat("اسم طويل",5000)),"موظف","قسم",100,Enc(new Dictionary<string,double>{{"الرعاية الصحية",3500}}),null)] };
    var font=OperatingSystem.IsWindows()?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts),"arial.ttf"):"/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
    try { SpecialPdf.Write(savedSpecial,huge,newer,"الرعاية الصحية",font); throw new Exception("oversized PDF row accepted"); } catch(InvalidDataException) { }
    if(File.ReadAllText(savedSpecial)!="old-file") throw new Exception("PDF overwritten after failure");
    try { SpecialExcel.Write(savedSpecial,huge,newer,"الرعاية الصحية"); throw new Exception("oversized Excel cell accepted"); } catch(InvalidDataException) { }
    if(File.ReadAllText(savedSpecial)!="old-file") throw new Exception("Excel overwritten after failure");
} finally { File.Delete(savedSpecial); }
Console.WriteLine("Special export fixture passed: oversized data rejected without overwriting existing file.");
var slipPath = Environment.GetEnvironmentVariable("SALARY_SLIP_TEST_PATH") ?? Path.Combine(Path.GetTempPath(), "salary-slip-" + Guid.NewGuid().ToString("N") + ".png");
try {
    var employee = new Employee(newer,"محمد وهمي عبدالله","موظف","قسم الحسابات",1234567.5,null,null);
    var slipSource = data with { Employees = [employee], Deductions = [new(newer,employee.Name,"شركة وهمية",10000,"إضبارة 123",null),new(newer,employee.Name,"رعاية تجريبية",3500,"",null)] };
    var slip = SlipData.From(slipSource,employee);
    if(slip.Lines.Count != 2 || slip.Net != 1234567.5) throw new Exception("slip data missing");
    var font=OperatingSystem.IsWindows()?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts),"arial.ttf"):"/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
    PayslipImage.Write(slipPath,slip,font);
    using var image=SkiaSharp.SKBitmap.Decode(slipPath);
    if(image==null || image.Width!=780 || image.Height<300) throw new Exception("invalid payslip PNG");
    var old=File.ReadAllBytes(slipPath);
    var huge=slip with { Lines=Enumerable.Range(0,1000).Select(_=>new SlipLine("سطر تجريبي طويل", "",1)).ToList() };
    try { PayslipImage.Write(slipPath,huge,font); throw new Exception("oversized slip accepted"); } catch(InvalidDataException) { }
    if(!File.ReadAllBytes(slipPath).SequenceEqual(old)) throw new Exception("existing image replaced on failure");
    var ambiguousSlip=slipSource with { Employees=[employee,employee] };
    try { SlipData.From(ambiguousSlip,employee); throw new Exception("ambiguous employee accepted"); } catch(InvalidDataException) { }
} finally { if(Environment.GetEnvironmentVariable("SALARY_SLIP_TEST_PATH")==null) File.Delete(slipPath); }
Console.WriteLine("Payslip fixture passed: two deductions, PNG, duplicate-name and overflow protection.");
var bookZip=Path.Combine(Path.GetTempPath(),"salary-book-"+Guid.NewGuid().ToString("N")+".zip");
var bookOut=Path.Combine(Path.GetTempPath(),"salary-book-"+Guid.NewGuid().ToString("N")+".pdf");
try {
    var id=Guid.NewGuid(); var book=new Book(id,"كتاب وهمي",newer,null,1);
    using(var z=System.IO.Compression.ZipFile.Open(bookZip,System.IO.Compression.ZipArchiveMode.Create)) {
        using var w=new StreamWriter(z.CreateEntry($"books/{id.ToString().ToUpperInvariant()}.pdf").Open());w.Write("%PDF-1.7\n%fictional");
    }
    BookArchive.Export(bookZip,book,bookOut);
    if(!File.ReadAllText(bookOut).StartsWith("%PDF-")) throw new Exception("book was not exported");
    var previous=File.ReadAllBytes(bookOut);
    using(var z=System.IO.Compression.ZipFile.Open(bookZip,System.IO.Compression.ZipArchiveMode.Update)) {
        z.GetEntry($"books/{id.ToString().ToUpperInvariant()}.pdf")!.Delete();
        using var w=new StreamWriter(z.CreateEntry($"books/{id.ToString().ToUpperInvariant()}.pdf").Open());w.Write("invalid file");
    }
    try { BookArchive.Export(bookZip,book,bookOut); throw new Exception("invalid book accepted"); } catch(InvalidDataException) {}
    if(!File.ReadAllBytes(bookOut).SequenceEqual(previous)) throw new Exception("previous book overwritten after failure");
} finally { File.Delete(bookZip);File.Delete(bookOut); }
Console.WriteLine("Book fixture passed: PDF header checked and previous file preserved on failure.");
var salaryReportPath=Environment.GetEnvironmentVariable("SALARY_REPORT_TEST_PATH") ?? Path.Combine(Path.GetTempPath(),"salary-report-"+Guid.NewGuid().ToString("N")+".pdf");
try {
    var e=new Employee(newer,"محمد وهمي عبدالله","موظف","قسم الحسابات",950000,
        Enc(new Dictionary<string,double>{{"الاسمي",1000000},{"الضريبة",50000},{"مجموع الاستقطاعات",50000}}),
        Enc(new Dictionary<string,string>{{"الاسمي","1000000"},{"الاستحقاق","1000000"},{"مجموع الاستقطاعات","50000"},{"الضريبة","50000"},{"IBAN","IQ123456789012345678901"},{"الرقم الموحد","12345678"},{"اسم الوكالة","وكالة وهمية"},{"اسم المديرية","مديرية وهمية"},{"ملاحظات","ملاحظة تجريبية"}}));
    var fixture=data with { Employees=[e], Deductions=[new(newer,e.Name,"مصرف وهمي / فرع وهمي",25000,"",null)] };
    var report=SalaryReportData.From(fixture,e,true,true);
    if(report.Net!=950000 || report.EntitlementTotal!=1000000 || report.DeductionTotal!=50000 || report.Entitlements.Count!=1 || report.Deductions.Count!=1 || report.Mismatches.Count!=0 || !report.Info.Single(x=>x.Label=="IBAN").Value.EndsWith("8901") || report.Info.Single(x=>x.Label=="IBAN").Value.Contains("IQ")) throw new Exception("salary report fields failed");
    if(report.Banks.Count!=1 || report.Banks[0].Branch!="فرع وهمي") throw new Exception("salary bank report failed");
    var font=OperatingSystem.IsWindows()?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts),"arial.ttf"):"/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
    SalaryReportPdf.Write(salaryReportPath,report,font);
    if(new FileInfo(salaryReportPath).Length<2000) throw new Exception("invalid salary report PDF");
    var original=File.ReadAllBytes(salaryReportPath);
    var huge=report with { Notes=string.Join(" ",Enumerable.Repeat("بيانات طويلة",200)) };
    try { SalaryReportPdf.Write(salaryReportPath,huge,font); throw new Exception("long salary report accepted"); } catch(InvalidDataException) { }
    if(!File.ReadAllBytes(salaryReportPath).SequenceEqual(original)) throw new Exception("salary PDF overwritten on failure");
    var oldEmployee=e with { Details=null };var oldData=fixture with { Employees=[oldEmployee] };
    if(!SalaryReportData.From(oldData,oldEmployee,false,true).MissingDetails) throw new Exception("old salary missing details not reported");
} finally { if(Environment.GetEnvironmentVariable("SALARY_REPORT_TEST_PATH")==null) File.Delete(salaryReportPath); }
Console.WriteLine("Salary report fixture passed: line mapping, masking, totals, A4 output and overflow protection.");

var settingsDir = Path.Combine(Path.GetTempPath(), "salary-settings-test-" + Guid.NewGuid().ToString("N"));
try {
    var settingsPath = Path.Combine(settingsDir, "settings.json");
    var originalSettings = new WindowsSettings("http://192.168.1.10:8765", true);
    WindowsSettingsStore.Save(originalSettings, settingsPath);
    if (WindowsSettingsStore.Load(settingsPath) != originalSettings) throw new Exception("settings did not persist");
    WindowsSettingsStore.Save(new WindowsSettings("", false), settingsPath);
    if (WindowsSettingsStore.Load(settingsPath) != new WindowsSettings("", false)) throw new Exception("settings did not update");
    File.WriteAllText(settingsPath, "invalid json");
    if (WindowsSettingsStore.Load(settingsPath) != new WindowsSettings()) throw new Exception("invalid settings not handled");
} finally { if (Directory.Exists(settingsDir)) Directory.Delete(settingsDir, true); }
Console.WriteLine("Windows settings fixture passed: save, update and invalid settings recovery.");

foreach (var valid in new[] { "http://192.168.1.10:8765", "http://10.3.4.5:8765", "http://172.16.0.1:8765" })
    if (!BackupReader.ValidServerAddress(new Uri(valid))) throw new Exception("private server rejected");
foreach (var invalidServer in new[] { "http://8.8.8.8", "http://192.168.1.2/path", "https://192.168.1.2", "http://user@192.168.1.2", "http://example.com" })
    if (BackupReader.ValidServerAddress(new Uri(invalidServer))) throw new Exception("unsupported server accepted");
Console.WriteLine("Server settings fixture passed: private addresses only, no paths or credentials.");
var editDir = Path.Combine(Path.GetTempPath(), "salary-account-edit-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(editDir);
try {
    var original = Path.Combine(editDir, "original.zip"); var changed = Path.Combine(editDir, "changed.zip");
    using (var zip = System.IO.Compression.ZipFile.Open(original, System.IO.Compression.ZipArchiveMode.Create)) {
        void Write(string name, string value) { using var w = new StreamWriter(zip.CreateEntry(name).Open()); w.Write(value); }
        Write("manifest.json", "{\"version\":4,\"months\":[\"2026-10\"],\"employees\":1,\"deductions\":0,\"staff\":0,\"accounts\":0,\"books\":0,\"futureField\":\"preserve\"}");
        Write("settings.json", "{}"); Write("months.jsonl", "{\"name\":\"2026-10\"}\n");
        Write("employees.jsonl", "{\"month\":\"2026-10\",\"name\":\"شخص تجريبي\",\"rank\":\"عقود\",\"workplace\":\"قسم\",\"net\":100,\"futureField\":\"untouched\"}\n");
        Write("deductions.jsonl", ""); Write("staff.jsonl", ""); Write("accounts.jsonl", ""); Write("books/index.json", "[]");
        Write("future-file.bin", "future information");
    }
    var before = File.ReadAllBytes(original);
    BackupAccountEditor.SaveCopy(original, changed, new Account("جهة وهمية", "IQ19TEST000000000000000", "فرع", "ABCD"), null);
    var result = BackupReader.Read(changed);
    if (result.Accounts.Count != 1 || result.Accounts[0].Iban != "IQ19TEST000000000000000" || !File.ReadAllBytes(original).SequenceEqual(before)) throw new Exception("account edit failed or source changed");
    using (var zip = System.IO.Compression.ZipFile.Open(changed, System.IO.Compression.ZipArchiveMode.Update)) {
        var entry = zip.GetEntry("accounts.jsonl")!;
        string text; using (var reader = new StreamReader(entry.Open())) text = reader.ReadToEnd();
        var accountNode = System.Text.Json.Nodes.JsonNode.Parse(text)!.AsObject(); accountNode["futureAccountField"] = "preserve";
        entry.Delete(); using var writer = new StreamWriter(zip.CreateEntry("accounts.jsonl").Open()); writer.WriteLine(accountNode.ToJsonString());
    }
    var updated = Path.Combine(editDir, "updated.zip");
    BackupAccountEditor.SaveCopy(changed, updated, new Account("جهة وهمية", "IQ19TEST000000000000000", "فرع جديد", "EFGH"), "جهة وهمية");
    var afterUpdate = BackupReader.Read(updated);
    if (afterUpdate.Accounts.Count != 1 || afterUpdate.Accounts[0].Branch != "فرع جديد") throw new Exception("account update failed");
    using (var zip = System.IO.Compression.ZipFile.OpenRead(updated)) { using var reader = new StreamReader(zip.GetEntry("accounts.jsonl")!.Open()); if (!reader.ReadToEnd().Contains("futureAccountField")) throw new Exception("unknown account field lost"); }
    try { BackupAccountEditor.SaveCopy(changed, updated, new Account("جهة وهمية", "IQ19TEST000000000000000", "", ""), null); throw new Exception("duplicate account accepted"); } catch (InvalidDataException) { }
    if (BackupReader.Read(updated).Accounts[0].Branch != "فرع جديد") throw new Exception("duplicate account changed previous output");
    using (var zip = System.IO.Compression.ZipFile.OpenRead(changed)) {
        using var reader = new StreamReader(zip.GetEntry("employees.jsonl")!.Open());
        if (!reader.ReadToEnd().Contains("futureField") || zip.GetEntry("future-file.bin") == null) throw new Exception("unknown data lost");
        using var manifest = new StreamReader(zip.GetEntry("manifest.json")!.Open());
        if (!manifest.ReadToEnd().Contains("futureField")) throw new Exception("unknown manifest field lost");
    }
    try { BackupAccountEditor.SaveCopy(original, changed, new Account("جهة", "IQ00BAD", "", ""), null); throw new Exception("invalid IBAN accepted"); } catch (InvalidDataException) { }
    if (BackupReader.Read(changed).Accounts.Count != 1) throw new Exception("invalid IBAN replaced prior output");
    try { BackupAccountEditor.SaveCopy(original, original, new Account("جهة", "IQ19TEST000000000000000", "", ""), null); throw new Exception("source overwrite accepted"); } catch (InvalidDataException) { }
} finally { Directory.Delete(editDir, true); }
Console.WriteLine("Backup account edit fixture passed: new account, IBAN checks, unknown fields and source protection.");

var portraitPreview = PreviewGeometry.Fit(595, 842);
var tallPreview = PreviewGeometry.Fit(1, 100000);
if (portraitPreview.Width > 1400 || portraitPreview.Height > 2000 || portraitPreview.Width < 1 || tallPreview.Width != 1 || tallPreview.Height != 2000) throw new Exception("preview size bounds failed");
foreach (var invalidSize in new[] { (0d, 100d), (double.NaN, 200d), (100d, double.PositiveInfinity) })
    try { PreviewGeometry.Fit(invalidSize.Item1, invalidSize.Item2); throw new Exception("invalid preview size accepted"); } catch (InvalidDataException) { }
Console.WriteLine("Preview geometry fixture passed: page aspect and invalid dimensions.");
