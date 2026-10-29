using System.Text.Json;
namespace SalaryWindows;
internal sealed record StaffField(string Label, string Value);
internal sealed record StaffSection(string Title, List<StaffField> Fields);
internal sealed record StaffReportData(string Name, string Status, string Subtitle, List<StaffSection> Sections)
{
    public static StaffReportData From(Staff staff)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        List<StaffSection> source;
        try { source = string.IsNullOrEmpty(staff.Details) ? [] : JsonSerializer.Deserialize<List<StaffSection>>(Convert.FromBase64String(staff.Details), options) ?? []; }
        catch (Exception e) when (e is JsonException or FormatException) { throw new InvalidDataException("تفاصيل سجل الملاك غير صالحة.", e); }
        string? Value(string section, params string[] names)
        {
            var keys = names.Select(PayrollMath.Normalize).ToHashSet();
            return source.Where(x => x.Title == section).SelectMany(x => x.Fields).FirstOrDefault(x => keys.Contains(PayrollMath.Normalize(x.Label)))?.Value;
        }
        string? Any(params string[] names)
        {
            var keys = names.Select(PayrollMath.Normalize).ToHashSet();
            return source.SelectMany(x => x.Fields).FirstOrDefault(x => keys.Contains(PayrollMath.Normalize(x.Label)))?.Value;
        }
        var result = new List<StaffSection>();
        void Add(string title, params (string Label, string? Value)[] fields)
        {
            var list = fields.Where(f => !string.IsNullOrWhiteSpace(f.Value)).Select(f => new StaffField(f.Label, f.Value!.Trim())).ToList();
            if (list.Count != 0) result.Add(new(title, list));
        }
        Add("المعلومات الشخصية", ("اسم الأم", Value("المعلومات الشخصية", "اسم الأم", "اسم الام الكامل", "اسم الام") ?? Any("اسم الأم", "اسم الام الكامل", "اسم الام", "اسم الوالدة")),
            ("التولد", Value("المعلومات الشخصية", "التولد")), ("الحالة الزوجية", Value("المعلومات الشخصية", "الحالة الزوجية")),
            ("اسم الزوجة / الزوج", Value("المعلومات الشخصية", "اسم الزوجة/ الزوج", "اسم الزوجة / الزوج")),
            ("التحصيل الدراسي", Value("التحصيل الدراسي", "التحصيل الدراسي")), ("الحالة الوظيفية", staff.Status));
        Add("المعلومات الوظيفية ومكان العمل", ("الرتبة", staff.Rank), ("المنصب الإداري", Value("الوظيفية", "المنصب الاداري")),
            ("المنصب", Value("الوظيفية", "المنصب")), ("العنوان الوظيفي", Value("الوظيفية", "العنوان الوظيفي")),
            ("محل العمل", staff.Workplace), ("الرقم الإحصائي", staff.Stat), ("الرقم الموحد", staff.Unified),
            ("رقم أمر المنصب", Value("الوظيفية", "رقم الامر الوزاري لاسناد المنصب")), ("تاريخ أمر المنصب", Value("الوظيفية", "تاريخ امر المنصب")),
            ("المباشرة بالمنصب", Value("الوظيفية", "تاريخ المباشرة بالمنصب")));
        Add("التعيين والمباشرة", ("رقم أمر التعيين", Value("التعيين والمباشرة", "رقم امر التعيين", "رقم الامر")),
            ("تاريخ أمر التعيين", Value("التعيين والمباشرة", "تاريخ التعيين", "تاريخ الامر")),
            ("أول مباشرة بالخدمة", Value("التعيين والمباشرة", "تاريخ مباشرة بالخدمة لاول مرة")),
            ("جهة التعيين", Value("الوظيفية", "جهة التعيين")), ("تاريخ مباشرة الإعادة", Value("التعيين والمباشرة", "تاريخ مباشرة بالاعادة")),
            ("رقم أمر الإعادة", Value("التعيين والمباشرة", "رقم امر الاعادة للخدمة")), ("تاريخ أمر الإعادة", Value("التعيين والمباشرة", "تاريخ امر الاعادة للخدمة")));
        Add("الترقية", ("شهر الترقية", Value("الترقية", "شهر الترقية", "الشهر")), ("سنة الترقية", Value("الترقية", "سنة الترقية")),
            ("رقم أمر الترقية", Value("الترقية", "رقم امر الترقية")), ("تاريخ الترقية", Value("الترقية", "تاريخ الترقية")));
        Add("السجل والملاحظات", ("تاريخ تحديث السجل", Value("السجل", "تاريخ التحديث")), ("الملاحظات", Value("الملاحظات", "الملاحظات")));
        return new(staff.Name, staff.Status, string.Join(" · ", new[] { staff.Rank, staff.Workplace }.Where(x => !string.IsNullOrWhiteSpace(x))), result);
    }
}
