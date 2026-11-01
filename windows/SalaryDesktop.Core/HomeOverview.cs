namespace SalaryDesktop.Core;

public sealed record HomeAlert(string Kind, int Count);
/// ملخص الرئيسية: الفئات من رتب كشف الرواتب، والمقارنة بالشهر السابق، وملاحظات التدقيق مجمّعة بنوعها.
public sealed record HomeOverview(IReadOnlyList<(string Name, int Count)> Categories, string? PreviousMonth, decimal? NetChange, int PeopleChange, IReadOnlyList<HomeAlert> Alerts, string? AlertError);

public static class HomeOverviews
{
    /// ترتيب الأهمية في شريط التنبيهات: ما يمنع الصرف أولاً، ثم تغيّرات الشهر.
    private static readonly string[] Priority =
    [
        "أسماء مكررة", "صافي ناقص", "IBAN يحتاج مراجعة", "IBAN مكرر", "IBAN فارغ", "رقم مكرر", "حساب الصافي", "مجموع الاستقطاعات",
        "تغيّر الصافي", "جديد في الرواتب", "خارج من الرواتب", "تغيّر الحساب", "تغيّرت الرتبة", "تغيّر محل العمل", "استقطاع بلا راتب",
    ];

    public static HomeOverview Build(BackupSnapshot source, string month, IReadOnlyList<StaffMember>? staff)
    {
        var current = source.Employees.Where(e => e.Month == month).ToArray();
        var counts = new int[FundReport.Categories.Length];
        foreach (var employee in current) counts[FundReport.CategoryOf(employee.Rank)]++;
        var categories = FundReport.Categories.Select((c, i) => (c.Name, counts[i])).ToArray();

        var key = MonthNames.SortKey(month);
        var previous = source.Months.Select(m => m.Name).Where(m => MonthNames.SortKey(m) < key && source.Employees.Any(e => e.Month == m))
            .OrderByDescending(MonthNames.SortKey).FirstOrDefault();
        decimal? netChange = null; var peopleChange = 0;
        if (previous is not null && current.Length > 0)
        {
            var before = source.Employees.Where(e => e.Month == previous).ToArray();
            if (current.All(e => e.Net is not null) && before.All(e => e.Net is not null)) netChange = current.Sum(e => e.Net!.Value) - before.Sum(e => e.Net!.Value);
            peopleChange = current.Length - before.Length;
        }

        IReadOnlyList<HomeAlert> alerts = []; string? error = null;
        if (current.Length > 0)
        {
            try
            {
                var review = SalaryReview.Prepare(source, month, staff);
                alerts = review.Rows.GroupBy(r => r.Kind, StringComparer.Ordinal)
                    .Select(g => new HomeAlert(g.Key, g.Select(r => r.Name).Distinct(StringComparer.Ordinal).Count()))
                    .OrderBy(a => Array.IndexOf(Priority, a.Kind) is var i && i >= 0 ? i : Priority.Length).ThenByDescending(a => a.Count).ToArray();
            }
            catch (InvalidDataException ex) { error = ex.Message; }
        }
        return new HomeOverview(categories, previous, netChange, peopleChange, alerts, error);
    }
}
