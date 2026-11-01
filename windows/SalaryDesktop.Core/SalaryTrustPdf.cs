using System.Globalization;
using SkiaSharp;
using UglyToad.PdfPig;

namespace SalaryDesktop.Core;

/// كشف أمانات الرواتب PDF (نقل لـ SalaryTrustPDF.swift): A4 بالعرض، الخلايا الطويلة (الاسم والحالة) تلتف على أسطر
/// ويزيد ارتفاع الصف، والجدول يتجزأ على الصفحات مع تكرار الرأس. لا يُقتطع أي نص؛ وإذا كان قيد واحد أطول من صفحة
/// يُرفض PDF برسالة (Excel يبقى متاحاً). بعد الإنشاء يُقرأ الملف ويُطابق التسلسل والمبالغ في كل صف.
public static class SalaryTrustPdf
{
    private static readonly float[] Widths = [28, 150, 75, 78, 125, 110, 110, 110];
    private const float PageW = 842, PageH = 595, Margin = 28, Bottom = 560, Content = 786;
    private static readonly SKColor Dark = new(31, 79, 120), HeadFill = new(235, 235, 235), Line = new(90, 90, 90);
    private static readonly int[] NumberColumns = [0, 5, 6, 7];

    private sealed record Placement(int Page, int Row, float Y, float Height);

    public static byte[] Generate(ReportSheet sheet, ReportAssets assets, DateTime stamp)
    {
        if (sheet.Columns.Count != Widths.Length || sheet.Rows.Count == 0 || sheet.Rows.Any(r => r.Length != Widths.Length)) throw new InvalidDataException("الكشف فارغ أو غير صالح.");
        using var canvas = new ReportCanvas(assets, stamp);
        using var headerPaint = canvas.Paint(12, true); using var titlePaint = canvas.Paint(12, true, SKColors.White);
        using var columnPaint = canvas.Paint(10, true); using var cellPaint = canvas.Paint(9, false);
        var x = new float[Widths.Length]; var right = PageW - Margin;
        for (int c = 0; c < Widths.Length; c++) { right -= Widths[c]; x[c] = right; }

        var headerLines = canvas.Lines(headerPaint, sheet.Header, Content - 8);
        var titleLines = canvas.Lines(titlePaint, sheet.Title, Content - 8);
        var columnLines = sheet.Columns.Select((col, c) => canvas.Lines(columnPaint, col.Title, Widths[c] - 8)).ToArray();
        var top = Margin + headerLines.Count * headerPaint.FontSpacing + 8;
        var titleHeight = Math.Max(26, titleLines.Count * titlePaint.FontSpacing + 8);
        var headHeight = Math.Max(24, columnLines.Max(l => l.Count) * columnPaint.FontSpacing + 6);
        var tableTop = top + titleHeight;
        var maximumRow = Bottom - tableTop - headHeight;
        if (maximumRow <= 40) throw new InvalidDataException("عنوان الكشف أطول من مساحة الصفحة؛ اختصر البحث.");
        var cells = sheet.Rows.Select(row => row.Select((value, c) => canvas.Lines(cellPaint, value, Widths[c] - 8)).ToArray()).ToArray();
        var heights = cells.Select(row => MathF.Ceiling(Math.Max(20, row.Max(l => l.Count) * cellPaint.FontSpacing + 6))).ToArray();
        if (heights.Any(h => h > maximumRow)) throw new InvalidDataException("أحد القيود أطول من صفحة PDF؛ صدّر Excel لهذا الكشف.");

        // تقسيم الصفوف على الصفحات أولاً ليُعرف عدد الصفحات في التذييل.
        var placements = new List<Placement>(); var page = 0; var y = tableTop + headHeight;
        for (int i = 0; i < heights.Length; i++)
        {
            if (y + heights[i] > Bottom) { page++; y = tableTop + headHeight; }
            placements.Add(new(page, i, y, heights[i])); y += heights[i];
        }
        var pages = page + 1;

        void Lines(SKPaint paint, IReadOnlyList<string> lines, float left, float width, float cellTop, float height, ReportCanvas.Align align)
        {
            var block = lines.Count * paint.FontSpacing; var start = cellTop + (height - block) / 2;
            for (int k = 0; k < lines.Count; k++)
                canvas.Text(paint, lines[k], left, left + width, start + k * paint.FontSpacing - paint.FontMetrics.Ascent, align, true);
        }
        for (int p = 0; p < pages; p++)
        {
            canvas.BeginPage(true);
            Lines(headerPaint, headerLines, Margin, Content, Margin, top - Margin - 4, ReportCanvas.Align.Center);
            var titleRect = SKRect.Create(Margin, top, Content, titleHeight);
            canvas.Fill(titleRect, Dark);
            Lines(titlePaint, titleLines, Margin, Content, top, titleHeight, ReportCanvas.Align.Center);
            for (int c = 0; c < Widths.Length; c++)
            {
                var cell = SKRect.Create(x[c], tableTop, Widths[c], headHeight);
                canvas.Fill(cell, HeadFill); canvas.Stroke(cell, Line, 0.5f);
                Lines(columnPaint, columnLines[c], x[c], Widths[c], tableTop, headHeight, ReportCanvas.Align.Center);
            }
            foreach (var place in placements.Where(pl => pl.Page == p))
                for (int c = 0; c < Widths.Length; c++)
                {
                    canvas.Stroke(SKRect.Create(x[c], place.Y, Widths[c], place.Height), Line, 0.5f);
                    Lines(cellPaint, cells[place.Row][c], x[c], Widths[c], place.Y, place.Height, NumberColumns.Contains(c) ? ReportCanvas.Align.Center : ReportCanvas.Align.Right);
                }
            canvas.EndPage(p + 1, pages);
        }
        var bytes = canvas.Finish();
        Verify(bytes, sheet, placements, x, pages);
        return bytes;
    }

    /// قراءة الملف الناتج: عدد الصفحات، وتسلسل كل صف ومبالغه (الأمانة والمصروف والمتبقي) في خلاياها.
    private static string Digits(string value) => new(value.Select(c => c is >= '٠' and <= '٩' ? (char)('0' + c - '٠') : c).Where(char.IsAsciiDigit).ToArray());
    private static void Verify(byte[] bytes, ReportSheet sheet, IReadOnlyList<Placement> placements, float[] x, int pages)
    {
        using var document = PdfDocument.Open(bytes);
        if (document.NumberOfPages != pages || placements.Count != sheet.Rows.Count) throw new InvalidDataException("تعذّر تدقيق صفحات كشف الأمانات.");
        foreach (var group in placements.GroupBy(p => p.Page))
        {
            var letters = document.GetPage(group.Key + 1).Letters;
            foreach (var place in group)
                foreach (var c in NumberColumns)
                {
                    var expected = Digits(sheet.Rows[place.Row][c]);
                    if (expected.Length == 0) continue;
                    float left = x[c], cellRight = x[c] + Widths[c], low = PageH - place.Y - place.Height, high = PageH - place.Y;
                    var actual = Digits(string.Concat(letters.Where(l =>
                    {
                        var r = l.GlyphRectangle; var cx = (r.Left + r.Right) / 2; var cy = (r.Bottom + r.Top) / 2;
                        return cx >= left && cx <= cellRight && cy >= low && cy <= high;
                    }).OrderBy(l => l.GlyphRectangle.Left).Select(l => l.Value)));
                    if (actual != expected)
                        throw new InvalidDataException($"تعذّرت مطابقة الصف {(place.Row + 1).ToString(CultureInfo.InvariantCulture)} والعمود {(c + 1).ToString(CultureInfo.InvariantCulture)} في PDF؛ لم يُنشأ الملف.");
                }
        }
    }
}
