using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace SalaryDesktop.Core;

/// أشكال يونيكود الصريحة تجعل استخراج خلايا الجدول ممكناً مع خطوط ويندوز.
internal static class ArabicPresentation
{
    private readonly record struct Forms(char Isolated, char Final, char Initial, char Medial);
    private static readonly Dictionary<char, Forms> Map = new()
    {
        ['\u0621'] = new('\uFE80', '\0', '\0', '\0'),
        ['\u0622'] = new('\uFE81', '\uFE82', '\0', '\0'),
        ['\u0623'] = new('\uFE83', '\uFE84', '\0', '\0'),
        ['\u0624'] = new('\uFE85', '\uFE86', '\0', '\0'),
        ['\u0625'] = new('\uFE87', '\uFE88', '\0', '\0'),
        ['\u0626'] = new('\uFE89', '\uFE8A', '\uFE8B', '\uFE8C'),
        ['\u0627'] = new('\uFE8D', '\uFE8E', '\0', '\0'),
        ['\u0628'] = new('\uFE8F', '\uFE90', '\uFE91', '\uFE92'),
        ['\u0629'] = new('\uFE93', '\uFE94', '\0', '\0'),
        ['\u062A'] = new('\uFE95', '\uFE96', '\uFE97', '\uFE98'),
        ['\u062B'] = new('\uFE99', '\uFE9A', '\uFE9B', '\uFE9C'),
        ['\u062C'] = new('\uFE9D', '\uFE9E', '\uFE9F', '\uFEA0'),
        ['\u062D'] = new('\uFEA1', '\uFEA2', '\uFEA3', '\uFEA4'),
        ['\u062E'] = new('\uFEA5', '\uFEA6', '\uFEA7', '\uFEA8'),
        ['\u062F'] = new('\uFEA9', '\uFEAA', '\0', '\0'),
        ['\u0630'] = new('\uFEAB', '\uFEAC', '\0', '\0'),
        ['\u0631'] = new('\uFEAD', '\uFEAE', '\0', '\0'),
        ['\u0632'] = new('\uFEAF', '\uFEB0', '\0', '\0'),
        ['\u0633'] = new('\uFEB1', '\uFEB2', '\uFEB3', '\uFEB4'),
        ['\u0634'] = new('\uFEB5', '\uFEB6', '\uFEB7', '\uFEB8'),
        ['\u0635'] = new('\uFEB9', '\uFEBA', '\uFEBB', '\uFEBC'),
        ['\u0636'] = new('\uFEBD', '\uFEBE', '\uFEBF', '\uFEC0'),
        ['\u0637'] = new('\uFEC1', '\uFEC2', '\uFEC3', '\uFEC4'),
        ['\u0638'] = new('\uFEC5', '\uFEC6', '\uFEC7', '\uFEC8'),
        ['\u0639'] = new('\uFEC9', '\uFECA', '\uFECB', '\uFECC'),
        ['\u063A'] = new('\uFECD', '\uFECE', '\uFECF', '\uFED0'),
        ['\u0641'] = new('\uFED1', '\uFED2', '\uFED3', '\uFED4'),
        ['\u0642'] = new('\uFED5', '\uFED6', '\uFED7', '\uFED8'),
        ['\u0643'] = new('\uFED9', '\uFEDA', '\uFEDB', '\uFEDC'),
        ['\u0644'] = new('\uFEDD', '\uFEDE', '\uFEDF', '\uFEE0'),
        ['\u0645'] = new('\uFEE1', '\uFEE2', '\uFEE3', '\uFEE4'),
        ['\u0646'] = new('\uFEE5', '\uFEE6', '\uFEE7', '\uFEE8'),
        ['\u0647'] = new('\uFEE9', '\uFEEA', '\uFEEB', '\uFEEC'),
        ['\u0648'] = new('\uFEED', '\uFEEE', '\0', '\0'),
        ['\u0649'] = new('\uFEEF', '\uFEF0', '\uFBE8', '\uFBE9'),
        ['\u064A'] = new('\uFEF1', '\uFEF2', '\uFEF3', '\uFEF4'),
        ['\u0671'] = new('\uFB50', '\uFB51', '\0', '\0'),
        ['\u0677'] = new('\uFBDD', '\0', '\0', '\0'),
        ['\u0679'] = new('\uFB66', '\uFB67', '\uFB68', '\uFB69'),
        ['\u067A'] = new('\uFB5E', '\uFB5F', '\uFB60', '\uFB61'),
        ['\u067B'] = new('\uFB52', '\uFB53', '\uFB54', '\uFB55'),
        ['\u067E'] = new('\uFB56', '\uFB57', '\uFB58', '\uFB59'),
        ['\u067F'] = new('\uFB62', '\uFB63', '\uFB64', '\uFB65'),
        ['\u0680'] = new('\uFB5A', '\uFB5B', '\uFB5C', '\uFB5D'),
        ['\u0683'] = new('\uFB76', '\uFB77', '\uFB78', '\uFB79'),
        ['\u0684'] = new('\uFB72', '\uFB73', '\uFB74', '\uFB75'),
        ['\u0686'] = new('\uFB7A', '\uFB7B', '\uFB7C', '\uFB7D'),
        ['\u0687'] = new('\uFB7E', '\uFB7F', '\uFB80', '\uFB81'),
        ['\u0688'] = new('\uFB88', '\uFB89', '\0', '\0'),
        ['\u068C'] = new('\uFB84', '\uFB85', '\0', '\0'),
        ['\u068D'] = new('\uFB82', '\uFB83', '\0', '\0'),
        ['\u068E'] = new('\uFB86', '\uFB87', '\0', '\0'),
        ['\u0691'] = new('\uFB8C', '\uFB8D', '\0', '\0'),
        ['\u0698'] = new('\uFB8A', '\uFB8B', '\0', '\0'),
        ['\u06A4'] = new('\uFB6A', '\uFB6B', '\uFB6C', '\uFB6D'),
        ['\u06A6'] = new('\uFB6E', '\uFB6F', '\uFB70', '\uFB71'),
        ['\u06A9'] = new('\uFB8E', '\uFB8F', '\uFB90', '\uFB91'),
        ['\u06AD'] = new('\uFBD3', '\uFBD4', '\uFBD5', '\uFBD6'),
        ['\u06AF'] = new('\uFB92', '\uFB93', '\uFB94', '\uFB95'),
        ['\u06B1'] = new('\uFB9A', '\uFB9B', '\uFB9C', '\uFB9D'),
        ['\u06B3'] = new('\uFB96', '\uFB97', '\uFB98', '\uFB99'),
        ['\u06BA'] = new('\uFB9E', '\uFB9F', '\0', '\0'),
        ['\u06BB'] = new('\uFBA0', '\uFBA1', '\uFBA2', '\uFBA3'),
        ['\u06BE'] = new('\uFBAA', '\uFBAB', '\uFBAC', '\uFBAD'),
        ['\u06C0'] = new('\uFBA4', '\uFBA5', '\0', '\0'),
        ['\u06C1'] = new('\uFBA6', '\uFBA7', '\uFBA8', '\uFBA9'),
        ['\u06C5'] = new('\uFBE0', '\uFBE1', '\0', '\0'),
        ['\u06C6'] = new('\uFBD9', '\uFBDA', '\0', '\0'),
        ['\u06C7'] = new('\uFBD7', '\uFBD8', '\0', '\0'),
        ['\u06C8'] = new('\uFBDB', '\uFBDC', '\0', '\0'),
        ['\u06C9'] = new('\uFBE2', '\uFBE3', '\0', '\0'),
        ['\u06CB'] = new('\uFBDE', '\uFBDF', '\0', '\0'),
        ['\u06CC'] = new('\uFBFC', '\uFBFD', '\uFBFE', '\uFBFF'),
        ['\u06D0'] = new('\uFBE4', '\uFBE5', '\uFBE6', '\uFBE7'),
        ['\u06D2'] = new('\uFBAE', '\uFBAF', '\0', '\0'),
        ['\u06D3'] = new('\uFBB0', '\uFBB1', '\0', '\0'),
    };
    private static string Shape(string text)
    {
        var chars = text.ToCharArray(); var shaped = new char[chars.Length];
        static bool JoinsNext(char c) => c == '\u0640' || Map.TryGetValue(c, out var f) && (f.Initial != '\0' || f.Medial != '\0');
        static bool JoinsPrevious(char c) => c == '\u0640' || Map.TryGetValue(c, out var f) && (f.Final != '\0' || f.Medial != '\0');
        for (int i = 0; i < chars.Length; i++)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(chars[i]);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
                throw new InvalidDataException("يتضمن اسم جهة أو فرع علامة لا يمكن التحقق من رسمها؛ راجع الاسم.");
            if (!Map.TryGetValue(chars[i], out var current)) { shaped[i] = chars[i] switch { '(' => ')', ')' => '(', '[' => ']', ']' => '[', _ => chars[i] }; continue; }
            bool previous = i > 0 && JoinsNext(chars[i - 1]) && JoinsPrevious(chars[i]);
            bool next = i + 1 < chars.Length && JoinsNext(chars[i]) && JoinsPrevious(chars[i + 1]);
            shaped[i] = previous && next && current.Medial != '\0' ? current.Medial : previous && current.Final != '\0' ? current.Final : next && current.Initial != '\0' ? current.Initial : current.Isolated;
            if (shaped[i] == '\0') throw new InvalidDataException("يتعذر رسم حرف في اسم الجهة بخط الجدول.");
        }
        Array.Reverse(shaped); return new string(shaped);
    }
    private static string[] Runs(string text)
    {
        if (!text.Any(c => c is >= '\u0600' and <= '\u08ff')) return [text];
        return Regex.Split(text, @"([A-Za-z0-9٠-٩۰-۹]+(?:[ .,/:-]+[A-Za-z0-9٠-٩۰-۹]+)*)").Where(part => part.Length > 0).ToArray();
    }
    private static string Visual(string text) => text.Any(c => c is >= '\u0600' and <= '\u08ff') && text.Any(c => Map.ContainsKey(c)) ? Shape(text) : text;
    public static float Width(string text, SKPaint paint) => Runs(text).Sum(run => paint.MeasureText(Visual(run)));
    public static void DrawRight(SKCanvas canvas, SKPaint paint, string text, float right, float baseline)
    {
        foreach (var run in Runs(text))
        {
            var visual = Visual(run);
            if (!paint.ContainsGlyphs(visual)) throw new InvalidDataException("خط الجدول لا يدعم بعض حروف الجهة.");
            var width = paint.MeasureText(visual); canvas.DrawText(visual, right - width, baseline, paint); right -= width;
        }
    }
    public static List<string> Wrap(string text, SKPaint paint, float width)
    {
        var result = new List<string>(); var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var proposed = line.Length == 0 ? word : line + " " + word;
            if (Width(proposed, paint) <= width) { line = proposed; continue; }
            if (line.Length > 0) { result.Add(line); line = ""; }
            var part = "";
            foreach (var rune in word.EnumerateRunes())
            {
                var next = part + rune;
                if (part.Length > 0 && Width(next, paint) > width) { result.Add(part); part = ""; }
                part += rune;
            }
            line = part;
        }
        if (line.Length > 0) result.Add(line);
        return result.Count == 0 ? [""] : result;
    }
    public static void Fit(SKCanvas canvas, SKPaint paint, string text, float left, float right, float baseline, bool rightAlign = false)
    {
        var size = paint.TextSize;
        while (Width(text, paint) > right - left - 6 && paint.TextSize > 7) paint.TextSize -= .5f;
        var width = Width(text, paint);
        if (width > right - left - 6) throw new InvalidDataException("لا يتسع نص في جدول الكتاب.");
        DrawRight(canvas, paint, text, rightAlign ? right - 3 : left + (right - left + width) / 2, baseline);
        paint.TextSize = size;
    }
}
