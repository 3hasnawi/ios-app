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
    private readonly LocalSnapshotStore store = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SalaryDesktop", "Backups"));
    private string page = "home";
    private bool loading;
    private int bankRequest;
    private int transferRequest;
    private TransferSnapshot? preparedTransfer;
    private BackupSnapshot? preparedSource;
    private string? previewFolder;
    private readonly DispatcherTimer searchDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private static readonly CultureInfo Numbers = CultureInfo.GetCultureInfo("en-US");

    public MainWindow()
    {
        InitializeComponent();
        searchDelay.Tick += (_, _) => { searchDelay.Stop(); RenderPage(); };
        Loaded += async (_, _) => await LoadStoredSnapshot();
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
            var loaded = await Task.Run(() => store.Import(dialog.FileName));
            SetSnapshot(loaded);
            SourceMessage.Text = "حُفظت نسخة محلية بعد فحصها؛ ملف الآيفون الأصلي لم يتغير.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException or OverflowException)
        {
            SourceMessage.Text = snapshot is null ? "لم تُفتح نسخة احتياطية." : "تعذّر فتح النسخة الجديدة؛ ما زالت البيانات السابقة معروضة.";
            MessageBox.Show(this, $"تعذّر قراءة النسخة والتحقق منها.\n{ex.Message}", "فتح النسخة", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { loading = false; }
    }

    private void SetSnapshot(BackupSnapshot loaded)
    {
        snapshot = loaded;
        OpenReconciliationButton.IsEnabled = true;
        MonthPicker.ItemsSource = loaded.Months.Select(m => m.Name).ToArray();
        MonthPicker.SelectedIndex = loaded.Months.Count > 0 ? 0 : -1;
        StatusBar.Text = "البيانات في نسخة محلية على هذا الجهاز؛ لا توجد مزامنة تلقائية مع الآيفون.";
        RenderPage();
    }
    private async Task LoadStoredSnapshot()
    {
        if (loading || snapshot is not null) return;
        loading = true;
        try
        {
            var loaded = await Task.Run(store.LoadCurrent);
            if (loaded is not null) { SetSnapshot(loaded); SourceMessage.Text = "تم فتح النسخة المحلية المحفوظة على هذا الجهاز."; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException or OverflowException)
        { SourceMessage.Text = "تعذّر فتح النسخة المحلية. لم تُستبدل؛ اختر نسخة احتياطية صالحة. " + ex.Message; }
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
        BankPanel.Visibility = page == "bank" ? Visibility.Visible : Visibility.Collapsed;
        TransferPanel.Visibility = page == "transfer" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        bool available = page is "payroll" or "deductions" or "groups" or "accounts" or "archive";
        DataPanel.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        UnavailablePanel.Visibility = page != "home" && page != "bank" && page != "transfer" && page != "settings" && !available ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = page switch
        {
            "payroll" => "الرواتب", "deductions" => "الاستقطاعات", "groups" => "جهات التنفيذ",
            "accounts" => "حسابات جهات التنفيذ", "archive" => "الأرشيف", "bank" => "كشف حساب البنك",
            "transfer" => "ملفات التحويل", "settings" => "الإعدادات", _ => "الرئيسية"
        };
        if (page == "home") { RenderHome(); return; }
        if (page == "bank") { RenderBank(); return; }
        if (page == "transfer") { RenderTransfers(); return; }
        if (page == "settings") { BackupExport.IsEnabled = snapshot is not null; return; }
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

    private async void ExportBackup(object sender, RoutedEventArgs e)
    {
        var source = snapshot;
        if (source is null) return;
        var dialog = new SaveFileDialog { Title = "حفظ نسخة احتياطية", Filter = "نسخة احتياطية (*.zip)|*.zip", FileName = "نسخة الرواتب.zip", AddExtension = true, DefaultExt = ".zip", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        BackupExport.IsEnabled = false;
        try
        {
            await Task.Run(() => LocalSnapshotStore.ExportOriginal(source, dialog.FileName));
            MessageBox.Show(this, "حُفظت نسخة مطابقة للنسخة المفتوحة بعد التحقق منها.", "النسخة الاحتياطية", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { MessageBox.Show(this, "تعذّر حفظ النسخة.\n" + ex.Message, "النسخة الاحتياطية", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { if (page == "settings") BackupExport.IsEnabled = snapshot is not null; }
    }

    private sealed record TransferRow(string Title, string Amount, string Branch, string Iban, string Issues);
    private TransferGroup CurrentTransferGroup => ((TransferGroupPicker.SelectedItem as ComboBoxItem)?.Tag as string) switch
    {
        "other" => TransferGroup.OtherBanks, "anbar" => TransferGroup.Anbar,
        "unresolved" => TransferGroup.Unresolved, _ => TransferGroup.Rafidain
    };
    private void TransferGroupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized && page == "transfer") RenderTransfers();
    }
    private async void RenderTransfers()
    {
        var ticket = ++transferRequest; var source = snapshot; var month = SelectedMonth; var group = CurrentTransferGroup;
        preparedTransfer = null; preparedSource = null; TransferExport.IsEnabled = false; TransferTable.ItemsSource = null;
        if (source is null || month.Length == 0) { TransferNotice.Text = "افتح نسخة الآيفون واختر الشهر أولاً."; return; }
        TransferNotice.Text = "جارٍ مراجعة بيانات التحويل محلياً…";
        try
        {
            var result = await Task.Run(() => TransferPreparation.Prepare(source, month));
            if (ticket != transferRequest || !ReferenceEquals(source, snapshot) || month != SelectedMonth || page != "transfer" || group != CurrentTransferGroup) return;
            var entries = result.Entries.Where(x => x.Group == group).ToArray();
            TransferTable.ItemsSource = entries.Select(x => new TransferRow(x.Title, Amount(x.Amount), x.Account?.Branch ?? "—", x.Account?.Iban ?? "—", string.Join(" · ", TransferPreparation.Issues(x)))).ToArray();
            var issues = entries.Count(x => TransferPreparation.Issues(x).Count != 0);
            var unresolved = result.Entries.Count(x => x.Group == TransferGroup.Unresolved);
            var warnings = result.SourceWarnings.ToList();
            if (unresolved > 0) warnings.Add($"{unresolved:N0} جهة تحتاج تصنيفاً.");
            if (issues > 0) warnings.Add($"{issues:N0} جهة تحتاج مراجعة الحساب أو المبلغ.");
            if (!TransferPreparation.ValidIban(result.PayerAccount) || string.IsNullOrWhiteSpace(result.Payer)) warnings.Add("اسم الجهة الدافعة أو حسابها غير صالح في النسخة.");
            TransferNotice.Text = $"{entries.Length:N0} جهة · {Amount(entries.Sum(x => x.Amount))}" + (warnings.Count == 0 ? " · راجع البيانات قبل التصدير." : " · " + string.Join(" · ", warnings));
            preparedTransfer = result; preparedSource = source;
            TransferExport.IsEnabled = group is TransferGroup.Rafidain or TransferGroup.OtherBanks && entries.Length > 0 && warnings.Count == 0;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or OverflowException)
        {
            if (ticket == transferRequest && ReferenceEquals(source, snapshot) && month == SelectedMonth && page == "transfer") TransferNotice.Text = "تعذّر تجهيز التحويل: " + ex.Message;
        }
    }
    private async void ExportTransfer(object sender, RoutedEventArgs e)
    {
        var plan = preparedTransfer; var source = preparedSource; var group = CurrentTransferGroup;
        if (plan is null || source is null || !ReferenceEquals(source, snapshot) || plan.Month != SelectedMonth || page != "transfer") return;
        var dialog = new SaveFileDialog { Title = "حفظ ملف التحويل", Filter = "ملف CSV (*.csv)|*.csv", FileName = group == TransferGroup.Rafidain ? "Rafidain.csv" : "OtherBanks.csv", AddExtension = true, DefaultExt = ".csv", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        if (!string.Equals(Path.GetExtension(dialog.FileName), ".csv", StringComparison.OrdinalIgnoreCase)) { MessageBox.Show(this, "اختر اسماً ينتهي بالامتداد CSV.", "ملف التحويل", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        TransferExport.IsEnabled = false;
        try
        {
            var date = DateOnly.FromDateTime(DateTime.Now);
            await Task.Run(() => TransferFileSave.Write(source, plan, group, date, dialog.FileName));
            if (!ReferenceEquals(source, snapshot) || plan.Month != SelectedMonth || page != "transfer" || group != CurrentTransferGroup)
                MessageBox.Show(this, "حُفظ ملف لقطة الشهر السابقة؛ تغيّر العرض أثناء الحفظ. راجع الملف قبل استخدامه.", "ملف التحويل", MessageBoxButton.OK, MessageBoxImage.Warning);
            else MessageBox.Show(this, "حُفظ الملف بعد التحقق من صفوفه. راجعه قبل إرساله إلى المصرف.", "ملف التحويل", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException)
        { MessageBox.Show(this, "تعذّر حفظ ملف التحويل.\n" + ex.Message, "ملف التحويل", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { if (page == "transfer") RenderTransfers(); }
    }

    private sealed record BankRow(string Title, string Value);
    private void OpenReconciliation(object sender, RoutedEventArgs e)
    {
        if (snapshot is null) return;
        try { new ReconciliationWindow(BankReconciliation.ReadFromBackup(snapshot)) { Owner = this }.ShowDialog(); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or OverflowException)
        { MessageBox.Show(this, "تعذّر فتح دفتر المطابقة المحفوظ. لم تُغيّر البيانات.\n" + ex.Message, "مطابقة الحساب", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void RenderBank()
    {
        var ticket = ++bankRequest;
        var source = snapshot;
        var month = SelectedMonth;
        BankRows.ItemsSource = null; BankTotalText.Text = "—"; BankReconcileText.Text = "";
        if (source is null || month.Length == 0) { BankNotice.Text = "افتح نسخة الآيفون واختر الشهر أولاً."; return; }
        BankNotice.Text = "جارٍ احتساب بيانات الشهر محلياً…";
        try
        {
            var result = await Task.Run(() => BankStatementCalculator.Calculate(source, month));
            if (ticket != bankRequest || !ReferenceEquals(source, snapshot) || month != SelectedMonth || page != "bank") return;
            BankTotalText.Text = Amount(result.BankTotal);
            var warnings = result.Warnings.ToList();
            if (!result.ExecutionMatched) warnings.Add("مطابقة ملف جهات التنفيذ غير مكتملة أو المبلغ مختلف.");
            BankNotice.Text = warnings.Count == 0 ? "راجع البيانات والمبلغ مع نسخة الآيفون قبل الاعتماد." : "مبلغ أولي · " + string.Join(" · ", warnings);
            var rows = new List<BankRow>
            {
                new("صافي الرواتب كاش", Amount(result.Cash)), new("صافي الرواتب بطاقة", Amount(result.Card)),
                new("صافي غير مصنف", Amount(result.Unclassified)), new("مجموع الصافي", Amount(result.Net)),
                new("الإجازات المتراكمة", Amount(result.Manual.AccumulatedLeave)), new("مكافآت المنتسبين", Amount(result.Manual.EmployeeRewards)),
                new("قائمة مفردة", Amount(result.Manual.SingleList)), new("مجموع استقطاعات المصارف", Amount(result.BankDeductions))
            };
            rows.AddRange(result.Breakdown.Select(pair => new BankRow(pair.Key, Amount(pair.Value))));
            rows.Add(new("اسمي العقود · أساس الحساب", Amount(result.ContractNominal)));
            rows.Add(new("12% من الاسمي", Amount(result.ContractNominalShare)));
            rows.Add(new("توقيفات العقود", Amount(result.ContractRetirement)));
            rows.Add(new("مساهمة العقود المحتسبة", Amount(result.ContractContribution)));
            BankRows.ItemsSource = rows;
            BankReconcileText.Text = result.ExecutionMatched ? $"المبلغان متطابقان · {Amount(result.ExecutionDeductions)}" :
                $"ملف جهات التنفيذ: {Amount(result.ExecutionDeductions)} · الفرق: {Amount(result.ExecutionDifference)} · قيود بلا مبلغ: {result.MissingExecutionAmounts:N0}";
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or OverflowException)
        {
            if (ticket == bankRequest && ReferenceEquals(source, snapshot) && month == SelectedMonth && page == "bank")
                BankNotice.Text = "تعذّر احتساب كشف البنك: " + ex.Message;
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
