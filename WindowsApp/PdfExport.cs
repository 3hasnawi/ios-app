using SkiaSharp;
using SkiaSharp.HarfBuzz;
using System.Globalization;
namespace SalaryWindows;
internal static class PdfExport
{
    internal sealed record Line(string Name, string Rank, string Amount, string Note, string Entity = "");
    public static void Entity(string path, BackupData data, string month, string title)
    {
        var records = data.Deductions.Where(x => x.Month == month && x.Title == title).ToList();
        var rankByName = data.Employees.Where(x => x.Month == month).GroupBy(x => PayrollMath.Key(x.Name)).ToDictionary(g => g.Key, g => g.First().Rank);
        var rows = records.Select(x => new Line(x.Name, string.IsNullOrEmpty(x.Rank) ? rankByName.GetValueOrDefault(PayrollMath.Key(x.Name)) ?? "" : x.Rank, x.Amount?.ToString("#,##0.##", CultureInfo.InvariantCulture) ?? "غير متوفر", x.Note ?? "", x.Title)).OrderBy(x => x.Name).ToList();
        Write(path, month, title, rows, total: records.All(x => x.Amount.HasValue) ? records.Sum(x => x.Amount!.Value) : null, isEntity: true, reportHeader: BackupSettings.ReportHeader(data));
    }
    public static void Payroll(string path, BackupData data, string month, string title)
    {
        var records = data.Employees.Where(x => x.Month == month).Select(x => (Employee: x, Items: PayrollMath.Decode<double>(x.Items)))
            .Where(x => x.Items.ContainsKey(title)).Select(x => (x.Employee, Amount: x.Items[title])).ToList();
        var rows = records.Select(x => new Line(x.Employee.Name, x.Employee.Rank, x.Amount.ToString("#,##0.##", CultureInfo.InvariantCulture), x.Employee.Workplace)).OrderBy(x => x.Name).ToList();
        Write(path, month, title, rows, total: records.Sum(x => x.Amount), reportHeader: BackupSettings.ReportHeader(data));
    }
    public static void Write(string path, string month, string title, List<Line> rows, string? fontPath = null, double? total = null, bool isEntity = false, string reportHeader = "قيادة شرطة محافظة الانبار")
    {
        fontPath ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
        if (!File.Exists(fontPath)) throw new InvalidOperationException("خط Arial غير موجود؛ لم يُنشأ الكشف.");
        using var font = SKTypeface.FromFile(fontPath) ?? throw new InvalidOperationException("تعذرت قراءة خط التقرير.");
        using var shaper = new SKShaper(font);
        using var paint = new SKPaint { Typeface = font, TextSize = 10, Color = SKColors.Black, IsAntialias = true, TextAlign = SKTextAlign.Right };
        using var border = new SKPaint { Color = new SKColor(195, 202, 199), Style = SKPaintStyle.Stroke, StrokeWidth = .5f };
        using var header = new SKPaint { Color = new SKColor(222, 235, 247) };
        using var titleFill = new SKPaint { Color = new SKColor(31, 79, 120) };
        var watermarkPath = Path.Combine(AppContext.BaseDirectory, "Resources", "WatermarkLogo.png");
        using var watermark = File.Exists(watermarkPath) ? SKBitmap.Decode(watermarkPath) : null;
        using var watermarkPaint = new SKPaint { Color = SKColors.White.WithAlpha(26), FilterQuality = SKFilterQuality.Medium, IsAntialias = true };
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var document = SKDocument.CreatePdf(temp) ?? throw new IOException("تعذّر إنشاء PDF."))
            {
                SKCanvas? canvas = null; float y = 0; int page = 0;
                const float left = 28, width = 786, pageHeight = 595, lineHeight = 14;
                float[] colWidths = isEntity ? [35, 75, 190, 105, 170, 211] : [35, 80, 240, 110, 321];
                List<string> Wrap(string text, float max)
                {
                    var result = new List<string>(); var current = "";
                    // تقسيم الكلمات الطويلة يحمي من قصّ البيانات بدلاً من إخفائها.
                    foreach (var word in (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries))
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
                void Text(string text, float x, float baseline)
                {
                    // HarfBuzz يشكّل العربية لكنه لا يطبق خوارزمية اتجاه النص؛ تُرسم الأرقام والنصوص اللاتينية كمقاطع مستقلة.
                    text = string.Concat(text.Select(c => c >= '٠' && c <= '٩' ? (char)('0' + c - '٠') : c >= '۰' && c <= '۹' ? (char)('0' + c - '۰') : c));
                    var runs = System.Text.RegularExpressions.Regex.Matches(text, @"[A-Za-z0-9][A-Za-z0-9.,:/%+\-]*(?:\s+[A-Za-z0-9][A-Za-z0-9.,:/%+\-]*)*|[^A-Za-z0-9]+")
                        .Select(m => m.Value).ToList();
                    foreach (var run in runs)
                    {
                        canvas!.DrawShapedText(shaper, run, x, baseline, paint);
                        x -= shaper.Shape(run, paint).Width;
                    }
                }
                paint.TextSize = 11;
                var reportTitle = isEntity ? $"قائمة الاستقطاعات لشهر {month} - {title}" : $"قائمة {title} لشهر {month}";
                var titleLines = Wrap(reportTitle, width - 10);
                if (titleLines.Count > 4) throw new InvalidDataException("عنوان الكشف طويل جداً ولا يتسع في الترويسة.");
                var headerEnd = 35 + 16 + titleLines.Count * 15 + Math.Max(15, 30 - titleLines.Count * 15) + 22;
                paint.TextSize = 10;
                var prepared = new List<(List<string>[] Lines, float Height)>();
                for (int i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    var values = isEntity ? new[] { (i + 1).ToString(), row.Rank, row.Name, row.Amount, row.Entity, row.Note } : new[] { (i + 1).ToString(), row.Rank, row.Name, row.Amount, row.Note };
                    var lines = values.Select((value, c) => Wrap(value ?? "", colWidths[c] - 10)).ToArray();
                    var height = Math.Max(20, lines.Max(x => x.Count) * lineHeight + 6);
                    if (headerEnd + height > pageHeight - 35) throw new InvalidDataException("أحد السجلات طويل جداً ولا يتسع في صفحة PDF؛ لم يُصدّر كشف ناقص.");
                    prepared.Add((lines, height));
                }
                int totalPages = 1; float simulatedY = headerEnd;
                foreach (var item in prepared)
                {
                    if (simulatedY + item.Height > pageHeight - 35) { totalPages++; simulatedY = headerEnd; }
                    simulatedY += item.Height;
                }
                if (simulatedY + 32 > pageHeight - 35) totalPages++;
                void Footer()
                {
                    canvas!.DrawLine(left, pageHeight - 26, left + width, pageHeight - 26, border);
                    var previousSize = paint.TextSize; paint.TextSize = 8.5f;
                    Text("( قيادة شرطة محافظة الانبار - قسم الحسابات )", left + width, pageHeight - 10);
                    Text($"صفحة {page} من {totalPages}", left + width / 2 + 45, pageHeight - 10);
                    Text(DateTime.Now.ToString("yyyy/MM/dd HH:mm"), left + 100, pageHeight - 10);
                    paint.TextSize = previousSize;
                }
                void StartPage()
                {
                    if (canvas != null) { Footer(); document.EndPage(); }
                    canvas = document.BeginPage(842, pageHeight); page++; y = 35;
                    if (watermark != null)
                    {
                        float side = pageHeight * .62f, imageHeight = side * watermark.Height / Math.Max(1, watermark.Width);
                        canvas.DrawBitmap(watermark, new SKRect(421 - side / 2, pageHeight / 2 - imageHeight / 2, 421 + side / 2, pageHeight / 2 + imageHeight / 2), watermarkPaint);
                    }
                    paint.TextSize = 12;
                    if (Wrap(reportHeader, width - 10).Count != 1) throw new InvalidDataException("ترويسة الكشف طويلة جداً ولا تتسع في صفحة PDF.");
                    Text(reportHeader, left + width / 2 + shaper.Shape(reportHeader, paint).Width / 2, y); y += 16;
                    paint.TextSize = 11;
                    canvas.DrawRect(left, y - 10, width, Math.Max(30, titleLines.Count * 15 + 8), titleFill);
                    paint.Color = SKColors.White;
                    foreach (var titleLine in titleLines) { Text(titleLine, left + width / 2 + shaper.Shape(titleLine, paint).Width / 2, y + 5); y += 15; }
                    paint.Color = SKColors.Black; y += Math.Max(15, 30 - titleLines.Count * 15);
                    paint.TextSize = 10;
                    canvas.DrawRect(left, y, width, 22, header); canvas.DrawRect(left, y, width, 22, border);
                    string[] labels = isEntity ? ["ت", "الرتبة", "الاسم الكامل", "المبلغ", "جهة التنفيذ", "رقم وتاريخ الاضبارة"] : ["ت", "الرتبة", "الاسم الكامل", "المبلغ", "محل العمل"];
                    float x = left + width;
                    for (int i = 0; i < labels.Length; i++) { Text(labels[i], x - colWidths[i] / 2 + shaper.Shape(labels[i], paint).Width / 2, y + 15); x -= colWidths[i]; }
                    y += 22;
                }
                StartPage();
                foreach (var item in prepared)
                {
                    var lines = item.Lines; var height = item.Height;
                    if (y + height > pageHeight - 35) StartPage();
                    if (y + height > pageHeight - 35) throw new InvalidDataException("أحد السجلات طويل جداً ولا يتسع في صفحة PDF؛ لم يُصدّر كشف ناقص.");
                    float x = left + width;
                    for (int i = 0; i < lines.Length; i++)
                    {
                        canvas!.DrawRect(x - colWidths[i], y, colWidths[i], height, border);
                        for (int j = 0; j < lines[i].Count; j++) Text(lines[i][j], x - 5, y + 14 + j * lineHeight);
                        x -= colWidths[i];
                    }
                    y += height;
                }
                if (y + 32 > pageHeight - 35) StartPage();
                canvas!.DrawRect(left, y, width, 22, header);
                var labelWidth = colWidths[0] + colWidths[1] + colWidths[2];
                canvas.DrawRect(left + width - labelWidth, y, labelWidth, 22, border);
                var totalLabel = total.HasValue ? "المجموع" : "المجموع غير متوفر";
                Text(totalLabel, left + width - labelWidth / 2 + shaper.Shape(totalLabel, paint).Width / 2, y + 15);
                var totalX = left + width - labelWidth;
                for (var c = 3; c < colWidths.Length; c++) { canvas.DrawRect(totalX - colWidths[c], y, colWidths[c], 22, border); totalX -= colWidths[c]; }
                if (total.HasValue)
                {
                    var number = total.Value.ToString("#,##0.##", CultureInfo.InvariantCulture);
                    Text(number, left + width - labelWidth - colWidths[3] / 2 + shaper.Shape(number, paint).Width / 2, y + 15);
                }
                Footer();
                document.EndPage(); document.Close();
            }
            File.Move(temp, path, true);
        }
        finally { try { File.Delete(temp); } catch { } }
    }
}
