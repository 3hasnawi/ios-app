using System.Security.Cryptography;
using AnbarPayroll;
using ClosedXML.Excel;

var path=args.Single();var store=new Store(Path.Combine(path,"store"));
void Check(bool condition,string name){if(!condition)throw new Exception(name);Console.WriteLine("PASS "+name);}
string source=Path.Combine(path,"valid.zip");
for(int i=0;i<3;i++)store.Import(source);
Check(Directory.GetDirectories(store.Root).Length==1,"repeated import retains one snapshot");
Check(SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(store.Archive))),"same compact ZIP bytes");
var active=store.Active;try{store.Import(Path.Combine(path,"damaged.zip"));throw new Exception("accepted changed content");}catch(InvalidDataException){}
Check(active==store.Active,"corrupt replacement is rolled back");
var months=store.Months();Check(months.Count==4&&months[0].Key==202611,"canonical Iraqi periods");
Check(store.Reviews(months[0]).Count==2,"new and absent agency alerts");
var f=new Filters(Search:"ادم",Month:11,Year:2026);var one=store.Find(f).Single();
Check(one.Amount==13000&&store.Summary(f)==(1,13000m),"combined filters / precise amount");
var employeeSearch=f with{SearchField=DeductionSearchField.Employee};
Check(store.Find(employeeSearch).Single()==one&&store.Summary(employeeSearch)==(1,13000m),"employee search returns only that employee's deductions for the selected month");
Check(!store.Find(employeeSearch with{Search="مديرية"}).Any(),"employee search excludes matches in unrelated fields");
var agencySearch=new Filters(Agency:"مديرية تنفيذ الربيع",Year:2026,Month:11,Category:"مديريات التنفيذ");
var scopedRows=store.Find(agencySearch).ToArray();
var scopedSheet=Statement.Entities(scopedRows,"قسم الحسابات",true).Sheets.Single();
Check(scopedRows.Length==4&&scopedRows.All(r=>r.Period==202611&&r.Agency=="مديرية تنفيذ الربيع")&&store.Summary(agencySearch).Total==scopedSheet.Total&&scopedSheet.Total==232000,"agency search, grid, and export share the selected directorate and month");
Check(store.Find(new(Reference:"١٧٠/٢٠٢٤")).Count()==4,"combined case and date, digit-normalized");
Check(store.Metadata("bank").Contains("movements"),"binary plist bank settings");
var entity=Statement.Entities(store.Find(new(Year:2026,Month:11)),store.Metadata("header"),true);
Check(entity.Sheets[0].Total==580000,"total equals all grouped details");
string xlsx=Path.Combine(path,"agency.xlsx");entity.Excel(xlsx);
using(var wb=new XLWorkbook(xlsx)){var w=wb.Worksheet(1);Check(w.RightToLeft&&w.Cell(6,4).GetDouble()==580000,"reopen workbook and total formula");}
var employee=Statement.Employee(store.Find(new(Person:one.Name)),months,one.Name,one.Agency,one.Reference,202502,202611,"بيانات وهمية");
Check(employee.Partial&&employee.PaidMonths==1&&employee.Sheets.Sum(y=>y.Total)==13000,"multi-year statement, exact case, incomplete months marked");
Check(entity.Html().Contains("580,000")&&employee.Html().Contains("13,000"),"preview values");
Check(Arabic.Period("رواتب تشرين٢_٢٠٢٦ تجريبي")==202611,"iPhone short month labels");
Check(store.Summary(new(Year:2026,Month:11,Category:"مديريات التنفيذ")).Total==232000,"exact group scope shared by grid and export");
var archived=store.Books().Single();var copy=Path.Combine(path,"book.pdf");store.SaveBook(archived,copy);
Check(File.ReadAllBytes(copy).SequenceEqual(File.ReadAllBytes(Path.Combine(path,"original-book.pdf"))),"archived iPhone PDF is byte-identical");
Check(store.PersonStatement(one.Name.Replace("آ","ا").Replace(" ",""),one.Agency,one.Reference,202611,202611,"").Sheets.Single().Total==13000,"iPhone employee-name normalization");
try{
    Statement.Employee([one],[months[0],months[0]],one.Name,one.Agency,one.Reference,202611,202611,"");
    throw new Exception("ambiguous period allowed");
}catch(InvalidDataException){Console.WriteLine("PASS duplicate periods cannot produce a statement");}
var alternative=new Store(Path.Combine(path,"variants"));
alternative.Import(Path.Combine(path,"duplicate.zip"));
try{
    alternative.PersonStatement(one.Name,one.Agency,one.Reference,202611,202611,"");
    throw new Exception("ambiguous employee allowed");
}catch(InvalidDataException){Console.WriteLine("PASS duplicate payroll identity cannot issue a statement");}
alternative.Import(Path.Combine(path,"refunds.zip"));
Check(alternative.Metadata("refunds")=="1"&&File.ReadAllBytes(alternative.Archive).SequenceEqual(File.ReadAllBytes(Path.Combine(path,"refunds.zip"))),"v14 draft, batch and binary supporting data survive intact");
if(OperatingSystem.IsWindows())AccessImportCheck.Run(path,Check);
await WindowsUpdateChecks.Run(path,Check);
AccountingTests.Run(path,Check);

PrintChecks.Run(path,Check);
AccountingTests.Volume(path,Check);
