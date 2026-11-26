using System.Globalization;
using System.Text;

namespace SalaryDesktop.Core;

public sealed record MonthSummary(int Employees, decimal NetTotal, int MissingNet, int Deductions, int MissingAmounts)
{
    /// مجموع مبالغ الاستقطاع النافذة وعدد الجهات (عناوين الاستقطاع) في الشهر؛ للرئيسية.
    public decimal DeductionTotal { get; init; }
    public int Entities { get; init; }
}
public sealed record DeductionGroup(string Title, int People, decimal Total, int MissingAmounts);

public static class PayrollAnalysis
{
    public static string Normalize(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
        {
            if ((c >= '\u064b' && c <= '\u065f') || c is '\u0670' or '\u0640') continue;
            result.Append(c switch { 'آ' or 'أ' or 'إ' or 'ٱ' => 'ا', 'ة' => 'ه', 'ى' or 'ئ' => 'ي', 'ؤ' => 'و', _ => c });
        }
        return string.Join(' ', result.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
    public static bool IsInactive(Deduction row)
    {
        const string prefix = "⟦حالة الاستقطاع:";
        if (row.Amount is not null || !row.Note.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var end = row.Note.IndexOf('⟧', prefix.Length);
        if (end < 0) return false;
        var status = Normalize(row.Note[prefix.Length..end]);
        return status is "موقوف" or "هارب" or "محكوم";
    }
    public static MonthSummary Summarize(BackupSnapshot snapshot, string month)
    {
        var people = snapshot.Employees.Where(e => e.Month == month).ToArray();
        var entries = snapshot.Deductions.Where(d => d.Month == month && !IsInactive(d)).ToArray();
        return new MonthSummary(people.Length, people.Sum(e => e.Net ?? 0m), people.Count(e => e.Net is null),
            entries.Length, entries.Count(d => d.Amount is null))
        { DeductionTotal = entries.Sum(d => d.Amount ?? 0m), Entities = entries.Select(d => d.Title).Distinct(StringComparer.Ordinal).Count() };
    }
    public static IReadOnlyList<DeductionGroup> Groups(BackupSnapshot snapshot, string month) =>
        snapshot.Deductions.Where(d => d.Month == month && !IsInactive(d))
        .GroupBy(d => d.Title, StringComparer.Ordinal)
        .Select(g => new DeductionGroup(g.Key, g.Select(d => Normalize(d.Name).Replace(" ", "", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).Count(),
            g.Sum(d => d.Amount ?? 0m), g.Count(d => d.Amount is null)))
        .OrderByDescending(g => g.Total).ThenBy(g => g.Title, StringComparer.Ordinal).ToArray();
}
