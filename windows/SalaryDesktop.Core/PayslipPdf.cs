using SkiaSharp;

namespace SalaryDesktop.Core;

/// قسيمة راتب PDF من ملف المنتسب، بنفس ترويسة الكشوف وألوانها وعلامتها المائية وتذييلها.
public static class PayslipPdf
{
    private static readonly SKColor Dark = new(31, 79, 120), Light = new(222, 235, 247), Line = new(155, 184, 212);
    private const float Margin = 28, Row = 19;

    public static byte[] Generate(string header, string name, string rank, string workplace, string month, Payslip slip, ReportAssets assets, DateTime stamp)
    {
        using var canvas = new ReportCanvas(assets, stamp);
        var pages = 1; var page = 1;
        var leftCount = Math.Max(1, Math.Max(slip.Allowances.Count + (slip.Gross is null ? 0 : 1), slip.Deductions.Count + (slip.TotalDeductions is null ? 0 : 1)));
        var infoRows = (int)Math.Ceiling((3 + slip.Info.Count) / 2.0);
        var used = Margin + 24 + 30 + 10 + infoRows * Row + 14 + Row + leftCount * Row + 14 + Row * 1.6f + 14;
        var entityHeight = slip.EntityDeductions.Count == 0 ? 0 : Row * (slip.EntityDeductions.Count + 2) + 14;
        if (used + entityHeight > 842 - Margin - 30) pages = 2;
        canvas.BeginPage(false);
        float width = 595 - Margin * 2, right = 595 - Margin;
        using var headerPaint = canvas.Paint(14, true); using var title = canvas.Paint(13.5f, true, SKColors.White);
        using var bold = canvas.Paint(10.5f, true); using var text = canvas.Paint(10, false); using var big = canvas.Paint(13, true);
        canvas.Text(headerPaint, header, Margin, right, Margin + 16, ReportCanvas.Align.Center);
        var band = SKRect.Create(Margin, Margin + 24, width, 30); canvas.Fill(band, Dark);
        canvas.Text(title, $"قسيمة راتب · {ReportSheet.Eastern(month)}", band.Left, band.Right, band.Top + 20, ReportCanvas.Align.Center);
        var y = band.Bottom + 10;

        var info = new List<(string, string)> { ("الاسم", name), ("الرتبة", rank), ("محل العمل", workplace) };
        info.AddRange(slip.Info.Select(l => (l.Title, l.Value)));
        float labelW = width * .17f, valueW = width * .33f;
        for (int i = 0; i < info.Count; i += 2)
        {
            for (int k = 0; k < 2 && i + k < info.Count; k++)
            {
                var x = right - k * (labelW + valueW);
                var label = SKRect.Create(x - labelW, y, labelW, Row); var value = SKRect.Create(x - labelW - valueW, y, valueW, Row);
                canvas.Fill(label, Light); canvas.Stroke(label, Line, .5f); canvas.Stroke(value, Line, .5f);
                canvas.Text(bold, info[i + k].Item1, label.Left, label.Right, y + 13.5f, ReportCanvas.Align.Right);
                canvas.Text(text, info[i + k].Item2, value.Left, value.Right, y + 13.5f, ReportCanvas.Align.Right);
            }
            y += Row;
        }
        y += 14;

        // المخصصات يميناً والاستقطاعات يساراً، لكل منهما مجموعه.
        var half = (width - 12) / 2;
        void Table(float x, string heading, IReadOnlyList<ProfileLine> lines, ProfileLine? total, float top)
        {
            var head = SKRect.Create(x - half, top, half, Row); canvas.Fill(head, Light); canvas.Stroke(head, Line, .5f);
            canvas.Text(bold, heading, head.Left, head.Right, top + 13.5f, ReportCanvas.Align.Center);
            var yy = top + Row;
            if (lines.Count == 0 && total is null)
            {
                var empty = SKRect.Create(x - half, yy, half, Row); canvas.Stroke(empty, Line, .5f);
                canvas.Text(text, "لا توجد بنود في تفاصيل الراتب", empty.Left, empty.Right, yy + 13.5f, ReportCanvas.Align.Center);
            }
            foreach (var line in lines)
            {
                var label = SKRect.Create(x - half * .62f, yy, half * .62f, Row); var value = SKRect.Create(x - half, yy, half * .38f, Row);
                canvas.Stroke(label, Line, .5f); canvas.Stroke(value, Line, .5f);
                canvas.Text(text, line.Title, label.Left, label.Right, yy + 13.5f, ReportCanvas.Align.Right);
                canvas.Text(bold, line.Value, value.Left, value.Right, yy + 13.5f, ReportCanvas.Align.Center, line.Amount is not null);
                yy += Row;
            }
            if (total is not null)
            {
                var row = SKRect.Create(x - half, yy, half, Row); canvas.Fill(row, Light); canvas.Stroke(row, Line, .5f);
                canvas.Text(bold, total.Title, x - half * .62f, x, yy + 13.5f, ReportCanvas.Align.Right);
                canvas.Text(bold, total.Value, x - half, x - half * .62f, yy + 13.5f, ReportCanvas.Align.Center, true);
            }
        }
        Table(right, "المخصصات", slip.Allowances, slip.Gross, y);
        Table(right - half - 12, "الاستقطاعات", slip.Deductions, slip.TotalDeductions, y);
        y += Row * (leftCount + 1) + 14;

        var net = SKRect.Create(Margin, y, width, Row * 1.6f); canvas.Fill(net, new SKColor(227, 239, 236)); canvas.Stroke(net, new SKColor(19, 81, 72), .8f);
        canvas.Text(big, "صافي الراتب", net.MidX, net.Right, net.Top + 20, ReportCanvas.Align.Right);
        canvas.Text(big, slip.Net is { } amount ? ReportSheet.Money(amount) : "غير متاح", net.Left, net.MidX, net.Top + 20, ReportCanvas.Align.Center, true);
        y = net.Bottom + 14;

        if (slip.EntityDeductions.Count > 0)
        {
            if (pages == 2) { canvas.EndPage(page++, pages); canvas.BeginPage(false); y = Margin; }
            float[] widths = [width * .42f, width * .18f, width * .40f]; string[] heads = ["جهة التنفيذ (ملف الاستقطاعات)", "المبلغ", "الملاحظة"];
            float Col(int c) => right - widths.Take(c).Sum();
            for (int c = 0; c < 3; c++) { var r = SKRect.Create(Col(c) - widths[c], y, widths[c], Row); canvas.Fill(r, Light); canvas.Stroke(r, Line, .5f); canvas.Text(bold, heads[c], r.Left, r.Right, y + 13.5f, ReportCanvas.Align.Center); }
            y += Row;
            foreach (var line in slip.EntityDeductions)
            {
                string[] values = [line.Title, line.Value, line.Note];
                for (int c = 0; c < 3; c++) { var r = SKRect.Create(Col(c) - widths[c], y, widths[c], Row); canvas.Stroke(r, Line, .5f); canvas.Text(c == 1 ? bold : text, values[c], r.Left, r.Right, y + 13.5f, c == 1 ? ReportCanvas.Align.Center : ReportCanvas.Align.Right, c == 1 && line.Amount is not null); }
                y += Row;
            }
        }
        canvas.EndPage(page, pages);
        return canvas.Finish();
    }
}
