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
    private const float Margin = 32, TableBottom = 665, TotalHeight = 24;
    private static string Amount(long value) => (value < 0 ? "−" : "") + Math.Abs(value).ToString("N0", CultureInfo.GetCultureInfo("en-US"));
    private static string EasternDate(DateOnly date) => ReportSheet.Eastern(date.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture));

    /// كشف مطابقة حساب البنك بالشكل المعتمد في الآيفون (BankReconciliationPDF.swift): ترويسة الوزارة يميناً، العنوان والفترة في الوسط،
    /// بيانات الحساب يساراً، جدول التاريخ/البيان/دائن/مدين/الرصيد مع الرصيد الافتتاحي والمجموع، وتوقيع عضوي اللجنة في آخر صفحة.
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

        using var heading = Font(12, true); using var organization = Font(10, true); using var metadataFont = Font(10, false);
        using var periodFont = Font(8, true); using var bold = Font(9, true); using var signatureFont = Font(12, true);
        const string title = "كشف مطابقة حساب البنك";
        var periodText = $"الــفــتــرة مــن {EasternDate(period.Start)} إلــى {EasternDate(period.End)}";
        var metadata = new (string Label, string Value)[] { ("اسم الحساب", Value(account.Name)), ("الفرع", Value(account.Branch)), ("الحساب", Value(account.LocalAccount)), ("العملة", "IQD"), ("IBAN", Value(account.Iban)) };
        if (W(title, heading) > 223 || W(periodText, periodFont) > 223 || Organization.Any(o => W(o, organization) > 150)) throw new InvalidDataException("ترويسة الكشف لا تتسع؛ راجع التواريخ.");
        var metadataRight = Math.Min(306f, 595f / 2 - Math.Max(W(title, heading), W(periodText, periodFont)) / 2 - 8);
        var metadataRows = new List<(string Label, List<string> Lines, float LabelWidth, float ValueWidth, float Height)>();
        foreach (var (label, value) in metadata)
        {
            var labelWidth = MathF.Ceiling(W(label + ":", metadataFont) + 2);
            var valueWidth = metadataRight - Margin - labelWidth - 6;
            if (valueWidth < 50) throw new InvalidDataException("بيانات الحساب لا تتسع في رأس الكشف.");
            var lines = Lines(value, metadataFont, valueWidth);
            if (lines.Count > 2) throw new InvalidDataException("أحد بيانات الحساب طويل جداً للكشف.");
            metadataRows.Add((label, lines, labelWidth, valueWidth, Math.Max(19, MathF.Ceiling(lines.Count * metadataFont.FontSpacing + 4))));
        }
        var contentBottom = Math.Max(25 + metadataRows.Sum(r => r.Height), Math.Max(25 + 75f, 46 + 16f));
        var tableTop = contentBottom + 18; var rowTop = tableTop + 24;
        if (rowTop + TotalHeight >= TableBottom) throw new InvalidDataException("رأس الكشف طويل جداً.");

        var values = new List<string[]>();
        if (period.OpeningBalance != 0) values.Add([EasternDate(period.Start), "الرصيد الافتتاحي", "—", "—", Amount(period.OpeningBalance)]);
        foreach (var line in totals.Lines)
        {
            var m = line.Movement;
            values.Add([EasternDate(m.Date), m.Description, m.Direction == MovementDirection.Credit ? Amount(m.Amount) : "—", m.Direction == MovementDirection.Debit ? Amount(m.Amount) : "—", Amount(line.Balance)]);
        }
        string[] totalRow = ["", "المجموع", Amount(totals.Credits), Amount(totals.Debits), Amount(totals.Balance)];
        // أكبر خط يتيح صفحة واحدة، وإلا يبقى الحجم الواضح ويُقسَّم الكشف على صفحات.
        (float Size, List<(string[] Values, List<string> Description, float Height)> Rows)? Layout(float size, float minimum)
        {
            using var paint = Font(size, false);
            var rows = new List<(string[], List<string>, float)>();
            foreach (var row in values)
            {
                var description = Lines(row[1], paint, Widths[1] - 8);
                if (description.Count > 5) return null;
                for (int c = 0; c < 5; c++) if (c != 1 && W(row[c], paint) > Widths[c] - 8) return null;
                var height = Math.Max(minimum, MathF.Ceiling(description.Count * paint.FontSpacing + 4));
                if (height > TableBottom - rowTop - TotalHeight) return null;
                rows.Add((row, description, height));
            }
            return (size, rows);
        }
        (float Size, List<(string[] Values, List<string> Description, float Height)> Rows)? first = null, single = null;
        foreach (var (size, minimum) in new[] { (9f, 18f), (8.5f, 17f), (8f, 16f) })
        {
            if (Layout(size, minimum) is not { } candidate) continue;
            first ??= candidate;
            if (candidate.Rows.Sum(r => r.Height) + TotalHeight <= TableBottom - rowTop) { single = candidate; break; }
        }
        var chosen = single ?? first ?? throw new InvalidDataException("أحد بيانات الحركة لا يتسع بوضوح؛ اختصر البيان ثم حاول مجدداً.");
        for (int c = 0; c < 5; c++) if (W(totalRow[c], bold) > Widths[c] - 6) throw new InvalidDataException("مجموع الكشف لا يتسع في الجدول.");
        var signatures = new[] { BankReconciliation.SignatureLines(period, true), BankReconciliation.SignatureLines(period, false) };
        var signatureLayout = signatures.Select(lines => lines.Select(text =>
        {
            if (text.Any(char.IsControl)) throw new InvalidDataException("بيانات التوقيع غير صالحة.");
            var wrapped = Lines(text, signatureFont, 237);
            if (wrapped.Count > 2) throw new InvalidDataException("أحد أسطر التوقيع طويل جداً؛ اختصره من بيانات اللجنة.");
            return (Lines: wrapped, Height: Math.Max(22, MathF.Ceiling(wrapped.Count * signatureFont.FontSpacing + 3)));
        }).ToArray()).ToArray();
        if (signatureLayout.Any(column => column.Sum(l => l.Height) > 126)) throw new InvalidDataException("بيانات التوقيع طويلة جداً للكشف.");

        var gray = new SKColor(237, 237, 237); var dark = new SKColor(85, 85, 85);
        using var cellStroke = new SKPaint { Color = dark, Style = SKPaintStyle.Stroke, StrokeWidth = 0.5f, IsAntialias = true };
        using var border = new SKPaint { Color = new SKColor(115, 115, 115), Style = SKPaintStyle.Stroke, StrokeWidth = 0.6f, IsAntialias = true };
        using var fill = new SKPaint { Color = gray, Style = SKPaintStyle.Fill };
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
            using var rowFont = Font(chosen.Size, false);
            int index = 0, pages = 0; var finished = false;
            do
            {
                if (++pages > 2000) throw new InvalidDataException("الكشف يتجاوز عدد الصفحات المدعوم.");
                canvas = document.BeginPage(595, 842); canvas.Clear(SKColors.White);
                if (watermark is not null) { using var faint = new SKPaint { Color = SKColors.White.WithAlpha(15), FilterQuality = SKFilterQuality.High, IsAntialias = true }; canvas.DrawBitmap(watermark, SKRect.Create(120, 280, 355, 355), faint); }
                canvas.DrawRect(SKRect.Create(21, 18, 553, 804), border);
                var y = 25f;
                foreach (var row in metadataRows)
                {
                    Draw([row.Label + ":"], metadataFont, SKRect.Create(metadataRight - row.LabelWidth, y, row.LabelWidth, row.Height), 0);
                    Draw(row.Lines, metadataFont, SKRect.Create(Margin, y, row.ValueWidth, row.Height), 2);
                    y += row.Height;
                }
                for (int i = 0; i < Organization.Length; i++) Draw([Organization[i]], organization, SKRect.Create(412, 25 + i * 15, 150, 15), 1);
                Draw([title], heading, SKRect.Create(167.5f, 25, 260, 20), 1);
                Draw([periodText], periodFont, SKRect.Create(167.5f, 46, 260, 16), 1);
                if (!complete) { using var draft = Font(9, true); draft.Color = dark; Draw(["مسودة · المطابقة غير مكتملة"], draft, SKRect.Create(Margin, contentBottom + 2, 531, 14), 1); }
                string[] labels = ["التاريخ", "البيان", "دائن", "مدين", "الرصيد"];
                for (int c = 0; c < 5; c++)
                {
                    var cell = SKRect.Create(lefts[c], tableTop, Widths[c], 24);
                    canvas.DrawRect(cell, fill); canvas.DrawRect(cell, cellStroke);
                    Draw([labels[c]], bold, cell, 1);
                }
                y = rowTop;
                while (index < chosen.Rows.Count && y + chosen.Rows[index].Height <= TableBottom)
                {
                    var (row, description, height) = chosen.Rows[index];
                    for (int c = 0; c < 5; c++)
                    {
                        var cell = SKRect.Create(lefts[c], y, Widths[c], height);
                        canvas.DrawRect(cell, cellStroke);
                        var inner = SKRect.Create(cell.Left + 4, cell.Top + 2, cell.Width - 8, cell.Height - 4);
                        if (c == 1) Draw(description, rowFont, inner, 0); else Draw([row[c]], rowFont, inner, 1);
                    }
                    y += height; index++;
                }
                if (index == chosen.Rows.Count && y + TotalHeight <= TableBottom)
                {
                    for (int c = 0; c < 5; c++)
                    {
                        var cell = SKRect.Create(lefts[c], y, Widths[c], TotalHeight);
                        canvas.DrawRect(cell, fill); canvas.DrawRect(cell, cellStroke);
                        Draw([totalRow[c]], bold, cell, 1);
                    }
                    for (int column = 0; column < 2; column++)
                    {
                        float x = column == 0 ? 314 : 36, signatureY = 690;
                        foreach (var (lines, height) in signatureLayout[column])
                        {
                            Draw(lines, signatureFont, SKRect.Create(x + 4, signatureY, 237, height), 1);
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
