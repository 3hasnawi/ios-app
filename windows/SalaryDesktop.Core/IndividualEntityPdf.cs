using System.Globalization;
using System.Security.Cryptography;

namespace SalaryDesktop.Core;

/// كتاب جهة منفردة بنفس تخطيط «مسودة PDF» في الآيفون؛ قائمة الأسماء تظل ملفاً مستقلاً.
public static class IndividualEntityPdf
{
    private static readonly string[] Months = ["كانون الثاني", "شباط", "آذار", "نيسان", "أيار", "حزيران", "تموز", "آب", "أيلول", "تشرين الأول", "تشرين الثاني", "كانون الأول"];
    private static string Eastern(string value) => string.Concat(value.Select(c => c is >= '0' and <= '9' ? (char)('٠' + c - '0') : c));
    private static string Number(long n) => Eastern(n.ToString("N0", CultureInfo.GetCultureInfo("en-US")));
    public static byte[] Generate(IndividualEntityPlan plan, GroupLetterSettings settings, byte[] fontBytes, byte[] logoBytes)
    {
        if (plan.Id is not ("ramadi" or "housing" or "water" or "rafidain_anbar") || plan.Total <= 0 || plan.Total != plan.Rows.Sum(r => r.Amount) || plan.Words != ArabicAmountWords.Dinars(plan.Total) || !TransferPreparation.ValidIban(plan.PayerAccount) || plan.People <= 0 || plan.Rows.Count == 0 || plan.Rows.Any(r => r.Amount < 0 || string.IsNullOrWhiteSpace(r.Name)) || plan.Rows.Select(r => PayrollAnalysis.Normalize(r.Name).Replace(" ", "", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).Count() != plan.People) throw new InvalidDataException("بيانات جهة الكتاب أو مجموعها غير صالح.");
        if (plan.Id == "rafidain_anbar" && plan.BeneficiaryAccount is not null) throw new InvalidDataException("حساب كتاب فرع الأنبار غير صالح.");
        if (plan.Id != "rafidain_anbar" && (string.IsNullOrWhiteSpace(plan.BeneficiaryName) || string.IsNullOrWhiteSpace(plan.Bank) || string.IsNullOrWhiteSpace(plan.BeneficiaryAccount))) throw new InvalidDataException("حساب المستفيد أو اسمه أو مصرفه غير مكتمل.");
        if (plan.Id == "ramadi" && (!TransferPreparation.ValidIban(plan.BeneficiaryAccount!) || plan.PayerAccount == plan.BeneficiaryAccount)) throw new InvalidDataException("حساب المستفيد غير صالح.");
        if (plan.Id is "water" or "housing" && (plan.BeneficiaryAccount!.Length != 15 || plan.BeneficiaryAccount.Any(c => !char.IsAsciiDigit(c)))) throw new InvalidDataException("حساب المستفيد المحلي غير صالح.");
        if (plan.Id == "water" && (plan.People != 1 || plan.Rows.Any(r => string.IsNullOrWhiteSpace(r.Rank)) || plan.Rows.Select(r => PayrollAnalysis.Normalize(r.Rank)).Distinct().Count() != 1)) throw new InvalidDataException("كتاب الموارد المائية يحتاج منتسباً واحداً ورتبة موحدة.");
        var key = MonthNames.SortKey(plan.Month); var bodyKey = string.IsNullOrWhiteSpace(settings.BodyMonth) ? key : MonthNames.SortKey(settings.BodyMonth);
        if (key == 0 || bodyKey == 0 || settings.OutgoingMonth is < 1 or > 12 || settings.SignatureOffset is < -60 or > 60 || new[] { settings.Header, settings.Number, settings.First, settings.Second, settings.Third }.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("راجع الشهر والترويسة والعدد وأسماء اللجنة.");
        if (fontBytes.Length is 0 or > 3_000_000 || logoBytes.Length is 0 or > 5_000_000) throw new InvalidDataException("خط الكتاب أو الشعار غير صالح.");
        var bodyValues = new Dictionary<string, string> { ["المبلغ"] = Number(plan.Total), ["المبلغ كتابة"] = plan.Words, ["حسابنا"] = plan.PayerAccount,
            ["الشهر"] = Months[bodyKey % 100 - 1], ["السنة"] = Eastern((bodyKey / 100).ToString(CultureInfo.InvariantCulture)) };
        if (plan.BeneficiaryName is not null) bodyValues["المستفيد"] = plan.BeneficiaryName;
        if (plan.BeneficiaryAccount is not null) bodyValues["حساب المستفيد"] = plan.BeneficiaryAccount;
        if (plan.Bank is not null) bodyValues["المصرف"] = plan.Bank;
        if (plan.Id == "water") { bodyValues["الرتبة"] = plan.Rows[0].Rank; bodyValues["الاسم"] = plan.Rows[0].Name; }
        var body = IndividualLetterBody.Render(plan.Id, settings.BodyTemplate, bodyValues);
        // نفس كتل «مسودة PDF» في الآيفون (TransferWordEntityDraft): الموارد المائية لشخص واحد بلا قائمة مرفقة.
        var date = AdaptiveLetterPdf.OutgoingDate(plan.Month, settings.OutgoingMonth);
        var blocks = AdaptiveLetterPdf.Opening(settings, date, body);
        if (plan.Id != "water") blocks.Add(AdaptiveLetterPdf.Attachment(plan.Rows.Count));
        blocks.Add(AdaptiveLetterPdf.Signatures(settings, date));
        blocks.Add(AdaptiveLetterPdf.Footer(settings));
        var checks = new List<string> { plan.PayerAccount, plan.Total.ToString(CultureInfo.InvariantCulture) };
        if (plan.BeneficiaryAccount is not null) checks.Add(plan.BeneficiaryAccount);
        // خط الجدول غير مستعمل هنا؛ Arial من ويندوز للعنوان، وخط الكتاب بديله عند غيابه.
        return AdaptiveLetterPdf.Write(blocks, LetterFonts.Resolve(fontBytes, fontBytes, logoBytes), false, settings.SignatureOffset, checks);
    }
    public static void Save(IndividualEntityPlan plan, GroupLetterSettings settings, byte[] font, byte[] logo, string destination)
    {
        var target = Path.GetFullPath(destination); if (!string.Equals(Path.GetExtension(target), ".pdf", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("اختر ملف PDF صالحاً.");
        var bytes = Generate(plan, settings, font, logo); var temp = Path.Combine(Path.GetDirectoryName(target)!, ".entity-letter-" + Guid.NewGuid().ToString("N") + ".tmp");
        try { using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { file.Write(bytes); file.Flush(true); } using (var file = File.OpenRead(temp)) if (!SHA256.HashData(file).AsSpan().SequenceEqual(SHA256.HashData(bytes))) throw new InvalidDataException("تعذّر التحقق من الملف المحفوظ."); File.Move(temp, target, true); }
        finally { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
