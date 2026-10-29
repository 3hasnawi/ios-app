namespace SalaryWindows;
internal static class StaffMatching
{
    private static readonly string[] Inactive = ["تقاعد", "احاله", "وفاه", "متوفي", "شهيد", "استشهد", "فصل", "مفصول", "استقاله", "مستقيل", "ترقين", "طي قيد", "منقول", "نقل خارج", "انهاء خدمه", "منتهي"];
    private static string Number(string? s) => new string(PayrollMath.Normalize(s ?? "").Where(char.IsAsciiDigit).ToArray()).TrimStart('0');
    public static List<SalaryCheck> Run(BackupData data, string month)
    {
        if (data.Staff.Count == 0 || !data.Employees.Any(x => x.Month == month)) return [new("مطابقة الملاك", "لا تتوفر بيانات ملاك أو رواتب للمطابقة", [])];
        var staffByName = data.Staff.Select((s, i) => (Staff: s, Index: i)).GroupBy(x => PayrollMath.Key(x.Staff.Name)).ToDictionary(g => g.Key, g => g.ToList());
        var staffByNumber = data.Staff.Select((s, i) => (Staff: s, Index: i)).Where(x => Number(x.Staff.Unified).Length > 0).GroupBy(x => Number(x.Staff.Unified)).ToDictionary(g => g.Key, g => g.ToList());
        var matched = new HashSet<int>(); var noStaff = new List<string>(); var ambiguous = new List<string>(); var ranks = new List<string>();
        foreach (var emp in data.Employees.Where(x => x.Month == month))
        {
            var details = PayrollMath.Decode<string>(emp.Details); var number = Number(details.GetValueOrDefault("الرقم الموحد"));
            List<(Staff Staff, int Index)>? candidates = null;
            if (number.Length > 0) staffByNumber.TryGetValue(number, out candidates);
            if (candidates == null) staffByName.TryGetValue(PayrollMath.Key(emp.Name), out candidates);
            if (candidates == null || candidates.Count == 0) { noStaff.Add($"{emp.Name} · {emp.Rank} · {emp.Workplace}"); continue; }
            if (candidates.Count > 1) { ambiguous.Add($"{emp.Name} · يوجد أكثر من سجل مطابق في الملاك"); foreach (var candidate in candidates) matched.Add(candidate.Index); continue; }
            var match = candidates[0]; matched.Add(match.Index);
            if (!string.IsNullOrWhiteSpace(emp.Rank) && !string.IsNullOrWhiteSpace(match.Staff.Rank) && SalaryComparison.RankKey(emp.Rank) != SalaryComparison.RankKey(match.Staff.Rank))
                ranks.Add($"{emp.Name} · الرواتب: {emp.Rank} · الملاك: {match.Staff.Rank}");
        }
        var noSalary = data.Staff.Select((s, i) => (Staff: s, Index: i)).Where(x => !matched.Contains(x.Index) && !Inactive.Any(term => PayrollMath.Normalize(x.Staff.Status ?? "").Contains(term)))
            .Select(x => $"{x.Staff.Name} · {x.Staff.Rank} · {x.Staff.Status} · {x.Staff.Workplace}").ToList();
        return [new("لهم راتب وليسوا في الملاك", "بالرقم الموحد أو الاسم", noStaff), new("في الملاك بلا راتب", "المستمرون غير المطابقين", noSalary),
            new("الرتبة مختلفة عن الملاك", "للسجلات ذات المطابقة الواحدة", ranks), new("مطابقة ملاك غير حاسمة", "استُبعدت السجلات متعددة التطابق من النتائج الأخرى", ambiguous)];
    }
}
