using System.IO;
using System.Windows;
using System.Windows.Controls;
using SalaryDesktop.Core;

namespace SalaryDesktop;
public partial class DeductionComparisonWindow : Window
{
    private readonly BackupSnapshot source;
    private readonly Func<bool> current;
    private DeductionComparisonResult? result;
    private DeductionChange[] shown = [];
    private bool busy;
    public DeductionComparisonWindow(BackupSnapshot source, string month, Func<bool> current)
    {
        InitializeComponent(); this.source = source; this.current = current;
        var months = source.Months.Select(m => m.Name).Where(m => MonthNames.SortKey(m) > 0).Distinct().OrderBy(MonthNames.SortKey).ToArray();
        FromMonth.ItemsSource = months; ToMonth.ItemsSource = months;
        ToMonth.SelectedItem = months.Contains(month) ? month : months.LastOrDefault();
        var key = MonthNames.SortKey(ToMonth.SelectedItem as string ?? "");
        FromMonth.SelectedItem = months.LastOrDefault(m => MonthNames.SortKey(m) < key);
        LevelPicker.SelectedIndex = 0; KindPicker.SelectedIndex = 0;
        Closing += (_, e) => { if (busy) e.Cancel = true; };
    }
    private async void CompareClick(object sender, RoutedEventArgs e)
    {
        if (busy || FromMonth.SelectedItem is not string from || ToMonth.SelectedItem is not string to) { StatusText.Text = "اختر شهرين محفوظين."; return; }
        if (!current()) { StatusText.Text = "تغيّرت البيانات؛ أعد فتح المقارنة."; return; }
        bool empty = !source.Deductions.Any(d => d.Month == from && !PayrollAnalysis.IsInactive(d)) || !source.Deductions.Any(d => d.Month == to && !PayrollAnalysis.IsInactive(d));
        if (empty && Ui.Message(this, "أحد الشهرين لا يحتوي قيود استقطاع فعالة. هل تؤكد خلوه وتريد المقارنة؟", "شهر بلا قيود", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        busy = true; StatusText.Text = "جارٍ احتساب المقارنة…";
        try
        {
            var computed = await Task.Run(() => DeductionComparison.Compare(source, from, to, empty));
            if (!current() || !Equals(FromMonth.SelectedItem, from) || !Equals(ToMonth.SelectedItem, to)) { result = null; shown = []; Rows.ItemsSource = null; StatusText.Text = "تغيّرت البيانات أو الأشهر؛ أعد المقارنة."; return; }
            result = computed; ApplyFilter();
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException) { result = null; shown = []; Rows.ItemsSource = null; StatusText.Text = ex.Message; }
        finally { busy = false; }
    }
    private void MonthChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Rows is null) return;
        result = null; shown = []; Rows.ItemsSource = null;
        if (StatusText is not null) StatusText.Text = "اختر شهرين ثم اضغط عرض المقارنة.";
    }
    private void FilterChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();
    private void ApplyFilter()
    {
        if (Rows is null || result is null) return;
        var items = (LevelPicker.SelectedItem as ComboBoxItem)?.Tag as string == "people" ? result.People : result.Entries;
        var filter = (KindPicker.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        shown = items.Where(x => filter == "all" || x.Kind.ToString() == filter).ToArray();
        Rows.ItemsSource = shown; StatusText.Text = $"{shown.Length:N0} نتيجة · {result.From} ← {result.To}";
    }
    private ReportSheet? Report()
    {
        if (!current() || result is null || !Equals(FromMonth.SelectedItem, result.From) || !Equals(ToMonth.SelectedItem, result.To) || shown.Length == 0)
        { Ui.Message(this, "لا توجد نتائج صالحة للتصدير أو تغيّرت البيانات أو الأشهر؛ أعد المقارنة.", "مقارنة الاستقطاعات"); return null; }
        var cols = new[] { new ReportColumn("الاسم", 26, ReportColumnKind.Text), new ReportColumn("الجهة", 20, ReportColumnKind.Text), new ReportColumn("الإضبارة", 18, ReportColumnKind.Text), new ReportColumn("السابق", 14, ReportColumnKind.Text), new ReportColumn("الحالي", 14, ReportColumnKind.Text), new ReportColumn("الفرق", 14, ReportColumnKind.Text), new ReportColumn("الحالة", 12, ReportColumnKind.Text) };
        var rows = shown.Select(x => new[] { x.Name, x.Entity, x.Note, x.Before.ToString("0.############################", System.Globalization.CultureInfo.InvariantCulture), x.After.ToString("0.############################", System.Globalization.CultureInfo.InvariantCulture), x.Difference.ToString("0.############################", System.Globalization.CultureInfo.InvariantCulture), x.Status }).ToList();
        var level = (LevelPicker.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "حسب الاستقطاع";
        var status = (KindPicker.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "كل الحالات";
        return new ReportSheet(ReportSheet.HeaderFor(source), $"مقارنة الاستقطاعات · {result.From} — {result.To} · {level} · {status}", cols, rows, [], ShowTotal: false);
    }
    private async void PreviewClick(object sender, RoutedEventArgs e) { if (Report() is { } r) await Exports.Preview(this, "مقارنة الاستقطاعات", assets => ReportPdf.Generate([r], assets, DateTime.Now)); }
    private async void PdfClick(object sender, RoutedEventArgs e) { if (Report() is { } r) await Exports.SavePdf(this, "مقارنة الاستقطاعات", assets => ReportPdf.Generate([r], assets, DateTime.Now)); }
    private async void ExcelClick(object sender, RoutedEventArgs e) { if (Report() is { } r) { var entries = shown.ToArray(); await Exports.SaveExcel(this, "مقارنة الاستقطاعات", () => DeductionComparisonReport.Excel(r, entries)); } }
}
