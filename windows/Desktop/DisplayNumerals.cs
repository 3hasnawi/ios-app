using System.Text.RegularExpressions;

namespace AnbarPayroll.Desktop;

internal static partial class DisplayNumerals
{
    public static bool Hindi { get; set; }

    // حماية الآيبان والسويفت والحساب المحلي ذي الـ ١٥ خانة عند تنسيق متن مختلط.
    [GeneratedRegex(@"[A-Za-z0-9٠-٩۰-۹]+", RegexOptions.CultureInvariant)]
    private static partial Regex Tokens();

    public static string Format(string text)
    {
        bool hindi=Hindi;
        if(!text.Any(char.IsDigit))return text;
        return Tokens().Replace(text,m=>{
            bool account=m.Length==15 && m.Value.All(char.IsDigit) ||
                m.Value.Any(char.IsAsciiLetter) && (m.Length==8 || m.Length==11 || m.Value.StartsWith("IQ",StringComparison.OrdinalIgnoreCase));
            return string.Create(m.Length,(m.Value,hindi,account),(span,arg)=>{
                for(int i=0;i<span.Length;i++){
                    char c=arg.Value[i];
                    int digit=c is >= '0' and <= '9'?c-'0':c is >= '٠' and <= '٩'?c-'٠':c is >= '۰' and <= '۹'?c-'۰':-1;
                    span[i]=digit<0?c:(char)((!arg.account&&arg.hindi?'٠':'0')+digit);
                }
            });
        });
    }
}
