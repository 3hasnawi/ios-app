using Claunia.PropertyList;
using SkiaSharp;
namespace SalaryDesktop.Core;

public static class GroupLetterFont
{
    public static void Validate(byte[] bytes)
    {
        if (bytes.Length is 0 or > 3_000_000) throw new InvalidDataException("ملف خط ساكال العريض فارغ أو كبير جداً.");
        using var data = SKData.CreateCopy(bytes); using var face = SKTypeface.FromData(data) ?? throw new InvalidDataException("ملف الخط غير صالح.");
        if (!string.Equals(face.FamilyName, "Sakkal Majalla", StringComparison.OrdinalIgnoreCase) || face.FontStyle.Weight < 600) throw new InvalidDataException("اختر خط Sakkal Majalla العريض MAJALLAB.TTF.");
        using var paint = new SKPaint { Typeface = face };
        if (!paint.ContainsGlyphs("ابتثجحخدذرزسشصضطظعغفقكلمنهويأإآءؤئةى0123456789٠١٢٣٤٥٦٧٨٩")) throw new InvalidDataException("الخط لا يدعم الأحرف والأرقام المطلوبة.");
    }
    public static byte[]? Read(BackupSnapshot source)
    {
        if (!source.SettingsPlists.TryGetValue("transferHeaderBoldFont", out var plist)) return null;
        try
        {
            if (PropertyListParser.Parse(plist) is not NSData data) throw new InvalidDataException("صيغة الخط المحفوظ غير صالحة.");
            if (data.Bytes.Length == 0) return null;
            Validate(data.Bytes); return data.Bytes;
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException)
        { throw new InvalidDataException("تعذّر قراءة الخط المحفوظ من النسخة.", ex); }
    }
}
