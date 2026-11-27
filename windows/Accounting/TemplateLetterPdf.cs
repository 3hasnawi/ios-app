using System.Globalization;
namespace SalaryDesktop.Core;
public static class TemplateLetterPdf
{
    public sealed record ItemSummary(int Count, decimal Total);
    public static Dictionary<string,ItemSummary> Summarize(IEnumerable<Employee> employees){
        var result=new Dictionary<string,ItemSummary>(StringComparer.Ordinal);
        foreach(var employee in employees)foreach(var item in StandardReports.Items(employee)){
            result.TryGetValue(item.Key,out var old);
            result[item.Key]=new((old?.Count??0)+1,(old?.Total??0)+item.Value);
        }
        return result;
    }
    public static Dictionary<string,ItemSummary> Summary(BackupSnapshot s,string month){
        if(s.Months.SingleOrDefault(m=>m.Name==month)?.Summary is {Length:>0} data)
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string,ItemSummary>>(data,LetterTemplate.Json)??throw new InvalidDataException("تعذرت قراءة ملخص بنود الشهر.");
        return Summarize(s.Employees.Where(e=>e.Month==month));
    }
    public static string[] Items(BackupSnapshot s,string month)=>Summary(s,month).Where(p=>p.Value.Total!=0).Select(p=>p.Key).Order().ToArray();
    public static long Whole(decimal value)=>value>0&&value<=9_007_199_254_740_991m&&value==decimal.Truncate(value)?checked((long)value):throw new InvalidDataException("المبلغ يجب أن يكون عدداً صحيحاً موجباً.");
    public static byte[] Custom(LetterTemplate t,BackupSnapshot s,string month,decimal? manual,LetterFonts fonts){
        t.Validate();
        var key=MonthNames.SortKey(month);if(key==0)throw new InvalidDataException("اختر شهر الكتاب.");
        var values=new Dictionary<string,string>{{"الشهر",AdaptiveLetterPdf.MonthName(key%100)},{"السنة",AdaptiveLetterPdf.Eastern((key/100).ToString())}};
        var checks=new List<string>();
        decimal? amount=t.Source switch{
            "none"=>null,"manual"=>manual??throw new InvalidDataException("أدخل مبلغ الكتاب."),
            "payroll"=>Summary(s,month).GetValueOrDefault(t.SourceName)?.Total??throw new InvalidDataException("بند الرواتب غير موجود في الشهر المختار."),
            "execution"=>PayrollAnalysis.Groups(s,month).SingleOrDefault(g=>g.Title==t.SourceName&&g.MissingAmounts==0)?.Total??throw new InvalidDataException("راجع مبالغ الجهة في الشهر المختار."),
            _=>throw new InvalidDataException("مصدر المبلغ غير معروف.")};
        if(amount!=null){
            var n=Whole(amount.Value);
            if(!t.Body.Contains("{{المبلغ}}")&&!t.Body.Contains("{{المبلغ كتابة}}"))throw new InvalidDataException("أضف حقل المبلغ إلى المتن.");
            values["المبلغ"]=AdaptiveLetterPdf.Eastern(AdaptiveLetterPdf.Grouped(n));values["المبلغ كتابة"]=ArabicAmountWords.Dinars(n);
            if(t.Body.Contains("{{المبلغ}}"))checks.Add(n.ToString(CultureInfo.InvariantCulture));
        }
        if(!string.IsNullOrWhiteSpace(t.Account)){
            var account=TransferPreparation.NormalizeAccount(t.Account);
            if(!TransferPreparation.ValidIban(account)&&!(account.Length==15&&account.All(char.IsAsciiDigit)))throw new InvalidDataException("راجع رقم الحساب.");
            if(!t.Body.Contains("{{الحساب}}"))throw new InvalidDataException("أضف حقل الحساب إلى المتن.");
            values["الحساب"]=account;checks.Add(account);
        }
        var formatted=t with{Header=LetterTemplate.FormatNumbers(t.Header),Recipient=LetterTemplate.FormatNumbers(t.Recipient),Subject=LetterTemplate.FormatNumbers(t.Subject),Footer=LetterTemplate.FormatNumbers(t.Footer)};
        return Document(formatted,month,LetterTemplate.Fill(LetterTemplate.FormatNumbers(t.Body),values),fonts,null,checks,fitFooterWritingSpace:true);
    }
    public static byte[] Document(LetterTemplate t,string month,string body,LetterFonts fonts,LetterTable? table,IReadOnlyList<string> checks,bool fitFooterWritingSpace=false){
        var style=t.Style();var date=AdaptiveLetterPdf.OutgoingDate(month,style.OutgoingMonth);
        var blocks=AdaptiveLetterPdf.Opening(style,date,body);
        if(table!=null)blocks.Add(LetterBlock.Of(table));
        blocks.Add(AdaptiveLetterPdf.Signatures(style,date));blocks.Add(AdaptiveLetterPdf.Footer(style, fitFooterWritingSpace));
        return AdaptiveLetterPdf.Write(blocks,fonts,false,style.SignatureOffset,checks,t);
    }
}
