namespace SalaryWindows;
internal sealed record SalaryCheck(string Title, string Explanation, List<string> Lines);
internal static class SalaryChecks
{
    private static readonly string[] Allowances = ["الاسمي", "الخطورة", "الارزاق", "الاستثنائية", "الاطفال", "الزوجية", "الشهادة", "منصب", "مخصصات صنف", "جامعية", "مخصصات نقل", "مؤقتة", "اضافية", "مخصصات اخرى", "لقب علمي"];
    private sealed record Person(Employee Employee, Dictionary<string, string> Details, Dictionary<string, double> Values, string Key, string Iban);
    private static string Digits(string? value) => new string(PayrollMath.Normalize(value ?? "").Where(char.IsAsciiDigit).ToArray()).TrimStart('0');
    private static string Mask(string value) => value.Length == 0 ? "فارغ" : "…" + value[^Math.Min(value.Length, 4)..];
    public static List<SalaryCheck> Run(BackupData data, string month)
    {
        var people = data.Employees.Where(e => e.Month == month).Select(e => {
            var details = PayrollMath.Decode<string>(e.Details); var values = PayrollMath.Decode<double>(e.Items);
            foreach (var key in Allowances.Append("الاستحقاق")) if (details.TryGetValue(key, out var text) && PayrollMath.ParseNumber(text) is double x && x != 0) values[key] = x;
            var iban = new string(PayrollMath.Normalize(details.GetValueOrDefault("IBAN") ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
            return new Person(e, details, values, PayrollMath.Key(e.Name), iban);
        }).ToList();
        var checks = new List<SalaryCheck>();
        var withIban = people.Where(x => x.Iban.Length != 0).ToList(); var ibanLines = new List<string>();
        foreach (var group in withIban.GroupBy(x => x.Iban).Where(g => g.Select(x => x.Key).Distinct().Count() > 1))
            foreach (var p in group) ibanLines.Add($"{p.Employee.Name} · رقم حساب مكرر {Mask(group.Key)}");
        ibanLines.AddRange(withIban.Where(x => x.Iban.Length != 23 || !x.Iban.StartsWith("IQ")).Select(x => $"{x.Employee.Name} · رقم حساب غير صحيح ({x.Iban.Length} خانة) {Mask(x.Iban)}"));
        if (people.Count > 0 && withIban.Count * 2 >= people.Count) ibanLines.AddRange(people.Where(x => x.Iban.Length == 0).Select(x => $"{x.Employee.Name} · IBAN فارغ"));
        checks.Add(new("فحص IBAN", withIban.Count == 0 ? "لا توجد أرقام حسابات للفحص" : "الطول والبداية والتكرار؛ لا يتحقق هذا الفحص من صحة أرقام التحقق", ibanLines));
        foreach (var (label, key) in new[] { ("الرقم الوظيفي", "الرقم الوظيفي"), ("الرقم الموحد", "الرقم الموحد") })
        {
            var lines = new List<string>();
            foreach (var group in people.Where(x => Digits(x.Details.GetValueOrDefault(key)).Length > 0).GroupBy(x => Digits(x.Details.GetValueOrDefault(key))).Where(g => g.Select(x => x.Key).Distinct().Count() > 1))
                lines.Add($"{label} {group.Key} · {string.Join("، ", group.Select(x => x.Employee.Name))}");
            var present = people.Count(x => Digits(x.Details.GetValueOrDefault(key)).Length > 0);
            checks.Add(new($"تكرار {label}", present == 0 ? "لا تتوفر أرقام للفحص" : "الرقم نفسه لأكثر من اسم", lines));
        }
        var health = people.SelectMany(x => x.Values.Where(v => PayrollMath.Normalize(v.Key).Contains(PayrollMath.Normalize("الرعاية")) && v.Value != 0 && v.Value % 3500 != 0)
            .Select(v => $"{x.Employee.Name} · {v.Key}: {v.Value:N2}" )).ToList();
        bool hasHealth = people.Any(x => x.Values.Keys.Any(k => PayrollMath.Normalize(k).Contains(PayrollMath.Normalize("الرعاية"))));
        checks.Add(new("فحص الرعاية الصحية", hasHealth ? "مبالغ لا تقبل القسمة على 3,500" : "لا يتوفر بند الرعاية للفحص", health));
        bool hasTotal = people.Any(x => x.Values.ContainsKey("مجموع الاستقطاعات"));
        int netChecked = 0, grossChecked = 0; var netErrors = new List<string>(); var grossErrors = new List<string>();
        foreach (var p in people)
        {
            if (!p.Values.TryGetValue("الاستحقاق", out var gross)) continue;
            if (hasTotal && p.Employee.Net.HasValue)
            {
                netChecked++; var expected = gross - p.Values.GetValueOrDefault("مجموع الاستقطاعات");
                if (Math.Abs(expected - p.Employee.Net.Value) >= 1) netErrors.Add($"{p.Employee.Name} · المتوقع {expected:N2} · الصافي {p.Employee.Net:N2}");
            }
            if (p.Values.ContainsKey("الاسمي"))
            {
                grossChecked++; var sum = Allowances.Sum(k => p.Values.GetValueOrDefault(k));
                if (Math.Abs(sum - gross) >= 1) grossErrors.Add($"{p.Employee.Name} · المخصصات {sum:N2} · الاستحقاق {gross:N2}");
            }
        }
        bool netReliable = netChecked > 0 && (double)netErrors.Count / netChecked < .3;
        bool grossReliable = grossChecked > 0 && (double)grossErrors.Count / grossChecked < .3;
        var status = $"الصافي: {(netReliable ? "فُحص" : "غير متاح أو غير موثوق")} · الاستحقاق: {(grossReliable ? "فُحص" : "غير متاح أو غير موثوق")}";
        checks.Add(new("فحص الحساب", status, (netReliable ? netErrors : []).Concat(grossReliable ? grossErrors : []).ToList()));
        return checks;
    }
}
