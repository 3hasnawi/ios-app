using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SalaryDesktop.Core;

namespace SalaryDesktop;

public sealed class InspectorFieldItem : INotifyPropertyChanged
{
    private string editableValue = "";
    public required string Key { get; init; }
    public required string RawPropertyName { get; init; }
    public required string Value { get; init; }
    public bool IsEditable { get; init; }
    public string EditableValue
    {
        get => editableValue;
        set
        {
            if (editableValue == value) return;
            editableValue = value;
            PropertyChanged?.Invoke(this, new(nameof(EditableValue)));
            PropertyChanged?.Invoke(this, new(nameof(IsDirty)));
        }
    }
    public bool IsDirty => IsEditable && EditableValue != Value;
    public Visibility ReadOnlyVisibility => IsEditable ? Visibility.Collapsed : Visibility.Visible;
    public Visibility EditorVisibility => IsEditable ? Visibility.Visible : Visibility.Collapsed;
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow
{
    // لا تُعدّل نسخ الصفوف بالانعكاس؛ الحسابات تُحفظ عبر نفس المسار المحمي الذي تستخدمه نافذة الحساب.
    private sealed record InspectorDraft(BackupSnapshot Root, BackupSnapshot Base, PayrollImportState Payroll, TransferLinksSnapshot Links, List<InspectorFieldItem> Fields);
    private readonly Dictionary<ExecutionAccount, InspectorDraft> inspectorDrafts = new();
    private List<InspectorFieldItem> inspectorFields = [];
    private ExecutionAccount? inspectorAccount;
    private bool inspectorSaving;
    private string? inspectorReselectEntity;

    private void InitializeInspectorEvents() => DataTable.SelectionChanged += DataTable_InspectorSelectionChanged;
    private void DataTable_InspectorSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataTable.SelectedItem is null) { CloseInspector(); return; }
        RefreshInspectorCompatibility();
    }
    private void UpdateInspectorFromSelection() => RefreshInspectorCompatibility();
    private void InspectorTabChanged(object sender, RoutedEventArgs e) => FilterInspectorCompatibility();
    private void InspectorFilterBox_TextChanged(object sender, TextChangedEventArgs e) => FilterInspectorCompatibility();
    private void CloseInspectorClick(object sender, RoutedEventArgs e) => CloseInspector();

    private void InspectorFieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InspectorFieldItem.IsDirty)) UpdateInspectorSaveState();
    }
    private void UpdateInspectorSaveState()
    {
        if (InspectorInlineSaveBtn is null) return;
        var dirty = inspectorFields.Any(field => field.IsDirty);
        var stale = inspectorAccount is { } account && inspectorDrafts.TryGetValue(account, out var draft) &&
            (!ReferenceEquals(rootSnapshot, draft.Root) || !ReferenceEquals(baseSnapshot, draft.Base) || payrollState?.Revision != draft.Payroll.Revision || linkState?.Revision != draft.Links.Revision);
        InspectorInlineSaveBtn.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        InspectorInlineSaveBtn.IsEnabled = dirty && !inspectorSaving && !stale;
        if (InspectorDiscardBtn is not null) { InspectorDiscardBtn.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed; InspectorDiscardBtn.IsEnabled = !inspectorSaving; }
        if (InspectorStatusHintText is not null)
            InspectorStatusHintText.Text = !inspectorFields.Any(f => f.IsEditable)
                ? "هذه الحقول للعرض والنسخ؛ افتح التفاصيل للإجراءات المتاحة."
                : dirty && stale ? "تغيّرت البيانات؛ انسخ التعديلات إن احتجتها، ثم تراجع عنها وأعد إدخالها."
                : dirty ? "تعديلات غير محفوظة لهذا الحساب · Ctrl+S للحفظ."
                : "يمكن تعديل بيانات الحساب مباشرةً · F2 للتعديل.";
    }
    private async void SaveInspectorInlineChangesClick(object sender, RoutedEventArgs e)
    {
        if (inspectorSaving || inspectorAccount is not { } account || !inspectorDrafts.TryGetValue(account, out var draft) || !draft.Fields.Any(f => f.IsDirty)) return;
        if (!ReferenceEquals(rootSnapshot, draft.Root) || !ReferenceEquals(baseSnapshot, draft.Base) || payrollState?.Revision != draft.Payroll.Revision || linkState?.Revision != draft.Links.Revision)
        {
            Ui.Message(this, "تغيّرت البيانات منذ بدء التعديل. راجع القيم وأعد فتح الحساب قبل الحفظ.", "تعديل الحساب", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string Field(string key) => draft.Fields.Single(f => f.RawPropertyName == key).EditableValue;
        var changed = account with { Branch = Field("Branch"), Iban = Field("Iban"), Swift = Field("Swift"), LocalAccount = Field("LocalAccount") };
        inspectorSaving = true; InspectorFieldsList.IsEnabled = false; UpdateInspectorSaveState();
        try
        {
            var updated = await Task.Run(() =>
            {
                if (payrollStore.Load(draft.Root).Revision != draft.Payroll.Revision) throw new InvalidDataException("تغيّرت بيانات الأشهر؛ أعد فتح الحساب.");
                return linkStore.SaveAccount(draft.Base, draft.Links, account, changed);
            });
            inspectorDrafts.Remove(account);
            if (!ReferenceEquals(rootSnapshot, draft.Root) || !ReferenceEquals(baseSnapshot, draft.Base) || linkState?.Revision != draft.Links.Revision)
            {
                Ui.Message(this, "حُفظ الحساب للنسخة السابقة؛ لم تتغير النسخة المعروضة.", "تعديل الحساب", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var reselect = page == "accounts" && Equals(DataTable.SelectedItem, account);
            linkState = updated; snapshot = updated.Snapshot;
            if (reselect) inspectorReselectEntity = account.Entity;
            RenderPage();
            StatusBar.Text = "حُفظ الحساب محلياً؛ راجع الروابط واحفظ نسخة احتياطية تشمل التعديل.";
            RefreshBackupReminder(true);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or OverflowException)
        { Ui.Message(this, "تعذّر حفظ الحساب؛ التعديلات ما زالت في اللوحة.\n" + ex.Message, "تعديل الحساب", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { inspectorSaving = false; InspectorFieldsList.IsEnabled = true; UpdateInspectorSaveState(); }
    }
    private void DiscardInspectorChangesClick(object sender, RoutedEventArgs e)
    {
        if (inspectorSaving || inspectorAccount is not { } account) return;
        inspectorDrafts.Remove(account);
        RefreshInspectorCompatibility();
    }
    private void CopyInspectorFieldClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: InspectorFieldItem field }) return;
        var text = field.IsEditable ? field.EditableValue : field.Value;
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); StatusBar.Text = "نُسخت قيمة الحقل."; }
        catch (System.Runtime.InteropServices.ExternalException) { Ui.Message(this, "الحافظة مشغولة؛ أعد محاولة النسخ.", "نسخ", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void WindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (InspectorDrawer?.Visibility != Visibility.Visible) return;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S && inspectorFields.Any(f => f.IsDirty))
        { e.Handled = true; SaveInspectorInlineChangesClick(sender, new RoutedEventArgs()); return; }
        if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.F2)
        { e.Handled = true; var editor = FindInspectorEditor(InspectorFieldsList); if (editor is not null) { editor.Focus(); editor.SelectAll(); } else InspectorFilterBox.Focus(); return; }
        if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.F4 && Keyboard.FocusedElement is not ComboBox && InspectorSecondaryActionBtn.IsVisible && InspectorSecondaryActionBtn.IsEnabled && DataTable.SelectedItem is not null)
        { e.Handled = true; OpenSecondRow(sender, new RoutedEventArgs()); }
    }
    private static TextBox? FindInspectorEditor(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBox { IsVisible: true, IsEnabled: true } editor) return editor;
            if (FindInspectorEditor(child) is { } found) return found;
        }
        return null;
    }
    private bool ConfirmDiscardInspectorChanges()
    {
        if (inspectorSaving) { Ui.Message(this, "انتظر انتهاء حفظ الحساب.", "تعديل الحساب"); return false; }
        if (!inspectorDrafts.Values.Any(d => d.Fields.Any(f => f.IsDirty))) return true;
        if (Ui.Message(this, "توجد تعديلات حسابات لم تُحفظ. هل تريد إغلاق البرنامج وفقدان هذه التعديلات؟", "تعديلات غير محفوظة", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return false;
        inspectorDrafts.Clear(); return true;
    }
    private static string GetArabicInitials(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return "م";
        var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"{parts[0][0]}{parts[1][0]}" : fullName.Length >= 2 ? fullName[..2] : fullName;
    }
}
