using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AnbarPayroll;

public static class Arabic
{
    public static readonly string[] Months = ["كانون الثاني","شباط","آذار","نيسان","أيار","حزيران","تموز","آب","أيلول","تشرين الأول","تشرين الثاني","كانون الأول"];
    private static readonly string[] Alternate = ["يناير","فبراير","مارس","أبريل","مايو","يونيو","يوليو","أغسطس","سبتمبر","أكتوبر","نوفمبر","ديسمبر"];
    public static string Digits(string text) => string.Concat(text.Select(c => c is >= '٠' and <= '٩' ? (char)('0'+c-'٠') : c is >= '۰' and <= '۹' ? (char)('0'+c-'۰') : c));
    public static string Key(string text)
    {
        var result = new StringBuilder();
        foreach (var c in Digits(text))
        {
            if (c=='ـ' || CharUnicodeInfo.GetUnicodeCategory(c)==UnicodeCategory.NonSpacingMark) continue;
            result.Append(c switch { 'أ' or 'آ' or 'إ' or 'ٱ' => 'ا', 'ى' or 'ئ'=>'ي','ؤ'=>'و','ة'=>'ه',_=>char.ToLowerInvariant(c) });
        }
        return Regex.Replace(result.ToString().Trim(), @"\s+"," ");
    }
    public static string Match(string name)=>Key(name).Replace(" ","");
    public static string CaseKey(string note) => Regex.Replace(Regex.Replace(Digits(note).Trim(),@"\s+"," "),@"\s*/\s*","/");
    public static int Period(string label)
    {
        var parts=Regex.Split(Key(label),@"[\s_./-]+").Where(x=>x.Length>0).ToList();
        int year=parts.Select(word=>int.TryParse(word,out var n)?n:0).LastOrDefault(n=>n is >=1900 and <=2200);
        if(year==0)return 0;
        for(int i=0;i<parts.Count;i++){
            string word=parts[i],pair=i+1<parts.Count?word+" "+parts[i+1]:"";
            int month=word switch{"كانون2"=>1,"تشرين1"=>10,"تشرين2"=>11,"كانون1"=>12,_=>0};
            if(month==0)month=Array.FindIndex(Months,m=>Key(m)==word||Key(m)==pair)+1;
            if(month==0)month=Array.FindIndex(Alternate,m=>Key(m)==word)+1;
            if(month!=0)return year*100+month;
        }
        int numeric=parts.Select(w=>int.TryParse(w,out var n)?n:0).FirstOrDefault(n=>n is >=1 and <=12);
        return numeric==0?0:year*100+numeric;
    }
    public static string Category(string title)
    {
        var key=Key(title);
        if(key.StartsWith("مديريه تنفيذ")||key.StartsWith("مديريه التنفيذ"))return "مديريات التنفيذ";
        if(key.Contains("مصرف")||key.StartsWith("م "))return "المصارف";
        return "الجهات الأخرى";
    }
    public static string Eastern(string value)=>string.Concat(value.Select(c=>c is >= '0' and <= '9'?(char)('٠'+c-'0'):c));
    public static string Money(decimal value)=>value.ToString("#,##0.############################",CultureInfo.InvariantCulture);
}
public record Deduction(string Month,int Period,string Name,string Rank,string Agency,string Reference,decimal? Amount,string Status)
{
    public string Value=>Amount.HasValue?Arabic.Money(Amount.Value):Status;
}
public record MonthInfo(string Name,int Key,int Expected,int Actual)
{
    public bool Verified=>Expected>0&&Expected==Actual;
    public override string ToString()=>Name;
}
public enum DeductionSearchField { Any, Agency, Employee }
public record Filters(string Search="",string Agency="",string Reference="",int Year=0,int Month=0,string Person="",bool ExactReference=false,string Category="",DeductionSearchField SearchField=DeductionSearchField.Any);
public record Review(string Title,string Agency,string Month);
public record ArchivedBook(Guid Id,string Title,string Month,string Agency,int Pages);
