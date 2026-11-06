using System.Security.Cryptography;
using System.Text.Json;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Writer;

namespace SalaryLocal.Core;

/// جمع صفحات مولّدة ومدققة دون إعادة رسمها أو ضغطها كصور.
public static class LocalLetterPdfAssembly
{
    public const int MaximumBytes = 120 * 1024 * 1024;
    public const int MaximumPages = 600;
    private static string PageProof(Page page)
    {
        // تُقارن الحروف ومواضعها والصور والحدود قبل الدمج وبعده، لا عدد الصفحات وحده.
        double Round(double v) => Math.Round(v, 4);
        object Rect(UglyToad.PdfPig.Core.PdfRectangle r) => new { L = Round(r.Left), B = Round(r.Bottom), R = Round(r.Right), T = Round(r.Top) };
        var evidence = new {
            Width = Round(page.Width), Height = Round(page.Height), Rotation = page.Rotation.Value,
            Crop = Rect(page.CropBox.Bounds), Media = Rect(page.MediaBox.Bounds),
            Letters = page.Letters.Select(l => new { l.Value, l.FontName, Size = Round(l.PointSize), Box = Rect(l.GlyphRectangle), l.RenderingMode }).ToArray(),
            Images = page.GetImages().Select(i => new { i.WidthInSamples, i.HeightInSamples, i.BitsPerComponent, Box = Rect(i.Bounds), Sha256 = Convert.ToHexString(SHA256.HashData(i.RawMemory.Span)) }).ToArray()
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(evidence)));
    }
    public static byte[] AppendNames(byte[] letter, byte[] names)
    {
        ArgumentNullException.ThrowIfNull(letter); ArgumentNullException.ThrowIfNull(names);
        if (letter.Length == 0 || names.Length == 0 || (long)letter.Length + names.Length > MaximumBytes)
            throw new InvalidDataException("حجم الكتاب وقائمة الأسماء يتجاوز حد الدمج الآمن.");
        try
        {
            using var first = PdfDocument.Open(letter); using var list = PdfDocument.Open(names);
            if (first.NumberOfPages != 1 || list.NumberOfPages < 1 || list.NumberOfPages + 1 > MaximumPages)
                throw new InvalidDataException("يلزم كتاب من صفحة واحدة وقائمة أسماء ضمن حد الصفحات؛ لم يُحذف أي محتوى.");
            var proofs = first.GetPages().Concat(list.GetPages()).Select(PageProof).ToArray();
            var output = PdfMerger.Merge(new byte[][] { letter, names });
            if (output.Length > MaximumBytes) throw new InvalidDataException("حجم PDF المجمّع يتجاوز الحد الآمن.");
            using var merged = PdfDocument.Open(output);
            if (merged.NumberOfPages != proofs.Length || !merged.GetPages().Select(PageProof).SequenceEqual(proofs))
                throw new InvalidDataException("فشل فحص صفحات الكتاب والأسماء بعد الدمج؛ لم تُعتمد المسودة.");
            return output;
        }
        catch (Exception ex) when (ex is not InvalidDataException and not OutOfMemoryException and not OperationCanceledException)
        { throw new InvalidDataException("تعذّر جمع الكتاب والأسماء والتحقق منهما؛ لم يُحفظ ملف ناقص.", ex); }
    }
}
