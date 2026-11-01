using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SalaryDesktop;

/// خط الواجهة وشكل الأرقام من الإعدادات. الخطوط مضمّنة في البرنامج (Fonts\، ترخيص OFL)؛ الكشوف PDF/Excel تبقى بخط Arial المعتمد.
/// المورد AppFont ديناميكي في كل النوافذ، فتغيير الخط يسري فوراً. الأرقام: إنجليزية (0-9) أو هندية (٠-٩) لكل النصوص المعروضة.
public static class AppFonts
{
    public sealed record Option(string Key, string Name, string Family, string Note)
    {
        public FontFamily Font => new(new Uri("pack://application:,,,/SalaryDesktop;component/"), "./Fonts/#" + Family + ", Segoe UI");
        public string Preview => "نهاد جمعة الذيابي · 437,024";
    }

    public static readonly Option[] Options =
    [
        new("noto-kufi", "Noto Kufi Arabic", "Noto Kufi Arabic", "الأوضح للجداول والأرقام (المقترح)"),
        new("noto-sans", "Noto Sans Arabic", "Noto Sans Arabic", "قريب من خط النسخ، مريح للقراءة"),
        new("cairo", "Cairo", "Cairo", "عصري وأنيق"),
        new("tajawal", "Tajawal", "Tajawal", "خفيف ومرتب"),
        new("almarai", "Almarai", "Almarai", "بسيط وواضح"),
        new("plex", "IBM Plex Sans Arabic", "IBM Plex Sans Arabic", "رفيع"),
    ];
    public const string DefaultFont = "noto-kufi";
    public static string CurrentFont { get; private set; } = DefaultFont;
    public static bool EasternDigits { get; private set; }
    private static bool hooked;

    public static Option Find(string? key) => Options.FirstOrDefault(o => o.Key == key) ?? Options[0];

    public static void Apply(string? font, string? digits)
    {
        var option = Find(font);
        CurrentFont = option.Key;
        EasternDigits = digits == "arabic";
        Application.Current.Resources["AppFont"] = option.Font;
        if (!hooked)
        {
            hooked = true;
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => { if (sender is Window window) Digits(window); }));
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((sender, _) => { if (sender is ContextMenu menu) Digits(menu); }));
        }
        foreach (Window window in Application.Current.Windows) Digits(window);
    }

    /// شكل الأرقام موروث في شجرة العناصر، فيكفي ضبطه على النافذة أو القائمة المنبثقة.
    private static void Digits(DependencyObject root)
    {
        if (EasternDigits)
        {
            NumberSubstitution.SetCultureSource(root, NumberCultureSource.Override);
            NumberSubstitution.SetCultureOverride(root, CultureInfo.GetCultureInfo("ar-IQ"));
            NumberSubstitution.SetSubstitution(root, NumberSubstitutionMethod.Traditional);
        }
        else
        {
            NumberSubstitution.SetCultureSource(root, NumberCultureSource.Override);
            NumberSubstitution.SetCultureOverride(root, CultureInfo.GetCultureInfo("en-US")); // الثقافة المحايدة غير مقبولة هنا (اسمها فارغ).
            NumberSubstitution.SetSubstitution(root, NumberSubstitutionMethod.European);
        }
    }
}
