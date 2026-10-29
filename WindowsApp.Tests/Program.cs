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
    if (xml.Descendants(n + "sheetView").First().Attribute("rightToLeft")?.Value != "1" || xml.Descendants(n + "row").Count() != 4) throw new Exception("invalid sheet");
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
    if (xml.Descendants(ns + "row").Count() != 5 || !xml.Descendants(ns + "v").Any(v => v.Value == "1101")) throw new Exception("payroll report totals failed");
} finally { File.Delete(payrollFile); }
Console.WriteLine("Payroll export fixture passed: rows and totals.");
var pdfOutput = Environment.GetEnvironmentVariable("SALARY_PDF_TEST_PATH") ?? Path.Combine(Path.GetTempPath(), "salary-pdf-" + Guid.NewGuid().ToString("N") + ".pdf");
try {
    var lines = Enumerable.Range(1, 120).Select(i => new PdfExport.Line($"موظف تجريبي رقم {i}", "موظف", i.ToString(), "قسم الحسابات")).ToList();
    var font = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf") : "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
    PdfExport.Write(pdfOutput, "تشرين الأول 2026", "كشف تجريبي", lines, font);
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
