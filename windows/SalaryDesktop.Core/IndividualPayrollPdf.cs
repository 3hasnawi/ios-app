using System.Globalization;
using System.Security.Cryptography;

namespace SalaryDesktop.Core;

/// كتاب منفرد بنفس تخطيط «مسودة PDF» في الآيفون؛ يُتحقق من المبلغ وحسابَي الدفع والمستفيد بعد الرسم.
public static class IndividualPayrollPdf
{
    private static readonly string[] Months = ["كانون الثاني", "شباط", "آذار", "نيسان", "أيار", "حزيران", "تموز", "آب", "أيلول", "تشرين الأول", "تشرين الثاني", "كانون الأول"];
    private static string Eastern(string s) => string.Concat(s.Select(c => c is >= '0' and <= '9' ? (char)('٠' + c - '0') : c));
    private static string Number(long n) => Eastern(n.ToString("N0", CultureInfo.GetCultureInfo("en-US")));
    public static byte[] Generate(IndividualLetterPlan plan, GroupLetterSettings settings, byte[] fontBytes, byte[] tableFontBytes, byte[] logoBytes, string? foodContract, CashSalaryList? cashList = null)
    {
        if (plan.Id is not ("food_company" or "food_tax" or "tax_social" or "contracts" or "cash_salary") || plan.Total <= 0 || plan.Total != plan.Lines.Sum(l => l.Amount) || plan.Words != ArabicAmountWords.Dinars(plan.Total) || !TransferPreparation.ValidIban(plan.PayerAccount) || plan.BeneficiaryAccount is null || !TransferPreparation.ValidIban(plan.BeneficiaryAccount) || plan.PayerAccount == plan.BeneficiaryAccount)
            throw new InvalidDataException("المبلغ أو حسابا الكتاب غير صالحين.");
        var key = MonthNames.SortKey(plan.Month); var bodyKey = string.IsNullOrWhiteSpace(settings.BodyMonth) ? key : MonthNames.SortKey(settings.BodyMonth);
        if (key == 0 || bodyKey == 0 || settings.OutgoingMonth is < 1 or > 12 || settings.SignatureOffset is < -60 or > 60 ||
            new[] { settings.Header, settings.Number, settings.First, settings.Second, settings.Third }.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("راجع الشهر والترويسة والعدد وأسماء اللجنة.");
        if (plan.Id is "food_company" or "food_tax" && (string.IsNullOrWhiteSpace(foodContract) || foodContract.Length > 200 || foodContract.Any(char.IsControl))) throw new InvalidDataException("أكمل رقم وتاريخ عقد الإطعام قبل إعداد الكتاب.");
        if (plan.Id == "cash_salary" && (cashList is null || cashList.Month != plan.Month || cashList.Total != plan.Total || cashList.Rows.Count == 0 || cashList.Rows.Sum(r => (decimal)r.Net) != plan.Total)) throw new InvalidDataException("قائمة الكاش غير مطابقة للمبلغ؛ راجع الرواتب.");
        if (fontBytes.Length is 0 or > 3_000_000 || tableFontBytes.Length is 0 or > 3_000_000 || logoBytes.Length is 0 or > 5_000_000) throw new InvalidDataException("الخط أو الشعار غير صالح.");
        var recipient = plan.Id == "food_company" ? "شركة عراق الجوال للتجارة والنقل والمقاولات والتجهيزات الغذائية المحدودة" : plan.Id == "contracts" ? "قسم التقاعد والضمان الاجتماعي للعمال في محافظة الأنبار" : "مديرية خزينة الأنبار";
        var bodyValues = new Dictionary<string, string> { ["المبلغ"] = Number(plan.Total), ["المبلغ كتابة"] = plan.Words, ["حسابنا"] = plan.PayerAccount,
            ["المستفيد"] = recipient, ["حساب المستفيد"] = plan.BeneficiaryAccount, ["الشهر"] = Months[bodyKey % 100 - 1], ["السنة"] = Eastern((bodyKey / 100).ToString(CultureInfo.InvariantCulture)) };
        // مرجع العقد يُكتب كما أدخله المستخدم، مثل الآيفون.
        if (foodContract is not null) bodyValues["عقد الإطعام"] = foodContract;
        var body = IndividualLetterBody.Render(plan.Id, settings.BodyTemplate, bodyValues);
        // نفس كتل «مسودة PDF» في الآيفون (TransferWordPayrollDraft).
        var date = AdaptiveLetterPdf.OutgoingDate(plan.Month, settings.OutgoingMonth);
        var blocks = AdaptiveLetterPdf.Opening(settings, date, body);
        var checks = new List<string> { plan.PayerAccount, plan.BeneficiaryAccount, plan.Total.ToString(CultureInfo.InvariantCulture) };
        long Line(string title, int index)
        {
            var match = plan.Lines.FirstOrDefault(l => l.Title == title) ?? (index < plan.Lines.Count ? plan.Lines[index] : null);
            return match?.Amount ?? throw new InvalidDataException("بنود جدول الكتاب غير مكتملة.");
        }
        void Table(LetterTable table)
        {
            blocks.Add(LetterBlock.Of(table));
            foreach (var row in table.Rows) checks.AddRange(row.Where(cell => cell.Length > 0 && cell.All(c => char.IsDigit(c) || c == ',')));
        }
        if (plan.Id == "cash_salary") blocks.Add(AdaptiveLetterPdf.Attachment(cashList!.Rows.Count));
        else if (plan.Id == "tax_social")
        {
            if (plan.Lines.Count != 2) throw new InvalidDataException("بنود جدول الكتاب غير مكتملة.");
            Table(new LetterTable(["ت", "البيان", "المبلغ"], [
                ["1", "الحماية الاجتماعية", Number(Line("الحماية الاجتماعية", 0))],
                ["2", "ضريبة الدخل", Number(Line("ضريبة الدخل", 1))],
                ["", "المجموع", Number(plan.Total)]], ColumnWeights: [1, 6, 3], BoldLastRow: true, WidthFraction: 0.56, AlignRight: true));
        }
        else if (plan.Id == "contracts")
        {
            // ترتيب الآيفون: ٥٪ (التوقيفات) ثم ١٢٪ (حصة الاسمي) ثم المجموع، من اليمين.
            if (plan.Lines.Count is not (2 or 3)) throw new InvalidDataException("بنود جدول الكتاب غير مكتملة.");
            if (plan.Lines.Count == 3)
                Table(new LetterTable(["مبلغ نسبة ٥٪", "مبلغ نسبة ١٢٪", "التعديل اليدوي", "المجموع"], [
                    [Number(Line("التوقيفات التقاعدية", 1)), Number(Line("حصة التقاعد من الراتب الاسمي", 0)), Number(Line("التعديل اليدوي", 2)), Number(plan.Total)]]));
            else Table(new LetterTable(["مبلغ نسبة ٥٪", "مبلغ نسبة ١٢٪", "المجموع"], [
                [Number(Line("التوقيفات التقاعدية", 1)), Number(Line("حصة التقاعد من الراتب الاسمي", 0)), Number(plan.Total)]]));
        }
        blocks.Add(AdaptiveLetterPdf.Signatures(settings, date));
        blocks.Add(AdaptiveLetterPdf.Footer(settings));
        return AdaptiveLetterPdf.Write(blocks, LetterFonts.Resolve(fontBytes, tableFontBytes, logoBytes), false, settings.SignatureOffset, checks);
    }
    public static void Save(IndividualLetterPlan plan, GroupLetterSettings settings, byte[] font, byte[] tableFont, byte[] logo, string? foodContract, CashSalaryList? cash, string destination)
    {
        var target = Path.GetFullPath(destination); if (!string.Equals(Path.GetExtension(target), ".pdf", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("اختر ملف PDF صالحاً.");
        var bytes = Generate(plan, settings, font, tableFont, logo, foodContract, cash); var temp = Path.Combine(Path.GetDirectoryName(target)!, ".letter-" + Guid.NewGuid().ToString("N") + ".tmp");
        try { using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { file.Write(bytes); file.Flush(true); } using (var file = File.OpenRead(temp)) if (!SHA256.HashData(file).AsSpan().SequenceEqual(SHA256.HashData(bytes))) throw new InvalidDataException("تعذّر التحقق من الملف المحفوظ."); File.Move(temp, target, true); }
        finally { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
