using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Payroll.Core;

namespace Payroll.Desktop;

public partial class MainWindow
{
    private DeductionReportData? _entityReport;
    private int _reportRequest;
    private int _entityRequest;
    private bool _reportExportInProgress;

    private void ResetEntityReport(IReadOnlyList<string> months)
    {
        _entityRequest++;
        InvalidateEntityReport();
        ReportEntityBox.ItemsSource = null;
        ReportMonthBox.ItemsSource = months;
        ReportMonthBox.SelectedIndex = months.Count > 0 ? 0 : -1;
    }

    private void InvalidateEntityReport()
    {
        _reportRequest++;
        _entityReport = null;
        EntityReportGrid.ItemsSource = null;
        EntityReportTotal.Text = "";
        ExportEntityReportButton.IsEnabled = false;
        ExportEntityPdfButton.IsEnabled = false;
    }

    private void EntityReport_Click(object sender, RoutedEventArgs e)
    {
        PageTitle.Text = "كشف استقطاعات جهة";
        HomePanel.Visibility = TablePanel.Visibility = ComparisonPanel.Visibility = ArchivePanel.Visibility = Visibility.Collapsed;
        EntityReportPanel.Visibility = Visibility.Visible;
        if (_dataset is null) EntityReportHint.Text = "افتح نسخة الآيفون أولاً.";
    }

    private async void ReportMonth_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || ReportEntityBox is null || EntityReportGrid is null) return;
        InvalidateEntityReport();
        var request = ++_entityRequest;
        ReportEntityBox.ItemsSource = null;
        if (_dataset is null || ReportMonthBox.SelectedItem is not string month) return;
        var dataset = _dataset;
        try
        {
            var entities = await Task.Run(() => DeductionReport.Entities(dataset, month));
            if (request != _entityRequest || dataset != _dataset) return;
            ReportEntityBox.ItemsSource = entities;
            EntityReportHint.Text = entities.Count == 0 ? "لا توجد جهات في هذا الشهر." : "اختر الجهة ثم اضغط عرض الكشف.";
        }
        catch (Exception ex) { if (request == _entityRequest) EntityReportHint.Text = "تعذّرت قراءة الجهات: " + ex.Message; }
    }

    private void ReportEntity_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || EntityReportGrid is null) return;
        InvalidateEntityReport();
        EntityReportHint.Text = "اضغط عرض الكشف بعد اختيار الشهر والجهة.";
    }

    private async void ShowEntityReport_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null || ReportMonthBox.SelectedItem is not string month || ReportEntityBox.SelectedItem is not string entity)
        { EntityReportHint.Text = "اختر نسخة الآيفون والشهر والجهة أولاً."; return; }
        InvalidateEntityReport();
        var request = _reportRequest;
        var dataset = _dataset;
        ShowEntityReportButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => DeductionReport.Create(dataset, month, entity));
            if (request != _reportRequest || dataset != _dataset) return;
            _entityReport = result;
            EntityReportGrid.ItemsSource = result.Rows.Select((row, index) => new EntityReportDisplay(index + 1, row)).ToArray();
            EntityReportTotal.Text = $"المجموع: {result.Total.ToString("#,0.############################", CultureInfo.InvariantCulture)} د.ع";
            EntityReportHint.Text = $"{result.Rows.Count:N0} قيد بمبلغ. استُبعد {result.MissingAmounts:N0} بلا مبلغ، و{result.ZeroAmounts:N0} صفري، و{result.StatusRecords:N0} حالة. الرتبة غير مسجلة في {result.MissingRanks:N0} قيد.";
            ExportEntityReportButton.IsEnabled = ExportEntityPdfButton.IsEnabled = result.Rows.Count > 0 && !_reportExportInProgress;
        }
        catch (Exception ex) { if (request == _reportRequest) EntityReportHint.Text = "تعذّر إعداد الكشف: " + ex.Message; }
        finally { ShowEntityReportButton.IsEnabled = true; }
    }

    private async void ExportEntityReport_Click(object sender, RoutedEventArgs e)
    {
        if (_entityReport is null) { EntityReportHint.Text = "اعرض الكشف أولاً."; return; }
        var report = _entityReport;
        var header = ReportHeaderBox.Text;
        var picker = new SaveFileDialog { Title = "حفظ كشف الجهة", Filter = "ملف Excel (*.xlsx)|*.xlsx", DefaultExt = ".xlsx", AddExtension = true, OverwritePrompt = true, FileName = "كشف الاستقطاعات.xlsx" };
        if (picker.ShowDialog(this) != true) return;
        var target = Path.GetFullPath(picker.FileName);
        var relative = Path.GetRelativePath(_dataRoot, target);
        if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        { MessageBox.Show(this, "اختر موقعاً خارج مجلد بيانات التطبيق لحماية النسخة المحلية.", "تصدير الكشف"); return; }
        if (!string.Equals(Path.GetExtension(target), ".xlsx", StringComparison.OrdinalIgnoreCase))
        { MessageBox.Show(this, "اختر اسماً ينتهي بـ .xlsx.", "تصدير الكشف"); return; }
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, ".payroll-report-" + Guid.NewGuid().ToString("N") + ".tmp");
        _reportExportInProgress = true;
        ExportEntityReportButton.IsEnabled = ExportEntityPdfButton.IsEnabled = false;
        StatusText.Text = "جارٍ إعداد كشف Excel…";
        try
        {
            await Task.Run(() =>
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    DeductionExcelWriter.Write(report, header, stream);
                File.Move(temporary, target, true);
            });
            StatusText.Text = "حُفظ كشف الجهة بصيغة Excel. راجع السجلات المستبعدة والرتب الناقصة قبل اعتماده.";
        }
        catch (Exception ex) { TryDeletePreview(temporary); MessageBox.Show(this, "تعذّر تصدير الكشف.\n\n" + ex.Message, "تصدير الكشف", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _reportExportInProgress = false; ExportEntityReportButton.IsEnabled = ExportEntityPdfButton.IsEnabled = _entityReport?.Rows.Count > 0; }
    }

    private sealed class EntityReportDisplay(int number, DeductionReportRow row)
    {
        public int Number => number;
        public string Rank => row.Rank;
        public string Name => row.Name;
        public string Amount => row.Amount.ToString("#,0.############################", CultureInfo.InvariantCulture);
        public string Note => row.Note;
    }

    private async void ExportEntityPdf_Click(object sender, RoutedEventArgs e)
    {
        if (_entityReport is null) { EntityReportHint.Text = "اعرض الكشف أولاً."; return; }
        var report = _entityReport;
        var header = ReportHeaderBox.Text;
        var picker = new SaveFileDialog { Title = "حفظ كشف الجهة", Filter = "ملف PDF (*.pdf)|*.pdf", DefaultExt = ".pdf", AddExtension = true, OverwritePrompt = true, FileName = "كشف الاستقطاعات.pdf" };
        if (picker.ShowDialog(this) != true) return;
        var target = Path.GetFullPath(picker.FileName);
        var relative = Path.GetRelativePath(_dataRoot, target);
        if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        { MessageBox.Show(this, "اختر موقعاً خارج مجلد بيانات التطبيق لحماية النسخة المحلية.", "تصدير الكشف"); return; }
        if (!string.Equals(Path.GetExtension(target), ".pdf", StringComparison.OrdinalIgnoreCase))
        { MessageBox.Show(this, "اختر اسماً ينتهي بـ .pdf.", "تصدير الكشف"); return; }
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, ".payroll-report-" + Guid.NewGuid().ToString("N") + ".tmp");
        var fontDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var regular = Path.Combine(fontDirectory, "arial.ttf");
        var bold = Path.Combine(fontDirectory, "arialbd.ttf");
        _reportExportInProgress = true;
        ExportEntityReportButton.IsEnabled = ExportEntityPdfButton.IsEnabled = false;
        StatusText.Text = "جارٍ إعداد كشف PDF… قد تستغرق الكشوف الكبيرة بعض الوقت.";
        try
        {
            await Task.Run(() =>
            {
                if (!File.Exists(regular) || !File.Exists(bold)) throw new FileNotFoundException("يحتاج الكشف خط Arial العادي والعريض المثبّت على ويندوز.");
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    DeductionPdfWriter.Write(report, header, regular, bold, stream);
                File.Move(temporary, target, true);
            });
            StatusText.Text = "حُفظ كشف الجهة بصيغة PDF. راجع السجلات المستبعدة والرتب الناقصة قبل اعتماده.";
        }
        catch (Exception ex) { TryDeletePreview(temporary); MessageBox.Show(this, "تعذّر تصدير الكشف.\n\n" + ex.Message, "تصدير الكشف", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _reportExportInProgress = false; ExportEntityReportButton.IsEnabled = ExportEntityPdfButton.IsEnabled = _entityReport?.Rows.Count > 0; }
    }
}
