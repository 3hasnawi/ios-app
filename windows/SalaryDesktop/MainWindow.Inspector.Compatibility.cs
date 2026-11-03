using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace SalaryDesktop;

public partial class MainWindow
{
    private List<KeyValuePair<string, string>> inspectorFields = [];

    private void InitializeInspectorCompatibility()
    {
        DataTable.SelectionChanged += InspectorCompatibilitySelectionChanged;
        InspectorTabAll.Checked += InspectorCompatibilityTabChanged;
        InspectorTabSummary.Checked += InspectorCompatibilityTabChanged;
        InspectorTabDeductions.Checked += InspectorCompatibilityTabChanged;
        InspectorTabBank.Checked += InspectorCompatibilityTabChanged;
        PreviewKeyDown += InspectorCompatibilityKeyDown;
    }

    private void InspectorCompatibilitySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataTable.SelectedItem is null) { CloseInspector(); return; }
        RefreshInspectorCompatibility();
    }

    private void RefreshInspectorCompatibility()
    {
        if (InspectorDrawer is null || InspectorFieldsList is null || DataTable?.SelectedItem is not { } selected ||
            Tables.GetValueOrDefault(page) is not { } spec) return;

        // تعريفات الجدول تعرف أنواع الصفوف وعناوينها وتنسيق المبالغ المعتمد.
        // لا نعرض كائنات داخلية أو حقولاً انعكاسية غير مرتبطة بالقسم.
        var fields = new List<KeyValuePair<string, string>>();
        foreach (var column in spec.Columns)
        {
            var value = column.Value(selected);
            if (value is null) continue;
            // لا نقرب الكسور المالية المخزنة عند عرضها في اللوحة.
            var display = value is decimal preciseAmount
                ? preciseAmount.ToString("#,0.############################", Numbers)
                : Cell(value);
            if (!string.IsNullOrWhiteSpace(display)) fields.Add(new(column.Title, display));
        }
        if (fields.Count == 0) { CloseInspector(); return; }
        inspectorFields = fields;

        var title = FindExactInspectorField(fields, "الاسم", "جهة التنفيذ", "الجهة", "عنوان الكتاب") ?? "سجل محدد";
        InspectorTitleText.Text = title;
        InspectorCodeText.Text = FindExactInspectorField(fields, "الرقم الموحد", "الرقم الإحصائي") ?? "";
        InspectorCodeBadge.Visibility = InspectorCodeText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        InspectorSubtitleText.Text = FindExactInspectorField(fields, "محل العمل", "الرتبة", "المصرف والفرع") ?? PageTitle.Text;
        var amount = page == "payroll" ? FindExactInspectorField(fields, "الصافي") : FindExactInspectorField(fields, "المبلغ");
        InspectorAmountCard.Visibility = amount is null ? Visibility.Collapsed : Visibility.Visible;
        InspectorAmountLabel.Text = page == "payroll" ? "صافي الراتب المستحق" : "المبلغ";
        InspectorNetAmountText.Text = amount ?? "—";
        InspectorTabAll.Content = page == "payroll" ? "ملخص الراتب" : "الملخص";
        InspectorTabSummary.Content = InspectorTabAll.Content;
        InspectorPrimaryActionBtn.Content = OpenRowMenu.Header;
        InspectorPrimaryActionBtn.IsEnabled = OpenRowMenu.IsEnabled;
        InspectorSecondaryActionBtn.Content = SecondRowMenu.Header;
        InspectorSecondaryActionBtn.Visibility = SecondRowMenu.Visibility;
        InspectorAvatarText.Text = GetArabicInitials(title);
        FilterInspectorCompatibility();

        var wasClosed = InspectorDrawer.Visibility != Visibility.Visible;
        InspectorDrawer.Visibility = Visibility.Visible;
        if (wasClosed)
        {
            InspectorSlide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
                new DoubleAnimation(-20, 0, TimeSpan.FromMilliseconds(170)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    private void FilterInspectorCompatibility()
    {
        if (InspectorFieldsList is null) return;
        IEnumerable<KeyValuePair<string, string>> filtered = inspectorFields;
        if (InspectorTabDeductions?.IsChecked == true)
            filtered = inspectorFields.Where(p => p.Key.Contains("استقطاع") || p.Key.Contains("سلفة") || p.Key.Contains("تقاعد") || p.Key.Contains("ضريبة") || p.Key.Contains("غياب"));
        else if (InspectorTabBank?.IsChecked == true)
            filtered = inspectorFields.Where(p => p.Key.Contains("مصرف") || p.Key.Contains("حساب") || p.Key.Contains("IBAN", StringComparison.OrdinalIgnoreCase) || p.Key.Contains("حالة"));
        var list = filtered.ToList();
        InspectorFieldsList.ItemsSource = list.Count == 0 ? inspectorFields : list;
    }

    private void InspectorCompatibilityTabChanged(object sender, RoutedEventArgs e)
    {
        if (InspectorDrawer is not null && InspectorDrawer.Visibility == Visibility.Visible) RefreshInspectorCompatibility();
    }

    private void InspectorCompatibilityKeyDown(object sender, KeyEventArgs e)
    {
        if (!Locker.IsLocked && !e.Handled) WindowPreviewKeyDown(sender, e);
    }

    private void OpenInspectorClick(object sender, RoutedEventArgs e)
    {
        UpdateInspectorFromSelection();
        RefreshInspectorCompatibility();
    }
    private void CloseInspector()
    {
        if (InspectorDrawer is not null) InspectorDrawer.Visibility = Visibility.Collapsed;
    }

    private static string? FindExactInspectorField(List<KeyValuePair<string, string>> fields, params string[] names)
    {
        foreach (var name in names)
            if (fields.FirstOrDefault(field => field.Key.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Value.Length: > 0 } match) return match.Value;
        return null;
    }

}
