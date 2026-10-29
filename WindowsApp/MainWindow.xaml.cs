using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using System.Text.Json;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace SalaryWindows;
public sealed record DisplayItem(string Display, object Value)
{
    private int Divider => Display.IndexOf(" · ", StringComparison.Ordinal);
    public string Title => Divider < 0 ? Display : Display[..Divider].Trim();
    public string Details => Divider < 0 ? "" : Display[(Divider + 3)..].Trim();
    public string Glyph => Value switch { Employee => "\uE77B", Staff => "\uE716", Account => "\uE8C7", Book => "\uE8F1", _ => "\uE8A5" };
}
public sealed record BankRow(string Label, string Amount, double Value);
public sealed partial class MainWindow : Window
{
    private BackupData? data;
    private string? originalSourcePath;
    private CancellationTokenSource? searchCancellation;
    private readonly HashSet<string> temporaryPreviews = [];
    private bool viewReady;
    private string section = "home";
    private WindowsSettings settings = new();
    public MainWindow() { InitializeComponent(); settings = WindowsSettingsStore.Load(); ServerAddress.Text = settings.ServerAddress; HideZeroRows.IsChecked = settings.HideZeroBankRows; viewReady = true; MarkActiveNavigation("home"); Closed += (_, _) => { DeleteArchive(data?.ArchivePath); foreach (var path in temporaryPreviews) DeletePreview(path); }; }
    private static void DeleteArchive(string? path) { if (path != null) try { File.Delete(path); } catch { } }
    private static void DeletePreview(string path)
    {
        try { File.Delete(path); var folder = Path.GetDirectoryName(path); if (folder != null && Path.GetFileName(folder).StartsWith("salary-preview-", StringComparison.Ordinal)) Directory.Delete(folder, false); } catch { }
    }
    private void ShowSection(string page, string title)
    {
        section = page; PageTitle.Text = title; MarkActiveNavigation(page);
        HomePanel.Visibility = page == "home" ? Visibility.Visible : Visibility.Collapsed;
        SearchPanel.Visibility = page == "search" ? Visibility.Visible : Visibility.Collapsed;
        DataPanel.Visibility = page is "reports" or "payroll" or "audit" or "staff" or "books" or "accounts" ? Visibility.Visible : Visibility.Collapsed;
        BankPanel.Visibility = page == "bank" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        MonthPicker.Visibility = page == "settings" ? Visibility.Collapsed : Visibility.Visible;
        ComparePicker.Visibility = page == "audit" ? Visibility.Visible : Visibility.Collapsed;
        AddAccountButton.Visibility = page == "accounts" ? Visibility.Visible : Visibility.Collapsed;
        if (DataPanel.Visibility == Visibility.Visible) RefreshData();
        if (page == "bank") RefreshBank();
    }
    private void MarkActiveNavigation(string page)
    {
        var items = new Dictionary<string, Button> { ["home"] = HomeNav, ["search"] = SearchNav, ["reports"] = ReportsNav,
            ["payroll"] = PayrollNav, ["bank"] = BankNav, ["audit"] = AuditNav, ["staff"] = StaffNav,
            ["books"] = BooksNav, ["accounts"] = AccountsNav, ["settings"] = SettingsNav };
        foreach (var (key, button) in items)
        {
            var selected = key == page;
            button.Background = new SolidColorBrush(selected ? Color.FromArgb(255, 225, 242, 232) : Color.FromArgb(0, 255, 255, 255));
            button.Foreground = new SolidColorBrush(selected ? Color.FromArgb(255, 18, 110, 79) : Color.FromArgb(255, 36, 68, 59));
        }
    }
    private void Home_Click(object sender, RoutedEventArgs e) => ShowSection("home", "الرئيسية");
    private void SearchPage_Click(object sender, RoutedEventArgs e) => ShowSection("search", "البحث الشامل");
    private void Reports_Click(object sender, RoutedEventArgs e) => ShowSection("reports", "جهات التنفيذ");
    private void PayrollReports_Click(object sender, RoutedEventArgs e) => ShowSection("payroll", "تقارير بنود الرواتب");
    private void Bank_Click(object sender, RoutedEventArgs e) => ShowSection("bank", "كشف حساب البنك");
    private void Audit_Click(object sender, RoutedEventArgs e) => ShowSection("audit", "التدقيق");
    private void Staff_Click(object sender, RoutedEventArgs e) => ShowSection("staff", "الملاك");
    private void Books_Click(object sender, RoutedEventArgs e) => ShowSection("books", "الكتب الرسمية");
    private void Accounts_Click(object sender, RoutedEventArgs e) => ShowSection("accounts", "حسابات جهات التنفيذ");
    private async void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        try { if (data == null) { await Error("افتح نسخة احتياطية أولاً."); return; } await EditAccount(null); }
        catch (Exception ex) { await Error(ex.Message); }
    }
    private async Task EditAccount(Account? original)
    {
        if (data == null) return;
        var snapshot = data;
        var entity = new TextBox { Header = "جهة التنفيذ", Text = original?.Entity ?? "" };
        var iban = new TextBox { Header = "IBAN", Text = original?.Iban ?? "", FlowDirection = FlowDirection.LeftToRight };
        var branch = new TextBox { Header = "الفرع", Text = original?.Branch ?? "" };
        var swift = new TextBox { Header = "SWIFT", Text = original?.Swift ?? "", FlowDirection = FlowDirection.LeftToRight };
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock { Text = "يُحفظ التعديل في ملف نسخة مستقل. لا يُرفع إلى السيرفر حالياً.", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(entity); body.Children.Add(iban); body.Children.Add(branch); body.Children.Add(swift);
        var dialog = new ContentDialog { Title = original == null ? "إضافة حساب جهة" : "تعديل حساب جهة", Content = body, PrimaryButtonText = "حفظ نسخة معدلة", CloseButtonText = "إلغاء", XamlRoot = Content.XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(entity.Text)) { await Error("أدخل اسم الجهة."); return; }
        var clean = BackupAccountEditor.NormalizeIban(iban.Text);
        var picker = new FileSavePicker { SuggestedFileName = "نسخة الرواتب المعدلة" }; picker.FileTypeChoices.Add("ZIP", new List<string> { ".zip" });
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this)); var file = await picker.PickSaveFileAsync();
        if (file == null) return;
        if (originalSourcePath != null && string.Equals(Path.GetFullPath(file.Path), Path.GetFullPath(originalSourcePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("اختر ملفاً آخر للحفاظ على النسخة الأصلية.");
        var replacement = new Account(entity.Text, clean, branch.Text, swift.Text);
        await Task.Run(() => BackupAccountEditor.SaveCopy(snapshot.ArchivePath, file.Path, replacement, original?.Entity));
        StatusText.Text = "حُفظت نسخة معدلة مستقلة. افتحها لعرض التغيير؛ لم تُرفع إلى السيرفر.";
    }
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSection("settings", "الإعدادات");
    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var address = ServerAddress.Text.Trim();
        if (address.Length > 0 && (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || !BackupReader.ValidServerAddress(uri)))
        { SettingsMessage.Text = "أدخل عنوان السيرفر المحلي بصيغة http:// وعنوان IP فقط."; return; }
        var next = new WindowsSettings(address, HideZeroRows.IsChecked == true);
        try { WindowsSettingsStore.Save(next); settings = next; SettingsMessage.Text = "حُفظت الإعدادات على هذا الجهاز."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SettingsMessage.Text = "تعذّر حفظ الإعدادات على الجهاز."; }
    }
    private async void BankItem_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not BankRow row) return;
        try {
            var choice = await new ContentDialog { Title = row.Label, Content = row.Amount, PrimaryButtonText = "نسخ الرقم", CloseButtonText = "إغلاق", XamlRoot = Content.XamlRoot }.ShowAsync();
            if (choice == ContentDialogResult.Primary) { var package = new Windows.ApplicationModel.DataTransfer.DataPackage(); package.SetText(row.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)); Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package); Windows.ApplicationModel.DataTransfer.Clipboard.Flush(); }
        } catch (Exception ex) { await Error(ex.Message); }
    }
    private async void RefreshBank()
    {
        if (!viewReady || section != "bank") return;
        var snapshot = data; var month = MonthPicker.SelectedItem as string ?? ""; var hideZero = settings.HideZeroBankRows;
        if (snapshot == null || month.Length == 0) { BankTotal.Text = "—"; BankMonth.Text = ""; BankStatus.Text = "افتح نسخة احتياطية أولاً."; BankSalaryRows.ItemsSource = null; BankManualRows.ItemsSource = null; BankDeductionRows.ItemsSource = null; return; }
        BankMonth.Text = month; BankTotal.Text = "—"; BankSalaryRows.ItemsSource = null; BankManualRows.ItemsSource = null; BankDeductionRows.ItemsSource = null;
        BankStatus.Text = "جارٍ احتساب الكشف…";
        try {
            var result = await Task.Run(() => {
                var b = BankCalculator.Calculate(snapshot, month); var manual = ManualAmounts.ForMonth(snapshot, month);
                BankRow Row(string label, double value) => new(label, value.ToString("N0"), value);
                var salary = new[] { Row("صافي الرواتب كاش", b.Cash), Row("صافي الرواتب بطاقة", b.Card), Row("صافي غير مصنف", b.Unclassified) };
                var extra = new[] { Row("الإجازات المتراكمة", manual.Leave), Row("مكافآت المنتسبين", manual.Rewards), Row("قائمة مفردة", manual.Single) };
                var deductions = b.Breakdown.Select(x => Row(x.Key, x.Value)).Concat(new[] { Row("مجموع استقطاعات المصارف", b.BankDeductions), Row("التقاعد والضمان الاجتماعي", b.Contract) }).ToArray();
                return (b, manual, salary, extra, deductions);
            });
            if (!ReferenceEquals(data, snapshot) || section != "bank" || (MonthPicker.SelectedItem as string ?? "") != month || settings.HideZeroBankRows != hideZero) return;
            var b = result.b; BankMonth.Text = month; BankTotal.Text = (b.Subtotal + result.manual.Leave + result.manual.Rewards + result.manual.Single).ToString("N0") + " د.ع";
            BankStatus.Text = $"مطابقة المصارف والتنفيذ: {(b.Matched ? "متطابقة" : "تحتاج مراجعة")} · الفرق {b.BankDeductions - b.ExecutionDeductions:N0} · رواتب بلا صافي {b.MissingNet:N0} · عقود بلا اسمي {b.MissingNominal:N0}. راجع الحساب قبل الاعتماد.";
            BankSalaryRows.ItemsSource = result.salary.Where(x => !hideZero || x.Value != 0).ToList(); BankManualRows.ItemsSource = result.extra.Where(x => !hideZero || x.Value != 0).ToList(); BankDeductionRows.ItemsSource = result.deductions.Where(x => !hideZero || x.Value != 0).ToList();
        } catch (Exception ex) { if (section == "bank" && ReferenceEquals(data, snapshot)) { BankTotal.Text = "—"; BankStatus.Text = ex.Message; BankSalaryRows.ItemsSource = null; BankManualRows.ItemsSource = null; BankDeductionRows.ItemsSource = null; } }
    }
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var url = new TextBox { Header = "عنوان السيرفر المحلي", PlaceholderText = "http://192.168.1.10:8765", Text = settings.ServerAddress, FlowDirection = FlowDirection.LeftToRight };
        var token = new PasswordBox { Header = "رمز الوصول", FlowDirection = FlowDirection.LeftToRight };
        var content = new StackPanel { Spacing = 12, FlowDirection = FlowDirection.RightToLeft };
        content.Children.Add(new TextBlock { Text = "اقرأ آخر نسخة من السيرفر، أو اختر ملف نسخة محفوظاً. القراءة فقط، ولا تُرفع تغييرات.", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(url); content.Children.Add(token);
        var dialog = new ContentDialog { Title = "فتح نسخة احتياطية", Content = content, PrimaryButtonText = "تنزيل من السيرفر", SecondaryButtonText = "اختيار ملف", CloseButtonText = "إلغاء", XamlRoot = Content.XamlRoot };
        var choice = await dialog.ShowAsync(); if (choice == ContentDialogResult.None) return;
        string? path = null; string? selectedOriginal = null; bool retained = false;
        try
        {
            StatusText.Text = "جارٍ قراءة النسخة…";
            if (choice == ContentDialogResult.Primary)
            {
                if (!Uri.TryCreate(url.Text.Trim(), UriKind.Absolute, out var uri)) throw new InvalidOperationException("عنوان السيرفر غير صالح.");
                path = await BackupReader.Download(uri, token.Password);
            }
            else
            {
                var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".zip"); InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                var file = await picker.PickSingleFileAsync();
                if (file == null) { StatusText.Text = "لم تُختر نسخة."; return; }
                selectedOriginal = file.Path;
                path = Path.Combine(Path.GetTempPath(), "salary-" + Guid.NewGuid().ToString("N") + ".zip");
                await Task.Run(() => File.Copy(file.Path, path));
            }
            var loaded = await Task.Run(() => BackupReader.Read(path));
            var previous = data?.ArchivePath; data = loaded; originalSourcePath = selectedOriginal; retained = true;
            MonthPicker.ItemsSource = loaded.Manifest.Months; MonthPicker.SelectedIndex = loaded.Manifest.Months.Length > 0 ? 0 : -1;
            ComparePicker.ItemsSource = loaded.Manifest.Months; ComparePicker.SelectedIndex = loaded.Manifest.Months.Length > 1 ? 1 : -1;
            var reference = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var timestamp = loaded.Manifest.CreatedAt;
            var date = timestamp > 0 && timestamp < 252423993599 ? reference.AddSeconds(timestamp).ToLocalTime().ToString("g") : "غير معروف";
            StatusText.Text = $"النسخة للقراءة فقط · تاريخ إنشائها: {date} · أعد فتحها لتحديث البيانات";
            DeleteArchive(previous); UpdateHome(); SearchChanged(null!, null!); RefreshData(); RefreshBank();
        }
        catch (Exception ex)
        {
            StatusText.Text = data == null ? "لم تُفتح نسخة." : "تعذّر فتح النسخة الجديدة؛ تبقى البيانات السابقة معروضة.";
            await Error(ex is HttpRequestException ? "تعذّر الاتصال بالسيرفر أو لم يقبل رمز الوصول." : ex.Message);
        }
        finally { token.Password = ""; if (!retained) DeleteArchive(path); }
    }
    private async Task Error(string message) => await new ContentDialog { Title = "تنبيه", Content = message, CloseButtonText = "حسناً", XamlRoot = Content.XamlRoot }.ShowAsync();
    private void MonthChanged(object sender, SelectionChangedEventArgs e) { if (!viewReady) return; UpdateHome(); SearchChanged(null!, null!); RefreshData(); RefreshBank(); }
    private void UpdateHome()
    {
        if (data == null || MonthPicker.SelectedItem is not string month) return;
        var employees = data.Employees.Where(x => x.Month == month).ToList();
        NetText.Text = employees.Sum(x => x.Net ?? 0).ToString("N0") + " د.ع";
        CountText.Text = $"عدد الموظفين: {employees.Count:N0}";
        var counts = new int[4]; foreach (var employee in employees) counts[PayrollMath.Category(employee.Rank ?? "")]++;
        CategoriesText.Text = $"ضباط {counts[0]:N0}   ·   موظفون {counts[1]:N0}   ·   مراتب {counts[2]:N0}   ·   عقود {counts[3]:N0}";
    }
    private async void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!viewReady) return;
        searchCancellation?.Cancel();
        if (SearchBox == null || SearchResults == null) return;
        if (data == null || MonthPicker.SelectedItem is not string month || string.IsNullOrWhiteSpace(SearchBox.Text)) { SearchResults.ItemsSource = null; return; }
        var source = new CancellationTokenSource(); searchCancellation = source;
        var snapshot = data; var q = PayrollMath.Normalize(SearchBox.Text);
        try
        {
            await Task.Delay(250, source.Token);
            var results = await Task.Run(() => {
                bool Match(string? value) => PayrollMath.Normalize(value ?? "").Contains(q);
                var people = snapshot.Employees.Where(x => x.Month == month && Match(x.Name)).Take(100)
                    .Select(x => new DisplayItem($"راتب · {x.Name}   ·   {x.Rank}   ·   {x.Workplace}   ·   صافي: {(x.Net.HasValue ? x.Net.Value.ToString("N0") : "غير متوفر")}", x));
                var staff = snapshot.Staff.Where(x => Match(x.Name) || Match(x.Mother) || Match(x.Stat) || Match(x.Unified)).Take(100)
                    .Select(x => new DisplayItem($"ملاك · {x.Name}   ·   {x.Rank}   ·   {x.Workplace}", x));
                var entities = snapshot.Deductions.Where(x => x.Month == month && Match(x.Title)).GroupBy(x => x.Title).Take(100)
                    .Select(g => new DisplayItem($"جهة · {g.Key}   ·   المجموع: {(g.Any(x => !x.Amount.HasValue) ? "غير متاح لوجود مبلغ فارغ" : g.Sum(x => x.Amount!.Value).ToString("#,##0.##"))}", new EntitySelection(month, g.Key)));
                var books = snapshot.Books.Where(x => Match(x.Title) || Match(x.Entity) || Match(x.Month)).Take(100)
                    .Select(x => new DisplayItem($"كتاب · {x.Title}   ·   {x.Month}   ·   {x.Entity}", x));
                return people.Concat(staff).Concat(entities).Concat(books).ToList();
            }, source.Token);
            if (source.IsCancellationRequested) return;
            SearchResults.ItemsSource = results; SearchStatus.Text = "الرواتب والجهات للشهر المحدد؛ الملاك والكتب لجميع الأشهر. حد النتائج 100 لكل نوع.";
        }
        catch (OperationCanceledException) { }
    }
    private sealed record EntitySelection(string Month, string Title);
    private sealed record PayrollSelection(string Month, string Title);
    private sealed record SpecialSelection(string Month, string Title);
    private sealed record BankAmount(string Label, double Value);
    private sealed record AuditSelection(string Title, List<string> Lines);
    private void CompareChanged(object sender, SelectionChangedEventArgs e) { if (viewReady) RefreshData(); }
    private void DataFilterChanged(object sender, TextChangedEventArgs e) { if (viewReady) RefreshData(); }
    private async void RefreshData()
    {
        if (DataPanel == null || DataPanel.Visibility != Visibility.Visible) return;
        var snapshot = data; if (snapshot == null) { DataCaption.Text = "افتح نسخة احتياطية أولاً."; DataResults.ItemsSource = null; return; }
        var page = section; var month = MonthPicker.SelectedItem as string ?? ""; var comparisonMonth = ComparePicker.SelectedItem as string ?? ""; var q = PayrollMath.Normalize(DataFilter.Text);
        try
        {
            var result = await Task.Run(() => {
                bool Match(string? s) => PayrollMath.Normalize(s ?? "").Contains(q);
                var rows = new List<DisplayItem>(); string caption;
                if (page == "reports")
                {
                    var groups = snapshot.Deductions.Where(x => x.Month == month && Match(x.Title)).GroupBy(x => x.Title).OrderByDescending(g => g.Sum(x => x.Amount ?? 0)).ToList();
                    foreach (var g in groups.Take(500))
                    {
                        var unique = g.Select(x => PayrollMath.Key(x.Name)).Distinct().Count();
                        var amountText = g.Any(x => !x.Amount.HasValue) ? "غير متاح لوجود مبلغ فارغ" : g.Sum(x => x.Amount!.Value).ToString("#,##0.##");
                        rows.Add(new($"{g.Key}   ·   الموظفون: {g.Count():N0}   ·   المكرر: {g.Count() - unique:N0}   ·   المبلغ: {amountText}", new EntitySelection(month, g.Key)));
                    }
                    caption = $"{groups.Count:N0} جهة · اضغط جهة لمعاينة تفاصيلها · تظهر أول 500";
                }
                else if (page == "payroll")
                {
                    var sums = new Dictionary<string, (int Count, double Total)>();
                    foreach (var employee in snapshot.Employees.Where(x => x.Month == month))
                        foreach (var item in PayrollMath.Decode<double>(employee.Items))
                        {
                            var previous = sums.GetValueOrDefault(item.Key);
                            sums[item.Key] = (previous.Count + 1, previous.Total + item.Value);
                        }
                    var matching = sums.Where(x => Match(x.Key)).OrderByDescending(x => x.Value.Total).ToList();
                    foreach (var x in matching)
                    {
                        rows.Add(new DisplayItem($"{x.Key}   ·   الموظفون: {x.Value.Count:N0}   ·   المبلغ: {x.Value.Total:N0}", new PayrollSelection(month, x.Key)));
                        if (SpecialReports.Supports(x.Key)) rows.Add(new DisplayItem($"↳ تفاصيل الكشف الخاص · {x.Key}", new SpecialSelection(month, x.Key)));
                    }
                    caption = $"{matching.Count:N0} بند · اضغط بنداً لعرض التفاصيل وتصدير Excel";
                }
                else if (page == "staff")
                {
                    var matches = snapshot.Staff.Where(x => Match(x.Name) || Match(x.Mother) || Match(x.Stat) || Match(x.Unified)).Take(500);
                    rows.AddRange(matches.Select(x => new DisplayItem($"{x.Name}   ·   {x.Rank}   ·   {x.Status}   ·   {x.Workplace}", x)));
                    caption = $"عدد سجلات الملاك: {snapshot.Staff.Count:N0} · تظهر أول 500 · ابحث بالاسم أو اسم الأم أو الأرقام";
                }
                else if (page == "accounts")
                {
                    var matches = snapshot.Accounts.Where(x => Match(x.Entity) || Match(x.Iban) || Match(x.Branch)).OrderBy(x => x.Entity).Take(500).ToList();
                    rows.AddRange(matches.Select(x => new DisplayItem($"{x.Entity}   ·   {x.Branch}   ·   {x.Iban}", x)));
                    caption = $"عدد حسابات الجهات: {snapshot.Accounts.Count:N0} · اضغط حساباً لتعديله في نسخة مستقلة";
                }
                else if (page == "books")
                {
                    var matches = snapshot.Books.Where(x => x.Month == month && (Match(x.Title) || Match(x.Entity))).ToList();
                    rows.AddRange(matches.Select(x => new DisplayItem($"{x.Title}   ·   {x.Entity}   ·   {x.Pages} صفحة", x)));
                    caption = $"{matches.Count:N0} كتاب للشهر · اضغط كتاباً لحفظ ملف PDF وفتحه";
                }
                else if (page == "audit")
                {
                    var current = snapshot.Employees.Where(x => x.Month == month).ToList();
                    var groups = current.GroupBy(x => PayrollMath.Key(x.Name)).ToDictionary(g => g.Key, g => g.ToList());
                    var duplicates = groups.Count(x => x.Value.Count > 1);
                    var withoutSalary = snapshot.Deductions.Count(x => x.Month == month && !groups.ContainsKey(PayrollMath.Key(x.Name)));
                    rows.Add(new($"أسماء رواتب مكررة: {duplicates:N0} اسم", ""));
                    rows.Add(new($"استقطاعات بلا اسم مطابق في الرواتب: {withoutSalary:N0} سجل", ""));
                    rows.Add(new($"رواتب بلا صافي: {current.Count(x => x.Net == null):N0}", ""));
                    foreach (var check in SalaryChecks.Run(snapshot, month))
                        rows.Add(new($"{check.Title}: {check.Lines.Count:N0} · {check.Explanation}", new AuditSelection(check.Title, check.Lines)));
                    foreach (var check in StaffMatching.Run(snapshot, month))
                        rows.Add(new($"{check.Title}: {check.Lines.Count:N0} · {check.Explanation}", new AuditSelection(check.Title, check.Lines)));
                    var duplicatesDeduction = DeductionComparison.Duplicates(snapshot, month);
                    var duplicateLines = duplicatesDeduction.Select(g => $"{g[0].Name} · {g[0].Title} · {g[0].Note} · {g[0].Amount:N0} · {g.Count} مرات").ToList();
                    rows.Add(new($"تكرار محتمل في الاستقطاعات: {duplicateLines.Count:N0} حالة", new AuditSelection("التكرار المحتمل", duplicateLines)));
                    if (!string.IsNullOrEmpty(comparisonMonth) && comparisonMonth != month)
                    {
                        try
                        {
                            var result = DeductionComparison.Compare(snapshot, comparisonMonth, month);
                            foreach (var kind in new[] { "استقطاع جديد", "استقطاع متوقف", "زاد", "نقص" })
                            {
                                var changes = result.Entries.Where(x => x.Kind == kind).Select(x => $"{x.Name} · {x.Entity} · {x.Reference} · {x.Before:N0} ← {x.After:N0}").ToList();
                                rows.Add(new($"{kind}: {changes.Count:N0} استقطاع", new AuditSelection(kind, changes)));
                            }
                            var stoppedPeople = result.People.Where(x => x.After == 0).Select(x => $"{x.Name} · السابق {x.Before:N0}").ToList();
                            rows.Add(new($"توقف الاستقطاع بالكامل: {stoppedPeople.Count:N0} موظف", new AuditSelection("توقف الاستقطاع بالكامل", stoppedPeople)));
                        }
                        catch (InvalidDataException ex) { rows.Add(new(ex.Message, "")); }
                    }
                    if (!string.IsNullOrEmpty(comparisonMonth) && comparisonMonth != month)
                    {
                        try
                        {
                            var salary = SalaryComparison.Compare(snapshot, comparisonMonth, month);
                            foreach (var kind in new[] { "جدد", "خارجون", "تغير الصافي", "تغير الرتبة", "تغير محل العمل", "تغير الحساب المصرفي" })
                            {
                                var lines = salary.Changes.Where(x => x.Kind == kind).Select(x => $"{x.Name} · {x.Detail}").ToList();
                                rows.Add(new($"{kind}: {lines.Count:N0} موظف", new AuditSelection(kind, lines)));
                            }
                            if (salary.AmbiguousOld + salary.AmbiguousNew > 0)
                                rows.Add(new($"أسماء متكررة استُبعدت من مقارنة الرواتب: {salary.AmbiguousOld + salary.AmbiguousNew:N0} مجموعة", ""));
                        }
                        catch (InvalidDataException ex) { rows.Add(new(ex.Message, "")); }
                    }
                    caption = "المقارنة بالاسم والجهة والإضبارة؛ الأسماء المكررة مستبعدة من مقارنة الرواتب. فحوص الرواتب لا تزال غير مكتملة.";
                }
                else { caption = "القسم غير متاح."; }
                return (rows, caption);
            });
            if (!ReferenceEquals(data, snapshot) || section != page || (MonthPicker.SelectedItem as string ?? "") != month || (ComparePicker.SelectedItem as string ?? "") != comparisonMonth || PayrollMath.Normalize(DataFilter.Text) != q) return;
            DataResults.ItemsSource = result.rows; DataCaption.Text = result.caption;
        }
        catch (Exception ex) { if (section == page) { DataResults.ItemsSource = null; DataCaption.Text = ex.Message; } }
    }
    private async void Item_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not DisplayItem item || data == null) return;
        try
        {
            if (item.Value is Book book) { await OpenBook(book); return; }
            if (item.Value is Account accountItem) { await EditAccount(accountItem); return; }
            if (item.Value is SpecialSelection selectedSpecial)
            {
                var snapshot = data;
                var report = await Task.Run(() => SpecialReports.Build(snapshot, selectedSpecial.Month, selectedSpecial.Title));
                if (report != null)
                {
                    var shown = string.Join("\n", report.Lines.Take(500));
                    if (report.Lines.Count > 500) shown += "\n... تظهر أول 500 نتيجة.";
                    var body = new StackPanel { Spacing = 10 };
                    body.Children.Add(new TextBlock { Text = report.Explanation, TextWrapping = TextWrapping.Wrap });
                    body.Children.Add(new TextBox { Text = shown, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 450 });
                    var specialDialog = new ContentDialog { Title = report.Title, Content = body, PrimaryButtonText = "تصدير Excel", SecondaryButtonText = "تصدير PDF", CloseButtonText = "إغلاق", XamlRoot = Content.XamlRoot };
                    var specialPreview = false;
                    var shareSpecialExcel = false;
                    var specialShareButton = new Button { Content = "مشاركة Excel" };
                    specialShareButton.Click += (_, _) => { shareSpecialExcel = true; specialDialog.Hide(); };
                    body.Children.Add(specialShareButton);
                    var specialPreviewButton = new Button { Content = "معاينة PDF" };
                    specialPreviewButton.Click += (_, _) => { specialPreview = true; specialDialog.Hide(); };
                    body.Children.Add(specialPreviewButton);
                    var specialAction = await specialDialog.ShowAsync();
                    if (shareSpecialExcel) await ShareGeneratedFile(".xlsx", "الكشف الخاص", output => SpecialExcel.Write(output, snapshot, selectedSpecial.Month, selectedSpecial.Title));
                    else if (specialPreview) await OpenPreview(".pdf", output => SpecialPdf.Write(output, snapshot, selectedSpecial.Month, selectedSpecial.Title), report.Title);
                    else if (specialAction == ContentDialogResult.Primary || specialAction == ContentDialogResult.Secondary)
                    {
                        var picker = new FileSavePicker { SuggestedFileName = "الكشف الخاص" };
                        if (specialAction == ContentDialogResult.Primary) picker.FileTypeChoices.Add("Excel", new List<string> { ".xlsx" });
                        else picker.FileTypeChoices.Add("PDF", new List<string> { ".pdf" });
                        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                        var file = await picker.PickSaveFileAsync();
                        if (file != null)
                        {
                            if (specialAction == ContentDialogResult.Primary) await Task.Run(() => SpecialExcel.Write(file.Path, snapshot, selectedSpecial.Month, selectedSpecial.Title));
                            else await Task.Run(() => SpecialPdf.Write(file.Path, snapshot, selectedSpecial.Month, selectedSpecial.Title));
                        }
                    }
                }
                return;
            }
            if (item.Value is AuditSelection audit)
            {
                var preview = string.Join("\n", audit.Lines.Take(500));
                if (audit.Lines.Count > 500) preview += "\n... تظهر أول 500 حالة؛ التصدير يشمل جميع النتائج.";
                var contents = new TextBox { Text = preview, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 500 };
                var auditBody = new StackPanel { Spacing = 10 }; auditBody.Children.Add(contents);
                var auditDialog = new ContentDialog { Title = audit.Title, Content = auditBody, PrimaryButtonText = "تصدير Excel", CloseButtonText = "إغلاق", XamlRoot = Content.XamlRoot };
                var shareAudit = false; var shareAuditButton = new Button { Content = "مشاركة Excel" };
                shareAuditButton.Click += (_, _) => { shareAudit = true; auditDialog.Hide(); }; auditBody.Children.Add(shareAuditButton);
                var auditAction = await auditDialog.ShowAsync();
                var monthLabel = MonthPicker.SelectedItem as string ?? "";
                if (ComparePicker.SelectedItem is string previous && previous != monthLabel) monthLabel += " / " + previous;
                if (shareAudit) await ShareGeneratedFile(".xlsx", "نتائج التدقيق", output => ExcelExport.Audit(output, monthLabel, audit.Title, audit.Lines));
                else if (auditAction == ContentDialogResult.Primary)
                {
                    var picker = new FileSavePicker { SuggestedFileName = "نتائج التدقيق" };
                    picker.FileTypeChoices.Add("Excel", new List<string> { ".xlsx" });
                    InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                    var file = await picker.PickSaveFileAsync();
                    if (file != null) await Task.Run(() => ExcelExport.Audit(file.Path, monthLabel, audit.Title, audit.Lines));
                }
                return;
            }
            if (item.Value is BankAmount amount)
            {
                var copyAction = await new ContentDialog { Title = amount.Label, Content = amount.Value.ToString("N0"), PrimaryButtonText = "نسخ الرقم", CloseButtonText = "إغلاق", XamlRoot = Content.XamlRoot }.ShowAsync();
                if (copyAction == ContentDialogResult.Primary)
                {
                    var clipboard = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    clipboard.SetText(amount.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(clipboard);
                }
                return;
            }
            string title; string text;
            if (item.Value is Employee emp)
            {
                title = emp.Name;
                var ambiguous = data.Employees.Count(x => x.Month == emp.Month && PayrollMath.Key(x.Name) == PayrollMath.Key(emp.Name)) != 1;
                List<Deduction> deductions = ambiguous ? [] : data.Deductions.Where(x => x.Month == emp.Month && PayrollMath.Key(x.Name) == PayrollMath.Key(emp.Name)).ToList();
                var details = PayrollMath.Decode<string>(emp.Details); var items = PayrollMath.Decode<double>(emp.Items);
                var netText = emp.Net?.ToString("#,##0.##") ?? "غير متوفر";
                text = $"الشهر: {emp.Month}\nالرتبة: {emp.Rank}\nمحل العمل: {emp.Workplace}\nصافي الراتب: {netText}\n\nبنود الراتب:\n" +
                    string.Join("\n", items.Select(x => $"{x.Key}: {x.Value:#,##0.##}")) + "\n\nالاستقطاعات:\n" + (ambiguous ? "الاسم متكرر؛ تعذّر ربط الاستقطاعات بأمان." : string.Join("\n", deductions.Select(x => $"{x.Title}: {x.Amount?.ToString("#,##0.##") ?? "غير متوفر"}  {x.Note}"))) +
                    "\n\nالبيانات الأخرى:\n" + string.Join("\n", details.Select(x => $"{x.Key}: {x.Value}"));
            }
            else if (item.Value is Staff member)
            {
                title = member.Name; text = $"الرتبة: {member.Rank}\nالحالة: {member.Status}\nمحل العمل: {member.Workplace}\nاسم الأم: {member.Mother}\nالرقم الإحصائي: {member.Stat}\nالرقم الموحد: {member.Unified}\n";
                if (!string.IsNullOrEmpty(member.Details))
                {
                    using var doc = JsonDocument.Parse(Convert.FromBase64String(member.Details));
                    foreach (var section in doc.RootElement.EnumerateArray())
                    {
                        text += "\n" + section.GetProperty("title").GetString() + ":\n";
                        foreach (var field in section.GetProperty("fields").EnumerateArray()) text += $"{field.GetProperty("label").GetString()}: {field.GetProperty("value").GetString()}\n";
                    }
                }
            }
            else if (item.Value is EntitySelection entity)
            {
                title = entity.Title; var entries = data.Deductions.Where(x => x.Month == entity.Month && x.Title == entity.Title).ToList();
                var account = data.Accounts.FirstOrDefault(x => PayrollMath.Normalize(x.Entity) == PayrollMath.Normalize(entity.Title));
                var sumText = entries.Any(x => !x.Amount.HasValue) ? "غير متاح لوجود مبلغ فارغ" : entries.Sum(x => x.Amount!.Value).ToString("#,##0.##");
                text = $"الشهر: {entity.Month}\nالمجموع: {sumText}\nعدد الموظفين: {entries.Count:N0}\nالمكرر: {entries.Count - entries.Select(x => PayrollMath.Key(x.Name)).Distinct().Count():N0}\n" +
                    (account == null ? "" : $"IBAN: {account.Iban}\nالفرع: {account.Branch}\n") + "\n" + string.Join("\n", entries.Select(x => $"{x.Name} · {x.Amount:N0} · {x.Note}"));
            }
            else if (item.Value is PayrollSelection payroll)
            {
                title = payroll.Title;
                var snapshot = data;
                text = await Task.Run(() => {
                    var entries = snapshot.Employees.Where(x => x.Month == payroll.Month).Select(x => (Employee: x, Items: PayrollMath.Decode<double>(x.Items)))
                        .Where(x => x.Items.ContainsKey(payroll.Title)).Select(x => (x.Employee, Amount: x.Items[payroll.Title])).ToList();
                    return $"الشهر: {payroll.Month}\nعدد الموظفين: {entries.Count:N0}\nالمجموع: {entries.Sum(x => x.Amount):N0}\n\n" +
                        string.Join("\n", entries.Take(500).Select(x => $"{x.Employee.Name} · {x.Employee.Rank} · {x.Employee.Workplace} · {x.Amount:N0}")) +
                        (entries.Count > 500 ? "\n... تعرض المعاينة أول 500 سجل؛ التصدير يشمل الجميع." : "");
                });
            }
            else return;
            var textBox = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 500 };
            var contentPanel = new StackPanel { Spacing = 10 }; contentPanel.Children.Add(textBox);
            var dialog = new ContentDialog { Title = title, Content = contentPanel, CloseButtonText = "إغلاق", XamlRoot = Content.XamlRoot };
            var previewRequested = false;
            var salaryReportRequested = false;
            var excelShareRequested = false;
            if (item.Value is EntitySelection or PayrollSelection)
            {
                var shareExcelButton = new Button { Content = "مشاركة Excel" };
                shareExcelButton.Click += (_, _) => { excelShareRequested = true; dialog.Hide(); };
                contentPanel.Children.Add(shareExcelButton);
            }
            if (item.Value is Employee or EntitySelection or PayrollSelection or Staff)
            {
                var previewButton = new Button { Content = item.Value is Employee ? "معاينة قسيمة الراتب" : "معاينة PDF" };
                previewButton.Click += (_, _) => { previewRequested = true; dialog.Hide(); };
                contentPanel.Children.Add(previewButton);
            }
            if (item.Value is Employee)
            {
                var salaryReportButton = new Button { Content = "تقرير الراتب PDF" };
                salaryReportButton.Click += (_, _) => { salaryReportRequested = true; dialog.Hide(); };
                contentPanel.Children.Add(salaryReportButton);
            }
            if (item.Value is EntitySelection or PayrollSelection) { dialog.PrimaryButtonText = "تصدير Excel"; dialog.SecondaryButtonText = "تصدير PDF"; }
            // الكشف الخاص يبقى متاحاً من قائمة مستقلة حتى لا يحلّ محل خيارات التصدير.
            if (item.Value is Staff) dialog.PrimaryButtonText = "تصدير تقرير الملاك PDF";
            if (item.Value is Employee) { dialog.PrimaryButtonText = "حفظ القسيمة صورة"; dialog.SecondaryButtonText = "نسخ الصورة"; }
            var action = await dialog.ShowAsync();
            if (excelShareRequested && data != null)
            {
                var snapshot = data;
                if (item.Value is EntitySelection group) await ShareGeneratedFile(".xlsx", "كشف جهة التنفيذ", output => ExcelExport.Entity(output, snapshot, group.Month, group.Title));
                else if (item.Value is PayrollSelection payrollGroup) await ShareGeneratedFile(".xlsx", "كشف بند الرواتب", output => ExcelExport.Payroll(output, snapshot, payrollGroup.Month, payrollGroup.Title));
            }
            else if (salaryReportRequested && item.Value is Employee salaryEmployee && data != null)
                await SalaryReportOptions(data, salaryEmployee);
            else if (previewRequested && data != null)
            {
                var snapshot = data;
                if (item.Value is Employee person) await OpenPreview(".png", path => PayslipImage.Write(path, SlipData.From(snapshot, person)), "قسيمة الراتب");
                else if (item.Value is EntitySelection group) await OpenPreview(".pdf", path => PdfExport.Entity(path, snapshot, group.Month, group.Title), group.Title);
                else if (item.Value is PayrollSelection payrollGroup) await OpenPreview(".pdf", path => PdfExport.Payroll(path, snapshot, payrollGroup.Month, payrollGroup.Title), payrollGroup.Title);
                else if (item.Value is Staff staffMember) await OpenPreview(".pdf", path => StaffReportPdf.Write(path, StaffReportData.From(staffMember)), "تقرير الملاك");
            }
            else if (action == ContentDialogResult.Primary && item.Value is Employee selectedEmployee && data != null)
            {
                var snapshot = data;
                var picker = new FileSavePicker { SuggestedFileName = "قسيمة الراتب" };
                picker.FileTypeChoices.Add("PNG", new List<string> { ".png" });
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                var file = await picker.PickSaveFileAsync();
                if (file != null) await Task.Run(() => PayslipImage.Write(file.Path, SlipData.From(snapshot, selectedEmployee)));
            }
            else if (action == ContentDialogResult.Secondary && item.Value is Employee copyEmployee && data != null)
            {
                var snapshot = data;
                var path = await CreateTemporaryPreview(".png", output => PayslipImage.Write(output, SlipData.From(snapshot, copyEmployee)));
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
            }
            else if (action == ContentDialogResult.Primary && item.Value is EntitySelection selected && data != null)
            {
                var picker = new FileSavePicker { SuggestedFileName = "كشف جهة التنفيذ" };
                picker.FileTypeChoices.Add("Excel", new List<string> { ".xlsx" });
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                var file = await picker.PickSaveFileAsync();
                if (file != null) { var snapshot = data; await Task.Run(() => ExcelExport.Entity(file.Path, snapshot, selected.Month, selected.Title)); }
            }
            else if (action == ContentDialogResult.Primary && item.Value is PayrollSelection selectedPayroll && data != null)
            {
                var picker = new FileSavePicker { SuggestedFileName = "كشف بند الرواتب" };
                picker.FileTypeChoices.Add("Excel", new List<string> { ".xlsx" });
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                var file = await picker.PickSaveFileAsync();
                if (file != null) { var snapshot = data; await Task.Run(() => ExcelExport.Payroll(file.Path, snapshot, selectedPayroll.Month, selectedPayroll.Title)); }
            }
            else if (action == ContentDialogResult.Primary && item.Value is Staff staff)
            {
                var picker = new FileSavePicker { SuggestedFileName = "بيان معلومات المنتسب" };
                picker.FileTypeChoices.Add("PDF", new List<string> { ".pdf" });
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                var file = await picker.PickSaveFileAsync();
                if (file != null) await Task.Run(() => StaffReportPdf.Write(file.Path, StaffReportData.From(staff)));
            }
            else if (action == ContentDialogResult.Secondary && (item.Value is EntitySelection or PayrollSelection) && data != null)
            {
                var picker = new FileSavePicker { SuggestedFileName = "كشف الرواتب" };
                picker.FileTypeChoices.Add("PDF", new List<string> { ".pdf" });
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                var file = await picker.PickSaveFileAsync();
                if (file != null)
                {
                    var snapshot = data;
                    if (item.Value is EntitySelection selectedEntity) await Task.Run(() => PdfExport.Entity(file.Path, snapshot, selectedEntity.Month, selectedEntity.Title));
                    else if (item.Value is PayrollSelection selectedPayrollPdf) await Task.Run(() => PdfExport.Payroll(file.Path, snapshot, selectedPayrollPdf.Month, selectedPayrollPdf.Title));
                }
            }
        }
        catch (Exception ex) { await Error(ex.Message); }
    }
    private async Task<string> CreateTemporaryPreview(string extension, Action<string> create)
    {
        var folder = Path.Combine(Path.GetTempPath(), "salary-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var filename = extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ? "قسيمة الراتب.png" : extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ? "كشف الرواتب.xlsx" : "مستند الرواتب.pdf";
        var path = Path.Combine(folder, filename);
        try
        {
            await Task.Run(() => create(path));
            temporaryPreviews.Add(path);
            return path;
        }
        catch { DeletePreview(path); throw; }
    }
    private async Task ShareGeneratedFile(string extension, string title, Action<string> create)
    {
        var path = await CreateTemporaryPreview(extension, create);
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        SharePreparedFile(file, title);
    }
    private async Task OpenPreview(string extension, Action<string> create, string title = "معاينة المستند")
    {
        var path = await CreateTemporaryPreview(extension, create);
        await ShowDocumentPreview(path, title);
    }
    private async Task SalaryReportOptions(BackupData snapshot, Employee employee)
    {
        var sample = await Task.Run(() => SalaryReportData.From(snapshot, employee, true, true));
        var body = new StackPanel { Spacing = 12 };
        if (sample.MissingDetails) body.Children.Add(new TextBlock { Text = "تفاصيل الرواتب غير متوفرة في هذه النسخة؛ أعد استيراد ملف الشهر على الآيفون لإظهار المخصصات.", TextWrapping = TextWrapping.Wrap });
        foreach (var warning in sample.Mismatches) body.Children.Add(new TextBlock { Text = warning, TextWrapping = TextWrapping.Wrap });
        var hideZeros = new CheckBox { Content = "إخفاء البنود الصفرية", IsChecked = true };
        var hidePersonal = new CheckBox { Content = "إخفاء IBAN والأرقام الشخصية", IsChecked = true };
        body.Children.Add(hideZeros); body.Children.Add(hidePersonal);
        var dialog = new ContentDialog { Title = "تقرير راتب · " + employee.Month, Content = body, PrimaryButtonText = "حفظ PDF", SecondaryButtonText = "معاينة PDF", CloseButtonText = "إغلاق", XamlRoot = Content.XamlRoot };
        var action = await dialog.ShowAsync();
        if (action == ContentDialogResult.None) return;
        var removeZeros = hideZeros.IsChecked == true; var maskPersonal = hidePersonal.IsChecked == true;
        var report = await Task.Run(() => SalaryReportData.From(snapshot, employee, removeZeros, maskPersonal));
        if (action == ContentDialogResult.Secondary) await OpenPreview(".pdf", output => SalaryReportPdf.Write(output, report), "تقرير الراتب");
        else
        {
            var picker = new FileSavePicker { SuggestedFileName = "تقرير الراتب" }; picker.FileTypeChoices.Add("PDF", new List<string> { ".pdf" });
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file != null) await Task.Run(() => SalaryReportPdf.Write(file.Path, report));
        }
    }
    private async Task OpenBook(Book book)
    {
        if (data == null) return;
        var archive = data.ArchivePath;
        var dialog = new ContentDialog { Title = book.Title, Content = $"{book.Month} · {book.Pages} صفحة", PrimaryButtonText = "معاينة", SecondaryButtonText = "حفظ PDF", CloseButtonText = "إغلاق", XamlRoot = Content.XamlRoot };
        var action = await dialog.ShowAsync();
        if (action == ContentDialogResult.Primary) await OpenPreview(".pdf", output => BookArchive.Export(archive, book, output), book.Title);
        else if (action == ContentDialogResult.Secondary)
        {
            var picker = new FileSavePicker { SuggestedFileName = "كتاب رسمي" }; picker.FileTypeChoices.Add("PDF", new List<string> { ".pdf" });
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file != null) await Task.Run(() => BookArchive.Export(archive, book, file.Path));
        }
    }
}
