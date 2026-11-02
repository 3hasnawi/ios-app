using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace SalaryDesktop;

public partial class MainWindow
{
    private List<KeyValuePair<string, string>> inspectorFields = [];

    private void InitializeInspectorEvents()
    {
        DataTable.SelectionChanged -= DataTable_InspectorSelectionChanged;
        DataTable.SelectionChanged += DataTable_InspectorSelectionChanged;
    }

    private void DataTable_InspectorSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataTable.SelectedItem is null) { CloseInspector(); return; }
        UpdateInspectorFromSelection();
    }

    private void UpdateInspectorFromSelection()
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
            var display = Cell(value);
            if (!string.IsNullOrWhiteSpace(display)) fields.Add(new(column.Title, display));
        }
        if (fields.Count == 0) { CloseInspector(); return; }
        inspectorFields = fields;

        var title = FindFirstMatch(fields, "الاسم", "جهة التنفيذ", "الجهة", "عنوان الكتاب") ?? "سجل محدد";
        InspectorTitleText.Text = title;
        InspectorCodeText.Text = FindFirstMatch(fields, "الرقم الموحد", "الرقم الإحصائي") ?? "";
        InspectorSubtitleText.Text = FindFirstMatch(fields, "محل العمل", "الرتبة", "المصرف والفرع") ?? PageTitle.Text;
        InspectorNetAmountText.Text = FindFirstMatch(fields, "الصافي", "المبلغ", "الاستحقاق") ?? "—";
        InspectorAvatarText.Text = GetArabicInitials(title);
        FilterInspectorFields();

        var wasClosed = InspectorDrawer.Visibility != Visibility.Visible;
        InspectorDrawer.Visibility = Visibility.Visible;
        if (wasClosed)
        {
            InspectorSlide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
                new DoubleAnimation(-20, 0, TimeSpan.FromMilliseconds(170)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    private void FilterInspectorFields()
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

    private void InspectorTabChanged(object sender, RoutedEventArgs e)
    {
        if (InspectorDrawer is not null && InspectorDrawer.Visibility == Visibility.Visible) FilterInspectorFields();
    }

    private void OpenInspectorClick(object sender, RoutedEventArgs e) => UpdateInspectorFromSelection();
    private void CloseInspectorClick(object sender, RoutedEventArgs e) => CloseInspector();
    private void CloseInspector()
    {
        if (InspectorDrawer is not null) InspectorDrawer.Visibility = Visibility.Collapsed;
    }

    private static string? FindFirstMatch(List<KeyValuePair<string, string>> fields, params string[] names)
    {
        foreach (var name in names)
            if (fields.FirstOrDefault(field => field.Key.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Value.Length: > 0 } match) return match.Value;
        return null;
    }

    private static string GetArabicInitials(string fullName)
    {
        var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "م";
        return string.Concat(parts.Take(2).Select(part => StringInfo.GetNextTextElement(part)));
    }
}
