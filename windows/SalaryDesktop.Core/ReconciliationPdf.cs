using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace SalaryDesktop.Core;

/// كشف مسودة؛ لا يثبت صحة المطابقة أو حصول التحويلات في المصرف.
public static class ReconciliationPdf
{
    private const float PageWidth = 595, PageHeight = 842, Left = 30, Right = 565, TableTop = 170, Bottom = 680;
    private static readonly float[] X = [30, 125, 220, 315, 490, 565];
    private static string Number(long n) => n.ToString("N0", CultureInfo.GetCultureInfo("en-US"));
    private static string Date(DateOnly date) => date.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
    private static string[] Runs(string text)
    {
        if (!text.Any(c => c is >= '\u0600' and <= '\u08ff')) return [text];
        return Regex.Split(text, @"([A-Za-z0-9٠-٩۰-۹]+(?:[ .,/:-]+[A-Za-z0-9٠-٩۰-۹]+)*)").Where(part => part.Length > 0).ToArray();
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
    private static void Text(SKCanvas canvas, SKShaper shaper, SKPaint paint, string text, float x, float y, bool centered = false)
    {
        var width = Width(text, shaper, paint);
        DrawRight(canvas, shaper, paint, text, centered ? x + width / 2 : x, y);
    }
    public static byte[] Generate(BankPeriod period, string fontPath)
    {
        var totals = BankReconciliation.Calculate(period);
        if (period.Movements.Count == 0) throw new InvalidDataException("أضف حركة واحدة على الأقل قبل إعداد الكشف.");
        using var face = SKTypeface.FromFile(fontPath) ?? throw new InvalidDataException("تعذّر فتح خط الكشف العربي.");
        using var shaper = new SKShaper(face);
        using var paint = new SKPaint { Typeface = face, TextSize = 10, IsAntialias = true, Color = SKColors.Black };
        using var stroke = new SKPaint { Color = new SKColor(65, 65, 65), Style = SKPaintStyle.Stroke, StrokeWidth = 0.6f, IsAntialias = true };
        var descriptions = totals.Lines.Select(line => Wrap(line.Movement.Description, shaper, paint, X[4] - X[3] - 8)).ToArray();
        var firstName = Wrap(period.FirstMember, shaper, paint, 210);
        var secondName = Wrap(period.SecondMember, shaper, paint, 210);
        if (firstName.Count > 3 || secondName.Count > 3) throw new InvalidDataException("اسم عضو اللجنة طويل جداً للكشف؛ اختصر الاسم أو راجع التنسيق.");
        var required = string.Join(' ', totals.Lines.Select(x => x.Movement.Description)) + period.Month + period.FirstMember + period.SecondMember + "كشف مطابقة الحساب مدين دائن الرصيد الرصيد الافتتاحي عضوا اللجنة مسودة غير معتمدة";
        if (!paint.ContainsGlyphs(required)) throw new InvalidDataException("الخط المتاح لا يدعم بعض أحرف الكشف.");
        using var memory = new MemoryStream();
        using (var pdf = SKDocument.CreatePdf(memory) ?? throw new InvalidDataException("تعذّر إنشاء الكشف."))
        {
            SKCanvas? canvas = null; float y = 0; int page = 0;
            void StartPage()
            {
                if (++page > 2000) throw new InvalidDataException("الكشف يتجاوز عدد الصفحات المدعوم.");
                canvas = pdf.BeginPage(PageWidth, PageHeight); canvas.Clear(SKColors.White); y = TableTop;
                paint.TextSize = 17; Text(canvas, shaper, paint, "كشف مطابقة حساب المصرف", PageWidth / 2, 54, true);
                paint.TextSize = 10; Text(canvas, shaper, paint, "مسودة للمراجعة — غير معتمدة", PageWidth / 2, 78, true);
                Fit(canvas, shaper, paint, "شهر الرواتب: " + period.Month, Left, Right, 107, true);
                Fit(canvas, shaper, paint, "الفترة: " + Date(period.Start) + " - " + Date(period.End), Left, Right, 126, true);
                Fit(canvas, shaper, paint, "الرصيد الافتتاحي: " + Number(period.OpeningBalance), Left, Right, 145, true);
                var headings = new[] { "الرصيد", "مدين", "دائن", "البيان", "التاريخ" };
                for (int i = 0; i < headings.Length; i++) { canvas.DrawRect(X[i], y, X[i+1] - X[i], 25, stroke); Fit(canvas, shaper, paint, headings[i], X[i], X[i+1], y + 17); }
                y += 25;
            }
            void EndPage() { pdf.EndPage(); canvas = null; }
            StartPage();
            for (int i = 0; i < totals.Lines.Count; i++)
            {
                var line = totals.Lines[i]; var height = Math.Max(24f, descriptions[i].Count * 14f + 8f);
                if (height > Bottom - TableTop - 25) throw new InvalidDataException("بيان حركة طويل جداً للكشف.");
                if (y + height > Bottom) { EndPage(); StartPage(); }
                var c = canvas!;
                for (int col = 0; col < 5; col++) c.DrawRect(X[col], y, X[col+1] - X[col], height, stroke);
                Fit(c, shaper, paint, Number(line.Balance), X[0], X[1], y + 16);
                Fit(c, shaper, paint, line.Movement.Direction == MovementDirection.Debit ? Number(line.Movement.Amount) : "—", X[1], X[2], y + 16);
                Fit(c, shaper, paint, line.Movement.Direction == MovementDirection.Credit ? Number(line.Movement.Amount) : "—", X[2], X[3], y + 16);
                for (int j = 0; j < descriptions[i].Count; j++) Fit(c, shaper, paint, descriptions[i][j], X[3], X[4], y + 16 + j * 14, true);
                Fit(c, shaper, paint, Date(line.Movement.Date), X[4], X[5], y + 16);
                y += height;
            }
            if (y + 25 > Bottom) { EndPage(); StartPage(); }
            var last = canvas!;
            for (int col = 0; col < 5; col++) last.DrawRect(X[col], y, X[col+1] - X[col], 25, stroke);
            Fit(last, shaper, paint, Number(totals.Balance), X[0], X[1], y + 17);
            Fit(last, shaper, paint, Number(totals.Debits), X[1], X[2], y + 17);
            Fit(last, shaper, paint, Number(totals.Credits), X[2], X[3], y + 17);
            Fit(last, shaper, paint, "المجموع", X[3], X[4], y + 17);
            Text(last, shaper, paint, "عضو اللجنة", 155, 735, true); Text(last, shaper, paint, "عضو اللجنة", 440, 735, true);
            for (int j = 0; j < firstName.Count; j++) Text(last, shaper, paint, firstName[j], 440, 755 + j * 14, true);
            for (int j = 0; j < secondName.Count; j++) Text(last, shaper, paint, secondName[j], 155, 755 + j * 14, true);
            EndPage(); pdf.Close();
        }
        var bytes = memory.ToArray();
        if (bytes.Length < 8 || !bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8)) throw new InvalidDataException("تعذّر التحقق من ملف الكشف.");
        return bytes;
    }
    public static void SaveDraft(BankPeriod period, string fontPath, string destination)
    {
        var target = Path.GetFullPath(destination);
        if (!string.Equals(Path.GetExtension(target), ".pdf", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("اختر ملفاً بالامتداد PDF.");
        var directory = Path.GetDirectoryName(target) ?? throw new InvalidDataException("مجلد الحفظ غير صالح.");
        var bytes = Generate(period, fontPath);
        var temp = Path.Combine(directory, ".salary-reconciliation-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { file.Write(bytes); file.Flush(true); }
            using (var file = File.OpenRead(temp)) if (!SHA256.HashData(file).AsSpan().SequenceEqual(SHA256.HashData(bytes))) throw new InvalidDataException("تعذّر التحقق من الكشف المحفوظ.");
            File.Move(temp, target, true);
        }
        finally { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
