using System.Windows;
using System.Windows.Media;
using SalaryDesktop.Core;

namespace SalaryDesktop;

/// مراجعة الاستيراد الموحّد: يعرض نوع كل ملف كما تعرّف عليه البرنامج قبل البدء، والملف غير المعروف لا يُستورد.
public partial class ImportCenterWindow : Window
{
    public sealed record Row(string Name, string Kind, string Note, string Glyph, Brush Tint, Brush Ink);
    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];

    public ImportCenterWindow(IReadOnlyList<ImportCandidate> items, bool hasData)
    {
        InitializeComponent();
        Files.ItemsSource = items.Select(c => new Row(c.FileName, c.KindText, c.Note,
            c.Kind switch { ImportKind.Backup => "\uE8E5", ImportKind.Payroll => "\uE8A5", ImportKind.Annual => "\uE787", ImportKind.Accounts => "\uE825", _ => "\uE7BA" },
            Resource(c.Kind switch { ImportKind.Backup => "AccentSoft", ImportKind.Unknown => "WarnSoft", _ => "PrimarySoft" }),
            Resource(c.Kind switch { ImportKind.Backup => "Accent", ImportKind.Unknown => "Warn", _ => "Primary" }))).ToArray();
        var usable = items.Count(c => c.Kind != ImportKind.Unknown);
        var backups = items.Count(c => c.Kind == ImportKind.Backup);
        var parts = new List<string> { $"{items.Count:N0} ملف · {usable:N0} قابل للاستيراد" };
        if (backups > 1) parts.Add("ستُفتح أول نسخة احتياطية فقط");
        if (!hasData && backups == 0 && usable > 0) parts.Add("لا توجد بيانات مفتوحة؛ يبدأ البرنامج بيانات جديدة من أول ملف رواتب");
        parts.Add("كل ملف يُراجع في نافذته قبل الحفظ، والملفات الأصلية لا تتغير");
        Summary.Text = string.Join(" · ", parts);
        ImportButton.IsEnabled = usable > 0;
    }

    private void Confirm(object sender, RoutedEventArgs e) => DialogResult = true;
}
