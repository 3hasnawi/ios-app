using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using Payroll.Core;

namespace Payroll.Desktop;

public partial class MainWindow : Window
{
    private const string AllMonths = "كل الأشهر";
    private readonly string _dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IraqiPayrollNew");
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private string? _dataset;
    private bool _deductions = true;
    private int _page;
    private bool _hasMore;
    private int _queryGeneration;
    private CancellationTokenSource? _importCancellation;
    private bool _closeAfterCancel;

    public MainWindow()
    {
        InitializeComponent();
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); _page = 0; _ = RefreshRowsAsync(); };
        Loaded += async (_, _) => await LoadPreviousAsync();
        Closing += WindowClosing;
        Closed += (_, _) => ClearPreviewFiles();
    }

    private async Task LoadPreviousAsync()
    {
        try
        {
            var marker = Path.Combine(_dataRoot, "current-dataset.txt");
            if (!File.Exists(marker)) return;
            var id = (await File.ReadAllTextAsync(marker)).Trim();
            if (!id.StartsWith("dataset-", StringComparison.Ordinal) || !Guid.TryParseExact(id[8..], "N", out _)) return;
            var path = Path.Combine(_dataRoot, id);
            if (!File.Exists(Path.Combine(path, "data.sqlite")) || !File.Exists(Path.Combine(path, "original.zip"))) return;
            var info = await Task.Run(() => PayrollRepository.GetInfo(path));
            Activate(path, info);
            StatusText.Text = "تم فتح البيانات المحلية السابقة للقراءة.";
        }
        catch (Exception ex) { StatusText.Text = "تعذّر فتح البيانات المحلية: " + ex.Message; }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_importCancellation != null) return;
        var picker = new OpenFileDialog { Title = "اختر النسخة الاحتياطية من الآيفون", Filter = "نسخة احتياطية ZIP (*.zip)|*.zip", CheckFileExists = true, Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        _importCancellation = new CancellationTokenSource();
        ImportButton.IsEnabled = false;
        var progress = new Progress<string>(text => StatusText.Text = text);
        try
        {
            var result = await Task.Run(() => IosBackupImporter.ImportAsync(picker.FileName, _dataRoot, progress, _importCancellation.Token));
            var info = await Task.Run(() => PayrollRepository.GetInfo(result.Directory));
            Activate(result.Directory, info);
            try
            {
                var temporary = Path.Combine(_dataRoot, "current-" + Guid.NewGuid().ToString("N") + ".tmp");
                await File.WriteAllTextAsync(temporary, Path.GetFileName(result.Directory));
                File.Move(temporary, Path.Combine(_dataRoot, "current-dataset.txt"), true);
            }
            catch (Exception ex) { StatusText.Text = "تم استيراد البيانات، لكن تعذّر تذكرها عند الفتح القادم: " + ex.Message; return; }
            StatusText.Text = result.ArchivedPayrollRows > 0
                ? $"اكتمل الاستيراد. عُرض آخر شهرين؛ بقيت {result.ArchivedPayrollRows:N0} سجلات أقدم داخل ملف النسخة الأصلية المحفوظ."
                : "اكتمل الاستيراد المحلي؛ بقي ملف النسخة الأصلية محفوظاً.";
        }
        catch (OperationCanceledException) { StatusText.Text = "أُلغي الاستيراد؛ بقيت البيانات السابقة كما هي."; }
        catch (Exception ex) { MessageBox.Show(this, "تعذّر استيراد النسخة. بقيت البيانات السابقة كما هي.\n\n" + ex.Message, "استيراد النسخة", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally
        {
            _importCancellation.Dispose();
            _importCancellation = null;
            ImportButton.IsEnabled = true;
            if (_closeAfterCancel) { _closeAfterCancel = false; Close(); }
        }
    }

    private void Activate(string path, DatasetInfo info)
    {
        _dataset = path;
        DatasetSummary.Text = $"الرواتب: {info.Employees:N0} سجل · الاستقطاعات: {info.Deductions:N0} قيد\nالملاك: {info.Staff:N0} سجل · الحسابات: {info.Accounts:N0}";
        MonthBox.ItemsSource = new[] { AllMonths }.Concat(info.Months).ToArray();
        MonthBox.SelectedIndex = 0;
        PreviousMonthBox.ItemsSource = info.Months;
        CurrentMonthBox.ItemsSource = info.Months;
        if (info.Months.Count > 0) CurrentMonthBox.SelectedIndex = 0;
        if (info.Months.Count > 1) PreviousMonthBox.SelectedIndex = 1;
        ComparisonGrid.ItemsSource = null;
        ResetArchive();
        if (TablePanel.Visibility == Visibility.Visible) { _page = 0; _ = RefreshRowsAsync(); }
    }

    private void Home_Click(object sender, RoutedEventArgs e) { PageTitle.Text = "الرئيسية"; HomePanel.Visibility = Visibility.Visible; TablePanel.Visibility = Visibility.Collapsed; ComparisonPanel.Visibility = Visibility.Collapsed; ArchivePanel.Visibility = Visibility.Collapsed; }
    private void Deductions_Click(object sender, RoutedEventArgs e) => ShowTable(true);
    private void Salaries_Click(object sender, RoutedEventArgs e) => ShowTable(false);
    private void ShowTable(bool deductions)
    {
        _deductions = deductions; _page = 0;
        PageTitle.Text = deductions ? "الاستقطاعات" : "الرواتب";
        PlaceColumn.Header = deductions ? "الجهة" : "محل العمل";
        HomePanel.Visibility = Visibility.Collapsed; TablePanel.Visibility = Visibility.Visible;
        ComparisonPanel.Visibility = Visibility.Collapsed;
        ArchivePanel.Visibility = Visibility.Collapsed;
        _ = RefreshRowsAsync();
    }
    private void Comparison_Click(object sender, RoutedEventArgs e)
    {
        PageTitle.Text = "مقارنة الاستقطاعات";
        HomePanel.Visibility = TablePanel.Visibility = Visibility.Collapsed;
        ArchivePanel.Visibility = Visibility.Collapsed;
        ComparisonPanel.Visibility = Visibility.Visible;
    }

    private void ComparisonMonth_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || ComparisonGrid is null) return;
        ComparisonGrid.ItemsSource = null;
        ComparisonHint.Text = "اضغط عرض المقارنة بعد اختيار الشهرين. تُقارن أسماء الجهات كما وردت في النسخة.";
    }

    private async void RunComparison_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null) { StatusText.Text = "افتح نسخة الآيفون أولاً."; return; }
        if (PreviousMonthBox.SelectedItem is not string previous || CurrentMonthBox.SelectedItem is not string current || previous == current)
        { StatusText.Text = "اختر شهرين مختلفين للمقارنة."; return; }
        var dataset = _dataset;
        RunComparisonButton.IsEnabled = false;
        PreviousMonthBox.IsEnabled = CurrentMonthBox.IsEnabled = false;
        try
        {
            var rows = await Task.Run(() => DeductionComparison.Compare(dataset, previous, current));
            if (dataset != _dataset) return;
            ComparisonGrid.ItemsSource = rows.Select(x => new ComparisonDisplay(x)).ToArray();
            var missing = rows.Sum(x => x.PreviousMissing + x.CurrentMissing);
            ComparisonHint.Text = $"{rows.Count:N0} جهة. تُقارن الأسماء كما وردت في النسخة؛ الأسماء المختلفة لا تُدمج تلقائياً. " +
                (missing > 0 ? $"تنبيه: {missing:N0} سجل بلا مبلغ؛ المجاميع والفروق غير مكتملة." : "لا توجد مبالغ مفقودة في السجلات المقارنة.");
            StatusText.Text = "اكتملت المقارنة.";
        }
        catch (Exception ex) { StatusText.Text = "تعذّرت المقارنة: " + ex.Message; }
        finally { RunComparisonButton.IsEnabled = true; PreviousMonthBox.IsEnabled = CurrentMonthBox.IsEnabled = true; }
    }

    private sealed class ComparisonDisplay(DeductionComparisonRow row)
    {
        private static string Format(decimal value) => value.ToString("#,0.############################", CultureInfo.InvariantCulture);
        public string Entity => row.Entity;
        public int PreviousCount => row.PreviousCount;
        public int CurrentCount => row.CurrentCount;
        public string PreviousDisplay => Format(row.PreviousAmount) + (row.PreviousMissing > 0 ? " (ناقص)" : "");
        public string CurrentDisplay => Format(row.CurrentAmount) + (row.CurrentMissing > 0 ? " (ناقص)" : "");
        public string DifferenceDisplay => row.PreviousMissing + row.CurrentMissing > 0 ? "غير مكتمل" : Format(row.Difference);
        public string MissingDisplay => $"{row.PreviousMissing} / {row.CurrentMissing}";
    }
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) { if (!IsLoaded) return; _page = 0; _ = RefreshRowsAsync(); }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (!IsLoaded) return; _searchTimer.Stop(); _searchTimer.Start(); }
    private void Previous_Click(object sender, RoutedEventArgs e) { if (_page > 0) { _page--; _ = RefreshRowsAsync(); } }
    private void Next_Click(object sender, RoutedEventArgs e) { if (_hasMore) { _page++; _ = RefreshRowsAsync(); } }

    private async Task RefreshRowsAsync()
    {
        var generation = ++_queryGeneration;
        if (_dataset is null) { RecordsGrid.ItemsSource = null; PageInfo.Text = "افتح نسخة الآيفون أولاً."; PreviousButton.IsEnabled = NextButton.IsEnabled = false; return; }
        var dataset = _dataset; var deductions = _deductions; var month = MonthBox.SelectedItem as string; var query = SearchBox.Text; var page = _page;
        if (month == AllMonths) month = null;
        try
        {
            var result = await Task.Run(() => PayrollRepository.GetPage(dataset, deductions, month, query, page));
            if (generation != _queryGeneration) return;
            RecordsGrid.ItemsSource = result.Rows.Select(row => new DisplayRow(row)).ToArray();
            _hasMore = result.HasMore; PreviousButton.IsEnabled = page > 0; NextButton.IsEnabled = result.HasMore;
            PageInfo.Text = $"الصفحة {page + 1:N0} · {result.Rows.Count:N0} سجل معروض";
        }
        catch (Exception ex) { if (generation == _queryGeneration) StatusText.Text = "تعذّرت قراءة الجدول: " + ex.Message; }
    }

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_importCancellation is null) return;
        e.Cancel = true; _closeAfterCancel = true; _importCancellation.Cancel();
        StatusText.Text = "جارٍ إلغاء الاستيراد بأمان…";
    }

    private sealed class DisplayRow(PayrollRow row)
    {
        public string Month => row.Month;
        public string Name => row.Name;
        public string Rank => row.Rank;
        public string Place => row.Place;
        public string Note => row.Note;
        public string AmountDisplay => row.Amount is null ? "غير مسجل" : decimal.TryParse(row.Amount, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) ? amount.ToString("#,0.############################", CultureInfo.InvariantCulture) : row.Amount;
    }
}
