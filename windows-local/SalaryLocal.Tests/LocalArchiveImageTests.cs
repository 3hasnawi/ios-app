using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using SalaryDesktop.Core;
using SalaryLocal.Core;
using SkiaSharp;
using UglyToad.PdfPig;

internal static class LocalArchiveImageTests
{
    private static int checks;
    private static readonly SKColor[] Colors = [SKColors.Red, SKColors.Lime, SKColors.Blue, SKColors.Yellow];
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { checks++; return; }
        throw new Exception("Invalid archive image was accepted");
    }

    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "local-archive-images-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            byte[] png = Quadrants(SKEncodedImageFormat.Png), jpeg = Quadrants(SKEncodedImageFormat.Jpeg);
            string firstPath = Path.Combine(root, "synthetic-1.png"), secondPath = Path.Combine(root, "synthetic-2.jpg");
            File.WriteAllBytes(firstPath, png); File.WriteAllBytes(secondPath, Exif(jpeg, 6));
            byte[] original1 = SHA256.HashData(File.ReadAllBytes(firstPath)), original2 = SHA256.HashData(File.ReadAllBytes(secondPath));
            var selected = LocalArchiveImages.ReadFiles([firstPath, secondPath]);
            Check(selected.Count == 2 && selected[0].Width == 120 && selected[0].Height == 80, "Local image selection");
            byte[] pdf = LocalArchiveImages.CreatePdf(selected);
            // Skia يكمّم MediaBox إلى بكسلات ٦٠٠dpi: نصف بكسل = ٠٫٠٦ نقطة، مع هامش خطأ float صغير.
            double pageTolerance = 72d / PdfOutput.RasterDpi / 2 + .001;
            using (var document = PdfDocument.Open(pdf))
            {
                Check(document.NumberOfPages == 2, "Image PDF page count");
                foreach (var page in document.GetPages())
                {
                    Check(Math.Abs(page.Width - 595) <= pageTolerance && Math.Abs(page.Height - 842) <= pageTolerance, $"A4 fixed page size: actual {page.Width:R} × {page.Height:R} pt, expected 595 × 842 pt ± {pageTolerance:R}");
                    var image = page.GetImages().Single();
                    Check(image.WidthInSamples == 120 && image.HeightInSamples == 80, "Original image dimensions retained");
                    Check(image.Bounds.Left >= 18 - pageTolerance && image.Bounds.Right <= page.Width - 18 + pageTolerance && image.Bounds.Bottom >= 18 - pageTolerance && image.Bounds.Top <= page.Height - 18 + pageTolerance, $"Whole image fitted inside margins without clipping: {image.Bounds}");
                }
                var embedded = document.GetPage(1).GetImages().Single();
                Check(embedded.TryGetPng(out var reconstructed), "Lossless PDF image can be recovered");
                using var actual = SKBitmap.Decode(reconstructed); using var expected = SKBitmap.Decode(png);
                Check(actual is not null && expected is not null && actual.Pixels.SequenceEqual(expected.Pixels), "PNG pixels preserved without JPEG recompression");
            }
            Check(original1.SequenceEqual(SHA256.HashData(File.ReadAllBytes(firstPath))) && original2.SequenceEqual(SHA256.HashData(File.ReadAllBytes(secondPath))), "Original files untouched");
            // ملفات المصدر يمكن أن تتغير بعد المراجعة، لكن الحفظ يستخدم اللقطة التي راجعها المستخدم.
            File.WriteAllBytes(firstPath, [1, 2, 3]);
            Check(ArchivePdf.Pages(LocalArchiveImages.CreatePdf(selected)) == 2, "Reviewed snapshots do not reread changed files");
            byte[] mutable = png.ToArray(); var owned = LocalArchiveImages.FromBytes(mutable, "synthetic.png"); Array.Fill(mutable, (byte)0);
            Check(ArchivePdf.Pages(LocalArchiveImages.CreatePdf([owned])) == 1, "Input byte array is owned safely");
            Check(owned.ClockwiseTurns == 0 && owned.RotateClockwise().ClockwiseTurns == 1, "Rotation is immutable");
            var rotate = owned; for (int i = 0; i < 4; i++) rotate = rotate.RotateClockwise();
            Check(LocalArchiveImages.PagePreview(owned, 842).SequenceEqual(LocalArchiveImages.PagePreview(rotate, 842)), "Four turns restore preview");
            OrientationChecks(jpeg);
            RejectionChecks(png, jpeg, root);
            LargeResolutionCheck();
            StorageChecks(selected, root);
            string? evidence = Environment.GetEnvironmentVariable("SALARY_ARCHIVE_IMAGE_EVIDENCE");
            if (!string.IsNullOrWhiteSpace(evidence)) WriteEvidence(evidence);
            Console.WriteLine($"Local archive images: {checks} assertions passed (image/PDF core only; WPF and scanner hardware not exercised).");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void OrientationChecks(byte[] jpeg)
    {
        int[][] expected = [[0, 1, 2, 3], [1, 0, 3, 2], [3, 2, 1, 0], [2, 3, 0, 1], [0, 2, 1, 3], [2, 0, 3, 1], [3, 1, 2, 0], [1, 3, 0, 2]];
        for (int origin = 1; origin <= 8; origin++)
        {
            var page = LocalArchiveImages.FromBytes(Exif(jpeg, origin), $"exif-{origin}.jpg");
            for (int turn = 0; turn < 4; turn++)
            {
                using var preview = SKBitmap.Decode(LocalArchiveImages.PagePreview(page, 842));
                Check(preview.Width == 595 && preview.Height == 842, "Preview is A4");
                var samples = new[] { preview.GetPixel(217, 341), preview.GetPixel(377, 341), preview.GetPixel(217, 501), preview.GetPixel(377, 501) };
                for (int corner = 0; corner < 4; corner++) Check(Near(samples[corner], Colors[expected[origin - 1][corner]]), $"EXIF {origin}, additional turn {turn}, corner {corner}");
                Check(preview.GetPixel(0, 0) == SKColors.White && preview.GetPixel(594, 841) == SKColors.White, "Preview margins remain white");
                page = page.RotateClockwise();
                var old = expected[origin - 1]; expected[origin - 1] = [old[2], old[0], old[3], old[1]];
            }
        }
    }

    private static bool Near(SKColor a, SKColor b) => Math.Abs(a.Red - b.Red) < 25 && Math.Abs(a.Green - b.Green) < 25 && Math.Abs(a.Blue - b.Blue) < 25;

    private static void RejectionChecks(byte[] png, byte[] jpeg, string root)
    {
        Reject(() => LocalArchiveImages.ReadFiles([]));
        Reject(() => LocalArchiveImages.ReadFiles(["1.png", "2.png", "3.png", "4.png"]));
        Reject(() => LocalArchiveImages.ReadFiles([Path.Combine(root, "unsupported.gif")]));
        Reject(() => LocalArchiveImages.FromBytes([], "empty.png"));
        Reject(() => LocalArchiveImages.FromBytes(new byte[LocalArchiveImages.MaxEncodedBytes + 1], "large.png"));
        Reject(() => LocalArchiveImages.FromBytes("not an image"u8.ToArray(), "bad.jpg"));
        Reject(() => LocalArchiveImages.FromBytes(jpeg[..(jpeg.Length / 2)], "partial.jpg"));
        Reject(() => LocalArchiveImages.FromBytes(png[..^10], "partial.png"));
        var corrupt = png.ToArray(); corrupt[^20] ^= 1;
        Reject(() => LocalArchiveImages.FromBytes(corrupt, "checksum.png"));
        var depth16 = png.ToArray(); depth16[24] = 16; FixHeaderCrc(depth16);
        Reject(() => LocalArchiveImages.FromBytes(depth16, "16bit.png"));
        var giant = png.ToArray(); BinaryPrimitives.WriteInt32BigEndian(giant.AsSpan(16, 4), LocalArchiveImages.MaxInputSide + 1); FixHeaderCrc(giant);
        Reject(() => LocalArchiveImages.FromBytes(giant, "giant-side.png"));
        var manyPixels = png.ToArray(); BinaryPrimitives.WriteInt32BigEndian(manyPixels.AsSpan(16, 4), 7000); BinaryPrimitives.WriteInt32BigEndian(manyPixels.AsSpan(20, 4), 7000); FixHeaderCrc(manyPixels);
        Reject(() => LocalArchiveImages.FromBytes(manyPixels, "too-many-pixels.png"));
        using (var animated = new MemoryStream())
        {
            animated.Write(png, 0, 33); var animation = new byte[8]; BinaryPrimitives.WriteInt32BigEndian(animation.AsSpan(0, 4), 2); WriteChunk(animated, "acTL", animation); animated.Write(png, 33, png.Length - 33);
            Reject(() => LocalArchiveImages.FromBytes(animated.ToArray(), "animated.png"));
        }
        var image = LocalArchiveImages.FromBytes(png, "good.png");
        Reject(() => LocalArchiveImages.CreatePdf([])); Reject(() => LocalArchiveImages.CreatePdf([image, image, image, image]));
        Reject(() => LocalArchiveImages.CreatePdf([image], maxOutputBytes: 64));
        Check(ArchivePdf.Pages(LocalArchiveImages.CreatePdf([image])) == 1, "Output limit is a managed rejection and does not break later PDF generation");
        string oversized = Path.Combine(root, "oversized.png");
        using (var file = File.Create(oversized)) file.SetLength(LocalArchiveImages.MaxEncodedBytes + 1L);
        Reject(() => LocalArchiveImages.ReadFiles([oversized]));
    }

    private static void LargeResolutionCheck()
    {
        // صورة A4 وهمية بدقة ٦٠٠ نقطة/بوصة، تُولّد صفاً صفاً دون مصفوفة اختبار ضخمة.
        var image = LocalArchiveImages.FromBytes(FlatPng(4961, 7016), "synthetic-a4-600dpi.png");
        using var document = PdfDocument.Open(LocalArchiveImages.CreatePdf([image]));
        var embedded = document.GetPage(1).GetImages().Single();
        Check(embedded.WidthInSamples == 4961 && embedded.HeightInSamples == 7016, "600 DPI A4 retains all original samples, with no 3000px downsampling");
    }

    private static void StorageChecks(IReadOnlyList<LocalArchiveImage> pages, string root)
    {
        using var store = new LocalPayrollStore(Path.Combine(root, "store")); string initial = store.GetStatus().RevisionId;
        byte[] first = LocalArchiveImages.CreatePdf(pages);
        store.AddArchiveBook("كتاب صور وهمي", "2026-11", "جهة وهمية", first, initial);
        var book = store.GetSnapshot().Books.Single(); string oldRevision = store.GetStatus().RevisionId;
        byte[] replacement = LocalArchiveImages.CreatePdf([pages[1].RotateClockwise(), pages[0], pages[1]]);
        store.ReplaceArchivePdf(book.Id, replacement, oldRevision);
        var changed = store.GetSnapshot().Books.Single();
        Check(changed.Id == book.Id && changed.CreatedAt == book.CreatedAt && changed.Pages == 3, "Image replacement preserves book identity and history");
        Check(store.ReadAttachment(LocalPayrollStore.ArchiveAttachmentKey(book.Id)).SequenceEqual(replacement), "Reviewed image PDF stored exactly");
        Reject(() => store.ReplaceArchivePdf(book.Id, first, oldRevision));
        Check(LocalPayrollStore.ValidateDatabase(store.DatabasePath).RevisionId == store.GetStatus().RevisionId && store.GetHistory().Count >= 2, "Image mutations use validated immutable store history");
    }

    private static byte[] Quadrants(SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(120, 80); using var canvas = new SKCanvas(bitmap); using var paint = new SKPaint();
        for (int i = 0; i < 4; i++) { paint.Color = Colors[i]; canvas.DrawRect((i % 2) * 60, (i / 2) * 40, 60, 40, paint); }
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(format, 100); return data.ToArray();
    }

    private static byte[] Exif(byte[] jpeg, int origin)
    {
        // EXIF صغير مولّد للاختبار: TIFF little-endian، وحقل الاتجاه فقط.
        byte[] payload = [69, 120, 105, 102, 0, 0, 73, 73, 42, 0, 8, 0, 0, 0, 1, 0, 18, 1, 3, 0, 1, 0, 0, 0, (byte)origin, 0, 0, 0, 0, 0, 0, 0];
        using var output = new MemoryStream(); output.Write(jpeg, 0, 2); output.Write([255, 225, 0, (byte)(payload.Length + 2)]); output.Write(payload); output.Write(jpeg, 2, jpeg.Length - 2); return output.ToArray();
    }

    private static byte[] FlatPng(int width, int height)
    {
        using var output = new MemoryStream(); output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), height); header[8] = 8; header[9] = 2; WriteChunk(output, "IHDR", header);
        using var pixels = new MemoryStream();
        using (var zipped = new ZLibStream(pixels, CompressionLevel.Optimal, true))
        {
            var row = new byte[width * 3 + 1]; Array.Fill(row, (byte)255); row[0] = 0;
            for (int y = 0; y < height; y++) zipped.Write(row);
        }
        WriteChunk(output, "IDAT", pixels.ToArray()); WriteChunk(output, "IEND", []); return output.ToArray();
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue; foreach (byte b in bytes) { crc ^= b; for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? 0xEDB88320U ^ (crc >> 1) : crc >> 1; } return crc ^ uint.MaxValue;
    }
    private static void FixHeaderCrc(byte[] png) => BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(29, 4), Crc(png.AsSpan(12, 17)));
    private static void WriteChunk(Stream output, string name, byte[] bytes)
    {
        byte[] size = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(size, bytes.Length); output.Write(size);
        byte[] tag = System.Text.Encoding.ASCII.GetBytes(name); output.Write(tag); output.Write(bytes);
        byte[] payload = new byte[4 + bytes.Length]; tag.CopyTo(payload, 0); bytes.CopyTo(payload, 4); BinaryPrimitives.WriteUInt32BigEndian(size, Crc(payload)); output.Write(size);
    }

    private static void WriteEvidence(string directory)
    {
        Directory.CreateDirectory(directory);
        byte[] portrait = SyntheticDocument(720, 1020, 1, SKEncodedImageFormat.Png), landscape = SyntheticDocument(1020, 720, 2, SKEncodedImageFormat.Jpeg);
        byte[] turned = Exif(landscape, 6);
        File.WriteAllBytes(Path.Combine(directory, "synthetic-portrait.png"), portrait); File.WriteAllBytes(Path.Combine(directory, "synthetic-landscape-exif6.jpg"), turned);
        var first = LocalArchiveImages.FromBytes(portrait, "synthetic-portrait.png"); var second = LocalArchiveImages.FromBytes(turned, "synthetic-landscape-exif6.jpg");
        var pages = new[] { first, second, first.RotateClockwise().RotateClockwise() };
        File.WriteAllBytes(Path.Combine(directory, "synthetic-images-3pages.pdf"), LocalArchiveImages.CreatePdf(pages));
        for (int i = 0; i < pages.Length; i++) File.WriteAllBytes(Path.Combine(directory, $"synthetic-page-preview-{i + 1}.png"), LocalArchiveImages.PagePreview(pages[i]));
        // مصفوفة الاتجاهات لتدقيق بصري مستقل عن صورة مستند غير متماثلة.
        using var output = new SKBitmap(595 * 4, 842 * 2); using var canvas = new SKCanvas(output);
        byte[] quadrants = Quadrants(SKEncodedImageFormat.Jpeg);
        for (int i = 0; i < 8; i++)
        {
            using var preview = SKBitmap.Decode(LocalArchiveImages.PagePreview(LocalArchiveImages.FromBytes(Exif(quadrants, i + 1), $"exif-{i + 1}.jpg"), 842));
            canvas.DrawBitmap(preview, (i % 4) * 595, (i / 4) * 842);
        }
        using var grid = SKImage.FromBitmap(output); using var data = grid.Encode(SKEncodedImageFormat.Png, 100); File.WriteAllBytes(Path.Combine(directory, "synthetic-exif-1-through-8.png"), data.ToArray());
        File.WriteAllText(Path.Combine(directory, "evidence-scope.txt"), "Synthetic image conversion outputs only. PDF and preview generated by the Core implementation. No real document data. This is not a WPF screenshot or scanner hardware test. EXIF grid is 1,2,3,4 then 5,6,7,8 left-to-right.\n");
    }

    private static byte[] SyntheticDocument(int width, int height, int marker, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height); using var canvas = new SKCanvas(bitmap); canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Color = new SKColor(20, 47, 76), IsAntialias = true, StrokeWidth = 3, Style = SKPaintStyle.Stroke };
        canvas.DrawRect(8, 8, width - 16, height - 16, paint);
        paint.Style = SKPaintStyle.Fill; paint.Color = new SKColor(8, 127, 135); canvas.DrawRect(36, 40, width - 72, 55, paint);
        for (int n = 0; n < marker; n++) { paint.Color = SKColors.Orange; canvas.DrawCircle(60 + n * 44, 130, 15, paint); }
        for (int row = 0; row < 9; row++) { paint.Color = row % 2 == 0 ? new SKColor(230, 239, 246) : new SKColor(245, 248, 251); canvas.DrawRect(36, 180 + row * 40, width - 72, 36, paint); paint.Color = new SKColor(87, 107, 122); canvas.DrawRect(width / 2, 193 + row * 40, width / 2 - 66, 5, paint); }
        paint.Color = SKColors.Red; canvas.DrawRect(14, 14, 15, 15, paint); paint.Color = SKColors.Lime; canvas.DrawRect(width - 29, 14, 15, 15, paint); paint.Color = SKColors.Blue; canvas.DrawRect(14, height - 29, 15, 15, paint); paint.Color = SKColors.Yellow; canvas.DrawRect(width - 29, height - 29, 15, 15, paint);
        paint.Style = SKPaintStyle.Stroke; paint.Color = new SKColor(8, 127, 135); canvas.DrawCircle(width - 100, height - 90, 44, paint); canvas.DrawLine(40, height - 50, width / 2, height - 50, paint);
        using var image = SKImage.FromBitmap(bitmap); using var encoded = image.Encode(format, 100); return encoded.ToArray();
    }
}
