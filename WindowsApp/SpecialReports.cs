namespace SalaryWindows;
internal sealed record SpecialReport(string Title, string Explanation, List<string> Lines);
internal static class SpecialReports
{
    public static bool Supports(string title)
    {
        var key = PayrollMath.Normalize(title);
        return key.Contains(PayrollMath.Normalize("الرعاية")) || key.Contains(PayrollMath.Normalize("شهداء")) || key.Contains(PayrollMath.Normalize("الجوال"));
    }
    public static SpecialReport? Build(BackupData data, string month, string title)
    {
        var key = PayrollMath.Normalize(title);
        if (!Supports(title)) return null;
        var people = data.Employees.Where(e => e.Month == month).Select(e => (Employee: e, Items: PayrollMath.Decode<double>(e.Items))).ToList();
        if (!people.Any(x => x.Items.ContainsKey(title))) throw new InvalidDataException("لا يوجد هذا البند في بيانات الشهر.");
        if (key.Contains(PayrollMath.Normalize("الرعاية")))
        {
            var subscribers = people.Where(x => x.Items.TryGetValue(title, out var a) && a != 0).ToList();
            var excluded = new[] { "عقد", "عقود", "رجل امن", "رجل الامن", "رجال الامن" }.Select(PayrollMath.Normalize).ToArray();
            var others = people.Where(x => (!x.Items.TryGetValue(title, out var a) || a == 0) && (x.Employee.Net ?? 0) > 0 && !excluded.Any(word => PayrollMath.Normalize(x.Employee.Rank ?? "").Contains(word))).ToList();
            var lines = new List<string> { $"المشتركون: {subscribers.Count:N0}", $"غير المشتركين: {others.Count:N0}" };
            lines.AddRange(subscribers.Select(x => {
                var amount = x.Items[title]; var exact = amount % 3500 == 0;
                var family = exact ? Math.Max(1d, Math.Round(amount / 3500, MidpointRounding.AwayFromZero)).ToString("N0") : (amount / 3500).ToString("N2");
                return $"مشترك · {x.Employee.Name} · {x.Employee.Rank} · {amount:N2} · الأفراد {family}" + (exact ? "" : " · يحتاج مراجعة");
            }));
            lines.AddRange(others.Select(x => $"غير مشترك · {x.Employee.Name} · {x.Employee.Rank}"));
            return new("الرعاية الصحية", "العقود ورجال الأمن مستبعدون من قائمة غير المشتركين؛ المبالغ غير القابلة للقسمة على 3500 تحتاج مراجعة.", lines);
        }
        if (key.Contains(PayrollMath.Normalize("شهداء")))
        {
            string[] names = ["ضباط", "موظفون", "مراتب", "عقود"]; int[] rates = [5000, 4000, 4000, 2000];
            var counts = new int[4]; var totals = new double[4];
            foreach (var (employee, items) in people) if (items.TryGetValue(title, out var value) && value != 0)
            { var group = PayrollMath.Category(employee.Rank ?? ""); counts[group]++; totals[group] += value; }
            return new("صندوق الشهداء حسب الفئة", "الأعداد تشمل من لديهم استقطاع غير صفري.", Enumerable.Range(0, 4).Select(i => $"{names[i]} · العدد {counts[i]:N0} · المقرر للفرد {rates[i]:N0} · المجموع الفعلي {totals[i]:N2}").ToList());
        }
        var amounts = people.Where(x => x.Items.TryGetValue(title, out var n) && n != 0).Select(x => x.Items[title]).ToList();
        var grouped = amounts.GroupBy(x => x).OrderBy(x => x.Key).Select(g => $"الاستقطاع {g.Key:N2} · عدد المشتركين {g.Count():N0} · المجموع {g.Sum():N2}").ToList();
        var total = amounts.Sum(); var fund = Math.Round(total * .03, MidpointRounding.AwayFromZero); var rest = total - fund;
        var tax = Math.Round(rest * .018, MidpointRounding.AwayFromZero); var company = rest - tax;
        grouped.Add($"مجموع الاستقطاع: {total:N2}"); grouped.Add($"حصة صندوق الشهداء 3%: {fund:N0}");
        grouped.Add($"بعد حصة الصندوق: {rest:N2}"); grouped.Add($"الضريبة 1.8%: {tax:N0}"); grouped.Add($"حصة الشركة: {company:N2}");
        return new("تفاصيل عقد الإطعام", "تُحسب الضريبة من المبلغ بعد حصة صندوق الشهداء.", grouped);
    }
}
