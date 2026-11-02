using System.IO;
using System.Runtime.InteropServices;

namespace SalaryDesktop;

/// المسح الضوئي من الطابعة أو الماسح عبر خدمة ويندوز WIA (نافذة المسح نفسها التي يستخدمها «الفاكس والمسح الضوئي»).
/// كل استدعاء يمسح صفحة واحدة؛ تُجمع الصفحات (حتى ثلاث) في PDF قبل الحفظ.
internal static class Scanner
{
    private const string Bmp = "{B96B3CAB-0728-11D3-9D7B-0000F81EF32E}";   // wiaFormatBMP: مدعومة في كل الماسحات، وتُضغط لاحقاً داخل PDF.
    private const int ScannerDevice = 1, ColorIntent = 1, MaximizeQuality = 131072;

    /// يعيد صورة الصفحة الممسوحة، أو null إذا ألغى المستخدم نافذة المسح.
    public static byte[]? Acquire()
    {
        var type = Type.GetTypeFromProgID("WIA.CommonDialog") ?? throw new InvalidDataException("خدمة المسح الضوئي في ويندوز (WIA) غير متاحة على هذا الجهاز.");
        object? dialog = null, image = null;
        var path = Path.Combine(Path.GetTempPath(), "salary-scan-" + Guid.NewGuid().ToString("N") + ".bmp");
        try
        {
            dialog = Activator.CreateInstance(type) ?? throw new InvalidDataException("تعذّر تشغيل خدمة المسح الضوئي.");
            dynamic wia = dialog;
            // جهاز ماسح، ألوان (لتظهر الأختام والتواقيع)، أعلى جودة، دون إعادة اختيار الجهاز كل مرة، مع نافذة إعدادات المسح، والإلغاء يعيد null.
            image = wia.ShowAcquireImage(ScannerDevice, ColorIntent, MaximizeQuality, Bmp, false, true, false);
            if (image is null) return null;
            ((dynamic)image).SaveFile(path);
            return File.ReadAllBytes(path);
        }
        catch (COMException ex) when ((uint)ex.HResult == 0x80210015)
        { throw new InvalidDataException("لم يُعثر على ماسح ضوئي متصل. شغّل الطابعة أو الماسح وتأكد من تثبيت تعريفه، أو امسح الكتاب إلى ملف PDF ثم أضفه من «إضافة من ملف».", ex); }
        catch (COMException ex) when ((uint)ex.HResult is 0x80210006 or 0x8021000A)
        { throw new InvalidDataException("الماسح مشغول أو غير جاهز؛ انتظر قليلاً ثم أعد المحاولة.", ex); }
        catch (COMException ex) when ((uint)ex.HResult == 0x80210003)
        { throw new InvalidDataException("لا توجد ورقة في الماسح؛ ضع الكتاب ثم أعد المحاولة.", ex); }
        catch (COMException ex) { throw new InvalidDataException("تعذّر المسح الضوئي: " + ex.Message, ex); }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException ex) { throw new InvalidDataException("خدمة المسح الضوئي في ويندوز لم تستجب كما يجب.", ex); }
        finally
        {
            if (image is not null && Marshal.IsComObject(image)) Marshal.FinalReleaseComObject(image);
            if (dialog is not null && Marshal.IsComObject(dialog)) Marshal.FinalReleaseComObject(dialog);
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
