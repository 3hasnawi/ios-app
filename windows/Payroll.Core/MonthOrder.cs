using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Payroll.Core;

public static partial class MonthOrder
{
    private static readonly Dictionary<string, int> Names = new(StringComparer.Ordinal)
    {
        ["كانون الثاني"] = 1, ["شباط"] = 2, ["اذار"] = 3, ["نيسان"] = 4, ["ايار"] = 5, ["حزيران"] = 6,
        ["تموز"] = 7, ["اب"] = 8, ["ايلول"] = 9, ["تشرين الاول"] = 10, ["تشرين الثاني"] = 11, ["كانون الاول"] = 12,
        ["يناير"] = 1, ["فبراير"] = 2, ["مارس"] = 3, ["ابريل"] = 4, ["مايو"] = 5, ["يونيو"] = 6,
        ["يوليو"] = 7, ["اغسطس"] = 8, ["سبتمبر"] = 9, ["اكتوبر"] = 10, ["نوفمبر"] = 11, ["ديسمبر"] = 12,
        ["كانون2"] = 1, ["تشرين1"] = 10, ["تشرين2"] = 11, ["كانون1"] = 12
    };

    public static int SortKey(string label)
    {
        var text = Normalize(label).Replace('_', ' ').Replace('-', ' ');
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int? year = null, month = null;
        var numbers = new List<int>();
        foreach (var word in words)
            if (int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            { if (n is >= 1900 and <= 2200) year = n; else numbers.Add(n); }
        for (var i = 0; i < words.Length; i++)
        {
            var pair = i + 1 < words.Length ? $"{words[i]} {words[i + 1]}" : "";
            if (Names.TryGetValue(pair, out var n) || Names.TryGetValue(words[i], out n)) { month = n; break; }
        }
        month ??= numbers.Where(n => n is >= 1 and <= 12).Cast<int?>().FirstOrDefault();
        return year is null || month is null ? 0 : year.Value * 100 + month.Value;
    }

    public static IReadOnlySet<string> LatestTwo(IEnumerable<string> monthNames)
    {
        var names = monthNames.Distinct(StringComparer.Ordinal).Select(n => (Name: n, Key: SortKey(n))).ToArray();
        if (names.Any(x => x.Key == 0)) throw new InvalidDataException("تعذّر تحديد أحد أشهر الرواتب؛ لم تُستبدل أي بيانات.");
        if (names.GroupBy(x => x.Key).Any(g => g.Count() > 1)) throw new InvalidDataException("يوجد أكثر من اسم للشهر نفسه؛ راجع النسخة قبل الاستيراد.");
        return names.OrderByDescending(x => x.Key).Take(2).Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
    }

    private static string Normalize(string value)
    {
        var b = new StringBuilder(value.Length);
        foreach (var ch in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark || ch == '\u0640') continue;
            var c = ch switch { 'أ' or 'إ' or 'آ' or 'ٱ' => 'ا', 'ى' => 'ي', _ => ch };
            if (c is >= '٠' and <= '٩') c = (char)('0' + c - '٠');
            else if (c is >= '۰' and <= '۹') c = (char)('0' + c - '۰');
            b.Append(c);
        }
        return b.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }
}
