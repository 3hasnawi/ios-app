using System.Text.Json;

namespace SalaryDesktop.Core;
public sealed record SalaryItemGroup(string Title, int People, decimal Total);
public sealed record SalaryItemRow(string Name, string Rank, string Workplace, decimal Amount);

public static class SalaryItemReports
{
    private static Dictionary<string, decimal> Items(Employee employee)
    {
        if (employee.Items is null || employee.Items.Length == 0) return new(StringComparer.Ordinal);
        try
        {
            var items = JsonSerializer.Deserialize<Dictionary<string, decimal>>(employee.Items) ?? throw new JsonException();
            if (items.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value is < -9_007_199_254_740_991m or > 9_007_199_254_740_991m)) throw new InvalidDataException("يوجد بند راتب غير صالح.");
            return items;
        }
        catch (JsonException ex) { throw new InvalidDataException("تعذّر قراءة بنود أحد الرواتب؛ لم يُحسب التقرير.", ex); }
    }
    public static IReadOnlyList<SalaryItemGroup> Groups(BackupSnapshot source, string month)
    {
        if (!source.Months.Any(m => m.Name == month)) throw new InvalidDataException("الشهر غير موجود.");
        var groups = new Dictionary<string, (int Count, decimal Total)>(StringComparer.Ordinal);
        foreach (var employee in source.Employees.Where(e => e.Month == month))
            foreach (var (title, amount) in Items(employee).Where(p => p.Value != 0))
            { var previous = groups.GetValueOrDefault(title); groups[title] = (checked(previous.Count + 1), checked(previous.Total + amount)); }
        return groups.Select(p => new SalaryItemGroup(p.Key, p.Value.Count, p.Value.Total)).OrderByDescending(g => g.Total).ThenBy(g => g.Title, StringComparer.Ordinal).ToArray();
    }
    public static IReadOnlyList<SalaryItemRow> Rows(BackupSnapshot source, string month, SalaryItemGroup group)
    {
        if (string.IsNullOrWhiteSpace(group.Title) || group.People <= 0) throw new InvalidDataException("البند غير صالح.");
        var rows = new List<SalaryItemRow>();
        foreach (var employee in source.Employees.Where(e => e.Month == month))
        {
            var items = Items(employee);
            if (items.TryGetValue(group.Title, out var amount) && amount != 0) rows.Add(new(employee.Name, employee.Rank, employee.Workplace, amount));
        }
        if (rows.Count != group.People || rows.Sum(r => r.Amount) != group.Total) throw new InvalidDataException("تغيّرت بنود الراتب أو لم يطابق الكشف ملخصه.");
        return rows.OrderBy(r => r.Name, StringComparer.Ordinal).ToArray();
    }
}
