using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace SalaryDesktop;

/// المظهر الفاتح أو الداكن أو حسب ويندوز. كل ألوان الواجهة موارد ديناميكية في App.xaml (الأسماء الأساسية ثم الألوان المشتقة T…)،
/// فتبديل المظهر يستبدل قيمها فقط. لون جديد في الواجهة يُضاف مورداً هنا وفي App.xaml بدل كتابته مباشرة.
public static class Themes
{
    private static readonly Dictionary<string, (string Light, string Dark)> Palette = new(StringComparer.Ordinal)
    {
        ["Primary"] = ("#135148", "#4CB8A2"),
        ["SurfaceSubtle"] = ("#F8FAFA", "#1C2826"),
        ["PrimarySoft"] = ("#E3EFEC", "#183A34"),
        ["Accent"] = ("#0877E8", "#4C9DF5"),
        ["AccentSoft"] = ("#E7F1FD", "#16304D"),
        ["Page"] = ("#F3F5F6", "#0F1918"),
        ["Line"] = ("#E2E8E9", "#283735"),
        ["Ink"] = ("#172D2B", "#E4ECEA"),
        ["Muted"] = ("#6B7F7C", "#93A6A3"),
        ["Warn"] = ("#A9620F", "#EBA851"),
        ["WarnSoft"] = ("#FFF5E5", "#33271A"),
        ["WarnLine"] = ("#F3D3A8", "#5A4123"),
        ["Violet"] = ("#6B4FD8", "#A897F0"),
        ["Danger"] = ("#B4362A", "#F08A7E"),
        ["DangerSoft"] = ("#FDECEA", "#3A1F1C"),
        ["VioletSoft"] = ("#EEE9FB", "#2A2440"),
        ["NavGroup"] = ("#F4F7F7", "#132321"),
        ["Tbg0877E8"] = ("#0877E8", "#1E77D2"),
        ["Tbg0B2420"] = ("#0B2420", "#FFFFFF"),
        ["Tbg7A8D8A"] = ("#7A8D8A", "#737978"),
        ["Tbg83958F"] = ("#83958F", "#6D7371"),
        ["TbgA3B2B0"] = ("#A3B2B0", "#565C5B"),
        ["TbgA9B8B9"] = ("#A9B8B9", "#515757"),
        ["TbgC5CFCE"] = ("#C5CFCE", "#3F4343"),
        ["TbgCFD9DA"] = ("#CFD9DA", "#373C3C"),
        ["TbgCFE2F8"] = ("#CFE2F8", "#222D3A"),
        ["TbgD3DCDD"] = ("#D3DCDD", "#343939"),
        ["TbgD9EAFB"] = ("#D9EAFB", "#1E2935"),
        ["TbgE1E7E8"] = ("#E1E7E8", "#2B2F2F"),
        ["TbgE1E8E7"] = ("#E1E8E7", "#2B2F2F"),
        ["TbgE8EEED"] = ("#E8EEED", "#262B2A"),
        ["TbgEDF1F2"] = ("#EDF1F2", "#232627"),
        ["TbgEEF3F2"] = ("#EEF3F2", "#222726"),
        ["TbgEFF6F5"] = ("#EFF6F5", "#1F2625"),
        ["TbgF1F4F6"] = ("#F1F4F6", "#202325"),
        ["TbgF1F5F4"] = ("#F1F5F4", "#202524"),
        ["TbgF4F7F7"] = ("#F4F7F7", "#1F2222"),
        ["TbgF5DDB8"] = ("#F5DDB8", "#473B29"),
        ["TbgF7FAF9"] = ("#F7FAF9", "#1C211F"),
        ["TbgFAFCFC"] = ("#FAFCFC", "#1A1F1F"),
        ["TbgFBFCFC"] = ("#FBFCFC", "#1B1E1E"),
        ["TbgFFF1E3"] = ("#FFF1E3", "#312417"),
        ["TbgFFFFFF"] = ("#FFFFFF", "#172422"),
        ["Tfg0D2A3D"] = ("#0D2A3D", "#70B4E1"),
        ["Tfg135148"] = ("#135148", "#73DECE"),
        ["Tfg1E7145"] = ("#1E7145", "#76DBA5"),
        ["Tfg2C6FB4"] = ("#2C6FB4", "#74A8DD"),
        ["Tfg2F4744"] = ("#2F4744", "#CAD4D3"),
        ["Tfg2F5873"] = ("#2F5873", "#84B0CD"),
        ["Tfg38504C"] = ("#38504C", "#C5CECD"),
        ["Tfg3A524F"] = ("#3A524F", "#C4CDCC"),
        ["Tfg4B625F"] = ("#4B625F", "#BAC3C2"),
        ["Tfg4F7491"] = ("#4F7491", "#A3B0BB"),
        ["Tfg5D716E"] = ("#5D716E", "#B1B8B7"),
        ["Tfg6B7F7C"] = ("#6B7F7C", "#A9B0AF"),
        ["Tfg718481"] = ("#718481", "#A5ACAB"),
        ["Tfg7A8D8A"] = ("#7A8D8A", "#A0A7A6"),
        ["Tfg7C8E8B"] = ("#7C8E8B", "#9FA6A5"),
        ["Tfg8A9B98"] = ("#8A9B98", "#969E9D"),
        ["Tfg8F5510"] = ("#8F5510", "#E9AE67"),
        ["TfgA76716"] = ("#A76716", "#E9B067"),
        ["TfgB03A2E"] = ("#B03A2E", "#DB7F76"),
        ["TfgB4362A"] = ("#B4362A", "#DE7C72"),
        ["TfgB5640C"] = ("#B5640C", "#E9AB67")
    };
    public static string Current { get; private set; } = "light";
    public static bool IsDark { get; private set; }
    private static bool hooked;

    public static void Apply(string theme)
    {
        Current = theme is "dark" or "system" ? theme : "light";
        IsDark = Current == "dark" || (Current == "system" && SystemPrefersDark());
        var resources = Application.Current.Resources;
        foreach (var (key, (light, dark)) in Palette)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(IsDark ? dark : light));
            brush.Freeze();
            resources[key] = brush;
        }
        if (!hooked)
        {
            hooked = true;
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => { if (sender is Window window) TitleBar(window); }));
            SystemEvents.UserPreferenceChanged += (_, e) => { if (e.Category == UserPreferenceCategory.General && Current == "system") Application.Current?.Dispatcher.BeginInvoke(() => Apply("system")); };
        }
        foreach (Window window in Application.Current.Windows) TitleBar(window);
    }

    private static bool SystemPrefersDark()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0; }
        catch (Exception ex) when (ex is System.Security.SecurityException or System.IO.IOException or UnauthorizedAccessException) { return false; }
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    /// شريط عنوان النافذة داكن مع المظهر الداكن (ويندوز 10 الإصدار 20H1 فما بعد؛ يتجاهله ما قبله).
    private static void TitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var value = IsDark ? 1 : 0;
        try { _ = DwmSetWindowAttribute(handle, 20, ref value, sizeof(int)); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }
}
