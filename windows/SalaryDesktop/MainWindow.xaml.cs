using System.Globalization;
using System.IO;
using System.Diagnostics;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    private readonly LocalSalaryTrustStore trustStore = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SalaryDesktop", "SalaryTrust"));
    private SalaryTrustWindow? trustController;
    private BackupSnapshot? trustSource;
    private readonly LocalReconciliationStore reconciliationStore = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SalaryDesktop", "Reconciliations"));
    private string page = "home";
    private string shownPage = "";
    private string? columnsPage;
    private int tableRequest;
    private int homeRequest;
    private bool salaryItemsBusy, auditBusy;
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
        InitializeInspectorEvents();
        InitializeInspectorCompatibility();
        searchDelay.Tick += (_, _) => { searchDelay.Stop(); RenderPage(); };
        // أي رسالة جديدة تعيد إظهار الشريط بعد إخفائه.
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock)).AddValueChanged(SourceMessage, (_, _) => SourceBanner.Visibility = Visibility.Visible);
        RestorePlacement();
        VersionText.Text = $"نسخة ويندوز · الإصدار {Updates.Current}";
        Loaded += async (_, _) =>
        {
            ShowTheme(); Locker.Start(); StartBackupTimer(); HookNotifications();
            await LoadStoredSnapshot(); RestorePage(); RenderPage();
            if (!App.Unattended) await FindUpdate(false);
        };
        Closing += WindowClosing;
        Closed += (_, _) => { TrustPanel.Content = null; trustController?.Close(); trustController = null; };
        Closed += (_, _) => { if (previewFolder is not null) { try { Directory.Delete(previewFolder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } } };
    }

    private async Task<bool> StartFromExcelFile(string? path, string? month = null)
    {
        if (loading || snapshot is not null) return false;
        var dialog = new PayrollImportWindow(null, month ?? "", path) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } preview) return false;
        if (snapshot is not null) { Ui.Message(this, "فُتحت بيانات أخرى؛ لم تُنشأ قاعدة جديدة.", "بدء البيانات"); return false; }
        loading = true; StartFromExcelButton.IsEnabled = false; SourceMessage.Text = "جارٍ إنشاء البيانات والتحقق منها محلياً…";
        var temporary = Path.Combine(Path.GetTempPath(), "salary-initial-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var loaded = await Task.Run(() => { var created = InitialDataset.Create(preview, temporary); return store.Import(temporary, expectedDigest: created.SourceDigest, requireEmptyStore: true); });
            SetSnapshot(loaded); SourceMessage.Text = "أُنشئت البيانات من ملف Excel وحُفظت محلياً دون تعديل الملف الأصلي.";
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException)
        { SourceMessage.Text = "لم تُنشأ البيانات؛ لم يُعدّل ملف Excel."; Ui.Message(this, "تعذّر إنشاء البيانات.\n" + ex.Message, "بدء البيانات", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
        finally { loading = false; StartFromExcelButton.IsEnabled = true; try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    private async void OpenBackup(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        var dialog = new OpenFileDialog { Title = "استرجاع من نسخة احتياطية", Filter = "نسخة احتياطية (*.zip)|*.zip", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        await OpenBackupFile(dialog.FileName);
    }
    /// فتح نسخة من مسار (من نافذة الاختيار أو بسحب الملف إلى البرنامج).
    private async Task<bool> OpenBackupFile(string selectedPath)
    {
        if (loading) return false;
        loading = true; BeginLoading("*", "جارٍ فتح النسخة والتحقق منها…");
        SourceMessage.Text = "جارٍ قراءة النسخة والتحقق من فهرسها دون تغيير الملف الأصلي…";
        try
        {
            string? inspectedDigest = null;
            if (rootSnapshot is { } previous && snapshot is { } shown)
            {
                var inspected = await Task.Run(() => BackupReader.Read(selectedPath));
                inspectedDigest = inspected.SourceDigest;
                var hasLocalChanges = payrollState?.Revision is not null || linkState?.Revision is not null || (await Task.Run(() => reconciliationStore.Load(shown))).Revision is not null || (await Task.Run(() => trustStore.Load(shown))).Revision is not null || ArchiveRevision(shown) is not null;
                if (inspected.SourceDigest != previous.SourceDigest && hasLocalChanges)
                {
                    // التعديلات المحلية لا تندمج مع النسخة الأخرى؛ يُعرض حفظها في نسخة شاملة قبل الانتقال حتى لا يضيع عمل ويندوز.
                    var choice = Ui.Message(this,
                        "توجد تعديلات محلية مرتبطة بالنسخة الحالية (أشهر مستوردة أو حسابات أو دفتر مطابقة أو كتب في الأرشيف). تبقى على الجهاز، لكنها لا تندمج تلقائياً مع النسخة الأخرى.\n\nنعم: احفظ نسخة شاملة بالتعديلات أولاً ثم افتح الأخرى.\nلا: افتح الأخرى دون حفظ.\nإلغاء: ابقَ على النسخة الحالية.",
                        "تغيير النسخة المفتوحة", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                    if (choice == MessageBoxResult.Cancel || choice == MessageBoxResult.None) { SourceMessage.Text = "لم تتغير النسخة المفتوحة."; return false; }
                    if (choice == MessageBoxResult.Yes)
                    {
                        loading = false;
                        // يُحفظ في ملف النسخ المختار إن وُجد، وإلا يُطلب مكان الحفظ.
                        var saved = HasBackupDestination ? await BackupToFile(true) : await SaveFullBackup();
                        loading = true;
                        if (!saved) { SourceMessage.Text = "لم تُحفظ النسخة الشاملة؛ لم تتغير النسخة المفتوحة."; return false; }
                    }
                }
            }
            var loaded = await Task.Run(() => store.Import(selectedPath, expectedDigest: inspectedDigest));
            SetSnapshot(loaded);
            SourceMessage.Text = "حُفظت نسخة محلية بعد فحصها؛ ملف الآيفون الأصلي لم يتغير.";
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException or OverflowException)
        {
            SourceMessage.Text = snapshot is null ? "لم تُفتح نسخة احتياطية." : "تعذّر فتح النسخة الجديدة؛ ما زالت البيانات السابقة معروضة.";
            Ui.Message(this, $"تعذّر قراءة النسخة والتحقق منها.\n{ex.Message}", "فتح النسخة", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally { loading = false; EndLoading("*"); }
    }

    private void SetSnapshot(BackupSnapshot loaded)
    {
        var archived = WithArchive(loaded);
        var payroll = payrollStore.Load(archived);
        var links = linkStore.Load(payroll.Snapshot);
        ApplySources(archived, payroll, links, null);
    }
    private void ApplySources(BackupSnapshot root, PayrollImportState payroll, TransferLinksSnapshot links, string? month)
    {
        rootSnapshot = root; payrollState = payroll; baseSnapshot = payroll.Snapshot; linkState = links; snapshot = links.Snapshot;
        OpenReconciliationButton.IsEnabled = true;
        MonthPicker.ItemsSource = snapshot.Months.Select(m => m.Name).ToArray();
        if (month is not null && snapshot.Months.Any(m => m.Name == month)) MonthPicker.SelectedItem = month;
        else MonthPicker.SelectedIndex = snapshot.Months.Count > 0 ? 0 : -1;
        StatusBar.Text = "البيانات في نسخة محلية على هذا الجهاز؛ لا توجد مزامنة تلقائية مع الآيفون.";
        SidebarData.Text = $"{snapshot.Months.Count:N0} شهر · {snapshot.StaffCount:N0} في الملاك · {snapshot.Accounts.Count:N0} حساب";
        RenderPage();
        RefreshBackupReminder(true);
    }
    private async Task LoadStoredSnapshot()
    {
        if (loading || snapshot is not null) return;
        loading = true; BeginLoading("*", "جارٍ فتح البيانات المحفوظة…");
        try
        {
            var loaded = await Task.Run(store.LoadCurrent);
            if (loaded is not null) { SetSnapshot(loaded); SourceMessage.Text = "تم فتح النسخة المحلية المحفوظة على هذا الجهاز."; }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException or OverflowException)
        { SourceMessage.Text = "تعذّر فتح النسخة المحلية. لم تُستبدل؛ اختر نسخة احتياطية صالحة. " + ex.Message; }
        finally { loading = false; EndLoading("*"); }
    }
    private RadioButton[] NavItems => [NavHome, NavPayroll, NavDeductions, NavStaff, NavArchive, NavGroups, NavBank, NavTransfer, NavAccounts, NavSettings];
    /// القائمة الجانبية تختار الصفحة؛ أزرار البطاقات تختار عنصر القائمة المقابل حتى تبقى العلامة صحيحة.
    private void Navigate(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string next, IsChecked: true })
        {
            if (next == page && shownPage == page) return;
            page = next;
            searchDelay.Stop();
            if (SearchBox is not null) SearchBox.Text = "";
            RenderPage();
            return;
        }
        if (sender is Button { Tag: string target } && NavHome is not null && NavItems.FirstOrDefault(item => Equals(item.Tag, target)) is { } radio) radio.IsChecked = true;
    }
    private void HideBanner(object sender, RoutedEventArgs e) => SourceBanner.Visibility = Visibility.Collapsed;
    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (Locker.IsLocked) return;
        if (e.Key == Key.Escape && NotificationPanel.Visibility == Visibility.Visible) { NotificationPanel.Visibility = Visibility.Collapsed; e.Handled = true; return; }
        if (e.Key == Key.Escape && InspectorDrawer is { Visibility: Visibility.Visible }) { CloseInspector(); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.L) { e.Handled = true; LockNow(this, new RoutedEventArgs()); return; }
            var index = e.Key switch { >= Key.D0 and <= Key.D9 => e.Key - Key.D0, >= Key.NumPad0 and <= Key.NumPad9 => e.Key - Key.NumPad0, _ => -1 };
            if (index >= 0) { NavItems[index == 0 ? 9 : index - 1].IsChecked = true; e.Handled = true; return; }
            if (e.Key is Key.K or Key.F) { e.Handled = true; OpenGlobalSearch(this, new RoutedEventArgs()); return; }
            else if (e.Key == Key.O) { e.Handled = true; ImportFiles(this, new RoutedEventArgs()); }
        }
        else if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            if (page == "staff" && staffLoadingDigest is null) { staff = null; staffError = null; staffDigest = null; }
            lock (rowIndexLock) rowIndexes.Clear();
            groupCache = null; payrollCache = null;
            RenderPage();
        }
    }
    private void SearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SearchBox.Text.Length > 0) { SearchBox.Text = ""; e.Handled = true; }
        else if (e.Key == Key.Down && DataTable.Items.Count > 0) { DataTable.SelectedIndex = Math.Max(0, DataTable.SelectedIndex); DataTable.Focus(); e.Handled = true; }
    }
    private void TableKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; CopyRows(sender, e); return; }
        if (e.Key != Key.Enter || DataTable.SelectedItem is null) return;
        e.Handled = true; OpenRow();
    }
    private void OpenSelectedRow(object sender, RoutedEventArgs e) { if (DataTable.SelectedItem is not null) OpenRow(); }
    private void OpenDataFolder(object sender, RoutedEventArgs e) =>
        OpenFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SalaryDesktop"));
    private void OpenLogFolder(object sender, RoutedEventArgs e) => OpenFolder(App.LogFolder);
    private void OpenFolder(string path)
    {
        try { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        { Ui.Message(this, "تعذّر فتح المجلد.\n" + ex.Message, "الإعدادات", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void MonthChanged(object sender, SelectionChangedEventArgs e) => RenderPage();
    private void SearchChanged(object sender, TextChangedEventArgs e) { searchDelay.Stop(); searchDelay.Start(); }
    private string SelectedMonth => MonthPicker.SelectedItem as string ?? "";
    private static string Amount(decimal amount) => amount.ToString("#,0.##", Numbers);

    private static readonly string[] DataPages = ["payroll", "deductions", "groups", "accounts", "staff"];
    private void RenderPage()
    {
        if (!IsInitialized || HomePanel is null || DataTable is null) return;
        bool enteringTrust = page == "trust" && shownPage != "trust";
        RefreshBackupReminder(); UpdateLoading(false);
        HomePanel.Visibility = page == "home" ? Visibility.Visible : Visibility.Collapsed;
        BankPanel.Visibility = page == "bank" ? Visibility.Visible : Visibility.Collapsed;
        TransferPanel.Visibility = page == "transfer" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        ArchivePanel.Visibility = page == "archive" ? Visibility.Visible : Visibility.Collapsed;
        TrustPanel.Visibility = page == "trust" ? Visibility.Visible : Visibility.Collapsed;
        NavTrustButton.Tag = page == "trust" ? "active" : null;
        bool available = DataPages.Contains(page);
        DataPanel.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        StaffReloadButton.Visibility = page == "staff" ? Visibility.Visible : Visibility.Collapsed;
        UnavailablePanel.Visibility = page != "home" && page != "bank" && page != "transfer" && page != "settings" && page != "archive" && page != "trust" && !available ? Visibility.Visible : Visibility.Collapsed;
        var month = SelectedMonth;
        PageTitle.Text = page switch
        {
            "payroll" => "الرواتب", "deductions" => "الاستقطاعات", "groups" => "جهات التنفيذ",
            "accounts" => "حسابات جهات التنفيذ", "staff" => "الملاك", "archive" => "الأرشيف", "bank" => "كشف حساب البنك",
            "transfer" => "ملفات التحويل", "trust" => "أمانات الرواتب", "settings" => "الإعدادات", _ => Greeting()
        };
        PageSubtitle.Text = page switch
        {
            "payroll" => month.Length > 0 ? $"رواتب {month}" : "اختر شهراً لعرض الرواتب",
            "deductions" => month.Length > 0 ? $"قيود استقطاع {month}" : "اختر شهراً لعرض الاستقطاعات",
            "groups" => month.Length > 0 ? $"مبالغ الاستقطاع لكل جهة · {month}" : "مبالغ الاستقطاع لكل جهة",
            "accounts" => "IBAN والفروع المحفوظة لجهات التنفيذ",
            "staff" => "سجلات الملاك من النسخة المفتوحة",
            "archive" => "الكتب والإشعارات: إضافة من ملف أو ماسح ضوئي، وربطها بالشهر والجهة",
            "bank" => month.Length > 0 ? $"مبالغ {month} للمراجعة مع المصرف" : "اختر شهراً",
            "transfer" => month.Length > 0 ? $"{month} · ملفات المصارف والكتب" : "اختر شهراً",
            "settings" => "النسخ الاحتياطي والبيانات المحلية", "trust" => "رواتب الكاش غير المستلمة · الدفتر المشترك مع الآيفون",
            _ => HomeSubtitle(month)
        };
        GlobalSearchButton.Visibility = Visibility.Collapsed;
        if (page != shownPage)
        {
            CloseInspector();
            shownPage = page;
            UIElement panel = page switch { "home" => HomePanel, "bank" => BankPanel, "transfer" => TransferPanel, "settings" => SettingsPanel, "archive" => ArchivePanel, "trust" => TrustPanel, _ => available ? (UIElement)DataPanel : UnavailablePanel };
            panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(170)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        }
        if (page == "home") { RenderHome(); return; }
        if (page == "bank") { RenderBank(); return; }
        if (page == "transfer") { RenderTransfers(); return; }
        if (page == "settings") { BackupExport.IsEnabled = snapshot is not null; ShowSettings(); return; }
        if (page == "archive") { RenderArchive(); return; }
        if (page == "trust") { RenderSalaryTrust(enteringTrust); return; }
        if (!available)
        {
            UnavailableTitle.Text = PageTitle.Text;
            UnavailableMessage.Text = "هذا القسم غير مكتمل في نسخة ويندوز بعد. لا يمكن استخدامه للحساب أو التصدير أو اعتماد التحويلات حالياً.";
            return;
        }
        RenderTable();
    }

    /// البحث والتصفية في الخلفية على نص مُوحّد محفوظ لكل صف، فلا تتجمد الواجهة مع عشرات آلاف السجلات.
    private async void RenderTable()
    {
        var ticket = ++tableRequest; var source = snapshot; var current = page; var month = SelectedMonth;
        var tokens = PayrollAnalysis.Normalize(SearchBox.Text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        EnsureColumns(current);
        var filters = (string?[])filterValues.Clone();
        if (source is null)
        {
            TableStatus.Text = "افتح نسخة الآيفون أولاً لعرض البيانات.";
            DataTable.ItemsSource = null; UpdateTotals();
            return;
        }
        if (current == "staff" && (staffDigest != source.SourceDigest || staff is null))
        {
            DataTable.ItemsSource = null; UpdateTotals(); TableStatus.Text = staffDigest == source.SourceDigest && staffError is not null ? staffError : "جارٍ قراءة الملاك من النسخة المحلية…";
            if (staffLoadingDigest != source.SourceDigest && !(staffDigest == source.SourceDigest && staffError is not null)) LoadStaff(source);
            return;
        }
        var staffRows = staff;
        BeginLoading(current, "جارٍ تحميل البيانات…");
        try
        {
            var result = await Task.Run(() => Query(current, source, staffRows, month, tokens, filters));
            if (ticket != tableRequest || !ReferenceEquals(source, snapshot) || current != page || columnsPage != current) return;
            var sorted = DataTable.Columns.FirstOrDefault(c => c.SortDirection is not null);
            var direction = sorted?.SortDirection;
            DataTable.ItemsSource = result.Rows;
            if (current == "accounts" && inspectorReselectEntity is { } entity)
            {
                inspectorReselectEntity = null;
                DataTable.SelectedItem = result.Rows.Cast<object>().OfType<ExecutionAccount>().FirstOrDefault(a => a.Entity == entity);
            }
            if (sorted is not null && direction is { } d && !string.IsNullOrEmpty(sorted.SortMemberPath))
            {
                var view = CollectionViewSource.GetDefaultView(DataTable.ItemsSource);
                view.SortDescriptions.Clear(); view.SortDescriptions.Add(new SortDescription(sorted.SortMemberPath, d)); sorted.SortDirection = d;
            }
            TableStatus.Text = result.Status;
            FillFilters(result.Options);
            shownSum = result.Sum; shownMissing = result.Missing; UpdateTotals();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException or OverflowException)
        {
            if (ticket != tableRequest || current != page) return;
            DataTable.ItemsSource = null; UpdateTotals(); TableStatus.Text = "تعذّر عرض البيانات: " + ex.Message;
        }
        finally { if (ticket == tableRequest) EndLoading(current); }
    }

    private sealed class RowIndex<T>(IReadOnlyList<T> source, string?[] months, string[] keys)
    {
        public IReadOnlyList<T> Source { get; } = source;
        public T[] Query(string? month, string[] tokens)
        {
            var result = new List<T>();
            for (int i = 0; i < keys.Length; i++)
            {
                if (month is not null && months[i] != month) continue;
                var key = keys[i]; var match = true;
                foreach (var token in tokens) if (!key.Contains(token, StringComparison.Ordinal)) { match = false; break; }
                if (match) result.Add(Source[i]);
            }
            return result.ToArray();
        }
    }
    private readonly Dictionary<string, object> rowIndexes = new(StringComparer.Ordinal);
    private readonly object rowIndexLock = new();
    private sealed record GroupCache(BackupSnapshot Source, string Month, IReadOnlyList<DeductionGroup> Groups);
    private volatile GroupCache? groupCache;
    private RowIndex<T> Index<T>(string name, IReadOnlyList<T> rows, Func<T, string?>? month, Func<T, string?[]> parts)
    {
        lock (rowIndexLock) if (rowIndexes.TryGetValue(name, out var cached) && cached is RowIndex<T> hit && ReferenceEquals(hit.Source, rows)) return hit;
        var months = new string?[rows.Count]; var keys = new string[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            months[i] = month?.Invoke(rows[i]);
            keys[i] = PayrollAnalysis.Normalize(string.Join(' ', parts(rows[i]).Where(p => p is not null)));
        }
        var built = new RowIndex<T>(rows, months, keys);
        lock (rowIndexLock) rowIndexes[name] = built;
        return built;
    }
    private void ReloadStaff(object sender, RoutedEventArgs e)
    {
        if (snapshot is null || staffLoadingDigest is not null) return;
        staff = null; staffError = null; staffDigest = null; RenderPage();
    }
    private async void LoadStaff(BackupSnapshot source)
    {
        var digest = source.SourceDigest; staffLoadingDigest = digest;
        BeginLoading("staff", "جارٍ قراءة الملاك…");
        try
        {
            var loaded = await Task.Run(() => StaffRecords.Read(source));
            if (snapshot?.SourceDigest != digest) return;
            staff = loaded; staffDigest = digest; staffError = null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException)
        { if (snapshot?.SourceDigest == digest) { staff = null; staffDigest = digest; staffError = "تعذّر قراءة الملاك: " + ex.Message; } }
        finally { if (staffLoadingDigest == digest) { staffLoadingDigest = null; EndLoading("staff"); } if (page == "staff" && snapshot?.SourceDigest == digest) RenderPage(); }
    }
    /// النقر المزدوج على شريط التمرير أو العناوين لا يفتح الصف المختار سابقاً.
    private void OpenArchiveBook(object sender, MouseButtonEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is DataGridRow) { OpenRow(); return; }
            if (node is DataGrid or System.Windows.Controls.Primitives.ScrollBar or System.Windows.Controls.Primitives.DataGridColumnHeader) return;
        }
    }
    private async void OpenRow()
    {
        if (page == "staff" && DataTable.SelectedItem is StaffMember member) { await OpenPerson(member.Name, null); return; }
        if (page == "payroll" && DataTable.SelectedItem is PayrollRow row) { await OpenPerson(row.Name, row.Month); return; }
        if (page == "deductions" && DataTable.SelectedItem is Deduction deduction) { await OpenPerson(deduction.Name, deduction.Month); return; }
        if (page == "groups" && DataTable.SelectedItem is DeductionGroup group) { ShowEntity(group.Title); return; }
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
                { Ui.Message(this, "حُفظ الحساب للنسخة السابقة؛ لم تتغير النسخة المعروضة.", "تعديل الحساب", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
                linkState = updated; snapshot = updated.Snapshot; RenderPage();
                Ui.Message(this, "حُفظ الحساب محلياً. راجع التصنيفات والكتب المرتبطة به، واحفظ نسخة احتياطية تشمل التعديلات.", "تعديل الحساب", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or OverflowException)
            { Ui.Message(this, "تعذّر حفظ الحساب.\n" + ex.Message, "تعديل الحساب", MessageBoxButton.OK, MessageBoxImage.Warning); }
            return;
        }
        if (page != "archive" || snapshot is null || DataTable.SelectedItem is not ArchiveBook book) return;
        try
        {
            // يُعرض الكتاب داخل البرنامج (نافذة المعاينة فيها الحفظ والطباعة)، لا في عارض خارجي.
            previewFolder ??= Path.Combine(Path.GetTempPath(), "salary-desktop-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(previewFolder);
            var destination = Path.Combine(previewFolder, Guid.NewGuid().ToString("N") + ".pdf");
            var source = snapshot;
            var bytes = await Task.Run(() => { BackupReader.CopyBook(source, book, destination); try { return File.ReadAllBytes(destination); } finally { File.Delete(destination); } });
            Exports.ShowBytes(this, book.Title, bytes);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            Ui.Message(this, $"تعذّر فتح الكتاب.\n{ex.Message}", "الأرشيف", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    /// استيراد ملف حسابات الجهات (من الاستيراد الموحّد)؛ quiet يكتفي بالملخص النهائي بدل رسالة نجاح لكل ملف.
    private async Task<bool> ImportAccountsFrom(string path, bool quiet = false)
    {
        var original = baseSnapshot; var active = snapshot; var links = linkState; var root = rootSnapshot; var payroll = payrollState;
        if (original is null || active is null || links is null || root is null || payroll is null) { Ui.Message(this, "افتح نسخة احتياطية أو ابدأ البيانات أولاً لاستيراد الحسابات إليها.", "استيراد الحسابات"); return false; }
        try
        {
            var preview = await Task.Run(() => AccountSpreadsheet.Read(path));
            if (!ReferenceEquals(active, snapshot)) { Ui.Message(this, "تغيّرت النسخة المفتوحة؛ أعد اختيار ملف الحسابات.", "استيراد الحسابات"); return false; }
            var review = new AccountImportReviewWindow(preview) { Owner = this };
            if (review.ShowDialog() != true) return false;
            var updated = await Task.Run(() => { if (payrollStore.Load(root).Revision != payroll.Revision) throw new InvalidDataException("تغيّرت بيانات الأشهر؛ أعد فتح النسخة."); return linkStore.ImportAccounts(original, links, preview); });
            if (!ReferenceEquals(original, baseSnapshot) || !ReferenceEquals(active, snapshot))
            { Ui.Message(this, "حُفظت الحسابات للنسخة السابقة، ولم تُغيّر النسخة المعروضة.", "استيراد الحسابات", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
            linkState = updated; snapshot = updated.Snapshot; RenderPage();
            if (!quiet) Ui.Message(this, "حُفظت الحسابات محلياً بعد فحصها. راجع تصنيف الجهات.", "استيراد الحسابات", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or OverflowException)
        { Ui.Message(this, "تعذّر استيراد الحسابات.\n" + ex.Message, "استيراد الحسابات", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
    }
    private async Task<bool> ImportAnnualFrom(string path, bool quiet = false)
    {
        var root = rootSnapshot; var payroll = payrollState; var active = snapshot; var links = linkState; var original = baseSnapshot;
        if (root is null || payroll is null || active is null || links is null || original is null) { Ui.Message(this, "افتح نسخة احتياطية أو ابدأ البيانات أولاً.", "استيراد الاستقطاعات السنوية"); return false; }
        var dialog = new AnnualImportWindow(active, path) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } preview || preview.Problems.Count != 0) return false;
        if (!ReferenceEquals(active, snapshot)) { Ui.Message(this, "تغيّرت النسخة المفتوحة؛ أعد مراجعة الملف.", "الاستيراد السنوي"); return false; }
        try
        {
            var result = await Task.Run(() => {
                if (linkStore.Load(original).Revision != links.Revision) throw new InvalidDataException("تغيّرت روابط الحسابات؛ أعد فتح النسخة.");
                var updated = payrollStore.ImportBatch(root, payroll, preview.Months, dialog.ReplaceExisting);
                var updatedLinks = linkStore.Load(updated.Snapshot);
                if (updatedLinks.Revision != links.Revision) throw new InvalidDataException("حُفظت الأشهر، لكن روابط الحسابات تغيّرت أثناء الحفظ؛ أعد فتح النسخة.");
                return (updated, updatedLinks);
            });
            if (!ReferenceEquals(root, rootSnapshot) || !ReferenceEquals(active, snapshot)) { Ui.Message(this, "حُفظت الأشهر للنسخة السابقة؛ لم تتغير النسخة المعروضة.", "الاستيراد السنوي"); return false; }
            ApplySources(root, result.updated, result.updatedLinks, preview.Months[0].Month);
            if (!quiet) Ui.Message(this, "حُفظت الأشهر معاً محلياً. راجع الاستقطاعات.", "الاستيراد السنوي", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or OverflowException)
        { Ui.Message(this, "تعذّر حفظ الملف السنوي.\n" + ex.Message, "الاستيراد السنوي", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
    }
    /// استيراد ملف رواتب أو استقطاعات شهر؛ الشهر من اسم الملف إن عُرف، وإلا يكتبه المستخدم في نافذة المراجعة.
    private void OpenSalaryTrust(object sender, RoutedEventArgs e)
    {
        if (page == "trust" && shownPage == page) return;
        foreach (var item in NavItems) item.IsChecked = false;
        page = "trust";
        searchDelay.Stop();
        if (SearchBox is not null) SearchBox.Text = "";
        RenderPage();
    }

    private void RenderSalaryTrust(bool entering)
    {
        var source = snapshot;
        try
        {
            if (!ReferenceEquals(trustSource, source) || (source is not null && trustController is null))
            {
                TrustPanel.Content = null;
                trustController?.Close(); trustController = null; trustSource = source;
                if (source is not null)
                {
                    var controller = new SalaryTrustWindow(source, trustStore, () => ReferenceEquals(snapshot, source) && !loading && !backingUp);
                    controller.DataChanged = () => RefreshBackupReminder(true);
                    trustController = controller;
                    TrustPanel.Content = controller.TakeEmbeddedView();
                }
            }
            else if (entering && trustController is not null) trustController.ReloadEmbedded();
            if (source is null) TrustPanel.Content = new TextBlock { Text = "افتح نسخة احتياطية أو استورد بيانات الرواتب أولاً.", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 16 };
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or OverflowException)
        {
            TrustPanel.Content = new TextBlock { Text = "تعذّر فتح دفتر الأمانات.\n" + ex.Message, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            trustController?.Close(); trustController = null; trustSource = null;
        }
    }

    private async Task<bool> ImportPayrollFrom(string path, string? month = null, bool quiet = false)
    {
        var root = rootSnapshot; var payroll = payrollState; var active = snapshot; var links = linkState; var original = baseSnapshot;
        if (root is null || payroll is null || active is null || links is null || original is null)
        { Ui.Message(this, "افتح نسخة احتياطية أو ابدأ البيانات أولاً لاستيراد بيانات الشهر إليها.", "استيراد الرواتب"); return false; }
        var dialog = new PayrollImportWindow(active, month ?? SelectedMonth, path) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } preview) return false;
        var replace = dialog.ReplaceExisting;
        if (!ReferenceEquals(active, snapshot)) { Ui.Message(this, "تغيّرت النسخة المفتوحة؛ أعد مراجعة الملف.", "استيراد الرواتب"); return false; }
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
            { Ui.Message(this, "حُفظت بيانات الشهر للنسخة السابقة؛ لم تتغير النسخة المعروضة.", "استيراد الرواتب", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
            ApplySources(root, result.updatedPayroll, result.updatedLinks, preview.Month);
            if (preview.Employees.Count > 0 && snapshot is { } trustSource)
            {
                try { await Task.Run(() => trustStore.AddCash(trustSource, preview.Month)); }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException)
                { Ui.Message(this, "حُفظت الرواتب، لكن لم تُحدّث الأمانات؛ راجع قائمة الكاش.\n" + ex.Message, "أمانات الرواتب", MessageBoxButton.OK, MessageBoxImage.Warning); }
            }
            if (!quiet) Ui.Message(this, "حُفظت بيانات الشهر محلياً؛ راجع التقارير والمبالغ.", "استيراد الرواتب", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or OverflowException)
        { Ui.Message(this, "تعذّر إكمال تحديث العرض. أعد فتح النسخة للتحقق مما حُفظ.\n" + ex.Message, "استيراد الرواتب", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
    }

    private async void ExportBackup(object sender, RoutedEventArgs e) => await SaveFullBackup();
    /// يحفظ نسخة شاملة بالتعديلات المحلية؛ يعيد true عند نجاح الحفظ دون تغيّر البيانات أثناءه.
    private async Task<bool> SaveFullBackup()
    {
        var source = snapshot; var original = baseSnapshot; var links = linkState; var root = rootSnapshot; var payroll = payrollState;
        if (source is null || original is null || links is null || root is null || payroll is null) return false;
        var dialog = new SaveFileDialog { Title = "حفظ نسخة احتياطية", Filter = "نسخة احتياطية (*.zip)|*.zip", FileName = $"نسخة الرواتب {DateTime.Now:yyyy-MM-dd}.zip", AddExtension = true, DefaultExt = ".zip", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return false;
        var destination = dialog.FileName;
        BackupExport.IsEnabled = false;
        try
        {
            var saved = await Task.Run(() => {
                if (linkStore.Load(original).Revision != links.Revision) throw new InvalidDataException("تغيّرت روابط الحسابات؛ أعد فتح النسخة قبل التصدير.");
                if (payrollStore.Load(root).Revision != payroll.Revision) throw new InvalidDataException("تغيّرت بيانات الأشهر؛ أعد فتح النسخة قبل التصدير.");
                var ledger = reconciliationStore.Load(source);
                var trust = trustStore.Load(source);
                ReconciliationBackupExport.Write(source, ledger.Periods, destination, trust.Ledger);
                return (Changed: linkStore.Load(original).Revision != links.Revision || payrollStore.Load(root).Revision != payroll.Revision || reconciliationStore.Load(source).Revision != ledger.Revision || trustStore.Load(source).Revision != trust.Revision, Ledger: LedgerRevisions(ledger.Revision, trust.Revision, ArchiveRevision(source)));
            });
            var changed = saved.Changed;
            if (!changed && ReferenceEquals(source, snapshot)) RememberBackup(root, payroll.Revision, links.Revision, saved.Ledger);
            Ui.Message(this, changed || !ReferenceEquals(source, snapshot) ? "حُفظت لقطة سابقة؛ تغيّرت البيانات أثناء التصدير، لذا أعد حفظ النسخة. اختبر الاستعادة على الآيفون." : "حُفظت النسخة مع الروابط ودفتر المطابقة بعد فحصها. اختبر استعادتها على الآيفون قبل الاعتماد.", "النسخة الاحتياطية", MessageBoxButton.OK, changed ? MessageBoxImage.Warning : MessageBoxImage.Information);
            return !changed && ReferenceEquals(source, snapshot);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        { Ui.Message(this, "تعذّر حفظ النسخة.\n" + ex.Message, "النسخة الاحتياطية", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
        finally { if (page == "settings") BackupExport.IsEnabled = snapshot is not null; RefreshBackupReminder(true); }
    }

    private sealed record TransferRow(TransferEntry Entry, string Title, string Amount, string Branch, string Iban, string Issues);
    private TransferGroup CurrentTransferGroup => ((TransferGroupPicker.SelectedItem as ListBoxItem)?.Tag as string) switch
    {
        "other" => TransferGroup.OtherBanks, "anbar" => TransferGroup.Anbar,
        "unresolved" => TransferGroup.Unresolved, _ => TransferGroup.Rafidain
    };
    private void TransferGroupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized && page == "transfer") RenderTransfers();
    }
    /// كل تبويب يعرض إجراءاته فقط: ملف التحويل وكتاب المجموعة وتدقيق الإصدار للرافدين والمصارف الأخرى، والكتب المنفردة وقوائم الأسماء
    /// لفرع الأنبار، وربط الحساب وتحديد المجموعة لكل جهة، ومراجع الكتب حيث تُصدر كتب.
    private void ShowTransferActions(TransferGroup group)
    {
        var bankGroup = group is TransferGroup.Rafidain or TransferGroup.OtherBanks;
        GroupCard.Visibility = bankGroup ? Visibility.Visible : Visibility.Collapsed;
        GroupCardTitle.Text = group == TransferGroup.Rafidain ? "مجموعة الرافدين" : "مجموعة الرشيد والمصارف الأخرى";
        IndividualCard.Visibility = group == TransferGroup.Anbar ? Visibility.Visible : Visibility.Collapsed;
        ReferencesCard.Visibility = group == TransferGroup.Unresolved ? Visibility.Collapsed : Visibility.Visible;
        UnresolvedCard.Visibility = group == TransferGroup.Unresolved ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void RenderTransfers()
    {
        var ticket = ++transferRequest; var source = snapshot; var month = SelectedMonth; var group = CurrentTransferGroup;
        ShowTransferActions(group);
        preparedTransfer = null; preparedSource = null; TransferExport.IsEnabled = false; TransferTable.ItemsSource = null; IssuanceNotice.Text = "";
        if (source is null || month.Length == 0) { TransferNotice.Text = "افتح نسخة الآيفون واختر الشهر أولاً."; return; }
        TransferNotice.Text = "جارٍ مراجعة بيانات التحويل محلياً…";
        BeginLoading("transfer", "جارٍ تجهيز ملفات التحويل…");
        try
        {
            var prepared = await Task.Run(() => {
                var transfer = TransferPreparation.Prepare(source, month);
                if (group is not (TransferGroup.Rafidain or TransferGroup.OtherBanks)) return (transfer, review: (GroupIssuanceReview?)null, error: (string?)null);
                try { return (transfer, review: GroupIssuance.Review(source, transfer, group), error: (string?)null); }
                catch (InvalidDataException ex) { return (transfer, review: (GroupIssuanceReview?)null, error: (string?)ex.Message); }
            });
            var result = prepared.transfer;
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
            if (prepared.review is { } issuance) IssuanceNotice.Text = issuance.BaselineMonth is null ? "لا يوجد إقرار سابق لهذه المجموعة." : issuance.Changes.Count == 0 ? "المجموعة تطابق آخر إقرار محفوظ." : $"تنبيه: {issuance.Changes.Count:N0} تغيير مقارنة بإقرار {issuance.BaselineMonth}؛ افتح تدقيق إصدار المجموعة لمراجعة التفاصيل.";
            else if (prepared.error is not null) IssuanceNotice.Text = "تعذّر تدقيق إصدار المجموعة: " + prepared.error;
            preparedTransfer = result; preparedSource = source;
            TransferExport.IsEnabled = group is TransferGroup.Rafidain or TransferGroup.OtherBanks && entries.Length > 0 && warnings.Count == 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException or OverflowException)
        {
            if (ticket == transferRequest && ReferenceEquals(source, snapshot) && month == SelectedMonth && page == "transfer") TransferNotice.Text = "تعذّر تجهيز التحويل: " + ex.Message;
        }
        finally { if (ticket == transferRequest) EndLoading("transfer"); }
    }
    private async void LinkTransferAccount(object sender, RoutedEventArgs e)
    {
        var original = baseSnapshot; var selected = TransferTable.SelectedItem as TransferRow; var links = linkState; var active = snapshot; var root = rootSnapshot; var payroll = payrollState;
        if (original is null || selected is null || links is null || active is null || root is null || payroll is null || !ReferenceEquals(active, preparedSource))
        { Ui.Message(this, "اختر جهة من الجدول أولاً بعد اكتمال تحميلها.", "ربط حساب الجهة", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var dialog = new AccountLinkWindow(selected.Entry, active.Accounts) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedAccount is not { } account) return;
        if (!ReferenceEquals(active, snapshot) || SelectedMonth != selected.Entry.Month) { Ui.Message(this, "تغيّرت البيانات؛ أعد اختيار الجهة.", "ربط حساب الجهة"); return; }
        try
        {
            var updated = await Task.Run(() => { if (payrollStore.Load(root).Revision != payroll.Revision) throw new InvalidDataException("تغيّرت بيانات الأشهر؛ أعد فتح النسخة."); return linkStore.SaveLink(original, links, selected.Entry, account); });
            if (!ReferenceEquals(original, baseSnapshot) || !ReferenceEquals(active, snapshot))
            { Ui.Message(this, "حُفظ الربط للنسخة السابقة، ولم تُغيّر النسخة المعروضة.", "ربط حساب الجهة", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            linkState = updated; snapshot = updated.Snapshot; RenderPage();
            Ui.Message(this, "حُفظ الربط على الجهاز؛ راجع تصنيف الجهة بعد تحديث القائمة. احفظ نسخة احتياطية تشمل التعديلات.", "ربط حساب الجهة", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or OverflowException)
        { Ui.Message(this, "تعذّر حفظ الربط.\n" + ex.Message, "ربط حساب الجهة", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void ClassifyTransfer(object sender, RoutedEventArgs e)
    {
        var original = baseSnapshot; var selected = TransferTable.SelectedItem as TransferRow; var links = linkState; var active = snapshot; var root = rootSnapshot; var payroll = payrollState;
        if (original is null || selected is null || links is null || active is null || root is null || payroll is null || !ReferenceEquals(active, preparedSource))
        { Ui.Message(this, "اختر جهة من الجدول أولاً بعد اكتمال تحميلها.", "تصنيف الجهة", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var dialog = new TransferCategoryWindow(selected.Entry) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var category = dialog.SelectedGroup;
        if (!ReferenceEquals(active, snapshot) || SelectedMonth != selected.Entry.Month) { Ui.Message(this, "تغيّرت البيانات؛ أعد اختيار الجهة.", "تصنيف الجهة"); return; }
        try
        {
            var updated = await Task.Run(() => { if (payrollStore.Load(root).Revision != payroll.Revision) throw new InvalidDataException("تغيّرت بيانات الأشهر؛ أعد فتح النسخة."); return linkStore.SaveCategory(original, links, selected.Entry, category); });
            if (!ReferenceEquals(original, baseSnapshot) || !ReferenceEquals(active, snapshot))
            { Ui.Message(this, "حُفظ التصنيف للنسخة السابقة، ولم تُغيّر النسخة المعروضة.", "تصنيف الجهة", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            linkState = updated; snapshot = updated.Snapshot; RenderPage();
            Ui.Message(this, "حُفظ التصنيف على الجهاز. راجع التنبيهات واحفظ نسخة احتياطية تشمل التعديلات.", "تصنيف الجهة", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or OverflowException)
        { Ui.Message(this, "تعذّر حفظ التصنيف.\n" + ex.Message, "تصنيف الجهة", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void ExportTransfer(object sender, RoutedEventArgs e)
    {
        var plan = preparedTransfer; var source = preparedSource; var group = CurrentTransferGroup;
        var original = baseSnapshot; var links = linkState; var root = rootSnapshot; var payroll = payrollState;
        if (plan is null || source is null || original is null || links is null || root is null || payroll is null || !ReferenceEquals(source, snapshot) || plan.Month != SelectedMonth || page != "transfer") return;
        var dialog = new SaveFileDialog { Title = "حفظ ملف التحويل", Filter = "ملف CSV (*.csv)|*.csv", FileName = group == TransferGroup.Rafidain ? "Rafidain.csv" : "OtherBanks.csv", AddExtension = true, DefaultExt = ".csv", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        if (!string.Equals(Path.GetExtension(dialog.FileName), ".csv", StringComparison.OrdinalIgnoreCase)) { Ui.Message(this, "اختر اسماً ينتهي بالامتداد CSV.", "ملف التحويل", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
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
                Ui.Message(this, "حُفظ ملف لقطة الشهر السابقة؛ تغيّر العرض أثناء الحفظ. راجع الملف قبل استخدامه.", "ملف التحويل", MessageBoxButton.OK, MessageBoxImage.Warning);
            else Ui.Message(this, "حُفظ الملف بعد التحقق من صفوفه. راجعه قبل إرساله إلى المصرف.", "ملف التحويل", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException)
        { Ui.Message(this, "تعذّر حفظ ملف التحويل.\n" + ex.Message, "ملف التحويل", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { if (page == "transfer") RenderTransfers(); }
    }
    private async void OpenSalaryItems(object sender, RoutedEventArgs e) => await ShowSalaryItems(null);
    private void OpenEmployeeDeductions(object sender, RoutedEventArgs e)
    {
        if (snapshot is not { } source) { Ui.Message(this, "افتح نسخة الآيفون أو استورد ملفات الاستقطاعات أولاً.", "كشف استقطاعات موظف"); return; }
        new EmployeeDeductionWindow(source) { Owner = this }.ShowDialog();
    }
    private async Task ShowSalaryItems(string? special)
    {
        var source = snapshot; var month = SelectedMonth;
        if (source is null || month.Length == 0) { Ui.Message(this, "افتح نسخة الآيفون واختر الشهر أولاً.", "بنود الرواتب"); return; }
        if (salaryItemsBusy) return;
        salaryItemsBusy = true; StatusBar.Text = "جارٍ حساب بنود الرواتب…";
        try
        {
            var groups = await Task.Run(() => SalaryItemReports.Groups(source, month));
            if (!ReferenceEquals(source, snapshot) || month != SelectedMonth) { Ui.Message(this, "تغيّر الشهر أو البيانات؛ أعد فتح التقرير.", "بنود الرواتب"); return; }
            // الكشوف الخاصة تفتح صفحتها مباشرة (الملخص والأسماء والتصدير) كما في الآيفون؛ وإلا تُعرض بنود الرواتب.
            if (special is not null && groups.FirstOrDefault(g => SalaryItemsWindow.Special(g.Title) == special) is { } item)
                new SpecialReportWindow(source, month, special, item) { Owner = this }.ShowDialog();
            else new SalaryItemsWindow(source, month, groups, special) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException)
        { Ui.Message(this, "تعذّر حساب بنود الرواتب.\n" + ex.Message, "بنود الرواتب", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { salaryItemsBusy = false; StatusBar.Text = "البيانات في نسخة محلية على هذا الجهاز؛ لا توجد مزامنة تلقائية مع الآيفون."; }
    }
    private void OpenSalaryReview(object sender, RoutedEventArgs e) => OpenSalaryReview((string?)null);
    private async void OpenSalaryReview(string? kind)
    {
        var source = snapshot; var month = SelectedMonth;
        if (source is null || month.Length == 0) { Ui.Message(this, "افتح نسخة الآيفون واختر الشهر أولاً.", "تدقيق الرواتب"); return; }
        if (auditBusy) return;
        auditBusy = true; StatusBar.Text = "جارٍ تدقيق الرواتب ومقارنة الشهر السابق…";
        try
        {
            // الملاك من الذاكرة إن قُرئت (لا تُقرأ من القرص في كل فتح)، والنتيجة محفوظة من حساب الرئيسية لنفس الشهر.
            var people = await StaffFor(source);
            var result = await Task.Run(() => SalaryReview.Prepare(source, month, people));
            if (!ReferenceEquals(source, snapshot) || month != SelectedMonth) { Ui.Message(this, "تغيّر الشهر أو البيانات؛ أعد فتح التدقيق.", "تدقيق الرواتب"); return; }
            // نافذة غير حاجبة حتى يُفتح ملف المنتسب من الحالة مباشرة بجانبها.
            new SalaryReviewWindow(result, kind, name => OpenPerson(name, month)) { Owner = this }.Show();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException)
        { Ui.Message(this, "تعذّر تدقيق الرواتب.\n" + ex.Message, "تدقيق الرواتب", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { auditBusy = false; StatusBar.Text = "البيانات في نسخة محلية على هذا الجهاز؛ لا توجد مزامنة تلقائية مع الآيفون."; }
    }
    private async void ImportSakkalFont(object sender, RoutedEventArgs e)
    {
        var active = snapshot; var original = baseSnapshot; var links = linkState; var root = rootSnapshot; var payroll = payrollState;
        if (active is null || original is null || links is null || root is null || payroll is null) { Ui.Message(this, "افتح البيانات أولاً.", "خط الكتب"); return; }
        var dialog = new OpenFileDialog { Title = "اختيار خط ساكال العريض", Filter = "خط TrueType (*.ttf)|*.ttf", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var updated = await Task.Run(() => {
                if (payrollStore.Load(root).Revision != payroll.Revision) throw new InvalidDataException("تغيّرت بيانات الأشهر؛ أعد اختيار الخط.");
                var info = new FileInfo(dialog.FileName); if (info.Length is <= 0 or > 3_000_000) throw new InvalidDataException("حجم ملف الخط غير صالح.");
                return linkStore.SaveGroupFont(original, links, File.ReadAllBytes(dialog.FileName));
            });
            if (!ReferenceEquals(active, snapshot) || !ReferenceEquals(root, rootSnapshot)) { Ui.Message(this, "حُفظ الخط للنسخة السابقة؛ لم تتغير النسخة المعروضة.", "خط الكتب"); return; }
            linkState = updated; snapshot = updated.Snapshot; RenderPage();
            Ui.Message(this, "حُفظ خط ساكال العريض محلياً. احفظ نسخة احتياطية لتضمينه.", "خط الكتب", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException or DllNotFoundException or TypeInitializationException)
        { Ui.Message(this, "تعذّر حفظ الخط.\n" + ex.Message, "خط الكتب", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void ImportReferences(object sender, RoutedEventArgs e)
    {
        var active = snapshot; var original = baseSnapshot; var links = linkState; var root = rootSnapshot; var payroll = payrollState;
        if (active is null || original is null || links is null || root is null || payroll is null) { Ui.Message(this, "افتح البيانات أولاً.", "مراجع الكتب"); return; }
        var dialog = new ReferenceImportWindow() { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } archive) return;
        try
        {
            var updated = await Task.Run(() => { if (payrollStore.Load(root).Revision != payroll.Revision) throw new InvalidDataException("تغيّرت بيانات الأشهر؛ أعد فتح المراجع."); return linkStore.SaveReferences(original, links, archive); });
            if (!ReferenceEquals(active, snapshot) || !ReferenceEquals(root, rootSnapshot)) { Ui.Message(this, "حُفظت المراجع للنسخة السابقة؛ لم تتغير النسخة المعروضة.", "مراجع الكتب"); return; }
            linkState = updated; snapshot = updated.Snapshot; RenderPage();
            Ui.Message(this, "حُفظت المراجع محلياً. راجع حسابات الكتب واحفظ نسخة احتياطية لتضمينها.", "مراجع الكتب", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException)
        { Ui.Message(this, "تعذّر حفظ المراجع.\n" + ex.Message, "مراجع الكتب", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void OpenIndividualEntity(object sender, RoutedEventArgs e)
    {
        var active = snapshot; var transfer = preparedTransfer; var month = SelectedMonth;
        var original = baseSnapshot; var links = linkState; var root = rootSnapshot; var payroll = payrollState;
        if (active is null || transfer is null || original is null || links is null || root is null || payroll is null || !ReferenceEquals(active, preparedSource) || transfer.Month != month)
        { Ui.Message(this, "اختر الشهر وانتظر تحميل التحويلات.", "كتب الجهات"); return; }
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
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException)
        { Ui.Message(this, "تعذّر فتح كتب الجهات.\n" + ex.Message, "كتب الجهات", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void OpenIndividualPayroll(object sender, RoutedEventArgs e)
    {
        var active = snapshot; var transfer = preparedTransfer; var month = SelectedMonth;
        var original = baseSnapshot; var links = linkState; var root = rootSnapshot; var payroll = payrollState;
        if (active is null || transfer is null || original is null || links is null || root is null || payroll is null || !ReferenceEquals(active, preparedSource) || transfer.Month != month)
        { Ui.Message(this, "اختر الشهر وانتظر تحميل التحويلات.", "الكتب المنفردة"); return; }
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
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException)
        { Ui.Message(this, "تعذّر فتح الكتب المنفردة.\n" + ex.Message, "الكتب المنفردة", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void OpenNameLists(object sender, RoutedEventArgs e)
    {
        var source = snapshot; var transfer = preparedTransfer; var month = SelectedMonth;
        var original = baseSnapshot; var links = linkState; var root = rootSnapshot; var payroll = payrollState;
        if (source is null || transfer is null || original is null || links is null || root is null || payroll is null || !ReferenceEquals(source, preparedSource) || transfer.Month != month)
        { Ui.Message(this, "اختر الشهر وانتظر اكتمال تحميل التحويلات.", "قوائم الأسماء"); return; }
        try
        {
            var lists = await Task.Run(() => NameLists.Prepare(source, transfer));
            if (!ReferenceEquals(source, snapshot) || month != SelectedMonth) { Ui.Message(this, "تغيّرت بيانات الشهر؛ أعد فتح القوائم.", "قوائم الأسماء"); return; }
            bool DataCurrent() => linkStore.Load(original).Revision == links.Revision && payrollStore.Load(root).Revision == payroll.Revision;
            if (!DataCurrent()) throw new InvalidDataException("تغيّرت بيانات الشهر؛ أعد فتح القائمة.");
            new NameListsWindow(month, lists, DataCurrent) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException)
        { Ui.Message(this, "تعذّر إعداد القوائم.\n" + ex.Message, "قوائم الأسماء", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void OpenGroupIssuance(object sender, RoutedEventArgs e)
    {
        var transfer = preparedTransfer; var active = snapshot; var original = baseSnapshot; var root = rootSnapshot; var payroll = payrollState; var links = linkState; var group = CurrentTransferGroup;
        if (transfer is null || active is null || original is null || root is null || payroll is null || links is null || !ReferenceEquals(active, preparedSource) || transfer.Month != SelectedMonth || group is not (TransferGroup.Rafidain or TransferGroup.OtherBanks))
        { Ui.Message(this, "اختر مجموعة الرافدين أو رشيد وانتظر اكتمال مراجعتها.", "تدقيق المجموعة"); return; }
        try
        {
            bool PayrollCurrent() => payrollStore.Load(root).Revision == payroll.Revision;
            var review = await Task.Run(() => { if (!PayrollCurrent() || linkStore.Load(original).Revision != links.Revision) throw new InvalidDataException("تغيّرت البيانات؛ أعد تدقيق المجموعة."); return GroupIssuance.Review(active, transfer, group); });
            if (!ReferenceEquals(active, snapshot) || transfer.Month != SelectedMonth || group != CurrentTransferGroup) throw new InvalidDataException("تغيّر الشهر أو المجموعة؛ أعد التدقيق.");
            var window = new GroupIssuanceWindow(original, links, linkStore, review, PayrollCurrent) { Owner = this }; window.ShowDialog();
            if (!ReferenceEquals(active, snapshot) || !ReferenceEquals(root, rootSnapshot)) return;
            if (!PayrollCurrent()) throw new InvalidDataException("تغيّرت بيانات الأشهر؛ أعد فتح النسخة.");
            var latest = linkStore.Load(original); linkState = latest; snapshot = latest.Snapshot; RenderPage();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException)
        { Ui.Message(this, "تعذّر تدقيق إصدار المجموعة.\n" + ex.Message, "تدقيق المجموعة", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void OpenGroupLetter(object sender, RoutedEventArgs e)
    {
        var plan = preparedTransfer; var active = snapshot; var original = baseSnapshot; var root = rootSnapshot; var payroll = payrollState; var links = linkState; var group = CurrentTransferGroup;
        if (plan is null || active is null || original is null || root is null || payroll is null || links is null || !ReferenceEquals(active, preparedSource) || plan.Month != SelectedMonth || group is not (TransferGroup.Rafidain or TransferGroup.OtherBanks))
        { Ui.Message(this, "اختر مجموعة الرافدين أو رشيد وانتظر اكتمال مراجعة بياناتها.", "كتاب المجموعة", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        try
        {
            var letter = await Task.Run(() => {
                if (payrollStore.Load(root).Revision != payroll.Revision || linkStore.Load(original).Revision != links.Revision) throw new InvalidDataException("تغيّرت بيانات التحويل؛ أعد فتح النسخة.");
                var references = LetterReferencePack.Read(active);
                _ = LetterReferencePack.IndividualAccount(references, group == TransferGroup.Rafidain ? "rafidain_group" : "other_banks_group", plan.PayerAccount);
                return GroupLetterPreparation.Prepare(plan, group, DateOnly.FromDateTime(DateTime.Now));
            });
            if (!ReferenceEquals(active, snapshot) || plan.Month != SelectedMonth || group != CurrentTransferGroup) { Ui.Message(this, "تغيّر الشهر أو المجموعة؛ أعد فتح الكتاب.", "كتاب المجموعة"); return; }
            bool PayrollCurrent() => payrollStore.Load(root).Revision == payroll.Revision;
            var window = new GroupLetterWindow(original, links, linkStore, letter, PayrollCurrent) { Owner = this };
            window.ShowDialog();
            if (!ReferenceEquals(active, snapshot) || !ReferenceEquals(root, rootSnapshot)) return;
            if (!PayrollCurrent()) { Ui.Message(this, "تغيّرت بيانات الأشهر؛ أعد فتح النسخة لمراجعتها.", "كتاب المجموعة", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            var latest = linkStore.Load(original); linkState = latest; snapshot = latest.Snapshot; RenderPage();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or OverflowException or DllNotFoundException or TypeInitializationException)
        { Ui.Message(this, "تعذّر إعداد كتاب المجموعة.\n" + ex.Message, "كتاب المجموعة", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private static readonly string[] SevereKinds = ["أسماء مكررة", "صافي ناقص", "IBAN يحتاج مراجعة", "IBAN مكرر", "IBAN فارغ", "رقم مكرر", "حساب الصافي", "مجموع الاستقطاعات"];
    private async void OpenSpecialReport(object sender, RoutedEventArgs e) { if (sender is Button { Tag: string kind }) await ShowSalaryItems(kind); }
}
