using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using UglyToad.PdfPig;

namespace SalaryDesktop.Core;

/// كتاب تجريبي مستقل عن القوالب؛ يُطابق مصادر جدول CSV ويُظهر الجدول في صفحة واحدة.
public static class GroupLetterPdf
{
    private const float W = 595, H = 842;
    private static readonly float[] X = [30, 120, 235, 380, 535, 565];
    private static readonly string[] Months = ["كانون الثاني", "شباط", "آذار", "نيسان", "أيار", "حزيران", "تموز", "آب", "أيلول", "تشرين الأول", "تشرين الثاني", "كانون الأول"];
    public const string DefaultBodyTemplate = "نرجو تحويل مبلغ قدره ({{المبلغ}}) ({{المبلغ كتابة}}) لا غيرها من حسابنا المرقم ({{حسابنا}}) المفتوح لديكم إلى الحسابات المدرجة تفاصيلها في القائمة المرفقة مع قرص بصيغة (CSV) عن استقطاعات شهر ({{الشهر}} / {{السنة}}) وتزويدنا بالإشعار اللازم بذلك.";
    private static string Eastern(string value) => string.Concat(value.Select(c => c is >= '0' and <= '9' ? (char)('٠' + c - '0') : c));
    private static string Group(long value) => value.ToString("N0", CultureInfo.GetCultureInfo("en-US"));
    private static List<string> Wrap(string value, SKShaper shaper, SKPaint paint, float width) => value.Split('\n').SelectMany(line => ReconciliationPdf.Wrap(line, shaper, paint, width)).ToList();
    private static void Center(SKCanvas canvas, SKShaper shaper, SKPaint paint, string text, float x, float y) => ReconciliationPdf.DrawRight(canvas, shaper, paint, text, x + ReconciliationPdf.Width(text, shaper, paint) / 2, y);
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
        using var fontData = SKData.CreateCopy(fontBytes);
        using var font = SKTypeface.FromData(fontData) ?? throw new InvalidDataException("تعذّر فتح خط الكتاب العربي.");
        using var tableData = SKData.CreateCopy(tableFontBytes);
        using var tableFont = SKTypeface.FromData(tableData) ?? throw new InvalidDataException("تعذّر فتح خط جدول الكتاب.");
        using var tableShaper = new SKShaper(tableFont);
        using var logo = SKBitmap.Decode(logoBytes) ?? throw new InvalidDataException("تعذّر فتح شعار الكتاب.");
        using var shaper = new SKShaper(font); using var paint = new SKPaint { Typeface = font, TextSize = 13, IsAntialias = true, Color = SKColors.Black };
        var requiredText = string.Join(' ', plan.Lines.SelectMany(l => new[] { l.Title, l.Branch, l.Iban })) + string.Join(' ', required) + settings.Footer + plan.Words;
        if (!paint.ContainsGlyphs(requiredText.Replace("\n", "", StringComparison.Ordinal))) throw new InvalidDataException("الخط المختار لا يدعم بعض حروف الكتاب أو الجدول.");
        using (var tablePaint = new SKPaint { Typeface = tableFont })
            if (!tablePaint.ContainsGlyphs((string.Join(' ', plan.Lines.SelectMany(l => new[] { l.Title, l.Branch, l.Iban })) + "المبلغ الفرع رقم الآيبان الجهة ٠١٢٣٤٥٦٧٨٩").Replace("\n", "", StringComparison.Ordinal)))
                throw new InvalidDataException("خط الجدول لا يدعم بعض حروف الجهات أو الحسابات.");
        using var stroke = new SKPaint { Color = new SKColor(55, 55, 55), Style = SKPaintStyle.Stroke, StrokeWidth = .6f, IsAntialias = true };
        using var memory = new MemoryStream();
        using (var pdf = SKDocument.CreatePdf(memory) ?? throw new InvalidDataException("تعذّر إنشاء كتاب المجموعة."))
        {
            var canvas = pdf.BeginPage(W,H); canvas.Clear(SKColors.White);
            paint.TextSize = 9; paint.Color = new SKColor(150, 80, 20); ReconciliationPdf.DrawRight(canvas, shaper, paint, "مسودة للمراجعة", 558, 25); paint.Color = SKColors.Black;
            canvas.DrawBitmap(logo, new SKRect(267, 30, 338, 125));
            paint.TextSize = 13;
            var header = Wrap(settings.Header, shaper, paint, 185); if (header.Count is 0 or > 6) throw new InvalidDataException("الترويسة لا تتسع في موضعها.");
            for (int i = 0; i < header.Count; i++) Center(canvas, shaper, paint, header[i], 125, 39 + i * 17);
            var date = Eastern($" / {settings.OutgoingMonth ?? key % 100} / {key / 100}");
            ReconciliationPdf.Fit(canvas, shaper, paint, "العدد: " + Eastern(settings.Number), 28, 225, 165, true);
            ReconciliationPdf.Fit(canvas, shaper, paint, "التاريخ:              " + date, 28, 225, 188, true);
            paint.TextSize = 15; Center(canvas, shaper, paint, "إلى / مصرف الرافدين / فرع الأنبار", W / 2, 230);
            Center(canvas, shaper, paint, "م / تحويل مبلغ", W / 2, 255);
            paint.TextSize = 13; ReconciliationPdf.DrawRight(canvas, shaper, paint, "تحية طيبة ..", 558, 292);
            var body = Body(plan, settings, displayKey / 100, displayKey % 100);
            var lines = Wrap(body, shaper, paint, 520); float y = 320;
            foreach (var line in lines) { if (y > 490) throw new InvalidDataException("متن الكتاب لا يتسع قبل التواقيع."); ReconciliationPdf.DrawRight(canvas, shaper, paint, line, 558, y); y += 19; }
            y += 18; Center(canvas, shaper, paint, "للتفضل بالاطلاع وإجراء اللازم وإعلامنا... مع التقدير.", W / 2, y); y += 23;
            paint.TextSize = 11; foreach (var line in new[] { "المرفقات:", $"قائمة أسماء الحسابات من تسلسل (١) لغاية تسلسل ({Eastern(plan.Lines.Count.ToString(CultureInfo.InvariantCulture))})", "قرص (CSV)." })
            { if (y > 575 + settings.SignatureOffset) throw new InvalidDataException("المرفقات تتداخل مع موضع اللجنة؛ عدّل التخصيص."); ReconciliationPdf.DrawRight(canvas, shaper, paint, line, 558, y); y += 15; }
            var signatureY = 625 + settings.SignatureOffset; paint.TextSize = 11;
            var signatures = new[] { ("معاون مدير تدقيق", settings.First, "المصروفات"), ("العميد", settings.Second, "مدير الحسابات"), ("لواء الشرطة الحقوقي", settings.Third, "قائد شرطة محافظة الأنبار") };
            float[] centers = [482, 298, 113];
            for (int i = 0; i < 3; i++)
            {
                var name = string.Join(' ', signatures[i].Item2.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                string[] values = [signatures[i].Item1, name, signatures[i].Item3, date];
                for (int j = 0; j < values.Length; j++) ReconciliationPdf.Fit(canvas, shaper, paint, values[j], centers[i] - 82, centers[i] + 82, signatureY + j * 16);
            }
            var footer = Wrap(settings.Footer, shaper, paint, 520); if (footer.Count > 5) throw new InvalidDataException("الهامش السفلي لا يتسع.");
            for (int i = 0; i < footer.Count; i++) ReconciliationPdf.DrawRight(canvas, shaper, paint, footer[i], 558, 767 + i * 13);
            pdf.EndPage();
            canvas = pdf.BeginPage(W,H); canvas.Clear(SKColors.White); paint.Typeface = tableFont;
            paint.TextSize = 13; Center(canvas, tableShaper, paint, $"استقطاعات قيادة شرطة الأنبار لشهر ({Months[key % 100 - 1]} / {Eastern((key / 100).ToString(CultureInfo.InvariantCulture))})", W / 2, 55);
            var cells = plan.Lines.Select((line,index) => new[] { Eastern(Group(line.Amount)), line.Branch, line.Iban, line.Title, Eastern((index + 1).ToString(CultureInfo.InvariantCulture)) }).ToArray();
            List<string>[][] wrapped = []; float[] heights = []; bool fits = false;
            for (float size = 10.5f; size >= 6.5f; size -= .5f)
            {
                paint.TextSize = size;
                wrapped = cells.Select(row => row.Select((cell, i) => i is 0 or 2 or 4 ? new List<string> { cell } : ArabicPresentation.Wrap(cell, paint, X[i + 1] - X[i] - 6)).ToArray()).ToArray();
                heights = wrapped.Select(row => Math.Max(11, row.Max(lines2 => lines2.Count) * (size + 2) + 4)).ToArray();
                if (heights.Sum() <= 710 - 22 - 22) { fits = true; break; }
            }
            if (!fits) throw new InvalidDataException("جدول المجموعة لا يتسع في صفحة واحدة بخط مقروء.");
            var headings = new[] { "المبلغ", "الفرع", "رقم الآيبان", "الجهة", "ت" }; float top = 80; const float headerHeight = 22;
            for (int i = 0; i < 5; i++) { canvas.DrawRect(X[i], top, X[i+1]-X[i], headerHeight, stroke); ArabicPresentation.Fit(canvas, paint, headings[i], X[i], X[i+1], top + 15); }
            top += headerHeight;
            var boxes = new List<(float Y, float Height)>();
            for (int row = 0; row < cells.Length; row++)
            {
                var h = heights[row]; boxes.Add((top,h));
                for (int col = 0; col < 5; col++)
                {
                    canvas.DrawRect(X[col], top, X[col+1]-X[col], h, stroke);
                    for (int j = 0; j < wrapped[row][col].Count; j++) ArabicPresentation.Fit(canvas, paint, wrapped[row][col][j], X[col], X[col+1], top + paint.TextSize + 2 + j * (paint.TextSize + 2), col is 1 or 3);
                }
                top += h;
            }
            for (int i = 0; i < 5; i++) canvas.DrawRect(X[i], top, X[i+1]-X[i], 22, stroke);
            ArabicPresentation.Fit(canvas, paint, Eastern(Group(plan.Total)), X[0], X[1], top + 15);
            ArabicPresentation.Fit(canvas, paint, "المجموع", X[3], X[4], top + 15);
            pdf.EndPage(); pdf.Close();
            var bytes = memory.ToArray();
            using var read = PdfDocument.Open(bytes);
            if (read.NumberOfPages != 2) throw new InvalidDataException("تعذّر التحقق من عدد صفحات الكتاب.");
            var firstPage = read.GetPage(1);
            var bodyRead = string.Concat(firstPage.Letters.Where(l => l.StartBaseLine.Y <= H - 280 && l.StartBaseLine.Y >= H - 510).Select(l => l.Value));
            var compactBody = TransferPreparation.NormalizeAccount(bodyRead).Replace(",", "", StringComparison.Ordinal).Replace("٬", "", StringComparison.Ordinal);
            if (!compactBody.Contains(plan.PayerAccount, StringComparison.Ordinal) || !compactBody.Contains(plan.Total.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                throw new InvalidDataException("تعذّر التحقق من المبلغ أو حساب الجهة الدافعة في متن الكتاب.");
            var page = read.GetPage(2);
            static string Identity(string value) => new string(PayrollAnalysis.Normalize(value.Normalize(NormalizationForm.FormKC).Replace('ی', 'ي')).Where(char.IsLetterOrDigit).Order().ToArray());
            for (int row = 0; row < plan.Lines.Count; row++)
            {
                var (rowTop,rowHeight) = boxes[row];
                string CellText(int col) => string.Concat(page.Letters.Where(l => { var p = l.GlyphRectangle; var cx = (p.Left+p.Right)/2; var cy = (p.Bottom+p.Top)/2; return cx >= X[col] && cx <= X[col+1] && cy >= H-rowTop-rowHeight && cy <= H-rowTop; }).OrderBy(l => l.GlyphRectangle.Left).Select(l => l.Value));
                var iban = new string(CellText(2).Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant();
                var amountText = TransferPreparation.NormalizeAccount(CellText(0)).Replace(",", "", StringComparison.Ordinal).Replace("٬", "", StringComparison.Ordinal);
                if (iban != plan.Lines[row].Iban) throw new InvalidDataException($"تعذّر التحقق من رقم حساب الصف {row + 1} في جدول الكتاب.");
                if (!long.TryParse(amountText, out var amount) || amount != plan.Lines[row].Amount) throw new InvalidDataException($"تعذّر التحقق من مبلغ الصف {row + 1} في جدول الكتاب.");
                if (Identity(CellText(1)) != Identity(plan.Lines[row].Branch)) throw new InvalidDataException($"تعذّر التحقق من فرع الصف {row + 1} في جدول الكتاب.");
                if (Identity(CellText(3)) != Identity(plan.Lines[row].Title)) throw new InvalidDataException($"تعذّر التحقق من اسم الصف {row + 1} في جدول الكتاب." + (Environment.GetEnvironmentVariable("SALARY_TEST_DEBUG_FAKE") == "1" ? " ACTUAL=" + Identity(CellText(3)) + " EXPECTED=" + Identity(plan.Lines[row].Title) : ""));
            }
            var totalRead = string.Concat(page.Letters.Where(l => { var p = l.GlyphRectangle; var cx = (p.Left + p.Right) / 2; var cy = (p.Bottom + p.Top) / 2; return cx >= X[0] && cx <= X[1] && cy >= H - top - 22 && cy <= H - top; }).OrderBy(l => l.GlyphRectangle.Left).Select(l => l.Value));
            var totalNumber = TransferPreparation.NormalizeAccount(totalRead).Replace(",", "", StringComparison.Ordinal).Replace("٬", "", StringComparison.Ordinal);
            if (!long.TryParse(totalNumber, out var readTotal) || readTotal != plan.Total) throw new InvalidDataException("مجموع جدول الكتاب غير مطابق.");
            return bytes;
        }
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
