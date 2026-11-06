using System.IO;
using System.Runtime.InteropServices;
using SalaryLocal.Core;

namespace SalaryLocal.App;

/// محوّل اختياري إلى نافذة جهاز WIA. وجود الكود لا يثبت توافق ماسح المستخدم أو تجربته.
internal static class LocalArchiveScanner
{
    private const string PngFormat = "{B96B3CAF-0728-11D3-9D7B-0000F81EF32E}";
    internal static LocalArchiveImage? AcquirePage()
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidDataException("المسح المباشر متاح فقط على Windows عبر تعريف يدعم WIA. يمكنك إضافة صور JPEG أو PNG بدلاً منه.");
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) throw new InvalidDataException("يجب فتح نافذة الماسح من واجهة Windows.");
        var type = Type.GetTypeFromProgID("WIA.CommonDialog") ?? throw new InvalidDataException("خدمة WIA غير متاحة. ثبّت تعريف الماسح، أو امسح إلى PNG أو JPEG ببرنامج الجهاز ثم أضف الصور.");
        object? dialog = null, image = null;
        string? temporary = null;
        try
        {
            dialog = Activator.CreateInstance(type) ?? throw new InvalidDataException("تعذّر فتح خدمة المسح WIA.");
            // صفحة واحدة بإعدادات الجهاز الظاهرة للمستخدم، والإلغاء لا يغيّر المسودة أو الأرشيف.
            if (Ui.ShowNativeDialog(() => { image = ((dynamic)dialog).ShowAcquireImage(1, 1, 131072, PngFormat, false, true, false); return image is not null; }) != true) return null;
            if (image is null) return null;
            int width = (int)((dynamic)image).Width, height = (int)((dynamic)image).Height;
            if (width <= 0 || height <= 0 || width > LocalArchiveImages.MaxInputSide || height > LocalArchiveImages.MaxInputSide || (long)width * height > LocalArchiveImages.MaxDecodedPixels)
                throw new InvalidDataException("أبعاد المسح تتجاوز ٤٠ مليون بكسل أو ١٢٠٠٠ بكسل للضلع. قلّل الدقة في نافذة الماسح وأعد المسح؛ صفحة A4 عادية بدقة ٦٠٠ نقطة/بوصة تقع ضمن الحد.");
            temporary = Directory.CreateTempSubdirectory("salary-local-scan-").FullName;
            string path = Path.Combine(temporary, "scan.png");
            ((dynamic)image).SaveFile(path);
            return LocalArchiveImages.ReadFiles([path]).Single();
        }
        catch (COMException ex) when ((uint)ex.HResult == 0x80210015)
        { throw new InvalidDataException("لم يُعثر على ماسح WIA متصل. تحقق من تشغيله وتعريفه، أو أضف صوراً محفوظة من برنامج الجهاز.", ex); }
        catch (COMException ex) when ((uint)ex.HResult is 0x80210006 or 0x8021000A)
        { throw new InvalidDataException("الماسح مشغول أو غير جاهز؛ أعد المحاولة بعد التأكد من الجهاز.", ex); }
        catch (COMException ex) when ((uint)ex.HResult == 0x80210003)
        { throw new InvalidDataException("لا توجد ورقة في الماسح؛ ضع الصفحة وأعد المحاولة.", ex); }
        catch (COMException ex) when ((uint)ex.HResult == 0x80210064)
        { return null; }
        catch (COMException ex)
        { throw new InvalidDataException("تعذّر المسح عبر WIA أو لم يدعم الجهاز إخراج PNG. استخدم برنامج الجهاز واحفظ صورة PNG أو JPEG ثم أضفها. الرمز: 0x" + ((uint)ex.HResult).ToString("X8"), ex); }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException ex)
        { throw new InvalidDataException("تعريف WIA لم يستجب بصيغة مدعومة. استخدم برنامج الماسح لإنتاج صور PNG أو JPEG.", ex); }
        finally
        {
            if (image is not null && Marshal.IsComObject(image)) Marshal.FinalReleaseComObject(image);
            if (dialog is not null && Marshal.IsComObject(dialog)) Marshal.FinalReleaseComObject(dialog);
            if (temporary is not null) try { Directory.Delete(temporary, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
