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
    private BackupSnapshot? baseSnapshot;
    private BackupSnapshot? rootSnapshot;
    private PayrollImportState? payrollState;
    private TransferLinksSnapshot? linkState;
    private readonly LocalSnapshotStore store = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SalaryDesktop", "Backups"));
    private readonly LocalTransferLinks linkStore = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SalaryDesktop", "TransferLinks"));
    private readonly LocalPayrollImports payrollStore = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SalaryDesktop", "PayrollImports"));
    private readonly LocalReconciliationStore reconciliationStore = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SalaryDesktop", "Reconciliations"));
    private string page = "home";
    private bool loading;
    private int bankRequest;
    private int transferRequest;
    private TransferSnapshot? preparedTransfer;
    private BackupSnapshot? preparedSource;
    private string? previewFolder;
    private IReadOnlyList<StaffMember>? staff; private string? staffDigest; private string? staffLoadingDigest; private string? staffError;
    private readonly DispatcherTimer searchDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private static readonly CultureInfo Numbers = CultureInfo.GetCultureInfo("en-US");

    public MainWindow()
    {
        InitializeComponent();
        searchDelay.Tick += (_, _) => { searchDelay.Stop(); RenderPage(); };
        Loaded += async (_, _) => { await LoadStoredSnapshot(); RenderPage(); };
        Closed += (_, _) => { if (previewFolder is not null) { try { Directory.Delete(previewFolder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } } };
    }

    private async void StartFromExcel(object sender, RoutedEventArgs e)
    {
        if (loading || snapshot is not null) return;
        var dialog = new PayrollImportWindow(null, "") { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } preview) return;
        if (snapshot is not null) { MessageBox.Show(this, "فُتحت بيانات أخرى؛ لم تُنشأ قاعدة جديدة.", "بدء البيانات"); return; }
        loading = true; StartFromExcelButton.IsEnabled = false; SourceMessage.Text = "جارٍ إنشاء البيانات والتحقق منها محلياً…";
        var temporary = Path.Combine(Path.GetTempPath(), "salary-initial-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var loaded = await Task.Run(() => { var created = InitialDataset.Create(preview, temporary); return store.Import(temporary, expectedDigest: created.SourceDigest, requireEmptyStore: true); });
            SetSnapshot(loaded); SourceMessage.Text = "أُنشئت البيانات من ملف Excel وحُفظت محلياً دون تعديل الملف الأصلي.";
            MessageBox.Show(this, "حُفظ الشهر الأول. استورد بقية الملفات والحسابات، واحفظ نسخة احتياطية بعد المراجعة.", "بدء البيانات", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException)
        { SourceMessage.Text = "لم تُنشأ البيانات؛ لم يُعدّل ملف Excel."; MessageBox.Show(this, "تعذّر إنشاء البيانات.\n" + ex.Message, "بدء البيانات", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { loading = false; StartFromExcelButton.IsEnabled = true; try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    private async void OpenBackup(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        var dialog = new OpenFileDialog { Title = "اختيار نسخة الآيفون الاحتياطية", Filter = "نسخة احتياطية (*.zip)|*.zip", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        var selectedPath = dialog.FileName;
        loading = true;
        SourceMessage.Text = "جارٍ قراءة النسخة والتحقق من فهرسها دون تغيير الملف الأصلي…";
        try
        {
            string? inspectedDigest = null;
            if (rootSnapshot is { } previous && snapshot is { } shown)
            {
                var inspected = await Task.Run(() => BackupReader.Read(selectedPath));
                inspectedDigest = inspected.SourceDigest;
                var hasLocalChanges = payrollState?.Revision is not null || linkState?.Revision is not null || (await Task.Run(() => reconciliationStore.Load(shown))).Revision is not null;
                if (inspected.SourceDigest != previous.SourceDigest && hasLocalChanges && MessageBox.Show(this,
                    "توجد تعديلات محلية مرتبطة بالنسخة الحالية. ستبقى محفوظة على الجهاز، لكنها لا تندمج تلقائياً مع النسخة الأخرى. احفظ نسخة احتياطية تشمل التعديلات أولاً إذا أردت نقلها. هل تريد فتح النسخة الأخرى؟",
                    "تغيير النسخة المفتوحة", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                { SourceMessage.Text = "لم تتغير النسخة المفتوحة."; return; }
            }
            var loaded = await Task.Run(() => store.Import(selectedPath, expectedDigest: inspectedDigest));
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
        var payroll = payrollStore.Load(loaded);
        var links = linkStore.Load(payroll.Snapshot);
        ApplySources(loaded, payroll, links, null);
    }
    private void ApplySources(BackupSnapshot root, PayrollImportState payroll, TransferLinksSnapshot links, string? month)
    {
        rootSnapshot = root; payrollState = payroll; baseSnapshot = payroll.Snapshot; linkState = links; snapshot = links.Snapshot;
        OpenReconciliationButton.IsEnabled = true;
        MonthPicker.ItemsSource = snapshot.Months.Select(m => m.Name).ToArray();
        if (month is not null && snapshot.Months.Any(m => m.Name == month)) MonthPicker.SelectedItem = month;
        else MonthPicker.SelectedIndex = snapshot.Months.Count > 0 ? 0 : -1;
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
        bool available = page is "payroll" or "deductions" or "groups" or "accounts" or "archive" or "staff";
        DataPanel.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        StaffReloadButton.Visibility = page == "staff" ? Visibility.Visible : Visibility.Collapsed;
        AccountImportButton.Visibility = page == "accounts" ? Visibility.Visible : Visibility.Collapsed;
        PayrollImportButton.Visibility = page is "payroll" or "deductions" ? Visibility.Visible : Visibility.Collapsed;
        AnnualImportButton.Visibility = page is "payroll" or "deductions" ? Visibility.Visible : Visibility.Collapsed;
        UnavailablePanel.Visibility = page != "home" && page != "bank" && page != "transfer" && page != "settings" && !available ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = page switch
        {
            "payroll" => "الرواتب", "deductions" => "الاستقطاعات", "groups" => "جهات التنفيذ",
            "accounts" => "حسابات جهات التنفيذ", "staff" => "الملاك", "archive" => "الأرشيف", "bank" => "كشف حساب البنك",
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
                TableStatus.Text = $"{accounts.Length:N0} حساب · انقر نقراً مزدوجاً لتعديل بيانات الحساب المحفوظ.";
                break;
            case "staff":
                Column("الاسم", "Name", 2); Column("الرتبة", "Rank", 1); Column("الرقم الموحد", "Unified", 1); Column("الحالة", "Status", 1); Column("محل العمل", "Workplace", 2);
                if (staffDigest != snapshot.SourceDigest || staff is null)
                {
                    DataTable.ItemsSource = null; TableStatus.Text = staffDigest == snapshot.SourceDigest && staffError is not null ? staffError : "جارٍ قراءة الملاك من النسخة المحلية…";
                    if (staffLoadingDigest != snapshot.SourceDigest && !(staffDigest == snapshot.SourceDigest && staffError is not null)) LoadStaff(snapshot);
                    break;
                }
                var staffRows = staff.Where(v => Matches(v.Name, v.Mother, v.Unified, v.Stat, v.Rank, v.Workplace, v.Status)).ToArray();
                DataTable.ItemsSource = staffRows; TableStatus.Text = $"{staffRows.Length:N0} سجل · انقر نقراً مزدوجاً لعرض تفاصيل المنتسب."; break;
            case "archive":
                Column("عنوان الكتاب", "Title", 3); Column("الشهر", "Month", 1); Column("الجهة", "Entity", 2); Column("الصفحات", "Pages", 1, true);
                var books = snapshot.Books.Where(v => Matches(v.Title, v.Month, v.Entity)).ToArray();
                DataTable.ItemsSource = books;
                TableStatus.Text = $"{books.Length:N0} كتاب · انقر نقراً مزدوجاً على الكتاب لفتحه محلياً في عارض PDF.";
                break;
        }
    }

    private void ReloadStaff(object sender, RoutedEventArgs e)
    {
        if (snapshot is null || staffLoadingDigest is not null) return;
        staff = null; staffError = null; staffDigest = null; RenderPage();
    }
    private async void LoadStaff(BackupSnapshot source)
    {
        var digest = source.SourceDigest; staffLoadingDigest = digest;
        try
        {
            var loaded = await Task.Run(() => StaffRecords.Read(source));
            if (snapshot?.SourceDigest != digest) return;
            staff = loaded; staffDigest = digest; staffError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException)
        { if (snapshot?.SourceDigest == digest) { staff = null; staffDigest = digest; staffError = "تعذّر قراءة الملاك: " + ex.Message; } }
        finally { if (staffLoadingDigest == digest) staffLoadingDigest = null; if (page == "staff" && snapshot?.SourceDigest == digest) RenderPage(); }
    }
    private async void OpenArchiveBook(object sender, MouseButtonEventArgs e)
    {
        if (page == "staff" && DataTable.SelectedItem is StaffMember member)
        { new StaffDetailsWindow(member) { Owner = this }.ShowDialog(); return; }
        if (page == "payroll" && DataTable.SelectedItem is Employee employee)
        {
            var detail = new EmployeeDetailsWindow(employee, snapshot) { Owner = this };
            detail.ShowDialog();
            return;
        }
        if (page == "accounts" && DataTable.SelectedItem is ExecutionAccount account)
        {
            var original = baseSnapshot; var links = linkState; var active = snapshot; var root = rootSnapshot; var payroll = payrollState;
            if (original is null || links is null || active is null || root is null || payroll is null) return;
            var dialog = new AccountEditorWindow(account) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Result is not { } edited) return;
            try
            {
                var updated = await Task.Run(() => { if (payrollStore.Load(root).Revision != payroll.Revision) throw new InvalidDataException("تغيّرت بيانات الأشهر؛ أعد فتح النسخة."); return linkStore.SaveAccount(original, links, account, edited); });
                if (!ReferenceEquals(original, baseSnapshot) || !ReferenceEquals(active, snapshot))
                { MessageBox.Show(this, "حُفظ الحساب للنسخة السابقة؛ لم تتغير النسخة المعروضة.", "تعديل الحساب", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
                linkState = updated; snapshot = updated.Snapshot; RenderPage();
                MessageBox.Show(this, "حُفظ الحساب محلياً. راجع التصنيفات والكتب المرتبطة به، واحفظ نسخة احتياطية تشمل التعديلات.", "تعديل الحساب", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or OverflowException)
            { MessageBox.Show(this, "تعذّر حفظ الحساب.\n" + ex.Message, "تعديل الحساب", MessageBoxButton.OK, MessageBoxImage.Warning); }
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
    private async void ImportAccounts(object sender, RoutedEventArgs e)
    {
        var original = baseSnapshot; var active = snapshot; var links = linkState; var root = rootSnapshot; var payroll = payrollState;
        if (original is null || active is null || links is null || root is null || payroll is null) { MessageBox.Show(this, "افتح نسخة الآيفون أولاً لاستيراد الحسابات إليها.", "استيراد الحسابات"); return; }
        var dialog = new OpenFileDialog { Title = "اختيار ملف حسابات الجهات", Filter = "ملف Excel (*.xlsx)|*.xlsx", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        var path = dialog.FileName; AccountImportButton.IsEnabled = false;
        try
        {
            var preview = await Task.Run(() => AccountSpreadsheet.Read(path));
            if (!ReferenceEquals(active, snapshot)) { MessageBox.Show(this, "تغيّرت النسخة المفتوحة؛ أعد اختيار ملف الحسابات.", "استيراد الحسابات"); return; }
            var review = new AccountImportReviewWindow(preview) { Owner = this };
            if (review.ShowDialog() != true) return;
            var updated = await Task.Run(() => { if (payrollStore.Load(root).Revision != payroll.Revision) throw new InvalidDataException("تغيّرت بيانات الأشهر؛ أعد فتح النسخة."); return linkStore.ImportAccounts(original, links, preview); });
            if (!ReferenceEquals(original, baseSnapshot) || !ReferenceEquals(active, snapshot))
            { MessageBox.Show(this, "حُفظت الحسابات للنسخة السابقة، ولم تُغيّر النسخة المعروضة.", "استيراد الحسابات", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            linkState = updated; snapshot = updated.Snapshot; RenderPage();
            MessageBox.Show(this, "حُفظت الحسابات محلياً بعد فحصها. راجع تصنيف الجهات واحفظ نسخة احتياطية تشمل التعديلات.", "استيراد الحسابات", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or OverflowException)
        { MessageBox.Show(this, "تعذّر استيراد الحسابات.\n" + ex.Message, "استيراد الحسابات", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { AccountImportButton.IsEnabled = true; }
    }
    private async void EditBankDetails(object sender, RoutedEventArgs e)
    {
        var active = snapshot; var month = SelectedMonth; var original = baseSnapshot; var links = linkState; var root = rootSnapshot; var payroll = payrollState;
        if (active is null || original is null || links is null || root is null || payroll is null || month.Length == 0) { MessageBox.Show(this, "افتح نسخة الآيفون واختر الشهر أولاً.", "بيانات الحساب"); return; }
        try
        {
            var payer = TransferPreparation.Payer(active); var manual = BankStatementCalculator.Manual(active, month);
            var dialog = new BankDetailsWindow(month, payer.Name, payer.Iban, manual) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Result is not { } input) return;
            var updated = await Task.Run(() => { if (payrollStore.Load(root).Revision != payroll.Revision) throw new InvalidDataException("تغيّرت بيانات الأشهر؛ أعد فتح النافذة."); return linkStore.SaveBankDetails(original, links, month, input.Name, input.Iban, input.Amounts); });
            if (!ReferenceEquals(active, snapshot) || !ReferenceEquals(root, rootSnapshot)) { MessageBox.Show(this, "حُفظت البيانات للنسخة السابقة؛ لم تتغير النسخة المعروضة.", "بيانات الحساب"); return; }
            linkState = updated; snapshot = updated.Snapshot; RenderPage();
            MessageBox.Show(this, "حُفظت بيانات الحساب ومبالغ الشهر. احفظ نسخة احتياطية لتضمين التغييرات.", "بيانات الحساب", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException)
        { MessageBox.Show(this, "تعذّر حفظ بيانات الحساب.\n" + ex.Message, "بيانات الحساب", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void ImportAnnual(object sender, RoutedEventArgs e)
    {
        var root = rootSnapshot; var payroll = payrollState; var active = snapshot; var links = linkState; var original = baseSnapshot;
        if (root is null || payroll is null || active is null || links is null || original is null) { MessageBox.Show(this, "افتح نسخة الآيفون أولاً.", "استيراد الاستقطاعات السنوية"); return; }
        var dialog = new AnnualImportWindow(active) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } preview || preview.Problems.Count != 0) return;
        if (!ReferenceEquals(active, snapshot)) { MessageBox.Show(this, "تغيّرت النسخة المفتوحة؛ أعد مراجعة الملف.", "الاستيراد السنوي"); return; }
        AnnualImportButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => {
                if (linkStore.Load(original).Revision != links.Revision) throw new InvalidDataException("تغيّرت روابط الحسابات؛ أعد فتح النسخة.");
                var updated = payrollStore.ImportBatch(root, payroll, preview.Months, dialog.ReplaceExisting);
                var updatedLinks = linkStore.Load(updated.Snapshot);
                if (updatedLinks.Revision != links.Revision) throw new InvalidDataException("حُفظت الأشهر، لكن روابط الحسابات تغيّرت أثناء الحفظ؛ أعد فتح النسخة.");
                return (updated, updatedLinks);
            });
            if (!ReferenceEquals(root, rootSnapshot) || !ReferenceEquals(active, snapshot)) { MessageBox.Show(this, "حُفظت الأشهر للنسخة السابقة؛ لم تتغير النسخة المعروضة.", "الاستيراد السنوي"); return; }
            ApplySources(root, result.updated, result.updatedLinks, preview.Months[0].Month);
            MessageBox.Show(this, "حُفظت الأشهر معاً محلياً. راجع الاستقطاعات ثم احفظ نسخة احتياطية تشمل التغييرات.", "الاستيراد السنوي", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or OverflowException)
        { MessageBox.Show(this, "تعذّر حفظ الملف السنوي.\n" + ex.Message, "الاستيراد السنوي", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { AnnualImportButton.IsEnabled = true; }
    }
    private async void ImportPayroll(object sender, RoutedEventArgs e)
    {
        var root = rootSnapshot; var payroll = payrollState; var active = snapshot; var links = linkState; var original = baseSnapshot;
        if (root is null || payroll is null || active is null || links is null || original is null)
        { MessageBox.Show(this, "افتح نسخة الآيفون أولاً لاستيراد بيانات الشهر إليها.", "استيراد الرواتب"); return; }
        var dialog = new PayrollImportWindow(active, SelectedMonth) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } preview) return;
        var replace = dialog.ReplaceExisting;
        if (!ReferenceEquals(active, snapshot)) { MessageBox.Show(this, "تغيّرت النسخة المفتوحة؛ أعد مراجعة الملف.", "استيراد الرواتب"); return; }
        PayrollImportButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => {
                if (linkStore.Load(original).Revision != links.Revision) throw new InvalidDataException("تغيّرت روابط الحسابات؛ أعد فتح النسخة أولاً.");
                var updatedPayroll = payrollStore.Import(root, payroll, preview, replace);
                var updatedLinks = linkStore.Load(updatedPayroll.Snapshot);
                if (updatedLinks.Revision != links.Revision) throw new InvalidDataException("حُفظ الشهر، لكن روابط الحسابات تغيّرت أثناء الحفظ؛ أعد فتح النسخة لمراجعتها.");
                return (updatedPayroll, updatedLinks);
            });
            if (!ReferenceEquals(root, rootSnapshot) || !ReferenceEquals(active, snapshot))
            { MessageBox.Show(this, "حُفظت بيانات الشهر للنسخة السابقة؛ لم تتغير النسخة المعروضة.", "استيراد الرواتب", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            ApplySources(root, result.updatedPayroll, result.updatedLinks, preview.Month);
            MessageBox.Show(this, "حُفظت بيانات الشهر محلياً؛ راجع التقارير والمبالغ، ثم احفظ نسخة احتياطية تشمل التغييرات.", "استيراد الرواتب", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or OverflowException)
        { MessageBox.Show(this, "تعذّر إكمال تحديث العرض. أعد فتح النسخة للتحقق مما حُفظ.\n" + ex.Message, "استيراد الرواتب", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { PayrollImportButton.IsEnabled = true; }
    }

    private async void ExportBackup(object sender, RoutedEventArgs e)
    {
        var source = snapshot; var original = baseSnapshot; var links = linkState; var root = rootSnapshot; var payroll = payrollState;
        if (source is null || original is null || links is null || root is null || payroll is null) return;
        var dialog = new SaveFileDialog { Title = "حفظ نسخة احتياطية", Filter = "نسخة احتياطية (*.zip)|*.zip", FileName = "نسخة الرواتب.zip", AddExtension = true, DefaultExt = ".zip", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        var destination = dialog.FileName;
        BackupExport.IsEnabled = false;
        try
        {
            var changed = await Task.Run(() => {
                if (linkStore.Load(original).Revision != links.Revision) throw new InvalidDataException("تغيّرت روابط الحسابات؛ أعد فتح النسخة قبل التصدير.");
                if (payrollStore.Load(root).Revision != payroll.Revision) throw new InvalidDataException("تغيّرت بيانات الأشهر؛ أعد فتح النسخة قبل التصدير.");
                var ledger = reconciliationStore.Load(source);
                ReconciliationBackupExport.Write(source, ledger.Periods, destination);
                return linkStore.Load(original).Revision != links.Revision || payrollStore.Load(root).Revision != payroll.Revision || reconciliationStore.Load(source).Revision != ledger.Revision;
            });
            MessageBox.Show(this, changed || !ReferenceEquals(source, snapshot) ? "حُفظت لقطة سابقة؛ تغيّرت البيانات أثناء التصدير، لذا أعد حفظ النسخة. اختبر الاستعادة على الآيفون." : "حُفظت النسخة مع الروابط ودفتر المطابقة بعد فحصها. اختبر استعادتها على الآيفون قبل الاعتماد.", "النسخة الاحتياطية", MessageBoxButton.OK, changed ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { MessageBox.Show(this, "تعذّر حفظ النسخة.\n" + ex.Message, "النسخة الاحتياطية", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { if (page == "settings") BackupExport.IsEnabled = snapshot is not null; }
    }

    private sealed record TransferRow(TransferEntry Entry, string Title, string Amount, string Branch, string Iban, string Issues);
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
            TransferTable.ItemsSource = entries.Select(x => new TransferRow(x, x.Title, Amount(x.Amount), x.Account?.Branch ?? "—", x.Account?.Iban ?? "—", string.Join(" · ", TransferPreparation.Issues(x)))).ToArray();
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
    private async void LinkTransferAccount(object sender, RoutedEventArgs e)
    {
        var original = baseSnapshot; var selected = TransferTable.SelectedItem as TransferRow; var links = linkState; var active = snapshot; var root = rootSnapshot; var payroll = payrollState;
        if (original is null || selected is null || links is null || active is null || root is null || payroll is null || !ReferenceEquals(active, preparedSource))
        { MessageBox.Show(this, "اختر جهة من الجدول أولاً بعد اكتمال تحميلها.", "ربط حساب الجهة", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var dialog = new AccountLinkWindow(selected.Entry, active.Accounts) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedAccount is not { } account) return;
        if (!ReferenceEquals(active, snapshot) || SelectedMonth != selected.Entry.Month) { MessageBox.Show(this, "تغيّرت البيانات؛ أعد اختيار الجهة.", "ربط حساب الجهة"); return; }
        try
        {
            var updated = await Task.Run(() => { if (payrollStore.Load(root).Revision != payroll.Revision) throw new InvalidDataException("تغيّرت بيانات الأشهر؛ أعد فتح النسخة."); return linkStore.SaveLink(original, links, selected.Entry, account); });
            if (!ReferenceEquals(original, baseSnapshot) || !ReferenceEquals(active, snapshot))
            { MessageBox.Show(this, "حُفظ الربط للنسخة السابقة، ولم تُغيّر النسخة المعروضة.", "ربط حساب الجهة", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            linkState = updated; snapshot = updated.Snapshot; RenderPage();
            MessageBox.Show(this, "حُفظ الربط على الجهاز؛ راجع تصنيف الجهة بعد تحديث القائمة. احفظ نسخة احتياطية تشمل التعديلات.", "ربط حساب الجهة", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or OverflowException)
        { MessageBox.Show(this, "تعذّر حفظ الربط.\n" + ex.Message, "ربط حساب الجهة", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void ClassifyTransfer(object sender, RoutedEventArgs e)
    {
        var original = baseSnapshot; var selected = TransferTable.SelectedItem as TransferRow; var links = linkState; var active = snapshot; var root = rootSnapshot; var payroll = payrollState;
        if (original is null || selected is null || links is null || active is null || root is null || payroll is null || !ReferenceEquals(active, preparedSource))
        { MessageBox.Show(this, "اختر جهة من الجدول أولاً بعد اكتمال تحميلها.", "تصنيف الجهة", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var dialog = new TransferCategoryWindow(selected.Entry) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var category = dialog.SelectedGroup;
        if (!ReferenceEquals(active, snapshot) || SelectedMonth != selected.Entry.Month) { MessageBox.Show(this, "تغيّرت البيانات؛ أعد اختيار الجهة.", "تصنيف الجهة"); return; }
        try
        {
            var updated = await Task.Run(() => { if (payrollStore.Load(root).Revision != payroll.Revision) throw new InvalidDataException("تغيّرت بيانات الأشهر؛ أعد فتح النسخة."); return linkStore.SaveCategory(original, links, selected.Entry, category); });
            if (!ReferenceEquals(original, baseSnapshot) || !ReferenceEquals(active, snapshot))
            { MessageBox.Show(this, "حُفظ التصنيف للنسخة السابقة، ولم تُغيّر النسخة المعروضة.", "تصنيف الجهة", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            linkState = updated; snapshot = updated.Snapshot; RenderPage();
            MessageBox.Show(this, "حُفظ التصنيف على الجهاز. راجع التنبيهات واحفظ نسخة احتياطية تشمل التعديلات.", "تصنيف الجهة", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or OverflowException)
        { MessageBox.Show(this, "تعذّر حفظ التصنيف.\n" + ex.Message, "تصنيف الجهة", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void ExportTransfer(object sender, RoutedEventArgs e)
    {
        var plan = preparedTransfer; var source = preparedSource; var group = CurrentTransferGroup;
        var original = baseSnapshot; var links = linkState; var root = rootSnapshot; var payroll = payrollState;
        if (plan is null || source is null || original is null || links is null || root is null || payroll is null || !ReferenceEquals(source, snapshot) || plan.Month != SelectedMonth || page != "transfer") return;
        var dialog = new SaveFileDialog { Title = "حفظ ملف التحويل", Filter = "ملف CSV (*.csv)|*.csv", FileName = group == TransferGroup.Rafidain ? "Rafidain.csv" : "OtherBanks.csv", AddExtension = true, DefaultExt = ".csv", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        if (!string.Equals(Path.GetExtension(dialog.FileName), ".csv", StringComparison.OrdinalIgnoreCase)) { MessageBox.Show(this, "اختر اسماً ينتهي بالامتداد CSV.", "ملف التحويل", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var destination = dialog.FileName;
        TransferExport.IsEnabled = false;
        try
        {
            var date = DateOnly.FromDateTime(DateTime.Now);
            var changed = await Task.Run(() => {
                if (linkStore.Load(original).Revision != links.Revision) throw new InvalidDataException("تغيّرت روابط الحسابات؛ أعد فتح النسخة قبل التصدير.");
                if (payrollStore.Load(root).Revision != payroll.Revision) throw new InvalidDataException("تغيّرت بيانات الأشهر؛ أعد فتح النسخة قبل التصدير.");
                TransferFileSave.Write(source, plan, group, date, destination);
                return linkStore.Load(original).Revision != links.Revision || payrollStore.Load(root).Revision != payroll.Revision;
            });
            if (changed || !ReferenceEquals(source, snapshot) || plan.Month != SelectedMonth || page != "transfer" || group != CurrentTransferGroup)
                MessageBox.Show(this, "حُفظ ملف لقطة الشهر السابقة؛ تغيّر العرض أثناء الحفظ. راجع الملف قبل استخدامه.", "ملف التحويل", MessageBoxButton.OK, MessageBoxImage.Warning);
            else MessageBox.Show(this, "حُفظ الملف بعد التحقق من صفوفه. راجعه قبل إرساله إلى المصرف.", "ملف التحويل", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException)
        { MessageBox.Show(this, "تعذّر حفظ ملف التحويل.\n" + ex.Message, "ملف التحويل", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { if (page == "transfer") RenderTransfers(); }
    }
    private async void OpenSalaryItems(object sender, RoutedEventArgs e)
    {
        var source = snapshot; var month = SelectedMonth;
        if (source is null || month.Length == 0) { MessageBox.Show(this, "افتح نسخة الآيفون واختر الشهر أولاً.", "بنود الرواتب"); return; }
        SalaryItemsButton.IsEnabled = false; StatusBar.Text = "جارٍ حساب بنود الرواتب…";
        try
        {
            var groups = await Task.Run(() => SalaryItemReports.Groups(source, month));
            if (!ReferenceEquals(source, snapshot) || month != SelectedMonth) { MessageBox.Show(this, "تغيّر الشهر أو البيانات؛ أعد فتح التقرير.", "بنود الرواتب"); return; }
            new SalaryItemsWindow(source, month, groups) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException)
        { MessageBox.Show(this, "تعذّر حساب بنود الرواتب.\n" + ex.Message, "بنود الرواتب", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { SalaryItemsButton.IsEnabled = true; StatusBar.Text = "البيانات في نسخة محلية على هذا الجهاز؛ لا توجد مزامنة تلقائية مع الآيفون."; }
    }
    private async void OpenSalaryReview(object sender, RoutedEventArgs e)
    {
        var source = snapshot; var month = SelectedMonth;
        if (source is null || month.Length == 0) { MessageBox.Show(this, "افتح نسخة الآيفون واختر الشهر أولاً.", "تدقيق الرواتب"); return; }
        AuditHomeButton.IsEnabled = false; StatusBar.Text = "جارٍ تدقيق الرواتب ومقارنة الشهر السابق…";
        try
        {
            var result = await Task.Run(() => SalaryReview.Prepare(source, month, source.StaffCount > 0 ? StaffRecords.Read(source) : []));
            if (!ReferenceEquals(source, snapshot) || month != SelectedMonth) { MessageBox.Show(this, "تغيّر الشهر أو البيانات؛ أعد فتح التدقيق.", "تدقيق الرواتب"); return; }
            new SalaryReviewWindow(result) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException)
        { MessageBox.Show(this, "تعذّر تدقيق الرواتب.\n" + ex.Message, "تدقيق الرواتب", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { AuditHomeButton.IsEnabled = true; StatusBar.Text = "البيانات في نسخة محلية على هذا الجهاز؛ لا توجد مزامنة تلقائية مع الآيفون."; }
    }
    private async void ImportSakkalFont(object sender, RoutedEventArgs e)
    {
        var active = snapshot; var original = baseSnapshot; var links = linkState; var root = rootSnapshot; var payroll = payrollState;
        if (active is null || original is null || links is null || root is null || payroll is null) { MessageBox.Show(this, "افتح البيانات أولاً.", "خط الكتب"); return; }
        var dialog = new OpenFileDialog { Title = "اختيار خط ساكال العريض", Filter = "خط TrueType (*.ttf)|*.ttf", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var updated = await Task.Run(() => {
                if (payrollStore.Load(root).Revision != payroll.Revision) throw new InvalidDataException("تغيّرت بيانات الأشهر؛ أعد اختيار الخط.");
                var info = new FileInfo(dialog.FileName); if (info.Length is <= 0 or > 3_000_000) throw new InvalidDataException("حجم ملف الخط غير صالح.");
                return linkStore.SaveGroupFont(original, links, File.ReadAllBytes(dialog.FileName));
            });
            if (!ReferenceEquals(active, snapshot) || !ReferenceEquals(root, rootSnapshot)) { MessageBox.Show(this, "حُفظ الخط للنسخة السابقة؛ لم تتغير النسخة المعروضة.", "خط الكتب"); return; }
            linkState = updated; snapshot = updated.Snapshot; RenderPage();
            MessageBox.Show(this, "حُفظ خط ساكال العريض محلياً. احفظ نسخة احتياطية لتضمينه.", "خط الكتب", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException or DllNotFoundException or TypeInitializationException)
        { MessageBox.Show(this, "تعذّر حفظ الخط.\n" + ex.Message, "خط الكتب", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void ImportReferences(object sender, RoutedEventArgs e)
    {
        var active = snapshot; var original = baseSnapshot; var links = linkState; var root = rootSnapshot; var payroll = payrollState;
        if (active is null || original is null || links is null || root is null || payroll is null) { MessageBox.Show(this, "افتح البيانات أولاً.", "مراجع الكتب"); return; }
        var dialog = new ReferenceImportWindow() { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } archive) return;
        try
        {
            var updated = await Task.Run(() => { if (payrollStore.Load(root).Revision != payroll.Revision) throw new InvalidDataException("تغيّرت بيانات الأشهر؛ أعد فتح المراجع."); return linkStore.SaveReferences(original, links, archive); });
            if (!ReferenceEquals(active, snapshot) || !ReferenceEquals(root, rootSnapshot)) { MessageBox.Show(this, "حُفظت المراجع للنسخة السابقة؛ لم تتغير النسخة المعروضة.", "مراجع الكتب"); return; }
            linkState = updated; snapshot = updated.Snapshot; RenderPage();
            MessageBox.Show(this, "حُفظت المراجع محلياً. راجع حسابات الكتب واحفظ نسخة احتياطية لتضمينها.", "مراجع الكتب", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException)
        { MessageBox.Show(this, "تعذّر حفظ المراجع.\n" + ex.Message, "مراجع الكتب", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void OpenIndividualEntity(object sender, RoutedEventArgs e)
    {
        var active = snapshot; var transfer = preparedTransfer; var month = SelectedMonth;
        var original = baseSnapshot; var links = linkState; var root = rootSnapshot; var payroll = payrollState;
        if (active is null || transfer is null || original is null || links is null || root is null || payroll is null || !ReferenceEquals(active, preparedSource) || transfer.Month != month)
        { MessageBox.Show(this, "اختر الشهر وانتظر تحميل التحويلات.", "كتب الجهات"); return; }
        try
        {
            bool PayrollCurrent() => payrollStore.Load(root).Revision == payroll.Revision;
            var references = await Task.Run(() => { if (!PayrollCurrent() || linkStore.Load(original).Revision != links.Revision) throw new InvalidDataException("تغيّرت البيانات؛ أعد فتح الكتب."); return LetterReferencePack.Read(active); });
            if (!ReferenceEquals(active, snapshot) || month != SelectedMonth) throw new InvalidDataException("تغيّرت بيانات الشهر؛ أعد فتح الكتب.");
            var window = new IndividualEntityWindow(original, links, transfer, linkStore, references, PayrollCurrent) { Owner = this }; window.ShowDialog();
            if (!ReferenceEquals(root, rootSnapshot) || !ReferenceEquals(active, snapshot)) return;
            if (!PayrollCurrent()) throw new InvalidDataException("تغيّرت بيانات الأشهر؛ أعد فتح النسخة.");
            var latest = linkStore.Load(original); linkState = latest; snapshot = latest.Snapshot; RenderPage();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException)
        { MessageBox.Show(this, "تعذّر فتح كتب الجهات.\n" + ex.Message, "كتب الجهات", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void OpenIndividualPayroll(object sender, RoutedEventArgs e)
    {
        var active = snapshot; var transfer = preparedTransfer; var month = SelectedMonth;
        var original = baseSnapshot; var links = linkState; var root = rootSnapshot; var payroll = payrollState;
        if (active is null || transfer is null || original is null || links is null || root is null || payroll is null || !ReferenceEquals(active, preparedSource) || transfer.Month != month)
        { MessageBox.Show(this, "اختر الشهر وانتظر تحميل التحويلات.", "الكتب المنفردة"); return; }
        try
        {
            bool Current() => linkStore.Load(original).Revision == links.Revision && payrollStore.Load(root).Revision == payroll.Revision;
            var prepared = await Task.Run(() => { if (!Current()) throw new InvalidDataException("تغيّرت البيانات؛ أعد فتح الكتب."); return (summary: BankStatementCalculator.Calculate(active, month), references: LetterReferencePack.Read(active)); });
            if (!ReferenceEquals(active, snapshot) || month != SelectedMonth) throw new InvalidDataException("تغيّرت بيانات الشهر؛ أعد فتح الكتب.");
            bool PayrollCurrent() => payrollStore.Load(root).Revision == payroll.Revision;
            var window = new IndividualPayrollWindow(original, links, linkStore, prepared.summary, transfer.Payer, transfer.PayerAccount, prepared.references, PayrollCurrent) { Owner = this }; window.ShowDialog();
            if (!ReferenceEquals(root, rootSnapshot) || !ReferenceEquals(active, snapshot)) return;
            if (!PayrollCurrent()) throw new InvalidDataException("تغيّرت بيانات الأشهر؛ أعد فتح النسخة.");
            var latest = linkStore.Load(original); linkState = latest; snapshot = latest.Snapshot; RenderPage();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException)
        { MessageBox.Show(this, "تعذّر فتح الكتب المنفردة.\n" + ex.Message, "الكتب المنفردة", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void OpenNameLists(object sender, RoutedEventArgs e)
    {
        var source = snapshot; var transfer = preparedTransfer; var month = SelectedMonth;
        var original = baseSnapshot; var links = linkState; var root = rootSnapshot; var payroll = payrollState;
        if (source is null || transfer is null || original is null || links is null || root is null || payroll is null || !ReferenceEquals(source, preparedSource) || transfer.Month != month)
        { MessageBox.Show(this, "اختر الشهر وانتظر اكتمال تحميل التحويلات.", "قوائم الأسماء"); return; }
        try
        {
            var lists = await Task.Run(() => NameLists.Prepare(source, transfer));
            if (!ReferenceEquals(source, snapshot) || month != SelectedMonth) { MessageBox.Show(this, "تغيّرت بيانات الشهر؛ أعد فتح القوائم.", "قوائم الأسماء"); return; }
            bool DataCurrent() => linkStore.Load(original).Revision == links.Revision && payrollStore.Load(root).Revision == payroll.Revision;
            if (!DataCurrent()) throw new InvalidDataException("تغيّرت بيانات الشهر؛ أعد فتح القائمة.");
            new NameListsWindow(month, lists, DataCurrent) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException)
        { MessageBox.Show(this, "تعذّر إعداد القوائم.\n" + ex.Message, "قوائم الأسماء", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void OpenGroupLetter(object sender, RoutedEventArgs e)
    {
        var plan = preparedTransfer; var active = snapshot; var original = baseSnapshot; var root = rootSnapshot; var payroll = payrollState; var links = linkState; var group = CurrentTransferGroup;
        if (plan is null || active is null || original is null || root is null || payroll is null || links is null || !ReferenceEquals(active, preparedSource) || plan.Month != SelectedMonth || group is not (TransferGroup.Rafidain or TransferGroup.OtherBanks))
        { MessageBox.Show(this, "اختر مجموعة الرافدين أو رشيد وانتظر اكتمال مراجعة بياناتها.", "كتاب المجموعة", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        try
        {
            var letter = await Task.Run(() => {
                if (payrollStore.Load(root).Revision != payroll.Revision || linkStore.Load(original).Revision != links.Revision) throw new InvalidDataException("تغيّرت بيانات التحويل؛ أعد فتح النسخة.");
                var references = LetterReferencePack.Read(active);
                _ = LetterReferencePack.IndividualAccount(references, group == TransferGroup.Rafidain ? "rafidain_group" : "other_banks_group", plan.PayerAccount);
                return GroupLetterPreparation.Prepare(plan, group, DateOnly.FromDateTime(DateTime.Now));
            });
            if (!ReferenceEquals(active, snapshot) || plan.Month != SelectedMonth || group != CurrentTransferGroup) { MessageBox.Show(this, "تغيّر الشهر أو المجموعة؛ أعد فتح الكتاب.", "كتاب المجموعة"); return; }
            bool PayrollCurrent() => payrollStore.Load(root).Revision == payroll.Revision;
            var window = new GroupLetterWindow(original, links, linkStore, letter, PayrollCurrent) { Owner = this };
            window.ShowDialog();
            if (!ReferenceEquals(active, snapshot) || !ReferenceEquals(root, rootSnapshot)) return;
            if (!PayrollCurrent()) { MessageBox.Show(this, "تغيّرت بيانات الأشهر؛ أعد فتح النسخة لمراجعتها.", "كتاب المجموعة", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            var latest = linkStore.Load(original); linkState = latest; snapshot = latest.Snapshot; RenderPage();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or OverflowException or DllNotFoundException or TypeInitializationException)
        { MessageBox.Show(this, "تعذّر إعداد كتاب المجموعة.\n" + ex.Message, "كتاب المجموعة", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private sealed record BankRow(string Title, string Value);
    private void OpenReconciliation(object sender, RoutedEventArgs e)
    {
        if (snapshot is null || rootSnapshot is not { } root || baseSnapshot is not { } original || payrollState is not { } payroll || linkState is not { } links) return;
        bool IsCurrent() => payrollStore.Load(root).Revision == payroll.Revision && linkStore.Load(original).Revision == links.Revision;
        try { new ReconciliationWindow(snapshot, SelectedMonth, IsCurrent) { Owner = this }.ShowDialog(); }
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
        StartFromExcelButton.Visibility = snapshot is null ? Visibility.Visible : Visibility.Collapsed;
        if (snapshot is null || SelectedMonth.Length == 0)
        {
            NetLabel.Text = "صافي الرواتب للشهر المختار"; NetAmount.Text = "—"; PayrollCount.Text = "لا توجد بيانات مفتوحة"; DataWarning.Text = "";
            SnapshotSummary.Text = "افتح نسخة الآيفون أو ابدأ بملف رواتب أو استقطاعات."; return;
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
