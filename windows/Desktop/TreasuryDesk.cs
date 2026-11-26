using System.IO;
using System.Windows.Controls;
using A=SalaryDesktop.Core;
namespace AnbarPayroll.Desktop;
public partial class MainWindow
{
    static string?[] TreasurySources(A.LetterTemplate template,string id,IEnumerable<string> names)
    {
        var available=names.ToHashSet(StringComparer.Ordinal);
        string? Resolve(int index,params string[] labels){
            if(template.TreasuryItems is {} saved && index<saved.Length)
                return available.Contains(saved[index])?saved[index]:null;
            var keys=labels.Select(A.PayrollAnalysis.Normalize).ToHashSet();
            var matches=available.Where(n=>keys.Contains(A.PayrollAnalysis.Normalize(n))).Take(2).ToArray();
            return matches.Length==1?matches[0]:null;
        }
        return id=="treasury_guarantee"?[Resolve(0,"تضمين","التضمين")]:
            [Resolve(0,"رسم طابع","رسم الطابع","رسم الطابع الضريبي"),Resolve(1,"بناء المدارس","بناء مدارس")];
    }

    void TreasuryBook(A.BackupSnapshot s,string month,string id){
        bool schools=id=="treasury_schools";var names=A.TemplateLetterPdf.Items(s,month);
        if(names.Length==0)throw new InvalidDataException("لا توجد بنود رواتب ذات مبالغ لهذا الشهر.");
        var first=Choices(names);var second=Choices(names);var reason=new TextBox();
        var template=Builtin(s,id);
        var sources=TreasurySources(template,id,names);
        first.SelectedItem=sources[0];if(schools)second.SelectedItem=sources[1];
        var inputs=Column(Labeled(schools?"بند رسم الطابع الضريبي":"بند التضمين",first,360));
        inputs.Children.Add(schools?Labeled("بند بناء المدارس",second,360):Labeled("سبب التضمين",reason,360));
        if(!Form(BookTitles[id],inputs))return;
        if(first.SelectedItem is not string firstName || schools && second.SelectedItem is not string)throw new InvalidDataException("اختر بنود مبلغ كتاب الخزينة.");
        string secondName=second.SelectedItem as string??"";
        if(schools&&firstName==secondName)throw new InvalidDataException("اختر بندين مختلفين.");
        if(!schools&&string.IsNullOrWhiteSpace(reason.Text))throw new InvalidDataException("أدخل سبب التضمين.");
        string description=reason.Text.Trim();
        var report=A.StandardReports.ItemList(s,month,firstName);
        string Money(long value)=>A.AdaptiveLetterPdf.Eastern(A.AdaptiveLetterPdf.Grouped(value));
        long Total(A.ReportSheet sheet)=>A.TemplateLetterPdf.Whole(sheet.Amounts!.Sum(v=>(decimal)A.TemplateLetterPdf.Whole(v)));
        var summaries=A.TemplateLetterPdf.Summary(s,month);
        void Verify(string item,A.ReportSheet sheet){
            if(!summaries.TryGetValue(item,out var summary)||summary.Count!=sheet.Rows.Count||summary.Total!=sheet.Total)
                throw new InvalidDataException("تفاصيل الرواتب لا تطابق ملخص بند "+item);
        }
        Verify(firstName,report);
        long total=Total(report);
        A.LetterTable table;
        if(schools){
            var secondReport=A.StandardReports.ItemList(s,month,secondName);Verify(secondName,secondReport);long other=Total(secondReport);var stamp=total;total=A.TemplateLetterPdf.Whole((decimal)total+other);
            table=new([], [new[]{"رسم طابع ضريبي",Money(stamp)},new[]{"بناء المدارس",Money(other)},new[]{"المجموع",Money(total)}],"Times New Roman",22,[2,1],BoldLastRow:true,WidthFraction:.5,AlignRight:true);
        }else{
            var ordered=report.Rows.Select((r,i)=>(r,amount:report.Amounts![i])).OrderBy(x=>A.PayrollAnalysis.Normalize(x.r[2])).ToList();
            var notes=ordered.Select(_=>new TextBox()).ToArray();var form=Column();
            for(int i=0;i<ordered.Count;i++){
                var r=ordered[i].r;if(string.IsNullOrWhiteSpace(r[1])||string.IsNullOrWhiteSpace(r[2]))throw new InvalidDataException("أكمل الاسم والرتبة في ملف الرواتب.");
                form.Children.Add(Labeled(r[1]+" "+r[2]+" · "+Money(A.TemplateLetterPdf.Whole(ordered[i].amount)),notes[i],400));
            }
            if(!Form("رقم وتاريخ الإضبارة",form))return;
            var rows=ordered.Select((p,i)=>new[]{A.AdaptiveLetterPdf.Eastern((i+1).ToString()),p.r[1],p.r[2],Money(A.TemplateLetterPdf.Whole(p.amount)),"خزينة الأنبار",notes[i].Text.Trim()}).ToList();
            rows.Add(["","","المجموع",Money(total),"",""]);
            table=new(["ت","الرتبة","الاسم الكامل","المبلغ","جهة التنفيذ","رقم وتاريخ الإضبارة"],rows,"Times New Roman",20,[30,70,150,80,110,130],BoldLastRow:true);
        }
        template=template with{TreasuryItems=schools?[firstName,secondName]:[firstName]};
        var payer=A.TransferPreparation.Payer(s).Iban;
        if(string.IsNullOrEmpty(template.Account)&&s.SettingsPlists.ContainsKey("transferReferenceArchiveData"))
            template=template with{Account=A.LetterReferencePack.IndividualAccount(A.LetterReferencePack.Read(s),"tax_social",payer)??""};
        byte[] Render(A.LetterTemplate t){
            t.Validate();A.IndividualLetterBody.Validate(id,t.Body);
            var beneficiary=A.TransferPreparation.NormalizeAccount(t.Account);
            if(!A.TransferPreparation.ValidIban(payer)||!A.TransferPreparation.ValidIban(beneficiary)||beneficiary==payer)throw new InvalidDataException("راجع آيبان الجهة الدافعة والخزينة.");
            int key=A.MonthNames.SortKey(month);
            var values=new Dictionary<string,string>{{"المبلغ",Money(total)},{"المبلغ كتابة",A.ArabicAmountWords.Dinars(total)},{"الشهر",A.AdaptiveLetterPdf.MonthName(key%100)},{"السنة",A.AdaptiveLetterPdf.Eastern((key/100).ToString())},{"حسابنا",payer},{"حساب المستفيد",beneficiary},{"سبب التضمين",description}};
            var result=A.TemplateLetterPdf.Document(t,month,A.LetterTemplate.Fill(A.LetterTemplate.FormatNumbers(t.Body),values),DocumentFonts(),table,[payer,beneficiary,total.ToString()]);
            using var document=UglyToad.PdfPig.PdfDocument.Open(result);
            if(document.NumberOfPages!=1)throw new InvalidDataException("لا يتسع كتاب الخزينة في صفحة واحدة. راجع حجم الخط وطول المتن والجدول.");
            return result;
        }
        var draft=EditLetter(template,id,s,month,true,Render);if(draft==null)return;
        var pdf=Render(draft);store.SaveLetterTemplate(s,id,draft);ShowPage(CurrentTransfers);
        _=SaveFile(BookTitles[id],"pdf",()=>pdf);
    }
}
