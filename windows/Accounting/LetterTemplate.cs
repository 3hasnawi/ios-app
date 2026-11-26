using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SalaryDesktop.Core;

public sealed record LetterTemplate
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web){DefaultIgnoreCondition=JsonIgnoreCondition.WhenWritingNull};
    public Guid Id {get;init;} = Guid.NewGuid();
    public string Title {get;init;} = "";
    public string Header {get;init;} = "جمهورية العراق\nوزارة الداخلية\nقيادة شرطة محافظة الأنبار\nقسم الحسابات";
    public string Number {get;init;} = "١‏/٢٢/ح/";
    public string Recipient {get;init;} = "مصرف الرافدين / فرع الأنبار";
    public string Subject {get;init;} = "تحويل مبلغ";
    public string Body {get;init;} = "";
    public string FirstSigner {get;init;} = "";
    public string SecondSigner {get;init;} = "";
    public string ThirdSigner {get;init;} = "";
    public string Footer {get;init;} = "نسخة منه إلى\nشعبة المصروفات / شعبة الموازنة / شعبة الرواتب / إجراء اللازم والمتابعة.";
    public string Source {get;init;} = "none";
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string[]? TreasuryItems {get;init;}
    public string SourceName {get;init;} = "";
    public string Account {get;init;} = "";
    public string? BodyAlignment {get;init;}
    public string? BodyKashida {get;init;}
    public string? BodySpacing {get;init;}
    public bool? BodyBold {get;init;}
    public int? SignatureVerticalOffset {get;init;}
    public int? SignatureHorizontalOffset {get;init;}
    public string? Attachments {get;init;}
    public string? FoodContractReference {get;init;}
    public string? BodyMonth {get;init;}
    public string? OutgoingMonth {get;init;}
    public Dictionary<string,int>? FontSizes {get;init;}
    public static readonly string[] Parts=["header","reference","recipient","subject","salutation","body","closing","attachments","signatures","footer","table"];
    public int Size(string part,int fallback)=>FontSizes?.TryGetValue(part,out int pt)==true ? pt*2 : fallback;
    public GroupLetterSettings Style(){
        int? month=null;
        if(!string.IsNullOrWhiteSpace(OutgoingMonth)){
            if(OutgoingMonth.Length>2||OutgoingMonth.Any(c=>!char.IsDigit(c))||!int.TryParse(TransferPreparation.NormalizeAccount(OutgoingMonth),out int n)||n is <1 or >12)throw new InvalidDataException("شهر الصادر من ١ إلى ١٢.");
            month=n;
        }
        if(!string.IsNullOrWhiteSpace(BodyMonth)&&MonthNames.SortKey(BodyMonth)==0)throw new InvalidDataException("أدخل اسم الشهر والسنة في شهر المتن.");
        return new(Header,Number,FirstSigner,SecondSigner,ThirdSigner,Footer,BodyMonth,month,SignatureVerticalOffset??0,Body,FoodContractReference,this);
    }
    public static LetterTemplate From(GroupLetterSettings s,string title,string body){
        var t=s.Template??new LetterTemplate{Title=title};
        return t with{Header=Default(s.Header,t.Header),Number=Default(s.Number,t.Number),Footer=Default(s.Footer,t.Footer),
            Body=Default(s.BodyTemplate,body),FirstSigner=s.First,SecondSigner=s.Second,ThirdSigner=s.Third,
            BodyMonth=s.BodyMonth,OutgoingMonth=s.OutgoingMonth?.ToString(),SignatureVerticalOffset=s.SignatureOffset,FoodContractReference=s.FoodContractReference};
    }
    static string Default(string? s,string fallback)=>string.IsNullOrWhiteSpace(s)?fallback:s;
    public void Validate(bool issuing=true){
        var texts=new[]{Title,Header,Number,Recipient,Subject,Body,Footer,FirstSigner,SecondSigner,ThirdSigner,Account,SourceName,BodyMonth??"",OutgoingMonth??"",FoodContractReference??"",Attachments??""};
        if(TreasuryItems is {} items && (items.Length is <1 or >2 || items.Any(i=>string.IsNullOrWhiteSpace(i)||i.Length>1000) || items.Distinct().Count()!=items.Length))
            throw new InvalidDataException("راجع بنود مبلغ كتاب الخزينة.");
        if(texts.Any(s=>s==null||Encoding.UTF8.GetByteCount(s)>12000||s.Any(c=>char.IsControl(c)&&c!='\n'&&c!='\t'))||Id==Guid.Empty||string.IsNullOrWhiteSpace(Title)
            ||FontSizes?.Any(p=>!Parts.Contains(p.Key)||p.Value is <8 or >24)==true||SignatureVerticalOffset is <-60 or >60||SignatureHorizontalOffset is <-30 or >30
            ||Source is not ("none" or "manual" or "payroll" or "execution")||BodyKashida is not(null or "none" or "low" or "medium" or "high")
            ||BodySpacing is not(null or "compact" or "medium" or "wide")||BodyAlignment is not(null or "justified" or "right" or "left" or "center"))throw new InvalidDataException("راجع بيانات قالب الكتاب.");
        if(!issuing)return;
        if(new[]{Header,Number,Recipient,Subject,Body,Footer}.Any(string.IsNullOrWhiteSpace))throw new InvalidDataException("أكمل الترويسة والعدد والجهة والموضوع والمتن ونسخة منه إلى.");
        _=AdaptiveLetterPdf.SignatureCells(Style()," / ١ / ٢٠٢٦");
    }
    public static string FormatNumbers(string text){
        return Regex.Replace(text,@"(?i)IQ(?:[ -]*[A-Z0-9]){21}|[A-Z]+[A-Z0-9]+|[0-9٠-٩۰-۹]+(?:[,٬][0-9٠-٩۰-۹]{3})*",m=>{
            if(char.IsAsciiLetter(m.Value[0]))return m.Value;
            var digits=string.Concat(m.Value.Where(char.IsDigit).Select(c=>(char)('0'+(int)char.GetNumericValue(c))));
            if(digits.Length>=13)return m.Value;
            var before=text[..m.Index].TrimEnd();var after=text[(m.Index+m.Length)..].TrimStart();
            var context=before[Math.Max(0,before.Length-32)..];
            bool money=m.Value.IndexOfAny([',','٬'])>=0||new[]{"مبلغ","قيمته","قيمتها","مقداره","دينار"}.Any(context.Contains)||after.StartsWith("دينار");
            if(money&&!before.EndsWith('/')&&!after.StartsWith('/')&&!before.EndsWith('-')&&!after.StartsWith('-')&&!new[]{"سنة","سنه","عام"}.Any(before.EndsWith))
                digits=long.Parse(digits,CultureInfo.InvariantCulture).ToString("N0",CultureInfo.InvariantCulture);
            return AdaptiveLetterPdf.Eastern(digits);
        });
    }
    public static string Fill(string body,IReadOnlyDictionary<string,string> values){
        var result=Regex.Replace(body,@"\{\{([^{}]+)\}\}",m=>values.TryGetValue(m.Groups[1].Value,out var v)&&v.Length>0?v:throw new InvalidDataException("راجع الحقل "+m.Value));
        if(result.Contains("{{")||result.Contains("}}"))throw new InvalidDataException("راجع حقول المتن بين الأقواس.");
        return result;
    }
}
