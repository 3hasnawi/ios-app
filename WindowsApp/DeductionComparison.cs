namespace SalaryWindows;
internal sealed record DeductionChange(string Name, string Entity, string Reference, double Before, double After)
{
    public string Kind => Before == 0 ? "استقطاع جديد" : After == 0 ? "استقطاع متوقف" : After > Before ? "زاد" : "نقص";
}
internal sealed record ComparisonResult(string OldMonth, string NewMonth, List<DeductionChange> Entries, List<DeductionChange> People);
internal static class DeductionComparison
{
    private readonly record struct Key(string Person, string Entity, string Reference);
    private sealed record Total(string Name, string Entity, string Reference, double Amount);
    public static ComparisonResult Compare(BackupData data, string oldMonth, string newMonth)
    {
        if (oldMonth == newMonth || !data.Manifest.Months.Contains(oldMonth) || !data.Manifest.Months.Contains(newMonth))
            throw new InvalidDataException("اختر شهرين مختلفين موجودين في النسخة.");
        var old = data.Deductions.Where(x => x.Month == oldMonth).ToList();
        var current = data.Deductions.Where(x => x.Month == newMonth).ToList();
        if (old.Count == 0 || current.Count == 0) throw new InvalidDataException("أحد الشهرين بلا قيود استقطاع؛ لا يمكن المقارنة قبل التأكد من بياناته.");
        Dictionary<Key, Total> Totals(List<Deduction> records, bool entity)
        {
            var result = new Dictionary<Key, Total>();
            foreach (var r in records)
            {
                var person = PayrollMath.Key(r.Name ?? ""); var name = PayrollMath.Normalize(r.Title ?? "");
                if (person.Length == 0 || name.Length == 0 || !r.Amount.HasValue || !double.IsFinite(r.Amount.Value) || r.Amount < 0)
                    throw new InvalidDataException("تعذرت المقارنة: استقطاع بلا اسم أو جهة، أو بمبلغ فارغ أو سالب أو غير صالح.");
                if (r.Amount == 0) continue;
                var k = new Key(person, entity ? name : "", entity ? PayrollMath.Normalize(r.Note ?? "") : "");
                if (result.TryGetValue(k, out var existing))
                {
                    var sum = existing.Amount + r.Amount.Value;
                    if (!double.IsFinite(sum)) throw new InvalidDataException("مجموع الاستقطاع غير صالح.");
                    result[k] = existing with { Amount = sum };
                }
                else result[k] = new(r.Name ?? "", entity ? r.Title ?? "" : "", entity ? r.Note ?? "" : "", r.Amount.Value);
            }
            return result;
        }
        List<DeductionChange> Changes(bool entity)
        {
            var a = Totals(old, entity); var b = Totals(current, entity);
            var result = new List<DeductionChange>();
            foreach (var key in a.Keys.Union(b.Keys))
            {
                a.TryGetValue(key, out var x); b.TryGetValue(key, out var y);
                double before = x?.Amount ?? 0, after = y?.Amount ?? 0;
                if (Math.Abs(after - before) < 0.000001) continue;
                var info = y ?? x!; result.Add(new(info.Name, info.Entity, info.Reference, before, after));
            }
            return result.OrderByDescending(x => Math.Abs(x.After - x.Before)).ToList();
        }
        return new(oldMonth, newMonth, Changes(true), Changes(false));
    }
    public static List<List<Deduction>> Duplicates(BackupData data, string month) => data.Deductions
        .Where(x => x.Month == month && !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.Title) && x.Amount.HasValue && double.IsFinite(x.Amount.Value) && x.Amount >= 0)
        .GroupBy(x => (PayrollMath.Key(x.Name), PayrollMath.Normalize(x.Title), PayrollMath.Normalize(x.Note ?? ""), x.Amount))
        .Where(g => g.Count() > 1).Select(g => g.ToList()).ToList();
}
