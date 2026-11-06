using System.Buffers.Binary;
using SalaryDesktop.Core;
using SkiaSharp;

namespace SalaryLocal.Core;

/// لقطة ثابتة للصورة المختارة؛ لا تُقرأ ملفات المستخدم مرة ثانية عند الحفظ.
public sealed class LocalArchiveImage
{
    internal byte[] Encoded { get; }
    internal SKEncodedOrigin Origin { get; }
    public string SourceName { get; }
    public int Width { get; }
    public int Height { get; }
    public int ClockwiseTurns { get; }
    public int EncodedLength => Encoded.Length;
    internal LocalArchiveImage(byte[] bytes, string name, int width, int height, SKEncodedOrigin origin, int turns = 0)
    { Encoded = bytes; SourceName = name; Width = width; Height = height; Origin = origin; ClockwiseTurns = turns; }
    public LocalArchiveImage RotateClockwise() => new(Encoded, SourceName, Width, Height, Origin, (ClockwiseTurns + 1) % 4);
}

/// تحويل محلي فقط، بلا تعرّف نصي أو تغيير الملف الأصلي. حدود الفك تُفحص قبل تخصيص البكسلات.
public static class LocalArchiveImages
{
    public const int MaxPages = 3;
    public const int MaxEncodedBytes = 20_000_000;
    public const int MaxInputSide = 12_000;
    public const long MaxDecodedPixels = 40_000_000;
    public const float PageWidth = 595, PageHeight = 842, Margin = 18;
    public const string Limits = "من صورة إلى ثلاث صور JPEG أو PNG عادية حتى ٨ بت للقناة؛ حتى ٢٠ ميغابايت و٤٠ مليون بكسل للصورة، وأقصى بُعد ١٢٠٠٠ بكسل. تُحفظ أبعاد الصورة دون خفض دقتها، بضغط غير متلف على A4 دون قصّ؛ المعاينة فقط مصغّرة.";

    public static IReadOnlyList<LocalArchiveImage> ReadFiles(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        CheckCount(paths.Count);
        var result = new List<LocalArchiveImage>(paths.Count);
        foreach (var path in paths)
        {
            string extension = Path.GetExtension(path);
            if (!new[] { ".jpg", ".jpeg", ".png" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("اختر صور JPEG أو PNG فقط، أو استخدم إضافة PDF.");
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is <= 0 or > MaxEncodedBytes) throw new InvalidDataException("ملف الصورة فارغ أو يتجاوز ٢٠ ميغابايت.");
            var bytes = new byte[checked((int)input.Length)];
            input.ReadExactly(bytes);
            if (input.ReadByte() != -1) throw new InvalidDataException("تغيّر حجم الصورة أثناء قراءتها؛ أعد اختيارها.");
            result.Add(FromOwnedBytes(bytes, Path.GetFileName(path)));
        }
        return result;
    }

    public static LocalArchiveImage FromBytes(byte[] bytes, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length is <= 0 or > MaxEncodedBytes) throw new InvalidDataException("ملف الصورة فارغ أو يتجاوز ٢٠ ميغابايت.");
        return FromOwnedBytes(bytes.ToArray(), Path.GetFileName(sourceName));
    }

    private static LocalArchiveImage FromOwnedBytes(byte[] bytes, string name)
    {
        CheckContainer(bytes);
        using var stream = new SKMemoryStream(bytes);
        using var codec = OpenCodec(stream);
        var result = new LocalArchiveImage(bytes, name, codec.Info.Width, codec.Info.Height, codec.EncodedOrigin);
        // قراءة الترويسة وحدها لا تثبت سلامة الصورة؛ تُرفض الصورة الناقصة قبل عرضها للمراجعة.
        using var decoded = Decode(codec);
        return result;
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(i => {
        uint value = (uint)i; for (int n = 0; n < 8; n++) value = (value & 1) != 0 ? 0xEDB88320U ^ (value >> 1) : value >> 1; return value;
    }).ToArray();

    private static void CheckContainer(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            int offset = 8; bool header = false, pixels = false, ended = false;
            while (offset <= bytes.Length - 12)
            {
                uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
                if (length > bytes.Length - offset - 12) throw new InvalidDataException("ملف PNG غير مكتمل.");
                int size = (int)length; var tag = bytes.AsSpan(offset + 4, 4);
                if (!header)
                {
                    if (!tag.SequenceEqual("IHDR"u8) || size != 13) throw new InvalidDataException("ترويسة PNG غير صالحة.");
                    if (bytes[offset + 16] > 8) throw new InvalidDataException("صور PNG ذات عمق ١٦ بت غير مدعومة؛ صدّر نسخة JPEG أو PNG بعمق ٨ بت للمراجعة دون تغيير الأصل.");
                    header = true;
                }
                if (tag.SequenceEqual("acTL"u8)) throw new InvalidDataException("صور PNG المتحركة غير مدعومة؛ اختر صورة ثابتة لكل صفحة.");
                uint crc = uint.MaxValue;
                foreach (byte value in bytes.AsSpan(offset + 4, size + 4)) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
                if ((crc ^ uint.MaxValue) != BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + size + 8, 4))) throw new InvalidDataException("ملف PNG تالف (فشل فحص سلامة البيانات).");
                if (tag.SequenceEqual("IDAT"u8)) pixels = true;
                offset += size + 12;
                if (tag.SequenceEqual("IEND"u8)) { ended = size == 0 && offset == bytes.Length; break; }
            }
            if (!header || !pixels || !ended) throw new InvalidDataException("ملف PNG غير مكتمل.");
        }
        else if (bytes.Length >= 2 && bytes[0] == 255 && bytes[1] == 216)
        {
            // رفض الدقة اللونية غير المدعومة بدلاً من خفضها بصمت أثناء الفك.
            int offset = 2;
            while (offset < bytes.Length - 3 && bytes[offset] == 255)
            {
                while (offset < bytes.Length && bytes[offset] == 255) offset++;
                if (offset >= bytes.Length) break;
                byte marker = bytes[offset++];
                if (marker is 0xDA or 0xD9) break;
                if (marker is 0x01 or >= 0xD0 and <= 0xD7) continue;
                if (offset > bytes.Length - 2) break;
                int size = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));
                if (size < 2 || size > bytes.Length - offset) break;
                if (marker is >= 0xC0 and <= 0xCF && marker is not 0xC4 and not 0xC8 and not 0xCC && size > 2 && bytes[offset + 2] != 8)
                    throw new InvalidDataException("دقة ألوان JPEG غير مدعومة؛ اختر صورة عادية بعمق ٨ بت للقناة.");
                offset += size;
            }
        }
    }

    private static SKCodec OpenCodec(SKStream stream)
    {
        var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("تعذّر قراءة الصورة؛ الملف تالف أو غير مدعوم.");
        try
        {
            if (codec.EncodedFormat is not SKEncodedImageFormat.Jpeg and not SKEncodedImageFormat.Png)
                throw new InvalidDataException("يقبل مسار الصور JPEG وPNG فقط؛ صيغة الصورة الفعلية غير مدعومة.");
            var info = codec.Info;
            if (info.Width <= 0 || info.Height <= 0 || info.Width > MaxInputSide || info.Height > MaxInputSide || (long)info.Width * info.Height > MaxDecodedPixels)
                throw new InvalidDataException("أبعاد الصورة تتجاوز حد القراءة الآمنة (٤٠ مليون بكسل وأقصى بُعد ١٢٠٠٠). قلّل دقة المسح أو حجم الصورة أولاً.");
            if (codec.FrameCount > 1) throw new InvalidDataException("الصور المتحركة غير مدعومة. اختر صورة ثابتة لكل صفحة.");
            return codec;
        }
        catch { codec.Dispose(); throw; }
    }

    private static SKBitmap Decode(SKCodec codec)
    {
        // أربع بايتات لكل بكسل، وصورة واحدة فقط قيد الفك في كل خطوة.
        var bitmap = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        try
        {
            if (bitmap.GetPixels() == IntPtr.Zero || codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
                throw new InvalidDataException("الصورة تالفة أو غير مكتملة؛ لم تُقبل أي صفحة منها.");
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static void CheckCount(int count)
    {
        if (count is < 1 or > MaxPages) throw new InvalidDataException("يقبل الأرشيف من صفحة إلى ثلاث صفحات فقط.");
    }

    private static void DrawPage(SKCanvas canvas, LocalArchiveImage page)
    {
        using var stream = new SKMemoryStream(page.Encoded);
        using var codec = OpenCodec(stream);
        using var decoded = Decode(codec);
        // لا تُخفض دقة المصدر؛ تصغير المعاينة لا يغيّر الصورة المضمّنة في PDF.
        var bitmap = decoded;
        using var image = SKImage.FromBitmap(bitmap);
        bool swapped = (int)page.Origin >= 5;
        if (page.ClockwiseTurns % 2 != 0) swapped = !swapped;
        float width = swapped ? bitmap.Height : bitmap.Width, height = swapped ? bitmap.Width : bitmap.Height;
        float scale = Math.Min((PageWidth - 2 * Margin) / width, (PageHeight - 2 * Margin) / height);
        canvas.Clear(SKColors.White);
        canvas.Save();
        canvas.Translate(PageWidth / 2, PageHeight / 2);
        canvas.RotateDegrees(90 * page.ClockwiseTurns);
        // اتجاهات EXIF الثمانية تشمل الانعكاس، وليس التدوير وحده.
        switch (page.Origin)
        {
            case SKEncodedOrigin.TopRight: canvas.Scale(-1, 1); break;
            case SKEncodedOrigin.BottomRight: canvas.RotateDegrees(180); break;
            case SKEncodedOrigin.BottomLeft: canvas.Scale(1, -1); break;
            case SKEncodedOrigin.LeftTop: canvas.RotateDegrees(90); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.RightTop: canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom: canvas.RotateDegrees(90); canvas.Scale(-1, 1); break;
            case SKEncodedOrigin.LeftBottom: canvas.RotateDegrees(270); break;
        }
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
        canvas.DrawImage(image, new SKRect(-bitmap.Width * scale / 2, -bitmap.Height * scale / 2, bitmap.Width * scale / 2, bitmap.Height * scale / 2), paint);
        canvas.Restore();
    }

    /// المعاينة تستخدم الرسم والهوامش والاتجاه نفسها المستخدمة عند إنشاء PDF.
    public static byte[] PagePreview(LocalArchiveImage page, int maxSide = 1200)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (maxSide is < 100 or > 1600) throw new ArgumentOutOfRangeException(nameof(maxSide));
        int width = (int)Math.Round(maxSide * PageWidth / PageHeight);
        using var bitmap = new SKBitmap(width, maxSide, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap)) { canvas.Scale(width / PageWidth, maxSide / PageHeight); DrawPage(canvas, page); }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static byte[] CreatePdf(IReadOnlyList<LocalArchiveImage> pages, int maxOutputBytes = (int)ArchivePdf.MaxBytes)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (maxOutputBytes is < 1 or > (int)ArchivePdf.MaxBytes) throw new ArgumentOutOfRangeException(nameof(maxOutputBytes));
        // تثبيت الترتيب عند بدء الإنشاء؛ تدوير الواجهة ينتج لقطة جديدة ولا يعدل هذه الصور.
        CheckCount(pages.Count);
        var selected = pages.ToArray();
        if (selected.Any(p => p is null)) throw new InvalidDataException("إحدى صفحات الكتاب مفقودة.");
        using var output = new BoundedPdfStream(maxOutputBytes);
        using (var document = PdfOutput.Create(output))
        {
            foreach (var page in selected)
            {
                var canvas = document.BeginPage(PageWidth, PageHeight);
                DrawPage(canvas, page); document.EndPage();
            }
            document.Close();
        }
        if (output.ExceededLimit) throw new InvalidDataException("تجاوز PDF حد الحجم المسموح؛ لم يُنشأ ملف صالح للحفظ. قلّل حجم صور المصدر في نسخة منفصلة ثم راجعها من جديد.");
        var bytes = output.ToArray();
        if (ArchivePdf.Pages(bytes) != selected.Length) throw new InvalidDataException("تعذّر التحقق من عدد صفحات PDF بعد الإنشاء.");
        return bytes;
    }

    private sealed class BoundedPdfStream(int maximum) : MemoryStream
    {
        public bool ExceededLimit { get; private set; }
        // لا يُرمى استثناء من استدعاء Skia الأصلي؛ يُوقف التخزين ويُرفض الناتج بعد إغلاق المستند.
        private bool Fits(long count) { ExceededLimit |= Position > maximum - count; return !ExceededLimit; }
        public override void Write(byte[] buffer, int offset, int count) { if (Fits(count)) base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { if (Fits(buffer.Length)) base.Write(buffer); }
        public override void WriteByte(byte value) { if (Fits(1)) base.WriteByte(value); }
        public override void SetLength(long value) { ExceededLimit |= value > maximum; if (!ExceededLimit) base.SetLength(value); }
    }
}
