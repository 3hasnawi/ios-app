using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SalaryDesktop.Core;

public enum NumeralStyle { Original, Arabic, Hindi }

public static partial class ReportNumerals
{
    public static NumeralStyle Style { get; set; }

    // حماية الآيبان والسويفت والحساب المحلي ذي الـ ١٥ خانة عند تنسيق متن مختلط.
    [GeneratedRegex(@"[A-Za-z0-9٠-٩۰-۹]+", RegexOptions.CultureInvariant)]
    private static partial Regex Tokens();

    public static string Format(string text)
    {
        var style=Style;
        if(style==NumeralStyle.Original || !text.Any(char.IsDigit))return text;
        return Tokens().Replace(text,m=>{
            bool account=m.Length==15 && m.Value.All(char.IsDigit) ||
                m.Value.Any(char.IsAsciiLetter) && (m.Length==8 || m.Length==11 || m.Value.StartsWith("IQ",StringComparison.OrdinalIgnoreCase));
            return string.Create(m.Length,(m.Value,style,account),(span,arg)=>{
                for(int i=0;i<span.Length;i++){
                    char c=arg.Value[i];
                    int digit=c is >= '0' and <= '9'?c-'0':c is >= '٠' and <= '٩'?c-'٠':c is >= '۰' and <= '۹'?c-'۰':-1;
                    span[i]=digit<0?c:(char)((!arg.account&&arg.style==NumeralStyle.Hindi?'٠':'0')+digit);
                }
            });
        });
    }

    public static string ExcelFormat(string format)=>Style switch{
        NumeralStyle.Hindi=>"[$-02000801]"+format,
        NumeralStyle.Arabic=>"[$-01000409]"+format,
        _=>format
    };

    internal static string ExcelStyles(string styles)
    {
        if(Style==NumeralStyle.Original)return styles;
        var root=XElement.Parse(styles);
        XNamespace ns=root.Name.Namespace;
        var formats=root.Element(ns+"numFmts");
        if(formats==null){formats=new XElement(ns+"numFmts");root.AddFirst(formats);}
        foreach(var fmt in formats.Elements(ns+"numFmt"))fmt.SetAttributeValue("formatCode",ExcelFormat((string)fmt.Attribute("formatCode")!));
        int generalId=formats.Elements().Select(e=>(int)e.Attribute("numFmtId")!).Append(163).Max()+1;
        formats.Add(new XElement(ns+"numFmt",new XAttribute("numFmtId",generalId),new XAttribute("formatCode",ExcelFormat("General"))));
        formats.SetAttributeValue("count",formats.Elements().Count());
        foreach(var xf in root.Element(ns+"cellXfs")!.Elements().Where(x=>(int?)x.Attribute("numFmtId") is null or 0)){
            xf.SetAttributeValue("numFmtId",generalId);xf.SetAttributeValue("applyNumberFormat",1);
        }
        return root.ToString(SaveOptions.DisableFormatting);
    }
}
