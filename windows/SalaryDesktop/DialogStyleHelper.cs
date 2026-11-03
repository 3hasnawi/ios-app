using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace SalaryDesktop;

public static class DialogStyleHelper
{
    public static void AttachFluentDialogBehavior()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
        // ينتظر معالجة الحقول والقوائم أولاً حتى لا يغلق النافذة أثناء إلغاء تعديل أو قائمة مفتوحة.
        EventManager.RegisterClassHandler(typeof(Window), UIElement.KeyDownEvent, new KeyEventHandler(OnWindowKeyDown));
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window win || win is MainWindow || !ReferenceEquals(e.OriginalSource, win)) return;
        win.UseLayoutRounding = true;
        win.SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(win, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(win, TextRenderingMode.ClearType);
        Typography.SetNumeralAlignment(win, FontNumeralAlignment.Tabular);
    }

    private static void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not Window win || win is MainWindow || e.Handled || e.Key != Key.Escape) return;
        for (DependencyObject? node = Keyboard.FocusedElement as DependencyObject; node != null;)
        {
            if (node is ComboBox { IsDropDownOpen: true }) return;
            node = node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        e.Handled = true;
        win.Close();
    }
}
