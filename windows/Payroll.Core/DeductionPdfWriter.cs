using System.Globalization;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using Unicode.Bidi;

namespace Payroll.Core;

/// <summary>Vector A4 PDF for an exact single-entity deduction report.</summary>
public static class DeductionPdfWriter
{
    private const float PageWidth = 595, PageHeight = 842, Margin = 28, Bottom = 796;
    private static readonly SKColor Dark = new(31, 78, 121), Light = new(221, 235, 247), Border = new(155, 183, 212), Zebra = new(243, 247, 251);
    private sealed record LayoutRow(string[][] Lines, float Height);
    private readonly record struct VisualRun(string Value, bool RightToLeft);

    public static void Write(DeductionReportData report, string header, string regularFontPath, string boldFontPath, Stream output)
    {
        if (report.Rows.Count == 0) throw new InvalidOperationException("لا توجد مبالغ لتصديرها في الكشف.");
        if (report.Rows.Count > 30_000) throw new InvalidOperationException("يتجاوز الكشف ٣٠٬٠٠٠ قيد؛ تصدير هذا الحجم غير مدعوم حالياً.");
        if (string.IsNullOrWhiteSpace(header)) throw new InvalidOperationException("أدخل ترويسة الكشف.");
        decimal total = 0;
        foreach (var row in report.Rows) total = checked(total + row.Amount);
        if (total != report.Total) throw new InvalidDataException("مجموع الكشف لا يطابق صفوفه؛ لم يُصدّر.");

        using var regular = SKTypeface.FromFile(regularFontPath) ?? throw new InvalidDataException("تعذّر فتح خط الكشف العادي.");
        using var bold = SKTypeface.FromFile(boldFontPath) ?? throw new InvalidDataException("تعذّر فتح خط الكشف العريض.");
        using var normalShaper = new SKShaper(regular);
        using var boldShaper = new SKShaper(bold);
        using var text = Font(regular, 10.5f);
        using var name = Font(bold, 11.5f);
        using var amount = Font(bold, 11f);
        using var heading = Font(bold, 11f);
        using var headerFont = Font(bold, 14f);
        using var titleFont = Font(bold, 14f, SKColors.White);
        using var totalFont = Font(bold, 11.5f);
        var tableWidth = PageWidth - Margin * 2;
        var maxAmountWidth = Math.Max(Width(Money(report.Total), boldShaper, totalFont), report.Rows.Max(r => Width(Money(r.Amount), boldShaper, amount))) + 16;
        var serialWidth = Math.Max(30, Width(report.Rows.Count.ToString(CultureInfo.InvariantCulture), boldShaper, amount) + 12);
        var amountWidth = Math.Max(95, maxAmountWidth);
        var remaining = tableWidth - serialWidth - amountWidth;
        if (remaining < 250) throw new InvalidOperationException("المبلغ أو التسلسل لا يتسع في صفحة A4 العمودية.");
        float[] widths = [serialWidth, remaining * .16f, remaining * .39f, amountWidth, remaining * .45f];
        var positions = new float[widths.Length];
        var right = PageWidth - Margin;
        for (int i = 0; i < widths.Length; i++) { right -= widths[i]; positions[i] = right; }

        var headerLines = Wrap(header, boldShaper, headerFont, tableWidth - 8);
        var title = $"قائمة الاستقطاعات لـ شهر {EasternDigits(report.Month)} - {report.Entity}";
        var titleLines = Wrap(title, boldShaper, titleFont, tableWidth - 8);
        var headerHeight = Math.Max(24, (float)Math.Ceiling(headerLines.Count * headerFont.FontSpacing + 4));
        var titleHeight = Math.Max(30, (float)Math.Ceiling(titleLines.Count * titleFont.FontSpacing + 8));
        string[] headings = ["ت", "الرتبة", "الاسم الكامل", "المبلغ", "رقم وتاريخ الاضبارة"];
        var headingLines = headings.Select((value, i) => Wrap(value, boldShaper, heading, widths[i] - 8)).ToArray();
        var headingHeight = Math.Max(24, (float)Math.Ceiling(headingLines.Max(x => x.Count) * heading.FontSpacing + 6));
        var tableTop = Margin + headerHeight + titleHeight + headingHeight;
        var room = Bottom - tableTop;
        if (room < 48) throw new InvalidOperationException("ترويسة الكشف أو عنوانه طويل جداً لصفحة A4 العمودية.");

        var layouts = new List<LayoutRow>(report.Rows.Count);
        for (var i = 0; i < report.Rows.Count; i++)
        {
            var row = report.Rows[i];
            string[] values = [(i + 1).ToString(CultureInfo.InvariantCulture), row.Rank, row.Name, Money(row.Amount), row.Note];
            SKPaint[] paints = [amount, text, name, amount, text];
            SKShaper[] shapers = [boldShaper, normalShaper, boldShaper, boldShaper, normalShaper];
            var lines = new string[5][];
            var rowHeight = 24f;
            for (int c = 0; c < 5; c++)
            {
                lines[c] = Wrap(values[c], shapers[c], paints[c], widths[c] - 8).ToArray();
                if ((c == 0 || c == 3) && lines[c].Length > 1) throw new InvalidOperationException("مبلغ أو تسلسل لا يتسع في الصفحة العمودية.");
                rowHeight = Math.Max(rowHeight, (float)Math.Ceiling(lines[c].Length * paints[c].FontSpacing + 8));
            }
            if (rowHeight + headingHeight > room) throw new InvalidOperationException("أحد صفوف الكشف أطول من الصفحة العمودية؛ صدّر Excel.");
            layouts.Add(new LayoutRow(lines, rowHeight));
        }
        var pages = new List<(int Start, int End)>();
        for (var start = 0; start < layouts.Count;)
        {
            var end = start;
            float used = 0;
            while (end < layouts.Count && used + layouts[end].Height <= room) used += layouts[end++].Height;
            if (end == layouts.Count && used + headingHeight > room) end--;
            if (end == start) throw new InvalidOperationException("تعذّر عرض مجموع الكشف في الصفحة الأخيرة.");
            pages.Add((start, end));
            start = end;
        }
        if (Width(Money(total), boldShaper, totalFont) > widths[3] - 8) throw new InvalidOperationException("مجموع الكشف لا يتسع في الصفحة العمودية.");

        using var document = SKDocument.CreatePdf(output) ?? throw new InvalidDataException("تعذّر إنشاء ملف PDF.");
        using var fill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = .5f, Color = Border, IsAntialias = true };
        for (var page = 0; page < pages.Count; page++)
        {
            var canvas = document.BeginPage(PageWidth, PageHeight);
            canvas.Clear(SKColors.White);
            var headerRect = SKRect.Create(Margin, Margin, tableWidth, headerHeight);
            DrawLines(canvas, headerLines, headerRect, boldShaper, headerFont, false);
            var titleRect = SKRect.Create(Margin, headerRect.Bottom, tableWidth, titleHeight);
            fill.Color = Dark; canvas.DrawRect(titleRect, fill);
            DrawLines(canvas, titleLines, titleRect, boldShaper, titleFont, false);
            var top = titleRect.Bottom;
            for (int c = 0; c < 5; c++)
            {
                var rect = SKRect.Create(positions[c], top, widths[c], headingHeight);
                fill.Color = Light; canvas.DrawRect(rect, fill); canvas.DrawRect(rect, stroke);
                DrawLines(canvas, headingLines[c], rect, boldShaper, heading, false);
            }
            var y = top + headingHeight;
            var (start, end) = pages[page];
            for (int i = start; i < end; i++)
            {
                var layout = layouts[i];
                if ((i & 1) == 1) { fill.Color = Zebra; canvas.DrawRect(SKRect.Create(Margin, y, tableWidth, layout.Height), fill); }
                for (int c = 0; c < 5; c++)
                {
                    var rect = SKRect.Create(positions[c], y, widths[c], layout.Height);
                    canvas.DrawRect(rect, stroke);
                    var paint = c == 2 ? name : c is 0 or 3 ? amount : text;
                    var shaper = c is 0 or 2 or 3 ? boldShaper : normalShaper;
                    DrawLines(canvas, layout.Lines[c], rect, shaper, paint, c is 1 or 2 or 4);
                }
                y += layout.Height;
            }
            if (page == pages.Count - 1)
            {
                var label = SKRect.Create(positions[2], y, widths[0] + widths[1] + widths[2], headingHeight);
                var sum = SKRect.Create(positions[3], y, widths[3], headingHeight);
                var blank = SKRect.Create(positions[4], y, widths[4], headingHeight);
                foreach (var rect in new[] { label, sum, blank }) { fill.Color = Light; canvas.DrawRect(rect, fill); canvas.DrawRect(rect, stroke); }
                DrawLines(canvas, ["المجموع"], label, boldShaper, totalFont, false);
                DrawLines(canvas, [Money(total)], sum, boldShaper, totalFont, false);
            }
            document.EndPage();
        }
        document.Close();
    }

    private static SKPaint Font(SKTypeface typeface, float size, SKColor? color = null) => new() { Typeface = typeface, TextSize = size, Color = color ?? SKColors.Black, IsAntialias = true };
    private static string Money(decimal value) => value.ToString("#,0.############################", CultureInfo.InvariantCulture);
    private static string EasternDigits(string value) => string.Concat(value.Select(c => c is >= '0' and <= '9' ? (char)('٠' + c - '0') : c));
    private static IReadOnlyList<VisualRun> Runs(string value)
    {
        if (value.Length == 0) return [];
        var bidi = BidiInfo.Create(value, Level.Rtl());
        if (bidi.Paragraphs.Count != 1) throw new InvalidOperationException("تعذّر تنسيق اتجاه أحد أسطر الكشف.");
        var paragraph = bidi.Paragraphs[0];
        var (levels, ranges) = bidi.VisualRuns(paragraph, paragraph.Range);
        return ranges.Select(range => new VisualRun(value.Substring(range.Start, range.Length), levels[range.Start].IsRtl())).ToArray();
    }

    private static SKShaper.Result Shape(VisualRun run, SKShaper shaper, SKPaint paint, float x = 0, float y = 0)
    {
        // Directional controls affect UAX #9 but are not printable glyphs. Keep ZWJ/ZWNJ for shaping.
        var value = new string(run.Value.Where(c => c is not ('\u200e' or '\u200f' or >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069')).ToArray());
        using var buffer = new HarfBuzzSharp.Buffer();
        buffer.AddUtf16(value);
        buffer.GuessSegmentProperties();
        buffer.Direction = run.RightToLeft ? HarfBuzzSharp.Direction.RightToLeft : HarfBuzzSharp.Direction.LeftToRight;
        var result = shaper.Shape(buffer, x, y, paint);
        if (result.Codepoints.Any(glyph => glyph == 0)) throw new InvalidOperationException("تعذّر تشكيل أحد حروف الكشف بالخط المتاح؛ لم يُصدّر.");
        return result;
    }

    private static float Width(string value, SKShaper shaper, SKPaint paint) => Runs(value).Sum(run => Shape(run, shaper, paint).Width);

    private static List<string> Wrap(string value, SKShaper shaper, SKPaint paint, float width)
    {
        if (width <= 1 || value.Length > 4096) throw new InvalidOperationException("أحد حقول الكشف طويل جداً للصفحة العمودية.");
        if (value.Any(c => char.IsControl(c) && c is not '\r' and not '\n'))
            throw new InvalidOperationException("يحتوي أحد حقول الكشف على محرف تحكم غير مدعوم؛ راجعه قبل التصدير.");
        var printable = new string(value.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray());
        if (!paint.ContainsGlyphs(printable))
            throw new InvalidOperationException("يحتوي الكشف على حروف غير مدعومة بخط Arial؛ لم يُصدّر حتى لا تظهر مربعات بدلاً منها.");
        var result = new List<string>();
        foreach (var paragraph in value.Replace("\r", "").Split('\n'))
        {
            var line = "";
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var proposed = line.Length == 0 ? word : line + " " + word;
                if (Width(proposed, shaper, paint) <= width) { line = proposed; continue; }
                if (line.Length > 0) { result.Add(line); line = ""; }
                foreach (var rune in word.EnumerateRunes())
                {
                    var proposedPart = line + rune;
                    if (Width(proposedPart, shaper, paint) > width)
                    {
                        if (line.Length == 0) throw new InvalidOperationException("أحد أحرف الكشف لا يتسع في العمود.");
                        result.Add(line); line = rune.ToString();
                        if (Width(line, shaper, paint) > width) throw new InvalidOperationException("أحد أحرف الكشف لا يتسع في العمود.");
                    }
                    else line = proposedPart;
                }
            }
            result.Add(line);
        }
        return result;
    }

    private static void DrawLines(SKCanvas canvas, IReadOnlyList<string> lines, SKRect rect, SKShaper shaper, SKPaint paint, bool alignRight)
    {
        var baseline = rect.Top + (rect.Height - lines.Count * paint.FontSpacing) / 2 - paint.FontMetrics.Ascent;
        foreach (var line in lines)
        {
            var width = Width(line, shaper, paint);
            if (width > rect.Width - 7) throw new InvalidOperationException("تعذّر احتواء نص في الكشف.");
            var x = alignRight ? rect.Right - 4 - width : rect.Left + (rect.Width - width) / 2;
            foreach (var run in Runs(line))
            {
                var shaped = Shape(run, shaper, paint, x, baseline);
                if (shaped.Codepoints.Length == 0) { x += shaped.Width; continue; }
                using var font = new SKFont(paint.Typeface, paint.TextSize, paint.TextScaleX, paint.TextSkewX);
                using var builder = new SKTextBlobBuilder();
                var glyphRun = builder.AllocatePositionedRun(font, shaped.Codepoints.Length);
                var glyphs = glyphRun.GetGlyphSpan();
                var positions = glyphRun.GetPositionSpan();
                for (var i = 0; i < shaped.Codepoints.Length; i++) { glyphs[i] = (ushort)shaped.Codepoints[i]; positions[i] = shaped.Points[i]; }
                using var blob = builder.Build();
                if (blob is not null) canvas.DrawText(blob, 0, 0, paint);
                x += shaped.Width;
            }
            baseline += paint.FontSpacing;
        }
    }
}
