using SkiaSharp;
using UglyToad.PdfPig;

namespace SalaryDesktop.Core;

/// صفحة مصوّرة للأرشيف (من الماسح أو ملف صورة) مع عدد أرباع اللفات باتجاه عقارب الساعة.
public sealed record ArchiveImage(byte[] Bytes, int Turns = 0);

/// ملفات كتب الأرشيف بقواعد الآيفون نفسها (OfficialBooks.swift): من صفحة إلى ثلاث صفحات، والصور تُجمع في PDF
/// بصفحات A4 وهامش ١٨ نقطة وأقصى بُعد ٣٠٠٠ بكسل، حتى يقبل الآيفون الكتاب عند استرجاع النسخة.
public static class ArchivePdf
{
    public const int MaxPages = 3;
    public const long MaxBytes = 60_000_000;
    private const float PageWidth = 595, PageHeight = 842, Margin = 18;
    private const int MaxPixels = 3000;
    public const string PagesRule = "يقبل الأرشيف كتاباً من صفحة إلى ثلاث صفحات (نفس حد الآيفون).";

    /// يتحقق من الملف ويعيد عدد صفحاته.
    public static int Pages(byte[] pdf)
    {
        if (pdf.Length < 5 || !pdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8)) throw new InvalidDataException("الملف ليس PDF صالحاً.");
        if (pdf.Length > MaxBytes) throw new InvalidDataException("ملف الكتاب كبير جداً (أكثر من ٦٠ ميغابايت).");
        int count;
        try { using var document = PdfDocument.Open(pdf); count = document.NumberOfPages; }
        catch (Exception ex) when (ex is not InvalidDataException and not OutOfMemoryException)
        { throw new InvalidDataException("تعذّر قراءة ملف PDF؛ قد يكون تالفاً أو محمياً بكلمة سر.", ex); }
        if (count is < 1 or > MaxPages) throw new InvalidDataException($"الملف فيه {count:N0} صفحة. {PagesRule}");
        return count;
    }

    private static int OriginTurns(SKEncodedOrigin origin) => origin switch
    {
        SKEncodedOrigin.RightTop => 1, SKEncodedOrigin.BottomRight => 2, SKEncodedOrigin.LeftBottom => 3, _ => 0,
    };

    /// يتحقق أن البيانات صورة يمكن قراءتها (JPG أو PNG أو BMP أو غيرها مما تدعمه Skia).
    public static void ValidateImage(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxBytes) throw new InvalidDataException("ملف الصورة فارغ أو كبير جداً.");
        using var codec = SKCodec.Create(new SKMemoryStream(bytes)) ?? throw new InvalidDataException("الملف ليس صورة مدعومة.");
        if (codec.Info.Width <= 0 || codec.Info.Height <= 0) throw new InvalidDataException("أبعاد الصورة غير صالحة.");
    }

    /// صورة مصغّرة PNG للمراجعة قبل الحفظ، بنفس الاتجاه الذي سيظهر في PDF.
    public static byte[] Thumbnail(ArchiveImage page, int maxSide)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(page.Bytes)) ?? throw new InvalidDataException("الملف ليس صورة مدعومة.");
        var turns = ((OriginTurns(codec.EncodedOrigin) + page.Turns) % 4 + 4) % 4;
        using var decoded = SKBitmap.Decode(codec) ?? throw new InvalidDataException("تعذّر قراءة الصورة.");
        var scale = Math.Min(1.0, (double)maxSide / Math.Max(decoded.Width, decoded.Height));
        int w = Math.Max(1, (int)(decoded.Width * scale)), h = Math.Max(1, (int)(decoded.Height * scale));
        using var small = decoded.Resize(new SKImageInfo(w, h), SKFilterQuality.Medium) ?? throw new InvalidDataException("تعذّر تصغير الصورة.");
        int rw = turns % 2 == 1 ? h : w, rh = turns % 2 == 1 ? w : h;
        using var rotated = new SKBitmap(rw, rh);
        using (var canvas = new SKCanvas(rotated))
        {
            canvas.Clear(SKColors.White);
            canvas.Translate(rw / 2f, rh / 2f); canvas.RotateDegrees(90 * turns);
            canvas.DrawBitmap(small, -w / 2f, -h / 2f);
        }
        using var image = SKImage.FromBitmap(rotated);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// يجمع الصور في PDF بصفحة A4 لكل صورة (مثل الآيفون)، مع تطبيق اتجاه الكاميرا والتدوير المختار.
    public static byte[] FromImages(IReadOnlyList<ArchiveImage> pages)
    {
        if (pages.Count is < 1 or > MaxPages) throw new InvalidDataException(PagesRule);
        using var output = new MemoryStream();
        var metadata = SKDocumentPdfMetadata.Default;
        metadata.RasterDpi = 300; metadata.EncodingQuality = 88; metadata.Producer = "تطبيق الرواتب";
        using (var document = SKDocument.CreatePdf(output, metadata) ?? throw new InvalidDataException("تعذّر إنشاء ملف PDF."))
        {
            foreach (var page in pages)
            {
                using var codec = SKCodec.Create(new SKMemoryStream(page.Bytes)) ?? throw new InvalidDataException("إحدى الصفحات ليست صورة مدعومة.");
                var turns = ((OriginTurns(codec.EncodedOrigin) + page.Turns) % 4 + 4) % 4;
                using var decoded = SKBitmap.Decode(codec) ?? throw new InvalidDataException("تعذّر قراءة إحدى الصور.");
                var shrink = Math.Min(1.0, (double)MaxPixels / Math.Max(decoded.Width, decoded.Height));
                using var resized = shrink < 1 ? decoded.Resize(new SKImageInfo(Math.Max(1, (int)(decoded.Width * shrink)), Math.Max(1, (int)(decoded.Height * shrink))), SKFilterQuality.High) : null;
                var bitmap = resized ?? decoded;
                using var image = SKImage.FromBitmap(bitmap);
                float w = bitmap.Width, h = bitmap.Height;
                float rw = turns % 2 == 1 ? h : w, rh = turns % 2 == 1 ? w : h;
                var scale = Math.Min((PageWidth - Margin * 2) / rw, (PageHeight - Margin * 2) / rh);
                var canvas = document.BeginPage(PageWidth, PageHeight);
                canvas.Clear(SKColors.White);
                canvas.Save();
                canvas.Translate(PageWidth / 2, PageHeight / 2);
                canvas.RotateDegrees(90 * turns);
                using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
                canvas.DrawImage(image, new SKRect(-w * scale / 2, -h * scale / 2, w * scale / 2, h * scale / 2), paint);
                canvas.Restore();
                document.EndPage();
            }
            document.Close();
        }
        var bytes = output.ToArray();
        if (Pages(bytes) != pages.Count) throw new InvalidDataException("تعذّر التحقق من صفحات الكتاب بعد تجميعها.");
        return bytes;
    }
}
