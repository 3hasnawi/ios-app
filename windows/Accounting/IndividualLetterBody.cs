using System.Text.RegularExpressions;

namespace SalaryDesktop.Core;

/// يحافظ تعديل المتن على جميع الحقول المحاسبية كما هي.
public static class IndividualLetterBody
{
    private static readonly Regex Tokens = new(@"\{\{[^{}]+\}\}", RegexOptions.CultureInvariant);
    public static string Default(string id)
    {
        const string start = "نرجو تحويل مبلغ قدره ({{المبلغ}}) ({{المبلغ كتابة}}) لا غيرها من حسابنا المرقم ({{حسابنا}}) المفتوح لديكم ";
        const string end = " لشهر ({{الشهر}} / {{السنة}}) وتزويدنا بالإشعار اللازم بذلك.";
        return id switch {
            "food_company" => start + "إلى حساب ({{المستفيد}}) المرقم ({{حساب المستفيد}}) (مصرف الرافدين / فرع الأنبار) عن مستحقات الشركة عقد تجهيز الأرزاق الجافة والطرية المرقم ({{عقد الإطعام}})" + end,
            "food_tax" => start + "إلى حساب ({{المستفيد}}) المرقم ({{حساب المستفيد}}) (مصرف الرافدين / فرع الأنبار) وذلك عن أمانات ضريبة عقد تجهيز الأرزاق الجافة والطرية المرقم ({{عقد الإطعام}})" + end,
            "tax_social" => start + "إلى حساب ({{المستفيد}}) المرقم ({{حساب المستفيد}}) (مصرف الرافدين / فرع الأنبار) وذلك عن مبالغ رسوم الضريبة والحماية الاجتماعية لرواتب قيادة شرطة الأنبار المبينة تفاصيلها أدناه" + end,
            "contracts" => start + "إلى حساب ({{المستفيد}}) المرقم ({{حساب المستفيد}}) (مصرف الرافدين / فرع الأنبار) وذلك عن المساهمات التقاعدية بنسبة ١٧٪ الخاصة بالمتعاقدين بصفة رجل أمن ضمن تشكيلات قيادتنا حسب الجدول أدناه" + end,
            "cash_salary" => start + "إلى حساب ({{المستفيد}}) المرقم ({{حساب المستفيد}}) (مصرف الرافدين / فرع الأنبار) وذلك عن أمانات رواتب منتسبي قيادتنا المبينة تفاصيلها بالقائمة المرفقة" + end,
            "ramadi" => start + "إلى حساب ({{المستفيد}}) المرقم ({{حساب المستفيد}}) والمفتوح لدى ({{المصرف}}) وذلك عن المبالغ المستقطعة والمحسومة من منتسبي قيادة شرطة الأنبار وحسب القوائم المرفقة طياً" + end,
            "housing" => start + "إلى حساب ({{المستفيد}}) المرقم ({{حساب المستفيد}}) والمفتوح لدى ({{المصرف}}) وذلك عن مبالغ السلف والقروض المستقطعة والمحسومة من منتسبي قيادة شرطة الأنبار المدرجة أسماؤهم في القائمة المرفقة طياً" + end,
            "water" => start + "إلى حساب ({{المستفيد}}) المرقم ({{حساب المستفيد}}) والمفتوح لدى ({{المصرف}}) وذلك عن مبالغ استقطاع منسوبنا {{الرتبة}} ({{الاسم}})" + end,
            "rafidain_anbar" => start + "إلى حساب مصرفكم (مصرف الرافدين / فرع الأنبار) وذلك عن مبالغ السلف والقروض المستقطعة والمحسومة من منتسبينا المدرجة أسماؤهم في القائمة المرفقة" + end,
            _ => throw new InvalidDataException("نوع متن الكتاب غير معروف.")
        };
    }
    public static void Validate(string id, string template)
    {
        if (string.IsNullOrWhiteSpace(template) || template.Length > 4000 || template.Any(c => char.IsControl(c) && c != '\n')) throw new InvalidDataException("راجع متن الكتاب.");
        var expected = Tokens.Matches(Default(id)).Select(m => m.Value).Order(StringComparer.Ordinal);
        var actual = Tokens.Matches(template).Select(m => m.Value).Order(StringComparer.Ordinal);
        var withoutTokens = Tokens.Replace(template, "");
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal) || withoutTokens.Contains("{{", StringComparison.Ordinal) || withoutTokens.Contains("}}", StringComparison.Ordinal))
            throw new InvalidDataException("احتفظ بجميع حقول المبلغ والحساب والشهر والحقول الأخرى بين الأقواس كما هي.");
    }
    public static string Render(string id, string? custom, IReadOnlyDictionary<string, string> values)
    {
        var template = string.IsNullOrWhiteSpace(custom) ? Default(id) : custom; Validate(id, template);
        return Tokens.Replace(template, match => {
            var name = match.Value[2..^2];
            if (!values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("قيمة أحد حقول الكتاب ناقصة.");
            return value;
        });
    }
}
