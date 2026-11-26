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
            result.Append(c switch { 'أ' or 'آ' or 'إ' or 'ٱ' => 'ا', 'ى'=>'ي','ة'=>'ه',_=>char.ToLowerInvariant(c) });
        }
        return Regex.Replace(result.ToString().Trim(), @"\s+"," ");
    }
    public static string CaseKey(string note) => Regex.Replace(Regex.Replace(Digits(note).Trim(),@"\s+"," "),@"\s*/\s*","/");
    public static int Period(string label)
    {
        var parts=Regex.Split(Key(label),@"[\s_./-]+").Where(x=>x.Length>0).ToList();
        var year=parts.FirstOrDefault(x=>x.Length==4 && int.TryParse(x,out _));
        if(year is null)return 0;
        parts.Remove(year);
        string name=string.Join(" ",parts);
        var index=Array.FindIndex(Months,x=>Key(x)==name);
        if(index<0)index=Array.FindIndex(Alternate,x=>Key(x)==name);
        int m=index+1;
        if(m==0 && !int.TryParse(name,out m))return 0;
        return m is >=1 and <=12 ? int.Parse(year)*100+m : 0;
    }
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
public record Filters(string Search="",string Agency="",string Reference="",int Year=0,int Month=0,string Person="",bool ExactReference=false);
public record Review(string Title,string Agency,string Month);
public record BankRow(string Month,string Date,string Description,string Direction,long Amount);
