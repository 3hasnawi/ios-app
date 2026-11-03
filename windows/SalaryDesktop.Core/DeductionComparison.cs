namespace SalaryDesktop.Core;

public enum DeductionChangeKind { Started, Stopped, Increased, Decreased, Unchanged }
public sealed record DeductionChange(string Name, string Entity, string Note, decimal Before, decimal After, DeductionChangeKind Kind)
{
    public decimal Difference => After - Before;
    private static string Display(decimal value) => value.ToString("#,0.############################", System.Globalization.CultureInfo.InvariantCulture);
    public string BeforeText => Display(Before);
    public string AfterText => Display(After);
    public string DifferenceText => Display(Difference);
    public string Status => Kind switch { DeductionChangeKind.Started => "بدأ", DeductionChangeKind.Stopped => "توقف", DeductionChangeKind.Increased => "زاد", DeductionChangeKind.Decreased => "نقص", _ => "ثابت" };
}
public sealed record DeductionComparisonResult(string From, string To, IReadOnlyList<DeductionChange> People, IReadOnlyList<DeductionChange> Entries);

/// مقارنة للقراءة فقط: التجميع للشخص أو للشخص والجهة والإضبارة؛ لا تُحذف القيود المتكررة.
public static class DeductionComparison
{
    private readonly record struct Key(string Person, string Entity, string Note);
    private sealed record Total(string Name, string Entity, string Note, decimal Amount);
    private static string Normalize(string text) => PayrollAnalysis.Normalize(string.Concat(text.Select(c => c switch { >= '٠' and <= '٩' => (char)('0' + c - '٠'), >= '۰' and <= '۹' => (char)('0' + c - '۰'), _ => c })));
    public static DeductionComparisonResult Compare(BackupSnapshot source, string from, string to, bool allowEmpty = false)
    {
        if (from == to || MonthNames.SortKey(from) == 0 || MonthNames.SortKey(to) == 0 || MonthNames.SortKey(from) >= MonthNames.SortKey(to) ||
            !source.Months.Any(m => m.Name == from) || !source.Months.Any(m => m.Name == to))
            throw new InvalidDataException("اختر شهرين محفوظين، ويجب أن يكون الشهر الأول أقدم من الثاني.");
        var old = source.Deductions.Where(d => d.Month == from && !PayrollAnalysis.IsInactive(d)).ToArray();
        var current = source.Deductions.Where(d => d.Month == to && !PayrollAnalysis.IsInactive(d)).ToArray();
        if (!allowEmpty && (old.Length == 0 || current.Length == 0)) throw new InvalidDataException("أحد الشهرين بلا قيود فعالة؛ أكّد خلوه قبل المقارنة.");
        Dictionary<Key, Total> Totals(Deduction[] rows, bool perEntity)
        {
            var result = new Dictionary<Key, Total>();
            foreach (var row in rows)
            {
                var name = PersonProfiles.Key(row.Name); var entity = Normalize(row.Title);
                if (name.Length == 0 || entity.Length == 0 || row.Amount is null or < 0) throw new InvalidDataException("تعذّرت المقارنة: يوجد قيد بلا اسم أو جهة أو بمبلغ ناقص أو سالب.");
                if (row.Amount == 0) continue;
                var key = new Key(name, perEntity ? entity : "", perEntity ? Normalize(row.Note) : "");
                var prior = result.GetValueOrDefault(key);
                result[key] = new Total(row.Name, perEntity ? row.Title : "", perEntity ? row.Note : "", checked((prior?.Amount ?? 0) + row.Amount.Value));
            }
            return result;
        }
        IReadOnlyList<DeductionChange> Calculate(bool perEntity)
        {
            var before = Totals(old, perEntity); var after = Totals(current, perEntity);
            return before.Keys.Concat(after.Keys).Distinct().OrderBy(k => k.Person, StringComparer.Ordinal).ThenBy(k => k.Entity, StringComparer.Ordinal).ThenBy(k => k.Note, StringComparer.Ordinal).Select(k =>
            {
                before.TryGetValue(k, out var a); after.TryGetValue(k, out var b);
                var x = a?.Amount ?? 0; var y = b?.Amount ?? 0; var show = b ?? a!;
                var kind = x == 0 ? DeductionChangeKind.Started : y == 0 ? DeductionChangeKind.Stopped : x == y ? DeductionChangeKind.Unchanged : y > x ? DeductionChangeKind.Increased : DeductionChangeKind.Decreased;
                return new DeductionChange(show.Name, show.Entity, show.Note, x, y, kind);
            }).ToArray();
        }
        return new(from, to, Calculate(false), Calculate(true));
    }
}
