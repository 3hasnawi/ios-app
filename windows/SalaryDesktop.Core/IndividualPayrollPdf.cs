using System.Globalization;
using System.Security.Cryptography;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using UglyToad.PdfPig;

namespace SalaryDesktop.Core;

/// مسودة كتاب منفرد؛ يُتحقق من المبلغ وحسابَي الدفع والمستفيد بعد الرسم.
public static class IndividualPayrollPdf
{
    private const float W = 595, H = 842;
    private static readonly string[] Months = ["كانون الثاني", "شباط", "آذار", "نيسان", "أيار", "حزيران", "تموز", "آب", "أيلول", "تشرين الأول", "تشرين الثاني", "كانون الأول"];
    private static string Eastern(string s) => string.Concat(s.Select(c => c is >= '0' and <= '9' ? (char)('٠' + c - '0') : c));
    private static string Number(long n) => Eastern(n.ToString("N0", CultureInfo.GetCultureInfo("en-US")));
    private static List<string> Wrap(string text, SKShaper shaper, SKPaint paint, float width) => text.Split('\n').SelectMany(line => ReconciliationPdf.Wrap(line, shaper, paint, width)).ToList();
    private static void Center(SKCanvas canvas, SKShaper shaper, SKPaint paint, string text, float center, float y) => ReconciliationPdf.DrawRight(canvas, shaper, paint, text, center + ReconciliationPdf.Width(text, shaper, paint) / 2, y);
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
        if (foodContract is not null) bodyValues["عقد الإطعام"] = Eastern(foodContract);
        var body = IndividualLetterBody.Render(plan.Id, settings.BodyTemplate, bodyValues);
        using var fontData = SKData.CreateCopy(fontBytes); using var face = SKTypeface.FromData(fontData) ?? throw new InvalidDataException("تعذّر فتح خط الكتاب.");
        using var tableData = SKData.CreateCopy(tableFontBytes); using var tableFace = SKTypeface.FromData(tableData) ?? throw new InvalidDataException("تعذّر فتح خط الجدول.");
        using var logo = SKBitmap.Decode(logoBytes) ?? throw new InvalidDataException("تعذّر فتح الشعار.");
        using var shaper = new SKShaper(face); using var paint = new SKPaint { Typeface = face, TextSize = 13, IsAntialias = true, Color = SKColors.Black };
        using var stroke = new SKPaint { Color = new SKColor(55, 55, 55), Style = SKPaintStyle.Stroke, StrokeWidth = .6f, IsAntialias = true };
        var requiredText = body + settings.Header + settings.Number + settings.First + settings.Second + settings.Third + settings.Footer;
        if (!paint.ContainsGlyphs(requiredText.Replace("\n", "", StringComparison.Ordinal))) throw new InvalidDataException("خط الكتاب لا يدعم بعض الحروف؛ راجع الخط والبيانات.");
        using var output = new MemoryStream(); using (var pdf = SKDocument.CreatePdf(output) ?? throw new InvalidDataException("تعذّر إنشاء الكتاب."))
        {
            var canvas = pdf.BeginPage(W, H); canvas.Clear(SKColors.White); canvas.DrawBitmap(logo, new SKRect(267, 30, 338, 125));
            paint.TextSize = 9; ReconciliationPdf.DrawRight(canvas, shaper, paint, "مسودة للمراجعة", 558, 25); paint.TextSize = 13;
            var header = Wrap(settings.Header, shaper, paint, 185); if (header.Count is 0 or > 6) throw new InvalidDataException("الترويسة لا تتسع.");
            for (var i = 0; i < header.Count; i++) Center(canvas, shaper, paint, header[i], 125, 39 + i * 17);
            var date = Eastern($" / {settings.OutgoingMonth ?? key % 100} / {key / 100}");
            ReconciliationPdf.Fit(canvas, shaper, paint, "العدد: " + Eastern(settings.Number), 28, 225, 165, true);
            ReconciliationPdf.Fit(canvas, shaper, paint, "التاريخ:              " + date, 28, 225, 188, true);
            paint.TextSize = 15; Center(canvas, shaper, paint, "إلى / مصرف الرافدين / فرع الأنبار", W / 2, 230); Center(canvas, shaper, paint, "م / تحويل مبلغ", W / 2, 255);
            paint.TextSize = 13; ReconciliationPdf.DrawRight(canvas, shaper, paint, "تحية طيبة ..", 558, 292); float y = 320;
            foreach (var line in Wrap(body, shaper, paint, 520)) { if (y > 470) throw new InvalidDataException("متن الكتاب لا يتسع قبل اللجنة."); ReconciliationPdf.DrawRight(canvas, shaper, paint, line, 558, y); y += 19; }
            y += 15; Center(canvas, shaper, paint, "للتفضل بالاطلاع وإجراء اللازم وإعلامنا... مع التقدير.", W / 2, y); y += 22;
            var tableBoxes = new List<(float Y, long Amount)>();
            if (plan.Id is "tax_social" or "contracts")
            {
                paint.Typeface = tableFace; paint.TextSize = 10;
                foreach (var item in plan.Lines.Concat([new IndividualLetterLine("المجموع", plan.Total)]))
                {
                    if (y + 19 > 575 + settings.SignatureOffset) throw new InvalidDataException("جدول الكتاب يتداخل مع اللجنة.");
                    canvas.DrawRect(265, y, 95, 19, stroke); canvas.DrawRect(360, y, 185, 19, stroke);
                    ArabicPresentation.Fit(canvas, paint, Number(item.Amount), 265, 360, y + 13);
                    ArabicPresentation.Fit(canvas, paint, item.Title, 360, 545, y + 13);
                    tableBoxes.Add((y, item.Amount)); y += 19;
                }
                paint.Typeface = face; y += 12;
            }
            if (cashList is not null && plan.Id == "cash_salary")
            { paint.TextSize = 12; ReconciliationPdf.DrawRight(canvas, shaper, paint, "المرفقات: قائمة أسماء عدد (" + Eastern(cashList.Rows.Count.ToString(CultureInfo.InvariantCulture)) + ")", 558, y); y += 18; }
            if (y > 575 + settings.SignatureOffset) throw new InvalidDataException("متن الكتاب أو مرفقاته تتداخل مع اللجنة.");
            var signatureY = 625 + settings.SignatureOffset; paint.TextSize = 11;
            var signatures = new[] { ("معاون مدير تدقيق", settings.First, "المصروفات"), ("العميد", settings.Second, "مدير الحسابات"), ("لواء الشرطة الحقوقي", settings.Third, "قائد شرطة محافظة الأنبار") }; float[] centers = [482, 298, 113];
            for (var i = 0; i < 3; i++) { var name = string.Join(' ', signatures[i].Item2.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)); string[] values = [signatures[i].Item1, name, signatures[i].Item3, date]; for (var j = 0; j < values.Length; j++) ReconciliationPdf.Fit(canvas, shaper, paint, values[j], centers[i] - 82, centers[i] + 82, signatureY + j * 16); }
            var footer = Wrap(settings.Footer, shaper, paint, 520); if (footer.Count > 5) throw new InvalidDataException("الهامش السفلي لا يتسع.");
            for (var i = 0; i < footer.Count; i++) ReconciliationPdf.DrawRight(canvas, shaper, paint, footer[i], 558, 767 + i * 13);
            pdf.EndPage(); pdf.Close();
            var bytes = output.ToArray(); using var read = PdfDocument.Open(bytes); if (read.NumberOfPages != 1) throw new InvalidDataException("عدد صفحات الكتاب غير مطابق.");
            var page = read.GetPage(1); var text = string.Concat(page.Letters.Where(l => l.StartBaseLine.Y <= H - 300 && l.StartBaseLine.Y >= H - 500).Select(l => l.Value));
            var compact = TransferPreparation.NormalizeAccount(text).Replace(",", "", StringComparison.Ordinal).Replace("٬", "", StringComparison.Ordinal);
            if (!compact.Contains(plan.PayerAccount, StringComparison.Ordinal) || !compact.Contains(plan.BeneficiaryAccount, StringComparison.Ordinal) || !compact.Contains(plan.Total.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)) throw new InvalidDataException("تعذّر التحقق من حسابات الكتاب أو مبلغه.");
            foreach (var (top, expected) in tableBoxes)
            {
                var cell = string.Concat(page.Letters.Where(l => { var r = l.GlyphRectangle; var cx = (r.Left + r.Right) / 2; var cy = (r.Bottom + r.Top) / 2; return cx >= 265 && cx <= 360 && cy >= H - top - 19 && cy <= H - top; }).OrderBy(l => l.GlyphRectangle.Left).Select(l => l.Value));
                var digits = TransferPreparation.NormalizeAccount(cell).Replace(",", "", StringComparison.Ordinal).Replace("٬", "", StringComparison.Ordinal);
                if (!long.TryParse(digits, out var amount) || amount != expected) throw new InvalidDataException("جدول مبالغ الكتاب غير مطابق.");
            }
            return bytes;
        }
    }
    public static void Save(IndividualLetterPlan plan, GroupLetterSettings settings, byte[] font, byte[] tableFont, byte[] logo, string? foodContract, CashSalaryList? cash, string destination)
    {
        var target = Path.GetFullPath(destination); if (!string.Equals(Path.GetExtension(target), ".pdf", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("اختر ملف PDF صالحاً.");
        var bytes = Generate(plan, settings, font, tableFont, logo, foodContract, cash); var temp = Path.Combine(Path.GetDirectoryName(target)!, ".letter-" + Guid.NewGuid().ToString("N") + ".tmp");
        try { using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { file.Write(bytes); file.Flush(true); } using (var file = File.OpenRead(temp)) if (!SHA256.HashData(file).AsSpan().SequenceEqual(SHA256.HashData(bytes))) throw new InvalidDataException("تعذّر التحقق من الملف المحفوظ."); File.Move(temp, target, true); }
        finally { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
