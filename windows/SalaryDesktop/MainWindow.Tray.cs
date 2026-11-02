using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace SalaryDesktop;

/// الإغلاق إلى منطقة الإشعارات (طلب المالك): زر ✕ يخفي النافذة ويبقي البرنامج يعمل في الخلفية للنسخ الدوري
/// وفحص السيرفر؛ والإغلاق النهائي من قائمة الأيقونة بجانب الساعة. الخيار في الإعدادات ← النسخ الاحتياطي.
public partial class MainWindow
{
    private TrayIcon? tray;
    private bool exiting;

    private void InitTray()
    {
        if (App.Unattended || tray is not null) return;
        try
        {
            tray = new TrayIcon("تطبيق الرواتب — يعمل في الخلفية للنسخ والمزامنة");
            tray.Open += ShowFromTray;
            tray.Menu += ShowTrayMenu;
        }
        catch (Exception ex) when (ex is Win32Exception or ExternalException or InvalidOperationException) { tray = null; }
    }

    /// يُسمح بالإغلاق الفعلي (الخروج من القائمة، أو إيقاف ويندوز أو برنامج التثبيت).
    internal void AllowExit() => exiting = true;

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (!exiting && !App.Unattended && tray is not null && UiPreferences.Current.CloseToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        SavePlacement();
    }

    private void HideToTray()
    {
        SavePlacement();
        NotificationPanel.Visibility = Visibility.Collapsed;
        Hide();
        if (!UiPreferences.Current.TrayHintShown)
        {
            tray?.Balloon("تطبيق الرواتب يعمل في الخلفية", "يستمر النسخ الاحتياطي وفحص السيرفر. انقر الأيقونة للفتح، أو الزر الأيمن ← «إغلاق البرنامج نهائياً».");
            UiPreferences.Current.TrayHintShown = true; UiPreferences.Save();
        }
    }

    internal void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = UiPreferences.Current.Maximized ? WindowState.Maximized : WindowState.Normal;
        Activate();
        // يضمن ظهور النافذة فوق غيرها عند فتحها من الأيقونة أو من تشغيل البرنامج مرة ثانية.
        Topmost = true; Topmost = false;
        Focus();
    }

    private void ShowTrayMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint, FlowDirection = FlowDirection.RightToLeft };
        MenuItem Item(string header, string glyph, Action action, bool bold = false, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Icon = new TextBlock { Text = glyph, Style = (Style)FindResource("Icon") } };
            item.Click += (_, _) => action();
            return item;
        }
        menu.Items.Add(Item("فتح تطبيق الرواتب", "", ShowFromTray, bold: true));
        menu.Items.Add(Item("نسخ احتياطي الآن", "", () => { ShowFromTray(); BackupNow(this, new RoutedEventArgs()); }, enabled: snapshot is not null && !loading));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("إغلاق البرنامج نهائياً", "", ExitApplication));
        // القائمة المنبثقة تحتاج نافذة أمامية حتى تُغلق عند النقر خارجها.
        menu.Opened += (_, _) => { if (PresentationSource.FromVisual(menu) is HwndSource source) TrayIcon.SetForegroundWindow(source.Handle); };
        menu.IsOpen = true;
    }

    private void ExitApplication()
    {
        exiting = true;
        tray?.Dispose(); tray = null;
        Close();
        Application.Current.Shutdown();
    }

    private void CloseToTrayChanged(object sender, RoutedEventArgs e)
    {
        UiPreferences.Current.CloseToTray = CloseToTrayBox.IsChecked == true;
        if (UiPreferences.Save() is { } error) StatusBar.Text = "تعذّر حفظ الخيار: " + error;
    }

    /// تنبيه من الأيقونة إذا كانت النافذة مخفية (مثل وجود نسخة أحدث على السيرفر أو فشل النسخ).
    private void TrayNotice(string title, string text, bool warning = false)
    {
        if (!IsVisible) tray?.Balloon(title, text, warning);
    }
}
