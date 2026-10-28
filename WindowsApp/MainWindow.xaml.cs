using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.IO.Compression;
using System.Text.Json;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace SalaryWindows;
internal sealed record DisplayItem(string Display, object Value);
public sealed partial class MainWindow : Window
{
    private BackupData? data;
    private CancellationTokenSource? searchCancellation;
    private string section = "home";
    public MainWindow() { InitializeComponent(); Closed += (_, _) => DeleteArchive(data?.ArchivePath); }
    private static void DeleteArchive(string? path) { if (path != null) try { File.Delete(path); } catch { } }
    private void ShowSection(string page, string title)
    {
        section = page; PageTitle.Text = title;
        HomePanel.Visibility = page == "home" ? Visibility.Visible : Visibility.Collapsed;
        SearchPanel.Visibility = page == "search" ? Visibility.Visible : Visibility.Collapsed;
        DataPanel.Visibility = page is "reports" or "payroll" or "bank" or "audit" or "staff" or "books" ? Visibility.Visible : Visibility.Collapsed;
        ComparePicker.Visibility = page == "audit" ? Visibility.Visible : Visibility.Collapsed;
        if (DataPanel.Visibility == Visibility.Visible) RefreshData();
    }
    private void Home_Click(object sender, RoutedEventArgs e) => ShowSection("home", "الرئيسية");
    private void SearchPage_Click(object sender, RoutedEventArgs e) => ShowSection("search", "البحث الشامل");
    private void Reports_Click(object sender, RoutedEventArgs e) => ShowSection("reports", "جهات التنفيذ");
    private void PayrollReports_Click(object sender, RoutedEventArgs e) => ShowSection("payroll", "تقارير بنود الرواتب");
    private void Bank_Click(object sender, RoutedEventArgs e) => ShowSection("bank", "كشف حساب البنك");
    private void Audit_Click(object sender, RoutedEventArgs e) => ShowSection("audit", "التدقيق");
    private void Staff_Click(object sender, RoutedEventArgs e) => ShowSection("staff", "الملاك");
    private void Books_Click(object sender, RoutedEventArgs e) => ShowSection("books", "الكتب الرسمية");
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var url = new TextBox { Header = "عنوان السيرفر المحلي", PlaceholderText = "http://192.168.1.10:8765", FlowDirection = FlowDirection.LeftToRight };
        var token = new PasswordBox { Header = "رمز الوصول", FlowDirection = FlowDirection.LeftToRight };
        var content = new StackPanel { Spacing = 12, FlowDirection = FlowDirection.RightToLeft };
        content.Children.Add(new TextBlock { Text = "اقرأ آخر نسخة من السيرفر، أو اختر ملف نسخة محفوظاً. القراءة فقط، ولا تُرفع تغييرات.", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(url); content.Children.Add(token);
        var dialog = new ContentDialog { Title = "فتح نسخة احتياطية", Content = content, PrimaryButtonText = "تنزيل من السيرفر", SecondaryButtonText = "اختيار ملف", CloseButtonText = "إلغاء", XamlRoot = Content.XamlRoot };
        var choice = await dialog.ShowAsync(); if (choice == ContentDialogResult.None) return;
        string? path = null; bool retained = false;
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
                path = Path.Combine(Path.GetTempPath(), "salary-" + Guid.NewGuid().ToString("N") + ".zip");
                await Task.Run(() => File.Copy(file.Path, path));
            }
            var loaded = await Task.Run(() => BackupReader.Read(path));
            var previous = data?.ArchivePath; data = loaded; retained = true;
            MonthPicker.ItemsSource = loaded.Manifest.Months; MonthPicker.SelectedIndex = loaded.Manifest.Months.Length > 0 ? 0 : -1;
            ComparePicker.ItemsSource = loaded.Manifest.Months; ComparePicker.SelectedIndex = loaded.Manifest.Months.Length > 1 ? 1 : -1;
            var reference = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var timestamp = loaded.Manifest.CreatedAt;
            var date = timestamp > 0 && timestamp < 252423993599 ? reference.AddSeconds(timestamp).ToLocalTime().ToString("g") : "غير معروف";
            StatusText.Text = $"النسخة للقراءة فقط · تاريخ إنشائها: {date} · أعد فتحها لتحديث البيانات";
            DeleteArchive(previous); UpdateHome(); SearchChanged(null!, null!); RefreshData();
        }
        catch (Exception ex)
        {
            StatusText.Text = data == null ? "لم تُفتح نسخة." : "تعذّر فتح النسخة الجديدة؛ تبقى البيانات السابقة معروضة.";
            await Error(ex is HttpRequestException ? "تعذّر الاتصال بالسيرفر أو لم يقبل رمز الوصول." : ex.Message);
        }
        finally { token.Password = ""; if (!retained) DeleteArchive(path); }
    }
    private async Task Error(string message) => await new ContentDialog { Title = "تنبيه", Content = message, CloseButtonText = "حسناً", XamlRoot = Content.XamlRoot }.ShowAsync();
    private void MonthChanged(object sender, SelectionChangedEventArgs e) { UpdateHome(); SearchChanged(null!, null!); RefreshData(); }
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
                    .Select(g => new DisplayItem($"جهة · {g.Key}   ·   المجموع: {g.Sum(x => x.Amount ?? 0):N0}", new EntitySelection(month, g.Key)));
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
    private sealed record BankAmount(string Label, double Value);
    private sealed record AuditSelection(string Title, List<string> Lines);
    private void CompareChanged(object sender, SelectionChangedEventArgs e) => RefreshData();
    private void DataFilterChanged(object sender, TextChangedEventArgs e) => RefreshData();
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
                        rows.Add(new($"{g.Key}   ·   الموظفون: {g.Count():N0}   ·   المكرر: {g.Count() - unique:N0}   ·   المبلغ: {g.Sum(x => x.Amount ?? 0):N0}", new EntitySelection(month, g.Key)));
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
                    rows.AddRange(matching.Select(x => new DisplayItem($"{x.Key}   ·   الموظفون: {x.Value.Count:N0}   ·   المبلغ: {x.Value.Total:N0}", new PayrollSelection(month, x.Key))));
                    caption = $"{matching.Count:N0} بند · اضغط بنداً لعرض التفاصيل وتصدير Excel";
                }
                else if (page == "staff")
                {
                    var matches = snapshot.Staff.Where(x => Match(x.Name) || Match(x.Mother) || Match(x.Stat) || Match(x.Unified)).Take(500);
                    rows.AddRange(matches.Select(x => new DisplayItem($"{x.Name}   ·   {x.Rank}   ·   {x.Status}   ·   {x.Workplace}", x)));
                    caption = $"عدد سجلات الملاك: {snapshot.Staff.Count:N0} · تظهر أول 500 · ابحث بالاسم أو اسم الأم أو الأرقام";
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
                        var previousMonth = comparisonMonth;
                        var previous = snapshot.Employees.Where(x => x.Month == previousMonth).GroupBy(x => PayrollMath.Key(x.Name)).ToDictionary(g => g.Key, g => g.ToList());
                        var changed = groups.Count(x => x.Value.Count == 1 && previous.TryGetValue(x.Key, out var old) && old.Count == 1 && x.Value[0].Net.HasValue && old[0].Net.HasValue && x.Value[0].Net != old[0].Net);
                        rows.Add(new($"تغير صافي الراتب: {changed:N0} · مقارنة بـ {previousMonth}", ""));
                    }
                    caption = "المقارنة بالاسم والجهة والإضبارة؛ تراجع حالات التكرار دون حذف. فحوص الرواتب لا تزال أولية وليست بديلاً عن تدقيق الآيفون.";
                }
                else
                {
                    var b = BankCalculator.Calculate(snapshot, month); var manual = ManualAmounts.ForMonth(snapshot, month);
                    void Add(string label, double amount) => rows.Add(new($"{label}   ·   {amount:N0}", new BankAmount(label, amount)));
                    Add("صافي الرواتب كاش", b.Cash); Add("صافي الرواتب بطاقة", b.Card); Add("صافي غير مصنف", b.Unclassified);
                    Add("الإجازات المتراكمة", manual.Leave); Add("مكافآت المنتسبين", manual.Rewards); Add("قائمة مفردة", manual.Single);
                    foreach (var item in b.Breakdown) Add(item.Key, item.Value);
                    Add("مجموع استقطاعات المصارف", b.BankDeductions); Add("التقاعد والضمان الاجتماعي", b.Contract);
                    Add("المجموع", b.Subtotal + manual.Leave + manual.Rewards + manual.Single);
                    caption = $"مطابقة المصارف والتنفيذ: {(b.Matched ? "متطابقة" : "تحتاج مراجعة")} · الفرق {b.BankDeductions - b.ExecutionDeductions:N0} · رواتب بلا صافي {b.MissingNet:N0} · عقود بلا اسمي {b.MissingNominal:N0}. الحساب يحتاج التحقق على جهاز ويندوز قبل الاعتماد.";
                }
                return (rows, caption);
            });
            if (section != page || (MonthPicker.SelectedItem as string ?? "") != month || (ComparePicker.SelectedItem as string ?? "") != comparisonMonth || PayrollMath.Normalize(DataFilter.Text) != q) return;
            DataResults.ItemsSource = result.rows; DataCaption.Text = result.caption;
        }
        catch (Exception ex) { if (section == page) { DataResults.ItemsSource = null; DataCaption.Text = ex.Message; } }
    }
    private async void Item_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not DisplayItem item || data == null) return;
        try
        {
            if (item.Value is Book book) { await SaveBook(book); return; }
            if (item.Value is AuditSelection audit)
            {
                var contents = new TextBox { Text = string.Join("\n", audit.Lines), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 500 };
                await new ContentDialog { Title = audit.Title, Content = contents, CloseButtonText = "إغلاق", XamlRoot = Content.XamlRoot }.ShowAsync();
                return;
            }
            if (item.Value is BankAmount amount)
            {
                var action = await new ContentDialog { Title = amount.Label, Content = amount.Value.ToString("N0"), PrimaryButtonText = "نسخ الرقم", CloseButtonText = "إغلاق", XamlRoot = Content.XamlRoot }.ShowAsync();
                if (action == ContentDialogResult.Primary)
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
                var deductions = data.Deductions.Where(x => x.Month == emp.Month && PayrollMath.Key(x.Name) == PayrollMath.Key(emp.Name)).ToList();
                var details = PayrollMath.Decode<string>(emp.Details); var items = PayrollMath.Decode<double>(emp.Items);
                text = $"الشهر: {emp.Month}\nالرتبة: {emp.Rank}\nمحل العمل: {emp.Workplace}\nصافي الراتب: {emp.Net:N0}\n\nبنود الراتب:\n" +
                    string.Join("\n", items.Select(x => $"{x.Key}: {x.Value:N0}")) + "\n\nالاستقطاعات:\n" + string.Join("\n", deductions.Select(x => $"{x.Title}: {x.Amount:N0}  {x.Note}")) +
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
                text = $"الشهر: {entity.Month}\nالمجموع: {entries.Sum(x => x.Amount ?? 0):N0}\nعدد الموظفين: {entries.Count:N0}\nالمكرر: {entries.Count - entries.Select(x => PayrollMath.Key(x.Name)).Distinct().Count():N0}\n" +
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
            var dialog = new ContentDialog { Title = title, Content = textBox, CloseButtonText = "إغلاق", XamlRoot = Content.XamlRoot };
            if (item.Value is EntitySelection or PayrollSelection) dialog.PrimaryButtonText = "تصدير Excel";
            var action = await dialog.ShowAsync();
            if (action == ContentDialogResult.Primary && item.Value is EntitySelection selected && data != null)
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
        }
        catch (Exception ex) { await Error(ex.Message); }
    }
    private async Task SaveBook(Book book)
    {
        var picker = new FileSavePicker { SuggestedFileName = "كتاب رسمي" }; picker.FileTypeChoices.Add("PDF", new List<string> { ".pdf" });
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync(); if (file == null || data == null) return;
        using (var zip = ZipFile.OpenRead(data.ArchivePath))
        {
            var entry = zip.GetEntry($"books/{book.Id.ToString().ToUpperInvariant()}.pdf") ?? throw new InvalidDataException("ملف الكتاب غير موجود داخل النسخة.");
            using var source = entry.Open(); using var destination = File.Create(file.Path); await source.CopyToAsync(destination);
        }
        await Windows.System.Launcher.LaunchFileAsync(file);
    }
}
