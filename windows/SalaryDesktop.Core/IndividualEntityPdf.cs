using System.Globalization;
using System.Security.Cryptography;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using UglyToad.PdfPig;

namespace SalaryDesktop.Core;

/// مسودة كتاب جهة منفردة؛ قائمة الأسماء تظل ملفاً مستقلاً.
public static class IndividualEntityPdf
{
    private const float W = 595, H = 842;
    private static readonly string[] Months = ["كانون الثاني", "شباط", "آذار", "نيسان", "أيار", "حزيران", "تموز", "آب", "أيلول", "تشرين الأول", "تشرين الثاني", "كانون الأول"];
    private static string Eastern(string value) => string.Concat(value.Select(c => c is >= '0' and <= '9' ? (char)('٠' + c - '0') : c));
    private static string Number(long n) => Eastern(n.ToString("N0", CultureInfo.GetCultureInfo("en-US")));
    private static List<string> Wrap(string text, SKShaper shaper, SKPaint paint, float width) => text.Split('\n').SelectMany(line => ReconciliationPdf.Wrap(line, shaper, paint, width)).ToList();
    private static void Center(SKCanvas canvas, SKShaper shaper, SKPaint paint, string text, float center, float y) => ReconciliationPdf.DrawRight(canvas, shaper, paint, text, center + ReconciliationPdf.Width(text, shaper, paint) / 2, y);
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
        using var data = SKData.CreateCopy(fontBytes); using var face = SKTypeface.FromData(data) ?? throw new InvalidDataException("تعذّر فتح خط الكتاب."); using var logo = SKBitmap.Decode(logoBytes) ?? throw new InvalidDataException("تعذّر فتح الشعار.");
        using var shaper = new SKShaper(face); using var paint = new SKPaint { Typeface = face, TextSize = 13, IsAntialias = true, Color = SKColors.Black };
        var requiredText = body + settings.Header + settings.Number + settings.First + settings.Second + settings.Third + settings.Footer;
        if (!paint.ContainsGlyphs(requiredText.Replace("\n", "", StringComparison.Ordinal))) throw new InvalidDataException("خط الكتاب لا يدعم بعض الحروف؛ راجع الخط والبيانات.");
        using var output = new MemoryStream(); using (var pdf = SKDocument.CreatePdf(output) ?? throw new InvalidDataException("تعذّر إنشاء الكتاب."))
        {
            var canvas = pdf.BeginPage(W, H); canvas.Clear(SKColors.White); canvas.DrawBitmap(logo, new SKRect(267, 30, 338, 125));
            paint.TextSize = 9; ReconciliationPdf.DrawRight(canvas, shaper, paint, "مسودة للمراجعة", 558, 25); paint.TextSize = 13;
            var header = Wrap(settings.Header, shaper, paint, 185); if (header.Count is 0 or > 6) throw new InvalidDataException("الترويسة لا تتسع.");
            for (var i = 0; i < header.Count; i++) Center(canvas, shaper, paint, header[i], 125, 39 + i * 17);
            var date = Eastern($" / {settings.OutgoingMonth ?? key % 100} / {key / 100}");
            ReconciliationPdf.Fit(canvas, shaper, paint, "العدد: " + Eastern(settings.Number), 28, 225, 165, true); ReconciliationPdf.Fit(canvas, shaper, paint, "التاريخ:              " + date, 28, 225, 188, true);
            paint.TextSize = 15; Center(canvas, shaper, paint, "إلى / مصرف الرافدين / فرع الأنبار", W / 2, 230); Center(canvas, shaper, paint, "م / تحويل مبلغ", W / 2, 255);
            paint.TextSize = 13; ReconciliationPdf.DrawRight(canvas, shaper, paint, "تحية طيبة ..", 558, 292); float y = 320;
            foreach (var line in Wrap(body, shaper, paint, 520)) { if (y > 490) throw new InvalidDataException("متن الكتاب لا يتسع قبل اللجنة."); ReconciliationPdf.DrawRight(canvas, shaper, paint, line, 558, y); y += 19; }
            y += 15; Center(canvas, shaper, paint, "للتفضل بالاطلاع وإجراء اللازم وإعلامنا... مع التقدير.", W / 2, y); y += 25;
            if (plan.Id != "water") { paint.TextSize = 12; ReconciliationPdf.DrawRight(canvas, shaper, paint, "المرفقات: قائمة أسماء عدد (" + Eastern(plan.People.ToString(CultureInfo.InvariantCulture)) + ")", 558, y); y += 18; }
            if (y > 575 + settings.SignatureOffset) throw new InvalidDataException("متن الكتاب أو مرفقاته تتداخل مع اللجنة.");
            var signatureY = 625 + settings.SignatureOffset; paint.TextSize = 11;
            var signatures = new[] { ("معاون مدير تدقيق", settings.First, "المصروفات"), ("العميد", settings.Second, "مدير الحسابات"), ("لواء الشرطة الحقوقي", settings.Third, "قائد شرطة محافظة الأنبار") }; float[] centers = [482, 298, 113];
            for (var i = 0; i < 3; i++) { var name = string.Join(' ', signatures[i].Item2.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)); string[] values = [signatures[i].Item1, name, signatures[i].Item3, date]; for (var j = 0; j < values.Length; j++) ReconciliationPdf.Fit(canvas, shaper, paint, values[j], centers[i] - 82, centers[i] + 82, signatureY + j * 16); }
            var footer = Wrap(settings.Footer, shaper, paint, 520); if (footer.Count > 5) throw new InvalidDataException("الهامش السفلي لا يتسع.");
            for (var i = 0; i < footer.Count; i++) ReconciliationPdf.DrawRight(canvas, shaper, paint, footer[i], 558, 767 + i * 13);
            pdf.EndPage(); pdf.Close(); var bytes = output.ToArray(); using var read = PdfDocument.Open(bytes); if (read.NumberOfPages != 1) throw new InvalidDataException("عدد صفحات الكتاب غير مطابق.");
            var page = read.GetPage(1); var text = string.Concat(page.Letters.Where(l => l.StartBaseLine.Y <= H - 300 && l.StartBaseLine.Y >= H - 510).Select(l => l.Value));
            var compact = TransferPreparation.NormalizeAccount(text).Replace(",", "", StringComparison.Ordinal).Replace("٬", "", StringComparison.Ordinal);
            if (!compact.Contains(plan.PayerAccount, StringComparison.Ordinal) || !compact.Contains(plan.Total.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) || (plan.BeneficiaryAccount is not null && !compact.Contains(plan.BeneficiaryAccount, StringComparison.Ordinal))) throw new InvalidDataException("تعذّر التحقق من حسابات الكتاب أو مبلغه.");
            return bytes;
        }
    }
    public static void Save(IndividualEntityPlan plan, GroupLetterSettings settings, byte[] font, byte[] logo, string destination)
    {
        var target = Path.GetFullPath(destination); if (!string.Equals(Path.GetExtension(target), ".pdf", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("اختر ملف PDF صالحاً.");
        var bytes = Generate(plan, settings, font, logo); var temp = Path.Combine(Path.GetDirectoryName(target)!, ".entity-letter-" + Guid.NewGuid().ToString("N") + ".tmp");
        try { using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { file.Write(bytes); file.Flush(true); } using (var file = File.OpenRead(temp)) if (!SHA256.HashData(file).AsSpan().SequenceEqual(SHA256.HashData(bytes))) throw new InvalidDataException("تعذّر التحقق من الملف المحفوظ."); File.Move(temp, target, true); }
        finally { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
