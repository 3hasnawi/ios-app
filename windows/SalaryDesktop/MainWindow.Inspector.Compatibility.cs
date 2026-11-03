using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using SalaryDesktop.Core;

namespace SalaryDesktop;

public partial class MainWindow
{
    private void InitializeInspectorCompatibility() => PreviewKeyDown += InspectorCompatibilityKeyDown;
    private void InspectorCompatibilitySelectionChanged(object sender, SelectionChangedEventArgs e) => DataTable_InspectorSelectionChanged(sender, e);

    private void RefreshInspectorCompatibility()
    {
        if (InspectorDrawer is null || InspectorFieldsList is null || DataTable?.SelectedItem is not { } selected || Tables.GetValueOrDefault(page) is not { } spec) return;
        if (inspectorAccount is { } previous && inspectorDrafts.TryGetValue(previous, out var oldDraft) && !oldDraft.Fields.Any(f => f.IsDirty)) inspectorDrafts.Remove(previous);
        inspectorAccount = selected as ExecutionAccount;
        if (inspectorAccount is { } account && inspectorDrafts.TryGetValue(account, out var existing)) inspectorFields = existing.Fields;
        else
        {
            var fields = new List<InspectorFieldItem>();
            foreach (var column in spec.Columns)
            {
                var editable = inspectorAccount is not null && column.Key is "Branch" or "Iban" or "Swift" or "LocalAccount" &&
                    rootSnapshot is not null && baseSnapshot is not null && payrollState is not null && linkState is not null && baseSnapshot.Version >= 2;
                var value = column.Value(selected);
                if (value is null && !editable) continue;
                var display = value is decimal amount ? amount.ToString("#,0.############################", Numbers) : value is null ? "" : Cell(value);
                if (string.IsNullOrWhiteSpace(display) && !editable) continue;
                var item = new InspectorFieldItem { Key = column.Title, RawPropertyName = column.Key, Value = display, EditableValue = display, IsEditable = editable };
                item.PropertyChanged += InspectorFieldChanged;
                fields.Add(item);
            }
            inspectorFields = fields;
            if (inspectorAccount is { } editableAccount && fields.Any(f => f.IsEditable) && rootSnapshot is { } root && baseSnapshot is { } original && payrollState is { } payroll && linkState is { } links)
                inspectorDrafts[editableAccount] = new(root, original, payroll, links, fields);
        }
        if (inspectorFields.Count == 0) { CloseInspector(); return; }
        string? Find(params string[] names) => names.Select(name => inspectorFields.FirstOrDefault(f => f.Key.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        var title = Find("الاسم", "جهة التنفيذ", "الجهة", "عنوان الكتاب") ?? "سجل محدد";
        InspectorTitleText.Text = title;
        InspectorCodeText.Text = Find("الرقم الموحد", "الرقم الإحصائي") ?? "";
        InspectorCodeBadge.Visibility = InspectorCodeText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        InspectorSubtitleText.Text = Find("محل العمل", "الرتبة", "المصرف والفرع") ?? PageTitle.Text;
        var amountText = page == "payroll" ? Find("الصافي") : Find("المبلغ");
        InspectorAmountCard.Visibility = amountText is null ? Visibility.Collapsed : Visibility.Visible;
        InspectorAmountLabel.Text = page == "payroll" ? "صافي الراتب المستحق" : "المبلغ";
        InspectorNetAmountText.Text = amountText ?? "—";
        InspectorTabAll.Content = page == "payroll" ? "ملخص الراتب" : "الملخص";
        InspectorTabSummary.Content = InspectorTabAll.Content;
        InspectorPrimaryActionBtn.Content = OpenRowMenu.Header;
        InspectorPrimaryActionBtn.IsEnabled = OpenRowMenu.IsEnabled;
        InspectorSecondaryActionBtn.Content = SecondRowMenu.Header;
        InspectorSecondaryActionBtn.Visibility = SecondRowMenu.Visibility;
        InspectorSecondaryActionBtn.IsEnabled = SecondRowMenu.IsEnabled;
        InspectorAvatarText.Text = GetArabicInitials(title);
        FilterInspectorCompatibility(); UpdateInspectorSaveState();
        var wasClosed = InspectorDrawer.Visibility != Visibility.Visible;
        InspectorDrawer.Visibility = Visibility.Visible;
        if (wasClosed) InspectorSlide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
            new DoubleAnimation(-20, 0, TimeSpan.FromMilliseconds(170)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }
    private void FilterInspectorCompatibility()
    {
        if (InspectorFieldsList is null) return;
        IEnumerable<InspectorFieldItem> filtered = inspectorFields;
        if (InspectorTabDeductions?.IsChecked == true)
            filtered = filtered.Where(p => p.Key.Contains("استقطاع") || p.Key.Contains("سلفة") || p.Key.Contains("تقاعد") || p.Key.Contains("ضريبة") || p.Key.Contains("غياب"));
        else if (InspectorTabBank?.IsChecked == true)
            filtered = filtered.Where(p => p.Key.Contains("مصرف") || p.Key.Contains("حساب") || p.Key.Contains("IBAN", StringComparison.OrdinalIgnoreCase) || p.Key.Contains("حالة"));
        var q = InspectorFilterBox?.Text.Trim() ?? "";
        if (q.Length > 0) filtered = filtered.Where(p => p.Key.Contains(q, StringComparison.OrdinalIgnoreCase) || (p.IsEditable ? p.EditableValue : p.Value).Contains(q, StringComparison.OrdinalIgnoreCase));
        var list = filtered.ToList();
        InspectorFieldsList.ItemsSource = list.Count == 0 && q.Length == 0 ? inspectorFields : list;
    }
    private void InspectorCompatibilityTabChanged(object sender, RoutedEventArgs e) => FilterInspectorCompatibility();
    private void InspectorCompatibilityKeyDown(object sender, KeyEventArgs e) { if (!Locker.IsLocked && !e.Handled) WindowPreviewKeyDown(sender, e); }
    private void OpenInspectorClick(object sender, RoutedEventArgs e) => UpdateInspectorFromSelection();
    private void CloseInspector()
    {
        if (InspectorDrawer is not null) InspectorDrawer.Visibility = Visibility.Collapsed;
        if (StatusBar is not null && inspectorDrafts.Values.Any(d => d.Fields.Any(f => f.IsDirty)))
            StatusBar.Text = "توجد تعديلات حسابات غير محفوظة؛ ارجع إلى الحساب لحفظها أو التراجع عنها.";
    }
}
