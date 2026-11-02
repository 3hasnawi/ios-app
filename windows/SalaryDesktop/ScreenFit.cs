using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SalaryDesktop;

/// يُبقي كل نافذة داخل منطقة العمل في شاشتها (بلا شريط المهام): على شاشات اللابتوب الصغيرة أو مع تكبير ١٢٥–١٥٠٪
/// كانت النافذة الرئيسية (١٤٦٠×٨٤٠) أكبر من الشاشة فيختفي شريط عنوانها وأزرار التصغير والإغلاق.
internal static class ScreenFit
{
    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => { if (sender is Window window) Fit(window); }));

    public static void Fit(Window window)
    {
        if (window.WindowState != WindowState.Normal || WorkArea(window) is not { } area) return;
        // الحد الأدنى للحجم لا يتجاوز الشاشة، وإلا لا يمكن تصغير النافذة لتتسع.
        window.MinWidth = Math.Min(window.MinWidth, area.Width);
        window.MinHeight = Math.Min(window.MinHeight, area.Height);
        var tooBig = window.ActualWidth > area.Width + 1 || window.ActualHeight > area.Height + 1;
        // النافذة الأكبر من الشاشة تُصغَّر لتتسع وتُوسَّط؛ والنافذة الرئيسية تُكبَّر بعدها لملء الشاشة.
        var width = Math.Min(window.ActualWidth, area.Width); var height = Math.Min(window.ActualHeight, area.Height);
        if (window.ActualWidth > area.Width) window.Width = width;
        if (window.ActualHeight > area.Height) window.Height = height;
        if (tooBig) { window.Left = area.Left + (area.Width - width) / 2; window.Top = area.Top + (area.Height - height) / 2; }
        else if (!double.IsNaN(window.Left) && !double.IsNaN(window.Top))
        {
            window.Left = Math.Clamp(window.Left, area.Left, Math.Max(area.Left, area.Right - width));
            window.Top = Math.Clamp(window.Top, area.Top, Math.Max(area.Top, area.Bottom - height));
        }
        // حجم «الاستعادة» صار يتسع للشاشة، فلا تعود النافذة خارجها عند إلغاء التكبير.
        if (tooBig && ReferenceEquals(window, Application.Current?.MainWindow) && !App.Unattended) window.WindowState = WindowState.Maximized;
    }

    /// منطقة العمل للشاشة التي تقع فيها النافذة بوحدات WPF؛ وإن تعذّر ذلك فمنطقة عمل الشاشة الرئيسية.
    private static Rect? WorkArea(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            var source = PresentationSource.FromVisual(window);
            if (handle != IntPtr.Zero && source?.CompositionTarget is { } target)
            {
                var monitor = MonitorFromWindow(handle, 2 /* MONITOR_DEFAULTTONEAREST */);
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
                {
                    var transform = target.TransformFromDevice;
                    var topLeft = transform.Transform(new Point(info.Work.Left, info.Work.Top));
                    var bottomRight = transform.Transform(new Point(info.Work.Right, info.Work.Bottom));
                    var rect = new Rect(topLeft, bottomRight);
                    if (rect.Width > 200 && rect.Height > 200) return rect;
                }
            }
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException) { }
        var primary = SystemParameters.WorkArea;
        return primary.Width > 200 && primary.Height > 200 ? primary : null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Bounds { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Bounds Monitor; public Bounds Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
