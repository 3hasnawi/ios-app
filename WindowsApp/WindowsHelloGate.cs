using Windows.Security.Credentials.UI;

namespace SalaryWindows;

// Windows يتولى البصمة أو الوجه أو PIN؛ التطبيق لا يخزن أياً منها.
internal static class WindowsHelloGate
{
    public static async Task<(bool Success, string Message)> Verify(IntPtr window)
    {
        try
        {
            var available = await UserConsentVerifier.CheckAvailabilityAsync();
            if (available != UserConsentVerifierAvailability.Available)
                return (false, "Windows Hello غير جاهز. أعدّ البصمة أو رمز PIN من إعدادات تسجيل الدخول في ويندوز.");
            var result = await UserConsentVerifierInterop.RequestVerificationForWindowAsync(window, "افتح تطبيق الرواتب");
            return result == UserConsentVerificationResult.Verified
                ? (true, "تم التحقق.") : (false, "لم يكتمل التحقق. حاول مرة أخرى.");
        }
        catch (Exception)
        {
            return (false, "تعذّر تشغيل Windows Hello. تحقق من إعدادات تسجيل الدخول في ويندوز.");
        }
    }
}
