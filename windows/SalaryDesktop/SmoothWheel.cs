using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SalaryDesktop;

/// التمرير بلوحة اللمس (Touchpad) بحسب مقدار الحركة: WPF يمرّر ٤٨ نقطة (أو ٣ صفوف) مع كل حدث عجلة مهما كان صغيراً،
/// ولوحات اللمس الدقيقة ترسل عشرات الأحداث الصغيرة في الثانية فيقفز الجدول بسرعة كبيرة. هنا يُمرَّر بنسبة المقدار (Delta ÷ 120)،
/// فتبقى عجلة الفأرة كما هي (نقرة = ٤٨ نقطة أو ٣ صفوف) وتصير لوحة اللمس سلسة. Ctrl+العجلة (التكبير في المعاينة) لا يتأثر.
internal static class SmoothWheel
{
    private const double PixelsPerNotch = 48;
    private sealed class Remainder { public double Value; }
    private static readonly ConditionalWeakTable<ScrollViewer, Remainder> Remainders = new();

    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnWheel));

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0 || Keyboard.Modifiers != ModifierKeys.None || e.OriginalSource is not DependencyObject origin) return;
        if (Scrollable(origin) is not { } viewer) return;
        // ScrollUnit=Item أو لوحة غير افتراضية: الإزاحة بالصفوف؛ وإلا بالنقاط.
        var owner = viewer.TemplatedParent as ItemsControl;
        var byItems = viewer.CanContentScroll && (owner is null || !(VirtualizingPanel.GetIsVirtualizing(owner) && VirtualizingPanel.GetScrollUnit(owner) == ScrollUnit.Pixel));
        var notches = e.Delta / 120.0;
        var step = byItems ? notches * Math.Max(1, SystemParameters.WheelScrollLines) : notches * PixelsPerNotch;
        var remainder = Remainders.GetOrCreateValue(viewer);
        remainder.Value += step;
        // الصفوف لا تقبل إزاحة كسرية؛ يُجمع الباقي حتى يكتمل صف.
        var move = byItems ? Math.Truncate(remainder.Value) : remainder.Value;
        remainder.Value -= move;
        if (move != 0) viewer.ScrollToVerticalOffset(Math.Clamp(viewer.VerticalOffset - move, 0, viewer.ScrollableHeight));
        e.Handled = true;
    }

    /// أقرب ScrollViewer تحت المؤشر يمكنه التمرير عمودياً؛ القوائم المنسدلة في نافذة منبثقة مستقلة فلا تمر من هنا.
    private static ScrollViewer? Scrollable(DependencyObject origin)
    {
        for (DependencyObject? node = origin; node is not null; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is ScrollViewer { ScrollableHeight: > 0, VerticalScrollBarVisibility: not ScrollBarVisibility.Disabled } viewer) return viewer;
        return null;
    }
}
