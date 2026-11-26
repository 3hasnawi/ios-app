using System.IO.Compression;
using System.Text.Json;
using AnbarPayroll;
using ClosedXML.Excel;
using UglyToad.PdfPig;
using A=SalaryDesktop.Core;

static class PrintChecks
{
    public static void Run(string dir,Action<bool,string> check){
        if(!OperatingSystem.IsWindows())return;
        string folder=Path.Combine(dir,"printed");Directory.CreateDirectory(folder);
        string fonts=Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var assets=new A.ReportAssets(File.ReadAllBytes(Path.Combine(fonts,"arial.ttf")),File.ReadAllBytes(Path.Combine(fonts,"arialbd.ttf")),null);
        var logo=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"IraqEmblem.png"));var times=File.ReadAllBytes(Path.Combine(fonts,"timesbd.ttf"));
        var style=new A.GroupLetterSettings("ترويسة تجريبية\nقسم الحسابات","١‏/٢٢/ح/","العضو الأول","العضو الثاني","العضو الثالث","نسخة منه إلى\nشعبة الحسابات",null,null,0,null);
        var s=new Store(Path.Combine(dir,"print-source"));s.Import(Path.Combine(dir,"exchange","windows-exchange.zip"));
        // يضم شهر المقارنة بندين فعليين حتى تُختبر تصفية كشفي الرعاية والصندوق من الواجهة.
        using(var demo=new XLWorkbook(Path.Combine(dir,"monthly.xlsx"))){
            var pay=demo.Worksheet("الرواتب");
            // فروق معروفة بين شهري المقارنة لاختبار التنقل بين بطاقات المراجعة.
            pay.Cell(2,3).Value="قسم سابق تجريبي";
            pay.Cell(2,4).Value="4500";
            pay.Cell(2,12).Value=2000;pay.Cell(2,13).Value=3500;
            var path=Path.Combine(dir,"screen-october.xlsx");demo.SaveAs(path);
            s.ApplyExcel(s.PreviewExcel(path,"تشرين الأول 2026"),s.Active);
        }
        var cycle=new Store(Path.Combine(dir,"monthly-cycle")).Snapshot();
        s.ImportStaff(A.StaffRecords.Read(cycle),"ملاك تجريبي",s.Active);
        s.ImportTrust(A.SalaryTrust.ReadFromBackup(cycle),s.Active);
        var snapshot=s.Snapshot("تشرين الثاني 2026");var refund=s.Refunds().Single();var groups=refund.Groups();
        void Pdf(string name,byte[] bytes,Func<int,bool> pages){using var pdf=PdfDocument.Open(bytes);check(pages(pdf.NumberOfPages)&&pdf.GetPages().All(p=>p.Letters.Count>0),"vector PDF page count and selectable text "+name);File.WriteAllBytes(Path.Combine(folder,name+".pdf"),bytes);}
        var t=A.LetterTemplate.From(style,"اختبار القالب","يرجى صرف المبلغ ({{المبلغ}}) لشهر {{الشهر}} من عام {{السنة}}.") with{FirstSigner="رئيس اللجنة\nاسم تجريبي\nمدير الحسابات",SecondSigner="",ThirdSigner="",Attachments="قائمة واحدة",Source="manual",BodyKashida="medium",SignatureHorizontalOffset=5,FontSizes=new(){{"body",13},{"signatures",10},{"attachments",11}}};
        Pdf("custom-template",A.TemplateLetterPdf.Custom(t,snapshot,"تشرين الثاني 2026",123456,A.LetterFonts.Resolve(times,assets.Bold,logo)),n=>n==1);
        foreach(var group in groups)Pdf("refund-"+group.Code,refund.Letter([group],style,A.LetterFonts.Resolve(times,assets.Bold,logo)),n=>n==1);
        var sheet=refund.ListSheet("اختبار");Pdf("refund-list",A.ReportPdf.Generate([sheet],assets,DateTime.UtcNow),n=>n==1);
        var split=Store.ActiveBatch(snapshot,"تشرين الثاني 2026")!;
        var groupPlan=A.GroupLetterPreparation.Prepare(split.Plan(true),A.TransferGroup.OtherBanks,refund.Date);
        Pdf("deferred-group",A.GroupLetterPdf.Generate(groupPlan,style,times,assets.Bold,logo),n=>n==2);
        var monthly=A.MonthlyBankReport.Sheet(snapshot,A.BankStatementCalculator.Calculate(snapshot,"تشرين الثاني 2026"));
        Pdf("bank-month",A.ReportPdf.Generate([monthly],assets,DateTime.UtcNow),n=>n==1);
        var period=A.BankReconciliation.ReadFromBackup(snapshot).Single();
        var extra=Enumerable.Range(1,90).Select(i=>new A.BankMovement(Guid.NewGuid(),period.Start,"صرف دفعة تجريبية من المصرف "+i,i,A.MovementDirection.Debit,Matched:true));
        Pdf("bank-long",A.ReconciliationPdf.Statement(period with{Movements=period.Movements.Concat(extra).ToArray(),OpeningBalance=10000,Committee=(period.Committee??new A.BankCommittee()) with{SecondDate="٢٠٢٦ / ١٢"}},A.TransferPreparation.Account(snapshot),true,assets),n=>n>=3);
        var trust=new A.SalaryTrustEntry(Guid.NewGuid().ToString(),"اسم أمانة تجريبي","عقد","مركز تجريبي",2026,11,3500,"حالة تجريبية","ملاحظة طويلة","مصروف",true,false,[],null,"٢٠٢٦/١١/١٠");
        var trustSheet=A.SalaryTrustReports.Sheet(new(2,[trust],true),[trust],"اختبار","الكل","الكل","");
        Pdf("trust",A.SalaryTrustReports.Pdf(trustSheet,assets,DateTime.UtcNow),n=>n==1);
        using var xl=new XLWorkbook(new MemoryStream(A.SalaryTrustReports.Excel(trustSheet,[trust])));
        check(xl.Worksheet(1).Cell(4,10).GetString()==trust.ImportedPaymentDate,"trust date and notes survive Excel output");
    }
}
