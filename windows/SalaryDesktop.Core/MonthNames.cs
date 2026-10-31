using System.Globalization;

namespace SalaryDesktop.Core;

public static class MonthNames
{
    private static readonly Dictionary<string, int> Names = new[] {
        ("كانون الثاني",1), ("شباط",2), ("آذار",3), ("نيسان",4), ("أيار",5), ("حزيران",6), ("تموز",7), ("آب",8), ("أيلول",9), ("تشرين الأول",10), ("تشرين الثاني",11), ("كانون الأول",12),
        ("يناير",1), ("فبراير",2), ("مارس",3), ("أبريل",4), ("مايو",5), ("يونيو",6), ("يوليو",7), ("أغسطس",8), ("سبتمبر",9), ("أكتوبر",10), ("نوفمبر",11), ("ديسمبر",12),
        ("كانون2",1), ("تشرين1",10), ("تشرين2",11), ("كانون1",12)
    }.ToDictionary(p => PayrollAnalysis.Normalize(p.Item1), p => p.Item2, StringComparer.Ordinal);
    public static int SortKey(string label)
    {
        var words = PayrollAnalysis.Normalize(label).Replace('_', ' ').Replace('-', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int? year = null, month = null; var numbers = new List<int>();
        foreach (var word in words)
        {
            int n = 0;
            if (word.Length is 0 or > 4) continue;
            bool numeric = true;
            foreach (var c in word) { var digit = CharUnicodeInfo.GetDecimalDigitValue(c); if (digit < 0) { numeric = false; break; } n = n * 10 + digit; }
            if (!numeric) continue;
            if (n is >= 1900 and <= 2200) year = n; else numbers.Add(n);
        }
        for (int i = 0; i < words.Length; i++)
        {
            if (i + 1 < words.Length && Names.TryGetValue(words[i] + " " + words[i + 1], out int pair)) { month = pair; break; }
            if (Names.TryGetValue(words[i], out int single)) { month = single; break; }
        }
        month ??= numbers.FirstOrDefault(n => n is >= 1 and <= 12);
        return year is not null && month is >= 1 and <= 12 ? year.Value * 100 + month.Value : 0;
    }
    public static int? Previous(string label)
    {
        var key = SortKey(label); var year = key / 100; var month = key % 100;
        if (year < 1900 || month is < 1 or > 12) return null;
        return month == 1 ? (year - 1) * 100 + 12 : year * 100 + month - 1;
    }
}
