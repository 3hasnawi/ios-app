using SkiaSharp;

namespace SalaryDesktop.Core;

/// إنشاء ملفات PDF بأعلى جودة (طلب المالك): النصوص والخطوط متجهة أصلاً، والصور (الشعار والعلامة المائية)
/// تُضمَّن بلا ضغط مُتلف، وأي عنصر يحتاج تحويلاً إلى صورة يُرسم بدقة 600 نقطة في البوصة بدل 72.
public static class PdfOutput
{
    public const float RasterDpi = 600;
    public static SKDocument Create(Stream output)
    {
        var metadata = SKDocumentPdfMetadata.Default;
        metadata.RasterDpi = RasterDpi;
        metadata.EncodingQuality = 101;   // أكثر من 100 = بلا ضغط مُتلف للصور.
        metadata.Producer = "تطبيق الرواتب";
        return SKDocument.CreatePdf(output, metadata) ?? throw new InvalidDataException("تعذّر إنشاء ملف PDF.");
    }
}
