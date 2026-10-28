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
        var rows = records.Select(x => new Line(x.Name, string.IsNullOrEmpty(x.Rank) ? rankByName.GetValueOrDefault(PayrollMath.Key(x.Name)) ?? "" : x.Rank, x.Amount?.ToString("N2", CultureInfo.InvariantCulture) ?? "غير متوفر", x.Note ?? "", x.Title)).OrderBy(x => x.Name).ToList();
        Write(path, month, title, rows, total: records.All(x => x.Amount.HasValue) ? records.Sum(x => x.Amount!.Value) : null, isEntity: true);
    }
    public static void Payroll(string path, BackupData data, string month, string title)
    {
        var records = data.Employees.Where(x => x.Month == month).Select(x => (Employee: x, Items: PayrollMath.Decode<double>(x.Items)))
            .Where(x => x.Items.ContainsKey(title)).Select(x => (x.Employee, Amount: x.Items[title])).ToList();
        var rows = records.Select(x => new Line(x.Employee.Name, x.Employee.Rank, x.Amount.ToString("N2", CultureInfo.InvariantCulture), x.Employee.Workplace)).ToList();
        Write(path, month, title, rows, total: records.Sum(x => x.Amount));
    }
    public static void Write(string path, string month, string title, List<Line> rows, string? fontPath = null, double? total = null, bool isEntity = false)
    {
        fontPath ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
        if (!File.Exists(fontPath)) throw new InvalidOperationException("خط Arial غير موجود؛ لم يُنشأ الكشف.");
        using var font = SKTypeface.FromFile(fontPath) ?? throw new InvalidOperationException("تعذرت قراءة خط التقرير.");
        using var shaper = new SKShaper(font);
        using var paint = new SKPaint { Typeface = font, TextSize = 10, Color = SKColors.Black, IsAntialias = true, TextAlign = SKTextAlign.Right };
        using var border = new SKPaint { Color = new SKColor(195, 202, 199), Style = SKPaintStyle.Stroke, StrokeWidth = .5f };
        using var header = new SKPaint { Color = new SKColor(222, 235, 247) };
        using var titleFill = new SKPaint { Color = new SKColor(31, 79, 120) };
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
                    var runs = System.Text.RegularExpressions.Regex.Matches(text, @"[A-Za-z0-9][A-Za-z0-9.,/%+\-]*(?:\s+[A-Za-z0-9][A-Za-z0-9.,/%+\-]*)*|[^A-Za-z0-9]+")
                        .Select(m => m.Value).ToList();
                    foreach (var run in runs)
                    {
                        canvas!.DrawShapedText(shaper, run, x, baseline, paint);
                        x -= shaper.Shape(run, paint).Width;
                    }
                }
                void StartPage()
                {
                    if (canvas != null) { Text("صفحة", left + width, pageHeight - 15); Text(page.ToString(), left + width - 42, pageHeight - 15); document.EndPage(); }
                    canvas = document.BeginPage(842, pageHeight); page++; y = 35;
                    paint.TextSize = 12; Text("قيادة شرطة محافظة الانبار", left + width / 2 + 100, y); y += 16;
                    paint.TextSize = 11;
                    var reportTitle = isEntity ? $"قائمة الاستقطاعات لشهر {month} - {title}" : $"قائمة {title} لشهر {month}";
                    var titleLines = Wrap(reportTitle, width - 10);
                    if (titleLines.Count > 4) throw new InvalidDataException("عنوان الكشف طويل جداً ولا يتسع في الترويسة.");
                    canvas.DrawRect(left, y - 10, width, Math.Max(30, titleLines.Count * 15 + 8), titleFill);
                    paint.Color = SKColors.White;
                    foreach (var titleLine in titleLines) { Text(titleLine, left + width - 8, y + 5); y += 15; }
                    paint.Color = SKColors.Black; y += Math.Max(15, 30 - titleLines.Count * 15);
                    paint.TextSize = 10;
                    canvas.DrawRect(left, y, width, 22, header); canvas.DrawRect(left, y, width, 22, border);
                    string[] labels = isEntity ? ["ت", "الرتبة", "الاسم الكامل", "المبلغ", "جهة التنفيذ", "رقم وتاريخ الاضبارة"] : ["ت", "الرتبة", "الاسم الكامل", "المبلغ", "محل العمل"];
                    float x = left + width;
                    for (int i = 0; i < labels.Length; i++) { Text(labels[i], x - 5, y + 15); x -= colWidths[i]; }
                    y += 22;
                }
                StartPage();
                var serial = 0;
                foreach (var row in rows)
                {
                    serial++;
                    var values = isEntity ? new[] { serial.ToString(), row.Rank, row.Name, row.Amount, row.Entity, row.Note } : new[] { serial.ToString(), row.Rank, row.Name, row.Amount, row.Note };
                    var lines = values.Select((value, i) => Wrap(value ?? "", colWidths[i] - 10)).ToArray();
                    var height = Math.Max(20, lines.Max(x => x.Count) * lineHeight + 6);
                    if (height > pageHeight - 150) throw new InvalidDataException("أحد السجلات طويل جداً ولا يتسع في صفحة PDF؛ لم يُصدّر كشف ناقص.");
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
                y += 20; Text("عدد السجلات", left + width, y); Text(rows.Count.ToString("N0", CultureInfo.InvariantCulture), left + width - 105, y);
                if (total.HasValue) { Text("المجموع", left + width - 200, y); Text(total.Value.ToString("N2", CultureInfo.InvariantCulture), left + width - 280, y); }
                else Text("المجموع غير متوفر", left + width - 200, y);
                Text("صفحة", left + width, pageHeight - 15); Text(page.ToString(), left + width - 42, pageHeight - 15);
                document.EndPage(); document.Close();
            }
            File.Move(temp, path, true);
        }
        finally { try { File.Delete(temp); } catch { } }
    }
}
