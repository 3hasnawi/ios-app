using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;
using UglyToad.PdfPig;

namespace SalaryDesktop.Core;

/// قائمة مستقلة عن الكتاب؛ يتحقق المصدّر من كل صف قبل حفظ الملف.
public static class NameListPdf
{
    private const float W = 595, H = 842, TableTop = 105, Bottom = 755;
    private static readonly float[] X = [30, 120, 240, 325, 530, 565];
    private static string Eastern(string s) => string.Concat(s.Select(c => c is >= '0' and <= '9' ? (char)('٠' + c - '0') : c));
    private static string Number(long n) => Eastern(n.ToString("N0", CultureInfo.GetCultureInfo("en-US")));
    private static string Identity(string value) => new string(PayrollAnalysis.Normalize(value.Normalize(NormalizationForm.FormKC).Replace('ی', 'ي').Replace('ھ', 'ه')).Where(char.IsLetterOrDigit).Order().ToArray());
    public static byte[] Generate(NameList list, byte[] fontBytes)
    {
        if (list.Problem is not null || list.Rows.Count is 0 or > 5000 || MonthNames.SortKey(list.Month) == 0 || string.IsNullOrWhiteSpace(list.Title) || fontBytes.Length is 0 or > 3_000_000)
            throw new InvalidDataException("القائمة غير جاهزة للتصدير؛ راجع بياناتها أولاً.");
        if (list.Rows.Select((row, i) => row.Number == i + 1 && row.Amount >= 0 && !string.IsNullOrWhiteSpace(row.Name)).Any(ok => !ok) ||
            list.Rows.Sum(row => (decimal)row.Amount) != list.Total || list.Total <= 0 || list.Total > 9_007_199_254_740_991L)
            throw new InvalidDataException("أسماء القائمة أو مجموعها غير صالح.");
        using var data = SKData.CreateCopy(fontBytes); using var face = SKTypeface.FromData(data) ?? throw new InvalidDataException("تعذّر فتح خط القائمة.");
        using var paint = new SKPaint { Typeface = face, TextSize = 9.5f, IsAntialias = true, Color = SKColors.Black };
        using var stroke = new SKPaint { Color = new SKColor(70, 70, 70), Style = SKPaintStyle.Stroke, StrokeWidth = .6f, IsAntialias = true };
        var cells = list.Rows.Select(r => new[] { Number(r.Amount), r.Workplace, r.Rank, r.Name, Eastern(r.Number.ToString(CultureInfo.InvariantCulture)) }).ToArray();
        List<string>[][] wrapped = []; float[] heights = [];
        void Prepare(float size)
        {
            paint.TextSize = size;
            wrapped = cells.Select(row => row.Select((cell, col) => col is 0 or 4 ? new List<string> { cell } : ArabicPresentation.Wrap(cell, paint, X[col + 1] - X[col] - 6)).ToArray()).ToArray();
            heights = wrapped.Select(row => Math.Max(13, row.Max(lines => lines.Count) * (size + 2) + 4)).ToArray();
        }
        var size = 9.5f; Prepare(size);
        if (heights.Sum() > Bottom - TableTop - 44)
            for (var candidate = 9f; candidate >= 7f; candidate -= .5f) { Prepare(candidate); size = candidate; if (heights.Sum() <= Bottom - TableTop - 44) break; }
        // If one page cannot hold the list legibly, use a comfortable size and repeat headings.
        if (heights.Sum() > Bottom - TableTop - 44) { size = 9.5f; Prepare(size); }
        if (heights.Any(h => h > Bottom - TableTop - 44)) throw new InvalidDataException("اسم أو محل عمل لا يتسع في القائمة.");
        var pages = new List<(int First, int End)>();
        for (int first = 0; first < cells.Length;)
        {
            var end = first; float used = 22;
            while (end < cells.Length && used + heights[end] + 22 <= Bottom - TableTop) used += heights[end++];
            if (end == first) throw new InvalidDataException("تعذّر توزيع القائمة على الصفحات.");
            pages.Add((first, end)); first = end;
        }
        using var output = new MemoryStream(); using (var pdf = SKDocument.CreatePdf(output) ?? throw new InvalidDataException("تعذّر إنشاء قائمة PDF."))
        {
            var boxes = new List<(int Page, int Row, float Y, float Height)>();
            float totalY = 0;
            for (int p = 0; p < pages.Count; p++)
            {
                var canvas = pdf.BeginPage(W, H); canvas.Clear(SKColors.White);
                paint.TextSize = 13; ArabicPresentation.Fit(canvas, paint, "قائمة " + list.Title, 30, 565, 38);
                paint.TextSize = 10; ArabicPresentation.Fit(canvas, paint, Eastern(list.Month), 30, 565, 58);
                ArabicPresentation.Fit(canvas, paint, "مسودة للمراجعة", 30, 565, 75);
                var headings = new[] { "المبلغ", "محل العمل", "الرتبة", "الاسم", "ت" }; float y = TableTop;
                paint.TextSize = size;
                for (int col = 0; col < 5; col++) { canvas.DrawRect(X[col], y, X[col + 1] - X[col], 22, stroke); ArabicPresentation.Fit(canvas, paint, headings[col], X[col], X[col + 1], y + 15); }
                y += 22;
                for (int row = pages[p].First; row < pages[p].End; row++)
                {
                    var h = heights[row]; boxes.Add((p + 1, row, y, h));
                    for (int col = 0; col < 5; col++)
                    {
                        canvas.DrawRect(X[col], y, X[col + 1] - X[col], h, stroke);
                        for (int line = 0; line < wrapped[row][col].Count; line++) ArabicPresentation.Fit(canvas, paint, wrapped[row][col][line], X[col], X[col + 1], y + size + 2 + line * (size + 2), col is 1 or 2 or 3);
                    }
                    y += h;
                }
                if (p == pages.Count - 1) { totalY = y; ArabicPresentation.Fit(canvas, paint, "المجموع: " + Number(list.Total), 30, 565, y + 17); }
                paint.TextSize = 9; ArabicPresentation.Fit(canvas, paint, Eastern($"{p + 1} / {pages.Count}"), 30, 565, 795);
                pdf.EndPage();
            }
            pdf.Close();
            var bytes = output.ToArray(); using var read = PdfDocument.Open(bytes);
            if (read.NumberOfPages != pages.Count) throw new InvalidDataException("عدد صفحات القائمة غير مطابق.");
            foreach (var (pageNumber, row, top, height) in boxes)
            {
                var page = read.GetPage(pageNumber);
                string Cell(int col) => string.Concat(page.Letters.Where(l => { var rect = l.GlyphRectangle; var cx = (rect.Left + rect.Right) / 2; var cy = (rect.Bottom + rect.Top) / 2; return cx >= X[col] && cx <= X[col + 1] && cy >= H - top - height && cy <= H - top; }).OrderBy(l => l.GlyphRectangle.Left).Select(l => l.Value));
                var amount = TransferPreparation.NormalizeAccount(Cell(0)).Replace(",", "", StringComparison.Ordinal).Replace("٬", "", StringComparison.Ordinal);
                var number = TransferPreparation.NormalizeAccount(Cell(4));
                if (!long.TryParse(amount, out var a) || a != list.Rows[row].Amount || !int.TryParse(number, out var n) || n != row + 1 || Identity(Cell(3)) != Identity(list.Rows[row].Name) || Identity(Cell(2)) != Identity(list.Rows[row].Rank) || Identity(Cell(1)) != Identity(list.Rows[row].Workplace))
                    throw new InvalidDataException($"تعذّر التحقق من الصف {row + 1} في قائمة الأسماء.");
            }
            var totalPage = read.GetPage(pages.Count);
            var totalText = string.Concat(totalPage.Letters.Where(l => { var r = l.GlyphRectangle; var cy = (r.Bottom + r.Top) / 2; return cy >= H - totalY - 22 && cy <= H - totalY; }).OrderBy(l => l.GlyphRectangle.Left).Select(l => l.Value));
            var totalDigits = new string(TransferPreparation.NormalizeAccount(totalText).Where(char.IsAsciiDigit).ToArray());
            if (!long.TryParse(totalDigits, out var total) || total != list.Total) throw new InvalidDataException("مجموع قائمة الأسماء غير مطابق.");
            return bytes;
        }
    }
    public static void Save(NameList list, byte[] font, string destination)
    {
        var target = Path.GetFullPath(destination);
        if (!string.Equals(Path.GetExtension(target), ".pdf", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("اختر ملف PDF صالحاً.");
        var bytes = Generate(list, font); var directory = Path.GetDirectoryName(target) ?? throw new InvalidDataException("مجلد الحفظ غير صالح.");
        var temporary = Path.Combine(directory, ".name-list-" + Guid.NewGuid().ToString("N") + ".tmp");
        try { using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { file.Write(bytes); file.Flush(true); }
            using (var file = File.OpenRead(temporary)) if (!SHA256.HashData(file).AsSpan().SequenceEqual(SHA256.HashData(bytes))) throw new InvalidDataException("تعذّر التحقق من القائمة المحفوظة.");
            File.Move(temporary, target, true); }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
