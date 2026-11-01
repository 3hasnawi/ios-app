using System.Windows;

namespace SalaryDesktop;

/// رسائل البرنامج باتجاه اليمين لليسار: محاذاة النص العربي وترتيب علامات الترقيم والأزرار.
internal static class Ui
{
    private const MessageBoxOptions Rtl = MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign;

    public static MessageBoxResult Message(Window owner, string text, string caption) =>
        MessageBox.Show(owner, text, caption, MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None, Rtl);

    public static MessageBoxResult Message(Window owner, string text, string caption, MessageBoxButton button, MessageBoxImage icon) =>
        MessageBox.Show(owner, text, caption, button, icon, MessageBoxResult.None, Rtl);
}
