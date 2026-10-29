namespace SalaryWindows;
internal sealed record SalaryChange(string Kind, string Name, string Detail);
internal sealed record SalaryComparisonResult(List<SalaryChange> Changes, int AmbiguousOld, int AmbiguousNew);
internal static class SalaryComparison
{
    public static SalaryComparisonResult Compare(BackupData data, string oldMonth, string newMonth)
    {
        if (oldMonth == newMonth || !data.Manifest.Months.Contains(oldMonth) || !data.Manifest.Months.Contains(newMonth))
            throw new InvalidDataException("اختر شهرين مختلفين من النسخة.");
        var old = data.Employees.Where(x => x.Month == oldMonth).GroupBy(x => PayrollMath.Key(x.Name)).ToDictionary(x => x.Key, x => x.ToList());
        var current = data.Employees.Where(x => x.Month == newMonth).GroupBy(x => PayrollMath.Key(x.Name)).ToDictionary(x => x.Key, x => x.ToList());
        if (old.Count == 0 || current.Count == 0) throw new InvalidDataException("لا توجد رواتب في أحد الشهرين؛ لا يمكن المقارنة.");
        var changes = new List<SalaryChange>();
        foreach (var key in old.Keys.Union(current.Keys))
        {
            old.TryGetValue(key, out var previous); current.TryGetValue(key, out var next);
            // الاسم المكرر لا يكفي لتحديد هوية الشخص بأمان.
            if (string.IsNullOrEmpty(key) || previous?.Count > 1 || next?.Count > 1) continue;
            var a = previous?.FirstOrDefault(); var b = next?.FirstOrDefault();
            if (a == null && b != null) { changes.Add(new("جدد", b.Name, "ظهر في الشهر الجديد")); continue; }
            if (a != null && b == null) { changes.Add(new("خارجون", a.Name, "لم يظهر في الشهر الجديد")); continue; }
            if (a == null || b == null) continue;
            if (a.Net.HasValue && b.Net.HasValue && Math.Abs(a.Net.Value - b.Net.Value) >= 1)
            {
                var before = PayrollMath.Decode<double>(a.Items); var after = PayrollMath.Decode<double>(b.Items);
                var reasons = before.Keys.Union(after.Keys).Where(k => k != "الاستحقاق" && k != "مجموع الاستقطاعات")
                    .Select(k => (Name: k, Diff: after.GetValueOrDefault(k) - before.GetValueOrDefault(k)))
                    .Where(x => Math.Abs(x.Diff) >= 1).OrderByDescending(x => Math.Abs(x.Diff)).Take(4)
                    .Select(x => $"{x.Name} {x.Diff:+#,##0.##;-#,##0.##;0}");
                changes.Add(new("تغير الصافي", b.Name, $"{a.Net:N0} ← {b.Net:N0} · {string.Join("، ", reasons)}"));
            }
            if (!string.IsNullOrWhiteSpace(a.Rank) && !string.IsNullOrWhiteSpace(b.Rank) && RankKey(a.Rank) != RankKey(b.Rank))
                changes.Add(new("تغير الرتبة", b.Name, $"{a.Rank} ← {b.Rank}"));
            if (!string.IsNullOrWhiteSpace(a.Workplace) && !string.IsNullOrWhiteSpace(b.Workplace) && PayrollMath.Key(a.Workplace) != PayrollMath.Key(b.Workplace))
                changes.Add(new("تغير محل العمل", b.Name, $"{a.Workplace} ← {b.Workplace}"));
            var aDetails = PayrollMath.Decode<string>(a.Details); var bDetails = PayrollMath.Decode<string>(b.Details);
            var ibanA = Iban(aDetails.GetValueOrDefault("IBAN")); var ibanB = Iban(bDetails.GetValueOrDefault("IBAN"));
            var bankA = aDetails.GetValueOrDefault("توطين المصرف") ?? ""; var bankB = bDetails.GetValueOrDefault("توطين المصرف") ?? "";
            if (ibanA != ibanB || (!string.IsNullOrWhiteSpace(bankA) && !string.IsNullOrWhiteSpace(bankB) && PayrollMath.Key(bankA) != PayrollMath.Key(bankB)))
                changes.Add(new("تغير الحساب المصرفي", b.Name, $"IBAN {Tail(ibanA)} ← {Tail(ibanB)}"));
        }
        return new(changes, old.Count(x => string.IsNullOrEmpty(x.Key) || x.Value.Count > 1), current.Count(x => string.IsNullOrEmpty(x.Key) || x.Value.Count > 1));
    }
    private static string Iban(string? value) => new(PayrollMath.Normalize(value ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
    private static string Tail(string value) => value.Length == 0 ? "فارغ" : "…" + value[^Math.Min(value.Length, 4)..];
    internal static string RankKey(string rank)
    {
        var value = PayrollMath.Normalize(rank);
        foreach (var (word, digit) in new[] { ("الاول", "1"), ("اول", "1"), ("الثاني", "2"), ("ثاني", "2"), ("الثالث", "3"), ("ثالث", "3"), ("الرابع", "4"), ("رابع", "4"), ("الخامس", "5"), ("خامس", "5") }) value = value.Replace(word, digit);
        return new(value.Where(x => !char.IsWhiteSpace(x) && x != '.' && x != '/' && x != '-').ToArray());
    }
}
