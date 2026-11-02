using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace SalaryDesktop.Core;

/// كشف مطابقة حساب البنك بالشكل المعتمد، وأدوات رسم النص العربي المشتركة مع بقية الكشوف (ReportCanvas).
public static class ReconciliationPdf
{
    private static string[] Runs(string text)
    {
        if (!text.Any(c => c is >= '\u0600' and <= '\u08ff')) return [text];
        return Regex.Split(text, @"([A-Za-z0-9٠-٩۰-۹]+(?:[ .,/:\u066B\u066C-]+[A-Za-z0-9٠-٩۰-۹]+)*)").Where(part => part.Length > 0).ToArray();
    }
    private static string ShapeRun(string run) => run.Any(c => c is >= '٠' and <= '٩' or >= '۰' and <= '۹') && !run.Any(char.IsAsciiLetter)
        ? string.Concat(run.EnumerateRunes().Reverse()) : run;
    internal static float Width(string text, SKShaper shaper, SKPaint paint) => Runs(text).Sum(run => shaper.Shape(ShapeRun(run), paint).Width);
    internal static void DrawRight(SKCanvas canvas, SKShaper shaper, SKPaint paint, string text, float right, float y)
    {
        foreach (var run in Runs(text)) { var value = ShapeRun(run); var width = shaper.Shape(value, paint).Width; canvas.DrawShapedText(shaper, value, right - width, y, paint); right -= width; }
    }
    internal static List<string> Wrap(string value, SKShaper shaper, SKPaint paint, float width)
    {
        var result = new List<string>(); var line = "";
        foreach (var word in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var proposed = line.Length == 0 ? word : line + " " + word;
            if (Width(proposed, shaper, paint) <= width) { line = proposed; continue; }
            if (line.Length > 0) { result.Add(line); line = ""; }
            var piece = "";
            foreach (var rune in word.EnumerateRunes())
            {
                var next = piece + rune;
                if (piece.Length > 0 && Width(next, shaper, paint) > width) { result.Add(piece); piece = ""; }
                piece += rune;
            }
            line = piece;
        }
        if (line.Length > 0) result.Add(line);
        return result.Count == 0 ? [""] : result;
    }
    internal static void Fit(SKCanvas canvas, SKShaper shaper, SKPaint paint, string text, float left, float right, float baseline, bool rightAlign = false)
    {
        var size = paint.TextSize;
        while (Width(text, shaper, paint) > right - left - 6 && paint.TextSize > 7) paint.TextSize -= 0.5f;
        var width = Width(text, shaper, paint);
        if (width > right - left - 6) throw new InvalidDataException("أحد الحقول أطول من المساحة المتاحة في الكشف.");
        DrawRight(canvas, shaper, paint, text, rightAlign ? right - 3 : left + (right - left + width) / 2, baseline);
        paint.TextSize = size;
    }
    /// توافق مع الاستدعاءات القديمة والاختبارات: كشف مسودة بخط واحد ودون بيانات حساب.
    public static byte[] Generate(BankPeriod period, string fontPath)
    {
        var font = File.ReadAllBytes(fontPath);
        return Statement(period, new BankAccountDetails("", "", "", ""), false, new ReportAssets(font, font, null));
    }

    private static readonly string[] Organization = ["جمهورية العراق", "وزارة الداخلية", "وكالة الوزارة لشؤون الشرطة", "قيادة شرطة محافظة الأنبار", "قسم الحسابات"];
    private static readonly float[] Widths = [78, 189, 88, 88, 88];   // من اليمين: التاريخ، البيان، دائن، مدين، الرصيد
    private const float Margin = 32, TotalHeight = 26, HeaderHeight = 26, SignatureWidth = 245;
    private static string Amount(long value) => (value < 0 ? "−" : "") + Math.Abs(value).ToString("N0", CultureInfo.GetCultureInfo("en-US"));
    private static string EasternDate(DateOnly date) => ReportSheet.Eastern(date.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture));

    /// كشف مطابقة حساب البنك: ترويسة الوزارة يميناً، العنوان والفترة في الوسط، وبيانات الحساب أسفلهما على سطرين في وسط الصفحة
    /// (تعديل المالك 2026-11-02)، ثم جدول التاريخ/البيان/دائن/مدين/الرصيد مع الرصيد الافتتاحي والمجموع، وتوقيع عضوي اللجنة (عريض 16) في آخر صفحة.
    /// complete = false يضيف «مسودة · المطابقة غير مكتملة».
    public static byte[] Statement(BankPeriod period, BankAccountDetails account, bool complete, ReportAssets assets)
    {
        var totals = BankReconciliation.Calculate(period);
        if (totals.Lines.Count == 0) throw new InvalidDataException("لا توجد حركات لعرضها في الكشف.");
        using var regularData = SKData.CreateCopy(assets.Regular); using var boldData = SKData.CreateCopy(assets.Bold);
        using var regular = SKTypeface.FromData(regularData) ?? throw new InvalidDataException("تعذّر فتح خط Arial.");
        using var boldFace = SKTypeface.FromData(boldData) ?? throw new InvalidDataException("تعذّر فتح خط Arial العريض.");
        using var regularShaper = new SKShaper(regular); using var boldShaper = new SKShaper(boldFace);
        using var watermark = assets.Watermark is { } logo ? SKBitmap.Decode(logo) : null;
        SKPaint Font(float size, bool isBold) => new() { Typeface = isBold ? boldFace : regular, TextSize = size, IsAntialias = true, Color = SKColors.Black };
        SKShaper ShaperOf(SKPaint paint) => paint.Typeface == boldFace ? boldShaper : regularShaper;
        float W(string text, SKPaint paint) => Width(text, ShaperOf(paint), paint);
        List<string> Lines(string text, SKPaint paint, float width) => Wrap(text, ShaperOf(paint), paint, width);
        static string Value(string raw)
        {
            var trimmed = raw.Trim();
            if (trimmed.Length > 100 || trimmed.Any(char.IsControl)) throw new InvalidDataException("أحد بيانات الحساب طويل جداً للكشف.");
            return trimmed.Length == 0 ? "—" : trimmed;
        }

        // الأحجام (طلب المالك 2026-11-02): Arial لكل الكشف؛ الترويسة 11 والعنوان 14، رؤوس الأعمدة والمبالغ والمجموع عريض 11،
        // البيان 10.5، والصف لا يقل عن 24 مع تظليل متناوب؛ تواقيع اللجنة عريض 16 بلا إطار.
        using var heading = Font(14, true); using var organization = Font(11, true);
        using var labelFont = Font(10.5f, true); using var valueFont = Font(10.5f, false);
        using var periodFont = Font(11, true); using var bold = Font(11, true); using var signatureFont = Font(16, true);
        using var amountFont = Font(11, true);
        const string title = "كشف مطابقة حساب البنك";
        var periodText = $"الــفــتــرة مــن {EasternDate(period.Start)} إلــى {EasternDate(period.End)}";
        if (W(title, heading) > 250 || W(periodText, periodFont) > 520 || Organization.Any(o => W(o, organization) > 160)) throw new InvalidDataException("ترويسة الكشف لا تتسع؛ راجع التواريخ.");
        // بيانات الحساب تحت العنوان مباشرة يسار ترويسة الوزارة (تعديل المالك): (الاسم، الفرع) ثم (العملة، IBAN)؛ الحساب المحلي لا يظهر في الكشف؛
        // السطر الأطول من المساحة يُقسم إلى سطر آخر ولا يُقص.
        const float accountLeft = Margin, accountRight = 392, accountWidth = accountRight - accountLeft, accountGap = 18, accountLine = 17;
        float SegmentWidth((string Label, string Value) s) => W(s.Label + ":", labelFont) + 5 + W(s.Value, valueFont);
        var accountLines = new List<List<(string Label, string Value)>>();
        foreach (var group in new[] { new[] { ("اسم الحساب", Value(account.Name)), ("الفرع", Value(account.Branch)) }, new[] { ("العملة", "IQD"), ("IBAN", Value(account.Iban)) } })
        {
            var line = new List<(string Label, string Value)>(); var width = 0f;
            foreach (var segment in group)
            {
                var size = SegmentWidth(segment);
                if (size > accountWidth) throw new InvalidDataException("أحد بيانات الحساب طويل جداً للكشف.");
                if (line.Count > 0 && width + accountGap + size > accountWidth) { accountLines.Add(line); line = []; width = 0; }
                width += (line.Count > 0 ? accountGap : 0) + size; line.Add(segment);
            }
            accountLines.Add(line);
        }
        if (accountLines.Count > 4) throw new InvalidDataException("بيانات الحساب طويلة جداً للكشف.");
        const float organizationLine = 16, periodHeight = 22;
        var accountTop = 56f;
        var contentBottom = Math.Max(25 + Organization.Length * organizationLine, accountTop + accountLines.Count * accountLine);
        // الفترة شريط بعرض الجدول فوقه مباشرة، و«مسودة» فوقها عند عدم الاكتمال.
        var periodTop = contentBottom + (complete ? 10 : 26);
        var tableTop = periodTop + periodHeight; var rowTop = tableTop + HeaderHeight;

        // تواقيع اللجنة: عريض 16 في عمودين أسفل آخر صفحة، والسطر الطويل (المنصب) ينزل إلى سطر ثانٍ.
        var signatures = new[] { BankReconciliation.SignatureLines(period, true), BankReconciliation.SignatureLines(period, false) };
        var signatureLayout = signatures.Select(lines => lines.Select(text =>
        {
            if (text.Any(char.IsControl)) throw new InvalidDataException("بيانات التوقيع غير صالحة.");
            var wrapped = Lines(text, signatureFont, SignatureWidth - 8);
            if (wrapped.Count > 2) throw new InvalidDataException("أحد أسطر التوقيع طويل جداً؛ اختصره من بيانات اللجنة.");
            return (Lines: wrapped, Height: Math.Max(24, MathF.Ceiling(wrapped.Count * signatureFont.FontSpacing + 4)));
        }).ToArray()).ToArray();
        var signatureHeight = signatureLayout.Max(column => column.Sum(l => l.Height));
        if (signatureHeight > 170) throw new InvalidDataException("بيانات التوقيع طويلة جداً للكشف.");
        var signatureTop = 812 - signatureHeight;
        var tableBottom = signatureTop - 16;
        if (rowTop + TotalHeight >= tableBottom) throw new InvalidDataException("رأس الكشف طويل جداً.");

        var values = new List<string[]>();
        if (period.OpeningBalance != 0) values.Add([EasternDate(period.Start), "الرصيد الافتتاحي", "—", "—", Amount(period.OpeningBalance)]);
        foreach (var line in totals.Lines)
        {
            var m = line.Movement;
            values.Add([EasternDate(m.Date), m.Description, m.Direction == MovementDirection.Credit ? Amount(m.Amount) : "—", m.Direction == MovementDirection.Debit ? Amount(m.Amount) : "—", Amount(line.Balance)]);
        }
        string[] totalRow = ["", "المجموع", Amount(totals.Credits), Amount(totals.Debits), Amount(totals.Balance)];
        // البيان ينزل إلى سطر آخر بدل تصغير الخط، والكشف يمتد على صفحات.
        using var rowFont = Font(10.5f, false);
        var rows = new List<(string[] Values, List<string> Description, float Height)>();
        foreach (var row in values)
        {
            var description = Lines(row[1], rowFont, Widths[1] - 8);
            if (description.Count > 5 || W(row[0], rowFont) > Widths[0] - 8) throw new InvalidDataException("أحد بيانات الحركة لا يتسع بوضوح؛ اختصر البيان ثم حاول مجدداً.");
            for (int c = 2; c < 5; c++) if (W(row[c], amountFont) > Widths[c] - 8) throw new InvalidDataException("أحد المبالغ لا يتسع في الجدول.");
            var height = Math.Max(24, MathF.Ceiling(description.Count * rowFont.FontSpacing + 6));
            if (height > tableBottom - rowTop - TotalHeight) throw new InvalidDataException("أحد بيانات الحركة لا يتسع بوضوح؛ اختصر البيان ثم حاول مجدداً.");
            rows.Add((row, description, height));
        }
        for (int c = 0; c < 5; c++) if (W(totalRow[c], bold) > Widths[c] - 6) throw new InvalidDataException("مجموع الكشف لا يتسع في الجدول.");

        var gray = new SKColor(237, 237, 237); var dark = new SKColor(85, 85, 85);
        using var cellStroke = new SKPaint { Color = dark, Style = SKPaintStyle.Stroke, StrokeWidth = 0.5f, IsAntialias = true };
        using var border = new SKPaint { Color = new SKColor(115, 115, 115), Style = SKPaintStyle.Stroke, StrokeWidth = 0.6f, IsAntialias = true };
        using var fill = new SKPaint { Color = gray, Style = SKPaintStyle.Fill };
        using var shade = new SKPaint { Color = new SKColor(0xF3, 0xF7, 0xFB), Style = SKPaintStyle.Fill };
        using var output = new MemoryStream();
        using (var document = PdfOutput.Create(output))
        {
            SKCanvas canvas = null!;
            // نص (سطر أو أكثر) في مستطيل، في المنتصف عمودياً؛ المحاذاة يمين أو وسط أو يسار.
            void Draw(IReadOnlyList<string> lines, SKPaint paint, SKRect rect, int align)
            {
                var shaper = ShaperOf(paint); var spacing = paint.FontSpacing; var ascent = -paint.FontMetrics.Ascent;
                var top = rect.MidY - lines.Count * spacing / 2;
                for (int i = 0; i < lines.Count; i++)
                {
                    var width = Width(lines[i], shaper, paint);
                    var right = align switch { 0 => rect.Right, 2 => rect.Left + width, _ => rect.MidX + width / 2 };
                    DrawRight(canvas, shaper, paint, lines[i], right, top + i * spacing + ascent - 1);
                }
            }
            float[] lefts = new float[5]; { var x = 595f - Margin; for (int c = 0; c < 5; c++) { x -= Widths[c]; lefts[c] = x; } }
            int index = 0, pages = 0; var finished = false;
            do
            {
                if (++pages > 2000) throw new InvalidDataException("الكشف يتجاوز عدد الصفحات المدعوم.");
                canvas = document.BeginPage(595, 842); canvas.Clear(SKColors.White);
                if (watermark is not null) { using var faint = new SKPaint { Color = SKColors.White.WithAlpha(15), FilterQuality = SKFilterQuality.High, IsAntialias = true }; canvas.DrawBitmap(watermark, SKRect.Create(120, 280, 355, 355), faint); }
                canvas.DrawRect(SKRect.Create(21, 18, 553, 804), border);
                for (int i = 0; i < Organization.Length; i++) Draw([Organization[i]], organization, SKRect.Create(400, 25 + i * organizationLine, 162, organizationLine), 1);
                Draw([title], heading, SKRect.Create(172.5f, 27, 250, 24), 1);
                for (int l = 0; l < accountLines.Count; l++)
                {
                    var segments = accountLines[l];
                    var total = segments.Sum(SegmentWidth) + accountGap * (segments.Count - 1);
                    var right = (accountLeft + accountRight) / 2 + total / 2;
                    var baseline = accountTop + l * accountLine + (accountLine - labelFont.FontSpacing) / 2 - labelFont.FontMetrics.Ascent - 1;
                    foreach (var segment in segments)
                    {
                        var label = segment.Label + ":";
                        // التسمية اللاتينية (IBAN) تُرسم ثم النقطتان على يسارها، كما في اتجاه السطر العربي.
                        if (segment.Label.All(char.IsAscii)) { DrawRight(canvas, boldShaper, labelFont, segment.Label, right, baseline); DrawRight(canvas, boldShaper, labelFont, ":", right - W(segment.Label, labelFont), baseline); }
                        else DrawRight(canvas, boldShaper, labelFont, label, right, baseline);
                        right -= W(label, labelFont) + 5;
                        DrawRight(canvas, regularShaper, valueFont, segment.Value, right, baseline); right -= W(segment.Value, valueFont) + accountGap;
                    }
                }
                if (!complete) { using var draft = Font(9, true); draft.Color = dark; Draw(["مسودة · المطابقة غير مكتملة"], draft, SKRect.Create(Margin, periodTop - 16, 531, 14), 1); }
                var periodBar = SKRect.Create(lefts[4], periodTop, 595f - Margin - lefts[4], periodHeight);
                canvas.DrawRect(periodBar, shade); canvas.DrawRect(periodBar, cellStroke);
                Draw([periodText], periodFont, periodBar, 1);
                string[] labels = ["التاريخ", "البيان", "دائن", "مدين", "الرصيد"];
                for (int c = 0; c < 5; c++)
                {
                    var cell = SKRect.Create(lefts[c], tableTop, Widths[c], HeaderHeight);
                    canvas.DrawRect(cell, fill); canvas.DrawRect(cell, cellStroke);
                    Draw([labels[c]], bold, cell, 1);
                }
                var y = rowTop;
                while (index < rows.Count && y + rows[index].Height <= tableBottom)
                {
                    var (row, description, height) = rows[index];
                    if (index % 2 == 1) canvas.DrawRect(SKRect.Create(lefts[4], y, 595f - Margin - lefts[4], height), shade);
                    for (int c = 0; c < 5; c++)
                    {
                        var cell = SKRect.Create(lefts[c], y, Widths[c], height);
                        canvas.DrawRect(cell, cellStroke);
                        var inner = SKRect.Create(cell.Left + 4, cell.Top + 2, cell.Width - 8, cell.Height - 4);
                        if (c == 1) Draw(description, rowFont, inner, 0); else Draw([row[c]], c == 0 ? rowFont : amountFont, inner, 1);
                    }
                    y += height; index++;
                }
                if (index == rows.Count && y + TotalHeight <= tableBottom)
                {
                    for (int c = 0; c < 5; c++)
                    {
                        var cell = SKRect.Create(lefts[c], y, Widths[c], TotalHeight);
                        canvas.DrawRect(cell, fill); canvas.DrawRect(cell, cellStroke);
                        Draw([totalRow[c]], bold, cell, 1);
                    }
                    for (int column = 0; column < 2; column++)
                    {
                        float x = column == 0 ? 306 : 44, signatureY = signatureTop;
                        foreach (var (lines, height) in signatureLayout[column])
                        {
                            Draw(lines, signatureFont, SKRect.Create(x, signatureY, SignatureWidth, height), 1);
                            signatureY += height;
                        }
                    }
                    finished = true;
                }
                document.EndPage();
            } while (!finished);
            document.Close();
        }
        var bytes = output.ToArray();
        if (bytes.Length < 8 || !bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8)) throw new InvalidDataException("تعذّر التحقق من ملف الكشف.");
        return bytes;
    }
    /// حفظ ملف الكشف عبر ملف مؤقت ثم نقله، فلا يُستبدل ملف موجود بملف ناقص.
    public static void Save(byte[] bytes, string destination)
    {
        var target = Path.GetFullPath(destination);
        if (!string.Equals(Path.GetExtension(target), ".pdf", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("اختر ملفاً بالامتداد PDF.");
        var directory = Path.GetDirectoryName(target) ?? throw new InvalidDataException("مجلد الحفظ غير صالح.");
        var temp = Path.Combine(directory, ".salary-reconciliation-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { file.Write(bytes); file.Flush(true); }
            using (var file = File.OpenRead(temp)) if (!SHA256.HashData(file).AsSpan().SequenceEqual(SHA256.HashData(bytes))) throw new InvalidDataException("تعذّر التحقق من الكشف المحفوظ.");
            File.Move(temp, target, true);
        }
        finally { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    public static void SaveDraft(BankPeriod period, string fontPath, string destination)
    {
        if (!string.Equals(Path.GetExtension(Path.GetFullPath(destination)), ".pdf", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("اختر ملفاً بالامتداد PDF.");
        Save(Generate(period, fontPath), destination);
    }
}
