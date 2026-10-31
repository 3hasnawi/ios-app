using System.Globalization;
using System.IO;
using System.Diagnostics;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;
using SalaryDesktop.Core;

namespace SalaryDesktop;

public partial class MainWindow : Window
{
    private BackupSnapshot? snapshot;
    private string page = "home";
    private bool loading;
    private string? previewFolder;
    private readonly DispatcherTimer searchDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private static readonly CultureInfo Numbers = CultureInfo.GetCultureInfo("en-US");

    public MainWindow()
    {
        InitializeComponent();
        searchDelay.Tick += (_, _) => { searchDelay.Stop(); RenderPage(); };
        Closed += (_, _) => { if (previewFolder is not null) { try { Directory.Delete(previewFolder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } } };
    }

    private async void OpenBackup(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        var dialog = new OpenFileDialog { Title = "اختيار نسخة الآيفون الاحتياطية", Filter = "نسخة احتياطية (*.zip)|*.zip", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        loading = true;
        SourceMessage.Text = "جارٍ قراءة النسخة والتحقق من فهرسها دون تغيير الملف الأصلي…";
        try
        {
            var loaded = await Task.Run(() => BackupReader.Read(dialog.FileName));
            snapshot = loaded;
            MonthPicker.ItemsSource = loaded.Months.Select(m => m.Name).ToArray();
            MonthPicker.SelectedIndex = loaded.Months.Count > 0 ? 0 : -1;
            SourceMessage.Text = "البيانات للعرض فقط. النسخة الأصلية لم تتغير، ولا يتم رفعها إلى الإنترنت.";
            StatusBar.Text = "النسخة الحالية مفتوحة من الجهاز. أعد فتحها عند تشغيل البرنامج من جديد.";
            RenderPage();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException or OverflowException)
        {
            SourceMessage.Text = snapshot is null ? "لم تُفتح نسخة احتياطية." : "تعذّر فتح النسخة الجديدة؛ ما زالت البيانات السابقة معروضة.";
            MessageBox.Show(this, $"تعذّر قراءة النسخة والتحقق منها.\n{ex.Message}", "فتح النسخة", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { loading = false; }
    }

    private void Navigate(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string next }) return;
        page = next;
        if (SearchBox is not null) SearchBox.Text = "";
        RenderPage();
    }
    private void MonthChanged(object sender, SelectionChangedEventArgs e) => RenderPage();
    private void SearchChanged(object sender, TextChangedEventArgs e) { searchDelay.Stop(); searchDelay.Start(); }
    private string SelectedMonth => MonthPicker.SelectedItem as string ?? "";
    private static string Amount(decimal amount) => amount.ToString("#,0.##", Numbers);

    private void RenderPage()
    {
        if (!IsInitialized || HomePanel is null || DataTable is null) return;
        HomePanel.Visibility = page == "home" ? Visibility.Visible : Visibility.Collapsed;
        bool available = page is "payroll" or "deductions" or "groups" or "accounts" or "archive";
        DataPanel.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        UnavailablePanel.Visibility = page != "home" && !available ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = page switch
        {
            "payroll" => "الرواتب", "deductions" => "الاستقطاعات", "groups" => "جهات التنفيذ",
            "accounts" => "حسابات جهات التنفيذ", "archive" => "الأرشيف", "bank" => "كشف حساب البنك",
            "transfer" => "ملفات التحويل", "settings" => "الإعدادات", _ => "الرئيسية"
        };
        if (page == "home") { RenderHome(); return; }
        if (!available)
        {
            UnavailableTitle.Text = PageTitle.Text;
            UnavailableMessage.Text = "هذا القسم غير مكتمل في نسخة ويندوز بعد. لا يمكن استخدامه للحساب أو التصدير أو اعتماد التحويلات حالياً.";
            return;
        }
        DataTable.Columns.Clear();
        if (snapshot is null)
        {
            TableStatus.Text = "افتح نسخة الآيفون أولاً لعرض البيانات.";
            DataTable.ItemsSource = null;
            return;
        }
        var tokens = PayrollAnalysis.Normalize(SearchBox.Text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Matches(params string?[] parts)
        {
            if (tokens.Length == 0) return true;
            var text = PayrollAnalysis.Normalize(string.Join(' ', parts.Where(p => p is not null)));
            return tokens.All(token => text.Contains(token, StringComparison.Ordinal));
        }
        switch (page)
        {
            case "payroll":
                Column("الاسم", "Name", 2); Column("الرتبة", "Rank", 1); Column("محل العمل", "Workplace", 2); Column("الصافي", "Net", 1, true);
                var employees = snapshot.Employees.Where(v => v.Month == SelectedMonth && Matches(v.Name, v.Rank, v.Workplace)).ToArray();
                DataTable.ItemsSource = employees;
                TableStatus.Text = $"{employees.Length:N0} سجل · انقر نقراً مزدوجاً على المنتسب لعرض تفاصيله؛ القيم الناقصة تظهر فارغة.";
                break;
            case "deductions":
                Column("الاسم", "Name", 2); Column("الرتبة", "Rank", 1); Column("جهة التنفيذ", "Title", 2); Column("المبلغ", "Amount", 1, true); Column("الملاحظة", "Note", 2);
                var deductions = snapshot.Deductions.Where(v => v.Month == SelectedMonth && Matches(v.Name, v.Title, v.Rank, v.Note)).ToArray();
                DataTable.ItemsSource = deductions;
                TableStatus.Text = $"{deductions.Length:N0} قيد · تظهر القيود كما سُجلت، بما فيها الحالات غير المحتسبة.";
                break;
            case "groups":
                Column("جهة التنفيذ", "Title", 3); Column("عدد الأشخاص", "People", 1, true); Column("المبلغ", "Total", 1, true); Column("مبالغ ناقصة", "MissingAmounts", 1, true);
                var groups = PayrollAnalysis.Groups(snapshot, SelectedMonth).Where(v => Matches(v.Title)).ToArray();
                DataTable.ItemsSource = groups;
                TableStatus.Text = $"{groups.Length:N0} جهة · المبالغ الناقصة لم تُحتسب في المجموع؛ راجع العمود الأخير.";
                break;
            case "accounts":
                Column("الجهة", "Entity", 2); Column("المصرف والفرع", "Branch", 2); Column("IBAN", "Iban", 2); Column("SWIFT", "Swift", 1); Column("الحساب المحلي", "LocalAccount", 1);
                var accounts = snapshot.Accounts.Where(v => Matches(v.Entity, v.Branch, v.Iban)).ToArray();
                DataTable.ItemsSource = accounts;
                TableStatus.Text = $"{accounts.Length:N0} حساب · عرض محلي للقراءة فقط.";
                break;
            case "archive":
                Column("عنوان الكتاب", "Title", 3); Column("الشهر", "Month", 1); Column("الجهة", "Entity", 2); Column("الصفحات", "Pages", 1, true);
                var books = snapshot.Books.Where(v => Matches(v.Title, v.Month, v.Entity)).ToArray();
                DataTable.ItemsSource = books;
                TableStatus.Text = $"{books.Length:N0} كتاب · انقر نقراً مزدوجاً على الكتاب لفتحه محلياً في عارض PDF.";
                break;
        }
    }

    private async void OpenArchiveBook(object sender, MouseButtonEventArgs e)
    {
        if (page == "payroll" && DataTable.SelectedItem is Employee employee)
        {
            var detail = new EmployeeDetailsWindow(employee) { Owner = this };
            detail.ShowDialog();
            return;
        }
        if (page != "archive" || snapshot is null || DataTable.SelectedItem is not ArchiveBook book) return;
        try
        {
            previewFolder ??= Path.Combine(Path.GetTempPath(), "salary-desktop-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(previewFolder);
            var destination = Path.Combine(previewFolder, Guid.NewGuid().ToString("N") + ".pdf");
            var source = snapshot;
            await Task.Run(() => BackupReader.CopyBook(source, book, destination));
            Process.Start(new ProcessStartInfo(destination) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            MessageBox.Show(this, $"تعذّر فتح الكتاب.\n{ex.Message}", "الأرشيف", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RenderHome()
    {
        if (snapshot is null || SelectedMonth.Length == 0)
        {
            NetLabel.Text = "صافي الرواتب للشهر المختار"; NetAmount.Text = "—"; PayrollCount.Text = "لا توجد بيانات مفتوحة"; DataWarning.Text = "";
            SnapshotSummary.Text = "اختر نسخة احتياطية للبدء."; return;
        }
        var totals = PayrollAnalysis.Summarize(snapshot, SelectedMonth);
        NetLabel.Text = totals.MissingNet > 0 ? "صافي الرواتب المتاح · بيانات ناقصة" : "صافي الرواتب للشهر المختار";
        NetAmount.Text = totals.Employees == 0 || totals.MissingNet == totals.Employees ? "غير متاح" : Amount(totals.NetTotal);
        PayrollCount.Text = $"{totals.Employees:N0} منتسب · {SelectedMonth}";
        DataWarning.Text = totals.MissingNet > 0 || totals.MissingAmounts > 0
            ? $"تنبيه: {totals.MissingNet:N0} راتب بلا صافي و{totals.MissingAmounts:N0} قيد بلا مبلغ؛ المجاميع أولية."
            : "النتائج للعرض والمراجعة؛ قارنها مع نسخة الآيفون قبل الاعتماد.";
        SnapshotSummary.Text = $"{snapshot.Months.Count:N0} شهر · {snapshot.Accounts.Count:N0} حساب جهة · {snapshot.Books.Count:N0} كتاب في الأرشيف";
    }
    private void Column(string title, string path, double weight, bool numeric = false)
    {
        var binding = new Binding(path) { TargetNullValue = "—", StringFormat = numeric ? "{0:#,0.##}" : null, ConverterCulture = Numbers };
        DataTable.Columns.Add(new DataGridTextColumn { Header = title, Binding = binding, Width = new DataGridLength(weight, DataGridLengthUnitType.Star) });
    }
}
