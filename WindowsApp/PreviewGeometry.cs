namespace SalaryWindows;

// حدّ حجم الصورة يمنع استهلاك الذاكرة عند معاينة صفحة PDF ذات أبعاد غير عادية.
internal static class PreviewGeometry
{
    public static (uint Width, uint Height) Fit(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            throw new InvalidDataException("أبعاد الصفحة غير صالحة.");
        var scale = Math.Min(1400.0 / width, 2000.0 / height);
        if (!double.IsFinite(scale) || scale <= 0) throw new InvalidDataException("أبعاد الصفحة غير صالحة.");
        return ((uint)Math.Clamp(Math.Round(width * scale), 1, 1400), (uint)Math.Clamp(Math.Round(height * scale), 1, 2000));
    }
}
