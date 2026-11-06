namespace SalaryLocal.Core;

/// تخطيط مستقل عن ويندوز: لا قصّ، لا تمديد، ولا تخمين لهوامش طابعة مجهولة.
public readonly record struct LocalPdfPageSize(double Width, double Height);
public readonly record struct LocalPdfPrintPlacement(double X, double Y, double Width, double Height, double Scale);
public static class LocalPdfPrintLayout
{
    public const long MaximumRasterPixels = 50_000_000;
    public static void ValidateRaster(int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > MaximumRasterPixels)
            throw new InvalidDataException("أبعاد الصفحة تتجاوز حد الرسم الآمن. لم تُخفض الدقة ولم تُقص الصفحة.");
    }
    public static LocalPdfPrintPlacement Fit(LocalPdfPageSize source, LocalPdfPageSize paper,
        double originX, double originY, double printableWidth, double printableHeight)
    {
        double[] values = [source.Width, source.Height, paper.Width, paper.Height, printableWidth, printableHeight];
        if (values.Any(v => !double.IsFinite(v) || v <= 0) || !double.IsFinite(originX) || !double.IsFinite(originY) || originX < 0 || originY < 0 || originX + printableWidth > paper.Width + .5 || originY + printableHeight > paper.Height + .5)
            throw new InvalidDataException("تعذّر التحقق من حجم الورق وهوامش الطابعة. اختر طابعة وإعداد ورق صالحين.");
        double scale = Math.Min(1, Math.Min(printableWidth / source.Width, printableHeight / source.Height));
        double width = source.Width * scale, height = source.Height * scale;
        return new(originX + (printableWidth - width) / 2, originY + (printableHeight - height) / 2, width, height, scale);
    }
}
