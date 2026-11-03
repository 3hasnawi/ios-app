using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SalaryDesktop.Core;

/// كتاب المجموعة بنفس تخطيط «مسودة PDF» في الآيفون؛ يُطابق مصادر جدول CSV ويُظهر الجدول في صفحة واحدة.
public static class GroupLetterPdf
{
    /// القوس الفارغ في نسخة التبليغ مساحة للكتابة اليدوية، لا مسافات عادية تُطوى عند الرسم.
    public static string FooterWritingSpace(string footer) => Regex.Replace(footer, @"\([ \t\u00A0]*\)",
        _ => "(" + new string('\u00A0', 20) + ")", RegexOptions.CultureInvariant);
    private static readonly string[] Months = ["كانون الثاني", "شباط", "آذار", "نيسان", "أيار", "حزيران", "تموز", "آب", "أيلول", "تشرين الأول", "تشرين الثاني", "كانون الأول"];
    public const string DefaultBodyTemplate = "نرجو تحويل مبلغ قدره ({{المبلغ}}) ({{المبلغ كتابة}}) لا غيرها من حسابنا المرقم ({{حسابنا}}) المفتوح لديكم إلى الحسابات المدرجة تفاصيلها في القائمة المرفقة مع قرص بصيغة (CSV) عن استقطاعات شهر ({{الشهر}} / {{السنة}}) وتزويدنا بالإشعار اللازم بذلك.";
    private static string Eastern(string value) => AdaptiveLetterPdf.Eastern(value);
    private static string Group(long value) => value.ToString("N0", CultureInfo.GetCultureInfo("en-US"));
    private static string Body(GroupLetterPlan plan, GroupLetterSettings settings, int year, int month)
    {
        var template = string.IsNullOrWhiteSpace(settings.BodyTemplate) ? DefaultBodyTemplate : settings.BodyTemplate;
        ValidateBodyTemplate(template);
        return template.Replace("{{المبلغ}}", Eastern(Group(plan.Total)), StringComparison.Ordinal).Replace("{{المبلغ كتابة}}", plan.Words, StringComparison.Ordinal)
            .Replace("{{حسابنا}}", plan.PayerAccount, StringComparison.Ordinal).Replace("{{الشهر}}", Months[month - 1], StringComparison.Ordinal).Replace("{{السنة}}", Eastern(year.ToString(CultureInfo.InvariantCulture)), StringComparison.Ordinal);
    }
    public static void ValidateBodyTemplate(string template)
    {
        if (template.Length > 4000 || template.Any(c => char.IsControl(c) && c != '\n')) throw new InvalidDataException("متن الكتاب غير صالح.");
        var tokens = Regex.Matches(template, @"\{\{[^{}]+\}\}").Select(m => m.Value).Order(StringComparer.Ordinal).ToArray();
        var expected = Regex.Matches(DefaultBodyTemplate, @"\{\{[^{}]+\}\}").Select(m => m.Value).Order(StringComparer.Ordinal).ToArray();
        if (!tokens.SequenceEqual(expected)) throw new InvalidDataException("يجب إبقاء حقول متن الكتاب المحاسبية دون حذف أو تكرار.");
        var stripped = Regex.Replace(template, @"\{\{[^{}]+\}\}", "");
        if (stripped.Contains("{{", StringComparison.Ordinal) || stripped.Contains("}}", StringComparison.Ordinal)) throw new InvalidDataException("تحتوي صيغة متن الكتاب على حقل غير صالح.");
    }
    public static byte[] Generate(GroupLetterPlan plan, GroupLetterSettings settings, byte[] fontBytes, byte[] tableFontBytes, byte[] logoBytes)
    {
        if (fontBytes.Length is 0 or > 3_000_000 || tableFontBytes.Length is 0 or > 3_000_000 || logoBytes.Length is 0 or > 5_000_000) throw new InvalidDataException("حجم الخط أو الشعار غير صالح.");
        GroupLetterPreparation.Verify(plan);
        if (!TransferPreparation.ValidIban(plan.PayerAccount) || plan.Lines.Any(l => l.Amount <= 0 || !TransferPreparation.ValidIban(l.Iban)))
            throw new InvalidDataException("بيانات كتاب المجموعة غير متطابقة.");
        var key = MonthNames.SortKey(plan.Month); var displayKey = string.IsNullOrWhiteSpace(settings.BodyMonth) ? key : MonthNames.SortKey(settings.BodyMonth);
        if (key == 0 || displayKey == 0 || settings.OutgoingMonth is < 1 or > 12 || settings.SignatureOffset is < -60 or > 60) throw new InvalidDataException("شهر الكتاب أو موضع اللجنة غير صالح.");
        string[] required = [settings.Header, settings.Number, settings.First, settings.Second, settings.Third];
        if (required.Any(s => string.IsNullOrWhiteSpace(s)) || required.Any(s => s.Length > 2000 || s.Any(c => char.IsControl(c) && c != '\n')) || settings.Footer.Length > 4000 || settings.Footer.Any(c => char.IsControl(c) && c != '\n'))
            throw new InvalidDataException("أكمل الترويسة والعدد وأسماء أعضاء اللجنة قبل إنشاء الكتاب.");
        // نفس كتل «مسودة PDF» في الآيفون (TransferWordGroupDraft): الكتاب في الصفحة الأولى وجدول المجموعة في صفحة واحدة مستقلة.
        var date = AdaptiveLetterPdf.OutgoingDate(plan.Month, settings.OutgoingMonth);
        var blocks = AdaptiveLetterPdf.Opening(settings, date, Body(plan, settings, displayKey / 100, displayKey % 100));
        blocks.Add(LetterBlock.Of(new LetterParagraph($"المرفقات:\nقائمة أسماء الحسابات من تسلسل (1) لغاية تسلسل ({plan.Lines.Count.ToString(CultureInfo.InvariantCulture)})\nقرص (CSV).", HalfPoints: 24, Bold: true)));
        blocks.Add(AdaptiveLetterPdf.Signatures(settings, date));
        blocks.Add(AdaptiveLetterPdf.Footer(settings with { Footer = FooterWritingSpace(settings.Footer) }));
        blocks.Add(LetterBlock.Break);
        blocks.Add(LetterBlock.Of(new LetterParagraph($"استقطاعات قيادة شرطة الأنبار لشهر ({AdaptiveLetterPdf.MonthName(key % 100)} / {AdaptiveLetterPdf.Eastern((key / 100).ToString(CultureInfo.InvariantCulture))})", HalfPoints: 28, Bold: true, Centered: true)));
        var rows = plan.Lines.Select((line, index) => new[] { (index + 1).ToString(CultureInfo.InvariantCulture), line.Title, line.Iban, line.Branch, AdaptiveLetterPdf.Grouped(line.Amount) }).ToList();
        rows.Add(["", "", "", "المجموع", AdaptiveLetterPdf.Grouped(plan.Total)]);
        blocks.Add(LetterBlock.Of(new LetterTable(["ت", "المديرية", "رقم الايبان", "الفرع", "المبلغ"], rows, "Arial", 22, [40, 252, 322, 220, 166], BoldLastRow: true)));
        var checks = new List<string> { plan.PayerAccount, plan.Total.ToString(CultureInfo.InvariantCulture) };
        foreach (var line in plan.Lines) { checks.Add(line.Iban); checks.Add(AdaptiveLetterPdf.Grouped(line.Amount)); }
        return AdaptiveLetterPdf.Write(blocks, LetterFonts.Resolve(fontBytes, tableFontBytes, logoBytes), true, settings.SignatureOffset, checks);
    }
    public static void SaveDraft(GroupLetterPlan plan, GroupLetterSettings settings, byte[] fontBytes, byte[] tableFontBytes, byte[] logoBytes, string destination)
    {
        var target = Path.GetFullPath(destination);
        if (!string.Equals(Path.GetExtension(target), ".pdf", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("اختر ملف PDF صالحاً.");
        var directory = Path.GetDirectoryName(target) ?? throw new InvalidDataException("مجلد الحفظ غير صالح.");
        var bytes = Generate(plan, settings, fontBytes, tableFontBytes, logoBytes);
        var temp = Path.Combine(directory, ".group-letter-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { file.Write(bytes); file.Flush(true); }
            using (var file = File.OpenRead(temp)) if (!SHA256.HashData(file).AsSpan().SequenceEqual(SHA256.HashData(bytes))) throw new InvalidDataException("تعذّر التحقق من الكتاب المحفوظ.");
            File.Move(temp, target, true);
        }
        finally { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
