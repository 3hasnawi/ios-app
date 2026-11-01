using System.IO;
using System.Windows;
using Microsoft.Win32;
using SalaryDesktop.Core;

namespace SalaryDesktop;
public partial class AnnualImportWindow : Window
{
    private sealed record MonthRow(string Month, int Count, int Inactive, int Existing);
    private readonly BackupSnapshot source; private AnnualDeductionPreview? preview; private bool working;
    public AnnualDeductionPreview? Result { get; private set; }
    public bool ReplaceExisting { get; private set; }
    private string? pending;
    /// path: ملف من الاستيراد الموحّد يُقرأ عند فتح النافذة مباشرة.
    public AnnualImportWindow(BackupSnapshot source, string? path = null)
    {
        InitializeComponent(); this.source = source; pending = path;
        Closing += (_, e) => { if (working) e.Cancel = true; };
        if (path is not null) Loaded += (_, _) => ChooseFile(this, new RoutedEventArgs());
    }
    private async void ChooseFile(object sender, RoutedEventArgs e)
    {
        if (working) return;
        string path;
        if (pending is not null) { path = pending; pending = null; }
        else
        {
            var dialog = new OpenFileDialog { Title = "اختيار ملف استقطاعات سنوي", Filter = "ملف Excel (*.xlsx)|*.xlsx", CheckFileExists = true, Multiselect = false };
            if (dialog.ShowDialog(this) != true) return;
            path = dialog.FileName;
        }
        working = true; Choose.IsEnabled = false; SaveButton.IsEnabled = false; preview = null; MonthsGrid.ItemsSource = null; Rows.ItemsSource = null; Problems.ItemsSource = null; Notice.Text = "جارٍ فحص الملف وتقسيمه إلى أشهر…";
        try
        {
            var result = await Task.Run(() => AnnualDeductionSpreadsheet.Read(path, source)); preview = result;
            MonthsGrid.ItemsSource = result.Months.Select(m => new MonthRow(m.Month, m.Deductions.Count, m.Deductions.Count(PayrollAnalysis.IsInactive), source.Deductions.Count(d => d.Month == m.Month))).ToArray();
            Rows.ItemsSource = result.Months.SelectMany(m => m.Deductions).ToArray(); Problems.ItemsSource = result.Problems;
            Notice.Text = $"{result.Months.Count:N0} شهر · {result.Months.Sum(m => m.Deductions.Count):N0} استقطاع · {result.Problems.Count:N0} مشكلة. " + (result.Problems.Count == 0 ? "راجع الأشهر قبل الحفظ." : "صحح الملف وأعد اختياره؛ لن يحفظ أي شهر مع وجود خطأ.");
            SaveButton.IsEnabled = result.Problems.Count == 0 && result.Months.Count > 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException)
        { Notice.Text = "تعذّر قراءة الملف: " + ex.Message; }
        finally { working = false; Choose.IsEnabled = true; }
    }
    private void Save(object sender, RoutedEventArgs e)
    {
        if (preview is null || preview.Problems.Count != 0 || preview.Months.Count == 0) return;
        var existing = preview.Months.Select(m => (m.Month, Count: source.Deductions.Count(d => d.Month == m.Month))).Where(m => m.Count > 0).ToArray();
        if (existing.Length > 0)
        {
            var details = string.Join("\n", existing.Select(m => $"{m.Month}: {m.Count:N0} استقطاع"));
            if (Ui.Message(this, "ستُستبدل استقطاعات الأشهر التالية، وتبقى الإصدارات السابقة محلياً:\n" + details + "\nهل تريد حفظ الملف كاملاً؟", "مراجعة الاستبدال", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            ReplaceExisting = true;
        }
        Result = preview; DialogResult = true;
    }
}
