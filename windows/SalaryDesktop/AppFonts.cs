using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SalaryDesktop;

/// خط الواجهة وشكل الأرقام من الإعدادات. الخطوط مضمّنة في البرنامج (Fonts\، ترخيص OFL)؛ الكشوف PDF/Excel تبقى بخط Arial المعتمد.
/// المورد AppFont ديناميكي في كل النوافذ، فتغيير الخط يسري فوراً. الأرقام: إنجليزية (0-9) أو هندية (٠-٩) لكل النصوص المعروضة.
public static class AppFonts
{
    /// Installed = خط من ويندوز نفسه (Arial مثبت في كل نسخ ويندوز ولا يُضمَّن في البرنامج)؛ Bold = كل نصوص الواجهة بالوزن العريض.
    public sealed record Option(string Key, string Name, string Family, string Note, bool Installed = false, bool Bold = false)
    {
        public FontFamily Font => Installed ? new FontFamily(Family + ", Segoe UI") : new FontFamily(new Uri("pack://application:,,,/SalaryDesktop;component/"), "./Fonts/#" + Family + ", Segoe UI");
        public FontWeight Weight => Bold ? FontWeights.Bold : FontWeights.Normal;
        public string Preview => "منتسب وهمي للعرض · 437,024";
    }

    public static readonly Option[] Options =
    [
        new("noto-kufi", "Noto Kufi Arabic", "Noto Kufi Arabic", "الأوضح للجداول والأرقام (المقترح)"),
        new("noto-sans", "Noto Sans Arabic", "Noto Sans Arabic", "قريب من خط النسخ، مريح للقراءة"),
        new("cairo", "Cairo", "Cairo", "عصري وأنيق"),
        new("tajawal", "Tajawal", "Tajawal", "خفيف ومرتب"),
        new("almarai", "Almarai", "Almarai", "بسيط وواضح"),
        new("plex", "IBM Plex Sans Arabic", "IBM Plex Sans Arabic", "رفيع"),
        new("arial", "Arial عادي", "Arial", "خط الكشوف المعتمد بوزن عادي", Installed: true),
        new("arial-bold", "Arial عريض", "Arial", "خط الكشوف المعتمد بوزن عريض لكل النصوص", Installed: true, Bold: true),
    ];
    public static FontWeight CurrentWeight => Find(CurrentFont).Weight;
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
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => { if (sender is Window window) { Digits(window); Weigh(window); } }));
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((sender, _) => { if (sender is ContextMenu menu) { Digits(menu); Weigh(menu); } }));
        }
        foreach (Window window in Application.Current.Windows) { Digits(window); Weigh(window); }
    }

    /// الوزن موروث مثل الخط: يُضبط على النافذة أو القائمة، والنصوص ذات الوزن المحدد (العناوين) تبقى كما هي.
    private static void Weigh(DependencyObject root) => root.SetValue(TextElement.FontWeightProperty, CurrentWeight);

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
