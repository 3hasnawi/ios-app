using System.ComponentModel;
using System.Windows;

namespace SalaryDesktop;

/// الإغلاق ينهي البرنامج؛ يُستبقى منع إغلاق مسودة غير محفوظة وتنشيط النسخة المفتوحة عند تشغيل نسخة ثانية.
public partial class MainWindow
{
    private bool exiting;

    internal void AllowExit() => exiting = true;

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (!exiting && !ConfirmDiscardInspectorChanges()) { e.Cancel = true; return; }
        SavePlacement();
    }

    internal void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = UiPreferences.Current.Maximized ? WindowState.Maximized : WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false;
        Focus();
    }

    private void CloseToTrayChanged(object sender, RoutedEventArgs e)
    {
        UiPreferences.Current.CloseToTray = false;
        if (UiPreferences.Save() is { } error) StatusBar.Text = "تعذّر حفظ الخيار: " + error;
    }

    // لا تُنشأ أيقونة خلفية؛ الإشعارات تظهر داخل نافذة التطبيق أثناء تشغيله.
    private void TrayNotice(string title, string text, bool warning = false) { }
}
