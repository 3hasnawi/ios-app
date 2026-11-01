using System.Globalization;
using System.Text.Json;

namespace SalaryDesktop.Core;

public sealed record ProfileLine(string Title, string Value, decimal? Amount = null, string Note = "");
public sealed record ProfileMonth(string Month, IReadOnlyList<Employee> Employees, IReadOnlyList<Deduction> Deductions)
{
    public Employee? Employee => Employees.Count == 1 ? Employees[0] : null;
    public bool Ambiguous => Employees.Count > 1;
}
public sealed record PersonProfile(string Key, string Name, IReadOnlyList<ProfileMonth> Months, StaffMember? Staff);
public sealed record Payslip(IReadOnlyList<ProfileLine> Allowances, ProfileLine? Gross, IReadOnlyList<ProfileLine> Deductions, ProfileLine? TotalDeductions,
    IReadOnlyList<ProfileLine> EntityDeductions, IReadOnlyList<ProfileLine> Info, decimal? Net);
public sealed record ProfileChange(string Title, string Before, string After, string Difference);
public sealed record DeductionHistoryRow(string Month, string Title, decimal? Amount, string Note);
public sealed record SearchPerson(string Key, string Name, string Rank, string Workplace, string LatestMonth, string Unified, string Stat, string Mother);

/// ملف المنتسب: كل ما يخصه في الأشهر المحفوظة، مربوطاً بالاسم الموحّد بلا مسافات مثل الآيفون.
public static class PersonProfiles
{
    public static string Key(string name) => PayrollAnalysis.Normalize(name).Replace(" ", "", StringComparison.Ordinal);

    private static readonly string[] AllowanceKeys = ["الاسمي", "عدد العلاوات", "الخطورة", "الارزاق", "الاستثنائية", "الاطفال", "الزوجية", "الشهادة", "منصب", "مخصصات صنف", "نسبة الصنف", "جامعية", "مخصصات نقل", "مؤقتة", "اضافية", "مخصصات اخرى", "لقب علمي", "نسبة من الاسمي"];
    private static readonly string[] CountKeys = ["عدد العلاوات", "نسبة الصنف", "نسبة من الاسمي"];
    private static readonly string[] DeductionKeys = ["توقيفات تقاعدية", "الرعاية الصحية", "صندوق الشهداء", "نشاط صحي", "نشاط رياضي", "الضريبة", "تضمين", "ايراد نهائي", "تامين صحي", "حماية الاجتماعية", "رسم طابع", "مجموع استقطاعات المصارف", "% استقطاع 3"];
    private static readonly (string Name, string Amount)[] Pairs = [("اسم الاستقطاع", "مبلغ الاستقطاع"), ("2اسم الاستقطاع", "2مبلغ الاستقطاع"), ("اسم استقطاع المؤقته", "استقطاع مؤقت")];
    private static readonly string[] InfoKeys = ["الدرجة الوظيفية", "الرقم الوظيفي", "الرقم الموحد", "رقم الضمان الصحي", "طريقة الدفع", "توطين المصرف", "IBAN", "اسم الوكالة", "اسم المديرية", "NO_markaz", "اسم الشهادة", "اسم الشهدة", "اسم المنصب", "اسم الصنف", "اسم المخصصات الاخرى", "اسم اللقب العلمي", "ملاحظات عامه", "ملاحظات"];

    public static Dictionary<string, string> Details(Employee employee)
    {
        if (employee.Details is null || employee.Details.Length == 0) return new(StringComparer.Ordinal);
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(employee.Details) ?? new(StringComparer.Ordinal); }
        catch (JsonException ex) { throw new InvalidDataException("تعذّر قراءة تفاصيل راتب المنتسب.", ex); }
    }

    public static PersonProfile Build(BackupSnapshot source, string name, IReadOnlyList<StaffMember>? staff)
    {
        var key = Key(name);
        if (key.Length == 0) throw new InvalidDataException("اسم المنتسب فارغ.");
        var months = source.Months.Select(m => m.Name)
            .Select(month => new ProfileMonth(month, source.Employees.Where(e => e.Month == month && Key(e.Name) == key).ToArray(),
                source.Deductions.Where(d => d.Month == month && Key(d.Name) == key).ToArray()))
            .Where(m => m.Employees.Count > 0 || m.Deductions.Count > 0)
            .OrderByDescending(m => MonthNames.SortKey(m.Month)).ToArray();
        var member = staff?.FirstOrDefault(s => Key(s.Name) == key);
        var display = months.SelectMany(m => m.Employees).Select(e => e.Name).FirstOrDefault() ?? member?.Name ?? months.SelectMany(m => m.Deductions).Select(d => d.Name).FirstOrDefault() ?? name;
        return new PersonProfile(key, display, months, member);
    }

    internal static decimal? Amount(string value)
    {
        var digits = string.Concat(value.Trim().Select(c => c is >= '٠' and <= '٩' ? (char)('0' + c - '٠') : c is '٬' or ',' ? '\0' : c is '٫' ? '.' : c).Where(c => c != '\0'));
        return decimal.TryParse(digits, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) ? amount : null;
    }
    private static string Display(string value) => Amount(value) is { } amount && !value.Contains('%') ? ReportSheet.Money(amount) : value;

    public static Payslip Payslip(ProfileMonth month)
    {
        var employee = month.Employee;
        var details = employee is null ? new Dictionary<string, string>(StringComparer.Ordinal) : Details(employee);
        string? Get(string key) => details.FirstOrDefault(p => PayrollAnalysis.Normalize(p.Key) == PayrollAnalysis.Normalize(key)).Value;
        var allowances = AllowanceKeys.Select(k => (k, v: Get(k))).Where(p => !string.IsNullOrWhiteSpace(p.v))
            .Select(p => new ProfileLine(p.k, CountKeys.Contains(p.k) ? p.v! : Display(p.v!), CountKeys.Contains(p.k) ? null : Amount(p.v!))).ToList();
        var deductions = DeductionKeys.Select(k => (k, v: Get(k))).Where(p => !string.IsNullOrWhiteSpace(p.v))
            .Select(p => new ProfileLine(p.k, Display(p.v!), Amount(p.v!))).ToList();
        foreach (var (nameKey, amountKey) in Pairs)
            if (Get(amountKey) is { Length: > 0 } value) deductions.Add(new ProfileLine(Get(nameKey) is { Length: > 0 } label ? label : amountKey, Display(value), Amount(value)));
        var gross = Get("الاستحقاق") is { Length: > 0 } g ? new ProfileLine("الاستحقاق", Display(g), Amount(g)) : null;
        var total = Get("مجموع الاستقطاعات") is { Length: > 0 } t ? new ProfileLine("مجموع الاستقطاعات", Display(t), Amount(t)) : null;
        var entities = month.Deductions.Select(d =>
        {
            var inactive = PayrollAnalysis.IsInactive(d);
            var note = d.Note; var end = note.IndexOf('⟧');
            if (inactive && end >= 0) note = note["⟦حالة الاستقطاع:".Length..end] + note[(end + 1)..];
            return new ProfileLine(d.Title, d.Amount is { } a ? ReportSheet.Money(a) : "غير محتسب", d.Amount, note.Trim());
        }).ToArray();
        var info = InfoKeys.Select(k => (k, v: Get(k))).Where(p => !string.IsNullOrWhiteSpace(p.v)).Select(p => new ProfileLine(p.k == "NO_markaz" ? "المركز" : p.k, p.v!)).ToArray();
        return new Payslip(allowances, gross, deductions, total, entities, info, employee?.Net);
    }

    /// الشهر السابق مباشرة الموجود في ملف المنتسب.
    public static ProfileMonth? Previous(PersonProfile profile, string month)
    {
        var key = MonthNames.SortKey(month);
        return profile.Months.Where(m => MonthNames.SortKey(m.Month) < key).OrderByDescending(m => MonthNames.SortKey(m.Month)).FirstOrDefault();
    }

    public static IReadOnlyList<ProfileChange> Changes(PersonProfile profile, string month)
    {
        var current = profile.Months.FirstOrDefault(m => m.Month == month); var previous = Previous(profile, month);
        if (current is null || previous is null) return [];
        var result = new List<ProfileChange>();
        string Money(decimal? v) => v is { } a ? ReportSheet.Money(a) : "—";
        string Diff(decimal? a, decimal? b) => a is { } x && b is { } y ? (y - x > 0 ? "+" : "") + ReportSheet.Money(y - x) : "";
        var before = previous.Employee; var after = current.Employee;
        if (before?.Net != after?.Net) result.Add(new("صافي الراتب", Money(before?.Net), Money(after?.Net), Diff(before?.Net, after?.Net)));
        if (before is not null && after is not null)
        {
            if (before.Rank != after.Rank) result.Add(new("الرتبة", before.Rank, after.Rank, "ترفيع أو تعديل"));
            if (before.Workplace != after.Workplace) result.Add(new("محل العمل", before.Workplace, after.Workplace, "نقل"));
            var a = Details(before); var b = Details(after);
            foreach (var k in new[] { "IBAN", "طريقة الدفع", "الدرجة الوظيفية" })
                if (a.GetValueOrDefault(k, "") != b.GetValueOrDefault(k, "")) result.Add(new(k, a.GetValueOrDefault(k, "—"), b.GetValueOrDefault(k, "—"), "تغيّر"));
            foreach (var k in AllowanceKeys.Concat(DeductionKeys).Concat(["الاستحقاق", "مجموع الاستقطاعات"]).Except(CountKeys))
            {
                var x = a.TryGetValue(k, out var va) ? Amount(va) : null; var y = b.TryGetValue(k, out var vb) ? Amount(vb) : null;
                if ((x ?? 0) != (y ?? 0)) result.Add(new(k, Money(x), Money(y), Diff(x ?? 0, y ?? 0)));
            }
        }
        var old = previous.Deductions.Where(d => !PayrollAnalysis.IsInactive(d)).GroupBy(d => d.Title).ToDictionary(g => g.Key, g => g.Sum(d => d.Amount ?? 0));
        var now = current.Deductions.Where(d => !PayrollAnalysis.IsInactive(d)).GroupBy(d => d.Title).ToDictionary(g => g.Key, g => g.Sum(d => d.Amount ?? 0));
        foreach (var title in old.Keys.Union(now.Keys).Order(StringComparer.Ordinal))
        {
            var x = old.GetValueOrDefault(title); var y = now.GetValueOrDefault(title);
            if (!old.ContainsKey(title)) result.Add(new("استقطاع جديد: " + title, "—", Money(y), "+" + Money(y)));
            else if (!now.ContainsKey(title)) result.Add(new("توقف الاستقطاع: " + title, Money(x), "—", Money(-x)));
            else if (x != y) result.Add(new("تغيّر الاستقطاع: " + title, Money(x), Money(y), Diff(x, y)));
        }
        return result;
    }

    public static IReadOnlyList<DeductionHistoryRow> History(PersonProfile profile) =>
        profile.Months.SelectMany(m => m.Deductions.Select(d => new DeductionHistoryRow(m.Month, d.Title, PayrollAnalysis.IsInactive(d) ? null : d.Amount, d.Note.StartsWith("⟦", StringComparison.Ordinal) && d.Note.IndexOf('⟧') is var end and > 0 ? d.Note["⟦حالة الاستقطاع:".Length..end] + d.Note[(end + 1)..] : d.Note))).ToArray();

    /// فهرس البحث الشامل: آخر شهر لكل منتسب من الرواتب، ثم من لا رواتب لهم من الملاك.
    public static IReadOnlyList<SearchPerson> SearchIndex(BackupSnapshot source, IReadOnlyList<StaffMember>? staff)
    {
        var people = new Dictionary<string, SearchPerson>(StringComparer.Ordinal);
        var staffByKey = new Dictionary<string, StaffMember>(StringComparer.Ordinal);
        foreach (var member in staff ?? Array.Empty<StaffMember>()) staffByKey.TryAdd(Key(member.Name), member);
        foreach (var month in source.Months.Select(m => m.Name).OrderByDescending(MonthNames.SortKey))
            foreach (var employee in source.Employees.Where(e => e.Month == month))
            {
                var key = Key(employee.Name); if (key.Length == 0 || people.ContainsKey(key)) continue;
                staffByKey.TryGetValue(key, out var member);
                var details = Details(employee);
                people[key] = new SearchPerson(key, employee.Name, employee.Rank, employee.Workplace, month,
                    member?.Unified is { Length: > 0 } u ? u : details.GetValueOrDefault("الرقم الموحد", ""), member?.Stat ?? "", member?.Mother ?? "");
            }
        foreach (var (key, member) in staffByKey)
            if (!people.ContainsKey(key)) people[key] = new SearchPerson(key, member.Name, member.Rank, member.Workplace, "", member.Unified, member.Stat, member.Mother);
        return people.Values.ToArray();
    }
}
