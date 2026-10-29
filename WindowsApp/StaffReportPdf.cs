using SkiaSharp;
using SkiaSharp.HarfBuzz;
using System.Text.RegularExpressions;
namespace SalaryWindows;
internal static class StaffReportPdf
{
    public static void Write(string path, StaffReportData data, string? fontPath = null, string? logoPath = null)
    {
        fontPath ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
        if (!File.Exists(fontPath)) throw new InvalidOperationException("خط Arial غير موجود؛ لم يُنشأ التقرير.");
        using var typeface = SKTypeface.FromFile(fontPath) ?? throw new InvalidOperationException("خط التقرير غير صالح.");
        using var shaper = new SKShaper(typeface);
        using var paint = new SKPaint { Typeface = typeface, TextSize = 10, Color = SKColors.Black, IsAntialias = true, TextAlign = SKTextAlign.Right };
        using var line = new SKPaint { Color = new SKColor(190, 190, 190), Style = SKPaintStyle.Stroke, StrokeWidth = .4f };
        using var fill = new SKPaint { Color = new SKColor(240, 240, 240) };
        const float margin = 28, width = 539, bottom = 800, top = 170;
        List<string> Wrap(string text, float max)
        {
            var result = new List<string>(); var current = "";
            foreach (var word in (text ?? "").Replace('\r',' ').Replace('\n',' ').Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (shaper.Shape(candidate, paint).Width <= max) { current = candidate; continue; }
                if (current.Length > 0) { result.Add(current); current = ""; }
                foreach (var c in word)
                {
                    var next = current + c;
                    if (current.Length > 0 && shaper.Shape(next, paint).Width > max) { result.Add(current); current = ""; }
                    current += c;
                }
            }
            if (current.Length > 0) result.Add(current);
            if (result.Count == 0) result.Add("");
            return result;
        }
        float Height(StaffField f, float w)
        {
            var label = Wrap(f.Label, w * .38f - 10).Count; var value = Wrap(f.Value, w * .62f - 10).Count;
            return Math.Max(20, Math.Max(label, value) * (paint.TextSize + 3) + 6);
        }
        float Required()
        {
            float used = 0;
            foreach (var section in data.Sections)
            {
                bool wide = section.Title == "السجل والملاحظات"; var cw = wide ? width : width / 2; used += 22;
                for (var i = 0; i < section.Fields.Count; i += wide ? 1 : 2)
                {
                    float h = Height(section.Fields[i], cw);
                    if (!wide && i + 1 < section.Fields.Count) h = Math.Max(h, Height(section.Fields[i + 1], cw));
                    used += h;
                }
                used += 4;
            }
            return used;
        }
        float size = 0;
        foreach (var s in new float[] { 10.5f,10,9.5f,9,8.5f,8,7.5f,7,6.5f })
        {
            paint.TextSize = s;
            if (Required() <= bottom - top && Wrap(data.Name, width - 142).Count == 1 && Wrap(data.Subtitle, width - 142).Count == 1 && Wrap(data.Status, 110).Count <= 2) { size = s; break; }
        }
        if (size == 0) throw new InvalidDataException("بيانات الملاك طويلة ولا تتسع في صفحة واحدة بخط مقروء؛ لم يُنشأ تقرير ناقص.");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var doc = SKDocument.CreatePdf(temp) ?? throw new IOException("تعذّر إنشاء التقرير."))
            {
                var canvas = doc.BeginPage(595, 842);
                void Text(string text, float x, float y)
                {
                    text = string.Concat(text.Select(c => c >= '٠' && c <= '٩' ? (char)('0' + c - '٠') : c >= '۰' && c <= '۹' ? (char)('0' + c - '۰') : c));
                    foreach (Match match in Regex.Matches(text, @"[A-Za-z0-9][A-Za-z0-9.,:/%+\-]*(?:\s+[A-Za-z0-9][A-Za-z0-9.,:/%+\-]*)*|[^A-Za-z0-9]+"))
                    { canvas.DrawShapedText(shaper, match.Value, x, y, paint); x -= shaper.Shape(match.Value, paint).Width; }
                }
                logoPath ??= Path.Combine(AppContext.BaseDirectory, "Resources", "HomeLogo.png");
                if (File.Exists(logoPath)) { using var image = SKBitmap.Decode(logoPath); if (image != null) canvas.DrawBitmap(image, new SKRect(275, 24, 319, 68)); }
                paint.TextSize = 10; Text("وزارة الداخلية", 562, 37); Text("قيادة شرطة محافظة الأنبار", 562, 53);
                Text("تقرير الملاك", 180, 37); Text(DateTime.Now.ToString("yyyy/MM/dd"), 180, 53);
                canvas.DrawLine(margin, 78, 567, 78, line);
                paint.TextSize = 15; Text("بيان معلومات المنتسب", 370, 103);
                canvas.DrawRect(margin, 112, width, 48, line);
                paint.TextSize = size; Text(data.Name, 562, 128); Text(data.Subtitle, 562, 147);
                var statusLines = Wrap(data.Status, 110); for (int j = 0; j < statusLines.Count; j++) Text(statusLines[j], 150, 130 + j * (size + 3));
                float y = top;
                foreach (var section in data.Sections)
                {
                    bool wide = section.Title == "السجل والملاحظات"; float cw = wide ? width : width / 2;
                    canvas.DrawRect(margin, y, width, 22, fill); Text(section.Title, 562, y + 15); y += 22;
                    for (var i = 0; i < section.Fields.Count; i += wide ? 1 : 2)
                    {
                        var h = Height(section.Fields[i], cw); if (!wide && i + 1 < section.Fields.Count) h = Math.Max(h, Height(section.Fields[i + 1], cw));
                        for (var j = 0; j < (wide ? 1 : 2) && i + j < section.Fields.Count; j++)
                        {
                            var f = section.Fields[i + j]; float x = margin + width - (j + 1) * cw, lw = cw * .38f;
                            canvas.DrawRect(x + cw - lw, y, lw, h, line); canvas.DrawRect(x, y, cw - lw, h, line);
                            var labels = Wrap(f.Label, lw - 10); var values = Wrap(f.Value, cw - lw - 10);
                            for (var k = 0; k < labels.Count; k++) Text(labels[k], x + cw - 5, y + size + 3 + k * (size + 3));
                            for (var k = 0; k < values.Count; k++) Text(values[k], x + cw - lw - 5, y + size + 3 + k * (size + 3));
                        }
                        y += h;
                    }
                    y += 4;
                }
                paint.TextSize = 8; Text("تقرير الملاك · صفحة 1 من 1", 362, 820);
                doc.EndPage(); doc.Close();
            }
            File.Move(temp, path, true);
        }
        finally { try { File.Delete(temp); } catch { } }
    }
}
