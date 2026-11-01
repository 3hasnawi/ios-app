using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SalaryDesktop.Core;
namespace SalaryDesktop;
public partial class PayrollImportWindow : Window
{
    private readonly BackupSnapshot source;
    private PayrollImportPreview? preview;
    public PayrollImportPreview? Result { get; private set; }
    public bool ReplaceExisting { get; private set; }
    public PayrollImportWindow(BackupSnapshot source, string month)
    { InitializeComponent(); this.source = source; MonthText.Text = month; }
    private void MonthChanged(object sender, TextChangedEventArgs e)
    { if (SaveButton is not null) { preview = null; SaveButton.IsEnabled = false; if (Employees is not null) { Employees.ItemsSource = null; Deductions.ItemsSource = null; Problems.ItemsSource = null; } } }
    private async void ChooseFile(object sender, RoutedEventArgs e)
    {
        var month = MonthText.Text.Trim(); var key = MonthNames.SortKey(month);
        if (key == 0) { MessageBox.Show(this, "اكتب الشهر والسنة، مثل أيلول 2026.", "الشهر"); return; }
        var existing = source.Months.Where(m => MonthNames.SortKey(m.Name) == key).ToArray();
        if (existing.Length > 1) { MessageBox.Show(this, "هناك أكثر من اسم محفوظ للشهر نفسه؛ راجع الأشهر قبل الاستيراد.", "الشهر"); return; }
        if (existing.Length == 1) { month = existing[0].Name; MonthText.Text = month; }
        var dialog = new OpenFileDialog { Title = "اختيار ملف الرواتب والاستقطاعات", Filter = "ملف Excel (*.xlsx)|*.xlsx", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        var path = dialog.FileName; Choose.IsEnabled = false; SaveButton.IsEnabled = false; preview = null; Notice.Text = "جارٍ قراءة الملف وفحصه…";
        try
        {
            var result = await Task.Run(() => PayrollSpreadsheet.Read(path, month));
            if (MonthText.Text.Trim() != month) { Notice.Text = "تغيّر الشهر أثناء القراءة؛ أعد اختيار الملف."; return; }
            preview = result; Employees.ItemsSource = result.Employees; Deductions.ItemsSource = result.Deductions; Problems.ItemsSource = result.Problems;
            Notice.Text = $"{result.Employees.Count:N0} راتب · {result.Deductions.Count:N0} استقطاع · {result.Problems.Count:N0} مشكلة. " + (result.Problems.Count == 0 ? "راجع السجلات قبل الحفظ." : "صحح الملف وأعد اختياره؛ لا يمكن حفظ بيانات ناقصة.");
            SaveButton.IsEnabled = result.Problems.Count == 0 && result.Employees.Count + result.Deductions.Count > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException)
        { Notice.Text = "تعذّر قراءة الملف: " + ex.Message; }
        finally { Choose.IsEnabled = true; }
    }
    private void Save(object sender, RoutedEventArgs e)
    {
        if (preview is null || preview.Month != MonthText.Text.Trim() || preview.Problems.Count != 0) return;
        var salaries = preview.Employees.Count > 0 ? source.Employees.Count(e => e.Month == preview.Month) : 0;
        var deductions = preview.Deductions.Count > 0 ? source.Deductions.Count(d => d.Month == preview.Month) : 0;
        if (salaries > 0 || deductions > 0)
        {
            var answer = MessageBox.Show(this, $"سيُستبدل في شهر {preview.Month} ما يخص الملفات المستوردة: {salaries:N0} راتب و{deductions:N0} استقطاع محفوظ. ستبقى نسخة البيانات السابقة محفوظة على الجهاز. هل تريد الحفظ؟", "مراجعة الاستبدال", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
            ReplaceExisting = true;
        }
        Result = preview; DialogResult = true;
    }
}
