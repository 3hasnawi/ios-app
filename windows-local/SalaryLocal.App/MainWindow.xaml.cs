using System.Windows.Documents;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SalaryDesktop.Core;
using SalaryLocal.Core;
namespace SalaryLocal.App;
public partial class MainWindow : Window
{
    partial void AddInteropActions(StackPanel target);
    private readonly Workspace workspace = new();
    private BackupSnapshot? snapshot;
    private string section = "home";
    private bool busy, loadingMonths, ready;
    private CancellationTokenSource? cancellation;
    private void CancelClick(object sender, RoutedEventArgs e) { cancellation?.Cancel(); CancelOperation.IsEnabled = false; Status.Text = "جارٍ إلغاء العملية بأمان…"; }
    private int searchGeneration;
    private DisplayRow[] rows = [], visible = [];
    private string Month => (Months.SelectedItem as StoredMonth)?.Name ?? "";
    private sealed record DisplayRow(string Name, string Rank, string Place, string Amount, string Note, decimal? Value, object Source, string SearchKey);
    public MainWindow() { InitializeComponent(); InitializeSessionLock(); Width = Math.Min(Width, SystemParameters.WorkArea.Width); Height = Math.Min(Height, SystemParameters.WorkArea.Height); }
    private async void LoadedWindow(object sender, RoutedEventArgs e) { if (sessionLock?.IsLocked == true) return; await Run(async () => { await Task.Run(workspace.Open); ready = true; await Reload(); }); }
    private void ClosingWindow(object? sender, System.ComponentModel.CancelEventArgs e) { if (busy) { e.Cancel = true; Ui.Message(this, "انتظر انتهاء العملية الحالية لحماية البيانات."); } else workspace.Dispose(); }
    private async Task Run(Func<Task> action, bool cancellable = false)
    {
        if (busy) return; Ui.EnsureUnlocked(); busy = true; cancellation = new CancellationTokenSource(); CancelOperation.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed; CancelOperation.IsEnabled = true; Toolbar.IsEnabled = Navigation.IsEnabled = TablePanel.IsEnabled = PageScroll.IsEnabled = false; Progress.Visibility = Visibility.Visible; Status.Text = "جارٍ تنفيذ العملية محلياً…";
        try { await action(); Status.Text = "جاهز • البيانات محفوظة محلياً"; }
        catch (OperationCanceledException) { Status.Text = "أُلغيت العملية؛ لم تُستبدل مساحة العمل الحالية."; }
        catch (Exception ex) { Status.Text = "توقفت العملية"; Ui.Message(this, "تعذّر إكمال العملية.\n" + ex.Message); if (!ready) { Page.Children.Clear(); Page.Children.Add(Ui.Text("تعذّر فتح مساحة العمل بأمان", 25)); Page.Children.Add(Ui.Text("لم تُنشأ قاعدة بديلة. أغلق أي نسخة أخرى من التطبيق، وتحقق من مجلد البيانات ومؤشر المساحة، ثم أعد المحاولة.")); Page.Children.Add(Ui.Text(workspace.Root)); Page.Children.Add(Ui.Button("إعادة محاولة فتح البيانات", () => LoadedWindow(this, new RoutedEventArgs()))); Page.Children.Add(Ui.Button("اختيار مساحة محفوظة للاستعادة", RecoverWorkspace)); Page.Children.Add(Ui.Button("استرجاع نسخة محلية إلى مساحة جديدة", RestoreClick)); } }
        finally { cancellation?.Dispose(); cancellation = null; CancelOperation.Visibility = Visibility.Collapsed; busy = false; Toolbar.IsEnabled = Navigation.IsEnabled = TablePanel.IsEnabled = ready; PageScroll.IsEnabled = true; Progress.Visibility = Visibility.Collapsed; }
    }
    private async Task Reload()
    {
        var old = Month; var months = await Task.Run(() => workspace.Store.GetMonths().OrderByDescending(m => MonthNames.SortKey(m.Name)).ToArray());
        loadingMonths = true; Months.ItemsSource = months; Months.SelectedItem = months.FirstOrDefault(m => m.Name == old) ?? months.FirstOrDefault(); loadingMonths = false;
        await LoadMonth();
    }
    private async Task LoadMonth() { Search.Text = ""; ++searchGeneration; var m = Month; snapshot = m.Length == 0 ? null : await Task.Run(() => workspace.Store.GetSnapshot(m)); await Render(); }
    private async void MonthChanged(object sender, SelectionChangedEventArgs e) { if (!loadingMonths && !busy) await Run(LoadMonth); }
    private async void Navigate(object sender, RoutedEventArgs e) { section = (string)((Button)sender).Tag; Search.Text = ""; await Run(Render); }
    private async Task Render()
    {
        ++searchGeneration; Page.Children.Clear(); TablePanel.Visibility = Visibility.Collapsed; PageScroll.Visibility = Visibility.Visible;
        foreach (Button b in Navigation.Children) { b.Background = (Brush)FindResource((string)b.Tag == section ? "Accent" : "Ink"); b.Foreground = Brushes.White; b.BorderThickness = new Thickness(0); }
        Notice.Text = snapshot is null ? "لا توجد أشهر رواتب أو استقطاعات حالياً. استورد Excel أو راجع الحسابات ومساحة العمل." : $"{Month} • كل الأرقام من البيانات المستوردة • لا مزامنة تلقائية بين الحاسبتين";
        if (section == "settings") { await Settings(); return; }
        if (section == "accounts") { await Accounts(); return; }
        if (section == "staff") { await StaffPage(); return; }
        if (section == "archive") { await ArchivePage(); return; }
        if (section == "trust") { await TrustPage(); return; }
        if (section == "reconciliation") { await Reconciliation(); return; }
        if (section == "history") { var history = await Task.Run(() => workspace.Store.GetHistory().Select(h => new { Month = string.IsNullOrEmpty(h.Month) ? "بيانات وإعدادات عامة" : h.Month, h.SourceName, h.CreatedAt, h.Employees, h.Deductions }).ToArray()); Page.Children.Add(Ui.Text("سجل الاستيراد المحفوظ", 24)); Page.Children.Add(Ui.Text("لا يُحذف التاريخ تلقائياً. استيراد الملف المطابق لا يضيف نسخة مكررة.")); var grid = GridFor(("الشهر", "Month"), ("الملف", "SourceName"), ("الوقت", "CreatedAt"), ("رواتب", "Employees"), ("استقطاعات", "Deductions")); grid.ItemsSource = history; grid.Height = 440; Page.Children.Add(grid); return; }
        if (snapshot is null) { Page.Children.Add(Ui.Card(new StackPanel { Children = { Ui.Text("لم تُفتح بيانات شهرية بعد.", 29), Ui.Text("استورد ملف Excel للشهر، راجع الأسماء والأخطاء، ثم احفظ. الملاك والكتب والأمانات والحسابات، إن كانت مستوردة، متاحة من أقسامها حتى دون شهر رواتب."), Ui.Button("استيراد أول ملف Excel", () => ImportClick(this, new RoutedEventArgs())), Ui.Button("استرجاع نسخة من الحاسبة الأخرى", () => RestoreClick()) } })); return; }
        if (section is "employees" or "deductions") { await ShowRows(); return; }
        if (section == "bank") { await Bank(); return; }
        if (section == "reports") { await Reports(); return; }
        if (section == "audit") { await Audit(); return; }
        if (section == "transfers") { await Transfers(); return; }
        if (section == "comparison") { await Comparison(); return; }
        var source = snapshot; var month = Month; var s = await Task.Run(() => PayrollAnalysis.Summarize(source, month));
        Page.Children.Add(Ui.Text("نسخة 0.6 تشمل تصحيح حساب البنك منذ 0.2. أعد مراجعة مبلغ العقود والكشوف السابقة قبل الاعتماد؛ هذه نسخة اختبار تحتاج فحص Windows."));
        Page.Children.Add(Ui.Text("نظرة على الشهر", 25));
        var cards = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 };
        cards.Children.Add(Metric("صافي الرواتب", ReportSheet.Money(s.NetTotal) + " د.ع", "مجموع صافي ملف الرواتب", true)); cards.Children.Add(Metric("المنتسبون", s.Employees.ToString("N0"), "سجلات رواتب هذا الشهر")); cards.Children.Add(Metric("استقطاعات التنفيذ", ReportSheet.Money(s.DeductionTotal) + " د.ع", $"{s.Deductions:N0} قيد • {s.Entities:N0} جهة")); Page.Children.Add(cards);
        if (source.Employees.Any(e => e.Net < 0) || source.Deductions.Any(d => d.Amount < 0)) Page.Children.Add(Ui.Text("تنبيه: توجد مبالغ سالبة محفوظة من مصدر سابق. لم تُصحح أو تُحوّل إلى صفر؛ راجع المصدر قبل اعتماد المجاميع أو التصدير."));
        if (s.MissingNet + s.MissingAmounts > 0) Page.Children.Add(Ui.Text($"تنبيه: {s.MissingNet} رواتب بلا صافي و{s.MissingAmounts} استقطاعات بلا مبلغ؛ المجاميع جزئية."));
        Page.Children.Add(Ui.Card(new StackPanel { Children = { Ui.Text("عمل الشهر في مكان واحد", 21), Ui.Text("ملفات المصدر لا تُنسخ إلى كل نسخة احتياطية. تُحفظ الأعمدة المشتركة مرة واحدة، ويُحتفظ بسجل الإصدارات دون تكرار الصفوف غير المتغيرة."), Ui.Button("عرض المنتسبين", () => Go("employees")), Ui.Button("الكشوف والتقارير", () => Go("reports")), Ui.Button("حفظ نسخة للنقل إلى الحاسبة الثانية", BackupClick) } }));
        Page.Children.Add(Ui.Card(new StackPanel { Children = { Ui.Text("حدود هذه النسخة الأولية", 18), Ui.Text("تدعم الملفات الشهرية والسنوية والحسابات والتدقيق والتقارير والنسخ المحلية. تختلف جاهزية دفاتر المطابقة وتبادل الآيفون عن اعتمادها على جهاز فعلي؛ راجع التنبيهات لكل مسار. تشمل 0.5 إدارة الملاك واستيراد Access، ومسودات كتب التحويل الأصلية، وأرشفة الصور وواجهة ماسح WIA، إضافةً إلى الأمانات والأرشيف. تتطلب هذه المسارات فحص Windows والخطوط والماسح الفعلي؛ راجع حدود النسخة في الدليل."), Ui.Text("راجع النتائج والكشوف على ويندوز ببيانات اختبار قبل اعتمادها محاسبياً.") } }));
    }
    private Border Metric(string title, string value, string detail, bool teal = false) { var panel = new StackPanel(); panel.Children.Add(Ui.Text(title)); panel.Children.Add(Ui.Text(value, 27)); panel.Children.Add(Ui.Text(detail, 12)); var card = Ui.Card(panel); if (teal) { card.Background = (Brush)FindResource("Accent"); panel.SetValue(TextElement.ForegroundProperty, Brushes.White); } return card; }
    private async void Go(string name) { section = name; await Run(Render); }
    private static DataGrid GridFor(params (string Title, string Path)[] cols) { var grid = new DataGrid(); foreach (var c in cols) grid.Columns.Add(new DataGridTextColumn { Header = c.Title, Binding = new Binding(c.Path), Width = new DataGridLength(1, DataGridLengthUnitType.Star) }); return grid; }
    private async Task ShowRows()
    {
        var src = snapshot!; var salary = section == "employees";
        rows = await Task.Run(() => salary ? src.Employees.Select(e => new DisplayRow(e.Name, e.Rank, e.Workplace, e.Net is {} n ? ReportSheet.Money(n) : "غير محتسب", "", e.Net, e, PayrollAnalysis.Normalize($"{e.Name} {e.Rank} {e.Workplace}"))).ToArray() : src.Deductions.Select(d => new DisplayRow(d.Name, d.Rank, d.Title, d.Amount is {} n ? ReportSheet.Money(n) : "غير محتسب", d.Note, d.Amount, d, PayrollAnalysis.Normalize($"{d.Name} {d.Rank} {d.Title} {d.Note}"))).ToArray());
        Table.Columns.Clear(); foreach (var c in new[] { ("الاسم", "Name"), ("الرتبة", "Rank"), (salary ? "محل العمل" : "جهة التنفيذ", "Place"), (salary ? "الصافي / د.ع" : "المبلغ / د.ع", "Amount"), ("ملاحظات", "Note") }) Table.Columns.Add(new DataGridTextColumn { Header = c.Item1, Binding = new Binding(c.Item2), SortMemberPath = c.Item2 == "Amount" ? "Value" : c.Item2, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        PageScroll.Visibility = Visibility.Collapsed; TablePanel.Visibility = Visibility.Visible; visible = rows; Table.ItemsSource = visible; TableCount.Text = $"{visible.Length:N0} سجل • انقر مرتين لعرض التفاصيل • البحث بالاسم أو الرتبة أو الجهة";
    }
    private async void SearchChanged(object sender, TextChangedEventArgs e)
    {
        var id = ++searchGeneration; var query = PayrollAnalysis.Normalize(Search.Text); var source = rows; await Task.Delay(180); if (id != searchGeneration) return;
        var filtered = await Task.Run(() => source.Where(r => r.SearchKey.Contains(query, StringComparison.Ordinal)).ToArray()); if (id != searchGeneration) return; visible = filtered; Table.ItemsSource = filtered; TableCount.Text = $"{filtered.Length:N0} من {source.Length:N0} سجل";
    }
    private void TableKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) OpenDetails(sender, null!); }
    private void OpenDetails(object sender, MouseButtonEventArgs e)
    {
        if (Table.SelectedItem is DisplayRow row && !busy) ShowPerson(row.Name);
    }
    private async Task Audit()
    {
        var month = Month;
        var result = await Task.Run(() => { var candidates = workspace.Store.GetMonths().Where(m => MonthNames.SortKey(m.Name) < MonthNames.SortKey(month) && m.EmployeeCount > 0).OrderByDescending(m => MonthNames.SortKey(m.Name)).FirstOrDefault(); var source = candidates is null ? workspace.Store.GetSnapshot(month) : workspace.Store.GetSnapshot(month, candidates.Name); return SalaryReview.Prepare(source, month, null); });
        Page.Children.Add(Ui.Text("تدقيق الرواتب", 25));
        Page.Children.Add(Ui.Text(result.PreviousMonth is null ? "فحوص الشهر الحالي؛ لا شهر سابق للمقارنة." : "المقارنة مع " + result.PreviousMonth));
        foreach (var note in result.Notes) Page.Children.Add(Ui.Text("⚠ " + note));
        Page.Children.Add(Ui.Text($"{result.Rows.Count:N0} ملاحظة • التدقيق لا يغيّر السجلات ولا يثبت ملكية الحسابات."));
        var search = new TextBox { ToolTip = "البحث في الملاحظات", Margin = new Thickness(0, 8, 0, 14) }; Page.Children.Add(search);
        var grid = GridFor(("نوع الملاحظة", "Kind"), ("الاسم", "Name"), ("التفصيل", "Detail"), ("السابق", "Before"), ("الحالي", "After")); grid.Height = 420; grid.ItemsSource = result.Rows; Page.Children.Add(grid);
        int generation = 0; var pageGeneration = searchGeneration;
        search.TextChanged += async (_, _) => { var current = ++generation; var query = PayrollAnalysis.Normalize(search.Text); await Task.Delay(180); if (current != generation || pageGeneration != searchGeneration) return; var found = await Task.Run(() => result.Rows.Where(r => PayrollAnalysis.Normalize(r.Kind + " " + r.Name + " " + r.Detail).Contains(query, StringComparison.Ordinal)).ToArray()); if (current == generation && pageGeneration == searchGeneration) grid.ItemsSource = found; };
        grid.MouseDoubleClick += (_, _) => { if (grid.SelectedItem is SalaryReviewRow row) Ui.Message(this, row.Kind + "\n" + row.Name + "\n\n" + row.Detail); };
        Page.Children.Add(Ui.Button("تصدير ملاحظات التدقيق المعروضة • Excel / PDF", () => {
            var found = grid.Items.Cast<SalaryReviewRow>().ToArray();
            var sheet = new ReportSheet(snapshot is null ? ReportSheet.DefaultHeader : ReportSheet.HeaderFor(snapshot), "تدقيق الرواتب - " + month,
                new[] { new ReportColumn("الحالة", 22, ReportColumnKind.Text), new ReportColumn("الاسم", 30, ReportColumnKind.Text), new ReportColumn("التفصيل", 48, ReportColumnKind.Text), new ReportColumn("السابق", 15, ReportColumnKind.Text), new ReportColumn("الحالي", 15, ReportColumnKind.Text) },
                found.Select(r => new[] { r.Kind, r.Name, r.Detail, r.Before is {} before ? ReportSheet.Money(before) : "—", r.After is {} after ? ReportSheet.Money(after) : "—" }).ToArray(), [], ShowTotal: false);
            SaveExcel(() => XlsxReport.Generate(sheet), "تدقيق الرواتب - " + month, () => Pdf(sheet));
        }));
    }
    private async Task Bank()
    {
        var src = snapshot!; var m = Month; var b = await Task.Run(() => BankStatementCalculator.Calculate(src, m)); Page.Children.Add(Ui.Text("كشف حساب البنك", 25)); Page.Children.Add(Ui.Text("تنبيه الإصدار 0.2: حصة العقود 12% تُقرّب لأقرب عشرة والنصف للأعلى، ثم تُضاف التوقيفات، مثل الآيفون. لا تعتمد نتائج البنك من 0.1؛ أعد توليدها ومراجعتها.")); var cards = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 }; cards.Children.Add(Metric("الصافي كاش", ReportSheet.Money(b.Cash), "دينار عراقي")); cards.Children.Add(Metric("الصافي بطاقة", ReportSheet.Money(b.Card), "دينار عراقي")); cards.Children.Add(Metric("مبلغ البنك المحسوب", ReportSheet.Money(b.BankTotal), "وفق المحرك المحاسبي الحالي", true)); Page.Children.Add(cards);
        Page.Children.Add(Ui.Text($"إضافات يدوية: {ReportSheet.Money(b.Manual.Total)} د.ع • العقود المحسوب: {ReportSheet.Money(b.CalculatedContractContribution)} د.ع" + (b.ManualContractOverride is null ? " • دون تعديل يدوي للعقود" : b.ContractOverrideStale ? " • تنبيه: تعديل العقود القديم غير صالح بعد تغير الأساس" : $" • العقود المعدل: {ReportSheet.Money(b.ContractContribution)} د.ع")));
        Page.Children.Add(Ui.Button("إضافات البنك اليدوية", EditBankAmounts)); Page.Children.Add(Ui.Button("مراجعة وتعديل مبلغ العقود", EditContractAmount)); Page.Children.Add(Ui.Button("الحساب الدافع", EditPayer)); foreach (var warning in b.Warnings.Concat(b.TransferWarnings)) Page.Children.Add(Ui.Text("⚠ " + warning));
        var grid = GridFor(("البند", "Key"), ("المبلغ / د.ع", "Value")); grid.ItemsSource = b.Breakdown.Concat(new[] { new KeyValuePair<string, decimal>("صافي غير مصنف", b.Unclassified), new("استقطاعات المصارف", b.BankDeductions), new("استقطاعات ملف التنفيذ", b.ExecutionDeductions), new("فرق المطابقة", b.ExecutionDifference), new("العقود / تقاعد وضمان", b.ContractContribution) }); grid.Height = 450; Page.Children.Add(grid);
    }
    private async Task Reports()
    {
        var src = snapshot!; var m = Month; var groups = await Task.Run(() => PayrollAnalysis.Groups(src, m));
        Page.Children.Add(Ui.Text("الكشوف المعتمدة • Excel / PDF", 25)); Page.Children.Add(Ui.Text("يُعاد استخدام محرك الكشوف الحالي دون تغيير صيغ المبالغ. الكشوف الخاصة والقياسية متاحة بملف PDF أو Excel. يمكن معاينة PDF وطباعته من البرنامج؛ كتب التحويل في قسم ملفات التحويل."));
        foreach (var g in groups) { var group = g; Page.Children.Add(Ui.Button($"{g.Title} • {ReportSheet.Money(g.Total)} د.ع • Excel / PDF", () => SaveExcel(() => XlsxReport.Generate(EntitySheet(src, m, group.Title)), group.Title, () => Pdf(EntitySheet(src, m, group.Title))))); }
        var items = await Task.Run(() => src.Employees.SelectMany(e => (e.Items is null ? new Dictionary<string, decimal>() : JsonSerializer.Deserialize<Dictionary<string, decimal>>(e.Items) ?? throw new InvalidDataException("بنود الرواتب غير صالحة.")).Keys).Distinct().Order().ToArray());
        Page.Children.Add(Ui.Button("تصدير مجمّع لجميع جهات التنفيذ • Excel / PDF", () => BulkEntities(src, m)));
        Page.Children.Add(Ui.Text("بنود الرواتب والكشوف الخاصة", 21));
        foreach (var item in items) { var key = item; Page.Children.Add(Ui.Button(key + " • Excel / PDF", () => SaveExcel(() => FundReport.Applies(key) ? FundReport.Excel(FundReport.Summary(src, m, key), FundReport.Title(src, m)) : FoodContractReport.Applies(key) ? FoodContractReport.Excel(FoodContractReport.Rows(src, m, key), FoodContractReport.Title(src, m)) : HealthCareReport.Applies(key) ? HealthCareReport.Excel(HealthCareReport.Build(src, m, key), HealthCareReport.Title(src, m, false), HealthCareReport.Title(src, m, true)) : XlsxReport.Generate(StandardReports.ItemList(src, m, key)), key, () => SpecialPdf(src, m, key)))); }
    }
    private static byte[] Pdf(ReportSheet sheet) => ReportPdf.Generate(new[] { sheet }, ReportAssets.Load(Path.Combine(AppContext.BaseDirectory, "WatermarkLogo.png")), DateTime.Now);
    private async void SaveExcel(Func<byte[]> generate, string title, Func<byte[]>? pdf = null)
    {
        if (busy) return;
        var mode = "excel";
        if (pdf is not null)
        {
            var panel = new StackPanel { Margin = new Thickness(24) }; panel.Children.Add(Ui.Text(title, 23));
            panel.Children.Add(Ui.Text("المعاينة والطباعة تستخدمان PDF نفسه. اختر الطابعة بعد مراجعة الصفحة؛ لا يُرسل شيء تلقائياً."));
            var dialog = Ui.Dialog(this, "التصدير والطباعة", panel, 650, 380); string? chosen = null;
            panel.Children.Add(Ui.Button("معاينة PDF والطباعة", () => { chosen = "preview"; dialog.Close(); }));
            panel.Children.Add(Ui.Button("حفظ Excel", () => { chosen = "excel"; dialog.Close(); }));
            panel.Children.Add(Ui.Button("حفظ PDF مباشرة", () => { chosen = "pdf"; dialog.Close(); }));
            panel.Children.Add(Ui.Button("إلغاء", dialog.Close)); dialog.ShowDialog(); if (chosen is null) return; mode = chosen;
        }
        if (mode == "preview")
        {
            await Run(async () => { var bytes = await Task.Run(pdf!); new LocalPdfPreviewWindow(this, title, bytes).ShowDialog(); }); return;
        }
        bool savePdf = mode == "pdf";
        var extension = savePdf ? ".pdf" : ".xlsx";
        var picker = new SaveFileDialog { Filter = savePdf ? "PDF (*.pdf)|*.pdf" : "Excel (*.xlsx)|*.xlsx", FileName = ReportSheet.FileName(title), DefaultExt = extension, AddExtension = true, OverwritePrompt = true };
        if (Ui.ShowNativeDialog(() => picker.ShowDialog(this)) != true) return;
        await Run(async () => { await Task.Run(() => ReportFiles.Save(savePdf ? pdf!() : generate(), picker.FileName, extension)); Ui.Message(this, "تم حفظ الكشف:\n" + picker.FileName); });
    }
    private void ExportTableClick(object sender, RoutedEventArgs e)
    {
        var selected = Table.Items.Cast<DisplayRow>().ToArray(); var header = snapshot is null ? ReportSheet.DefaultHeader : ReportSheet.HeaderFor(snapshot); var title = (section == "employees" ? "رواتب " : "استقطاعات ") + Month; SaveExcel(() => XlsxReport.Generate(TableReport.Build(header, title, new[] { "الاسم", "الرتبة", "الجهة", "المبلغ" }, 3, selected.Select(r => new[] { r.Name, r.Rank, r.Place, r.Amount }).ToArray(), selected.Select(r => r.Value).ToArray())), title, () => Pdf(TableReport.Build(header, title, new[] { "الاسم", "الرتبة", "الجهة", "المبلغ" }, 3, selected.Select(r => new[] { r.Name, r.Rank, r.Place, r.Amount }).ToArray(), selected.Select(r => r.Value).ToArray())));
    }
    private async void ImportClick(object sender, RoutedEventArgs e)
    {
        if (busy) return; var picker = new OpenFileDialog { Filter = "Excel (*.xlsx)|*.xlsx", CheckFileExists = true }; if (Ui.ShowNativeDialog(() => picker.ShowDialog(this)) != true) return;
        await Run(async () => {
            var detected = await Task.Run(() => DetectExcel(picker.FileName));
            if (detected.Kind == ImportKind.Annual) { await ImportAnnual(picker.FileName); return; }
            if (detected.Kind == ImportKind.Accounts) { await ImportAccounts(picker.FileName); return; }
            if (detected.Kind != ImportKind.Payroll) throw new InvalidDataException("تعذّر تحديد نوع الملف. " + detected.Note);
            var month = PickMonth(); if (month is null) return;
            var canonical = await Task.Run(() => workspace.Store.GetMonths().Where(m => MonthNames.SortKey(m.Name) == MonthNames.SortKey(month)).ToArray());
            if (canonical.Length > 1) throw new InvalidDataException("يوجد أكثر من اسم للشهر نفسه؛ راجع مساحة العمل قبل الاستيراد.");
            month = canonical.FirstOrDefault()?.Name ?? month;
            var revision = await Task.Run(() => workspace.Store.GetStatus().RevisionId);
            var parsed = await Task.Run(() => ReadVerified(picker.FileName, () => PayrollSpreadsheet.Read(picker.FileName, month)));
            var preview = parsed.Preview; var existing = await Task.Run(() => workspace.Store.GetMonths().FirstOrDefault(m => m.Name == month));
            var needsReplace = existing is not null && ((preview.Employees.Count > 0 && existing.EmployeeCount > 0) || (preview.Deductions.Count > 0 && existing.DeductionCount > 0));
            if (!Review(preview, needsReplace)) return;
            var result = await Task.Run(() => workspace.Store.Import(preview, Path.GetFileName(picker.FileName), parsed.Digest, revision, needsReplace));
            var trustHint = preview.Employees.Count > 0 && MonthNames.SortKey(month) > SalaryTrust.OpeningCutoff
                ? "\nراجع كاش هذا الشهر من صفحة أمانات الرواتب بعد حفظ الافتتاحي؛ استيراد الرواتب وحده لا يضيف قيود الأمانات." : "";
            await Reload(); Ui.Message(this, (result.Changed ? $"حُفظ الاستيراد: {result.Employees:N0} رواتب، {result.Deductions:N0} استقطاعات.\nأنشئ نسخة احتياطية قبل النقل إلى الحاسبة الأخرى." : "الملف مطابق للبيانات المحفوظة؛ لم تُضف نسخة مكررة.") + trustHint);
        });
    }
    private string? PickMonth()
    {
        var names = new[] { "كانون الثاني", "شباط", "آذار", "نيسان", "أيار", "حزيران", "تموز", "آب", "أيلول", "تشرين الأول", "تشرين الثاني", "كانون الأول" }; var panel = new StackPanel { Margin = new Thickness(24) }; panel.Children.Add(Ui.Text("حدد شهر الملف قبل القراءة", 23)); var combo = new ComboBox { ItemsSource = names, SelectedIndex = DateTime.Today.Month - 1 }; var year = new TextBox { Text = DateTime.Today.Year.ToString(), Margin = new Thickness(0, 15, 0, 15) }; panel.Children.Add(combo); panel.Children.Add(year); panel.Children.Add(Ui.Text("الشهر يخص جميع الصفوف. الملفات السنوية تُكتشف تلقائياً وتُراجع أشهرها في نافذة منفصلة.")); var dialog = Ui.Dialog(this, "شهر الاستيراد", panel, 520, 350); string? result = null;
        panel.Children.Add(Ui.Button("قراءة ومعاينة الملف", () => { if (!int.TryParse(year.Text, out var y) || y is < 1900 or > 2200) { Ui.Message(dialog, "أدخل سنة صحيحة."); return; } result = $"{combo.SelectedItem} {y}"; dialog.Close(); })); dialog.ShowDialog(); return result;
    }
    private bool Review(PayrollImportPreview preview, bool replace)
    {
        var dock = new DockPanel { Margin = new Thickness(22) }; var header = new StackPanel(); header.Children.Add(Ui.Text("مراجعة قبل الحفظ • " + preview.Month, 24)); header.Children.Add(Ui.Text($"{preview.Employees.Count:N0} رواتب • {preview.Deductions.Count:N0} استقطاعات • {preview.Problems.Count:N0} أخطاء")); header.Children.Add(Ui.Text("المعاينة تعرض أول 200 سجل؛ الحفظ يشمل جميع الصفوف السليمة. وجود أي خطأ يمنع الحفظ.")); DockPanel.SetDock(header, Dock.Top); dock.Children.Add(header);
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); dock.Children.Add(footer); var confirm = new CheckBox { Content = "أوافق على استبدال القسم الموجود لهذا الشهر مع حفظ إصداره السابق.", Margin = new Thickness(5, 15, 5, 15), Visibility = replace ? Visibility.Visible : Visibility.Collapsed }; footer.Children.Add(confirm); var dialog = Ui.Dialog(this, "مراجعة Excel", dock, 1000, 700); bool accepted = false;
        var save = Ui.Button("تأكيد وحفظ الاستيراد", () => { accepted = true; dialog.Close(); }); save.IsEnabled = preview.Problems.Count == 0 && preview.Employees.Count + preview.Deductions.Count > 0 && !replace; confirm.Checked += (_, _) => save.IsEnabled = preview.Problems.Count == 0; confirm.Unchecked += (_, _) => save.IsEnabled = false; footer.Children.Add(save); footer.Children.Add(Ui.Button("إلغاء دون تغيير البيانات", dialog.Close));
        var tabs = new TabControl(); var payroll = GridFor(("الاسم", "Name"), ("الرتبة", "Rank"), ("محل العمل", "Workplace"), ("الصافي", "Net")); payroll.ItemsSource = preview.Employees.Take(200); tabs.Items.Add(new TabItem { Header = "الرواتب", Content = payroll }); var deductions = GridFor(("الاسم", "Name"), ("الجهة", "Title"), ("المبلغ", "Amount"), ("ملاحظات", "Note")); deductions.ItemsSource = preview.Deductions.Take(200); tabs.Items.Add(new TabItem { Header = "الاستقطاعات", Content = deductions }); var errors = GridFor(("الورقة", "Sheet"), ("الصف", "Row"), ("الخطأ", "Message")); errors.ItemsSource = preview.Problems; tabs.Items.Add(new TabItem { Header = "الأخطاء", Content = errors }); tabs.SelectedIndex = preview.Problems.Count > 0 ? 2 : preview.Employees.Count > 0 ? 0 : 1; dock.Children.Add(tabs); dialog.ShowDialog(); return accepted;
    }
    private async Task Settings()
    {
        var status = await Task.Run(workspace.Store.GetStatus); Page.Children.Add(Ui.Text("النسخ ومساحة العمل", 25)); AddInteropActions(Page); AddMigrationActions(Page); AddSessionLockSettings(Page); Page.Children.Add(Ui.Card(new StackPanel { Children = { Ui.Text("قاعدة محلية مستقلة", 20), Ui.Text(workspace.DirectoryPath), Ui.Text($"حجم القاعدة: {status.DatabaseBytes / 1048576d:N2} MB • إصدارات محفوظة: {status.RevisionCount:N0}"), Ui.Text("لا يُقرأ مجلد التطبيق السابق. لا تُحذف نسخة أو مساحة عمل تلقائياً."), Ui.Button("إنشاء نسخة محلية مضغوطة", BackupClick), Ui.Button("فحص واسترجاع نسخة", RestoreClick) } }));
        Page.Children.Add(Ui.Text("صيغة .salarybackup مخصصة لهذه النسخة المحلية على الحاسبتين، وليست صيغة آيفون. لتبادل بيانات الآيفون استخدم مسار ZIP المخصص أعلاه بعد مراجعة نطاقه. لا تحتوي ملفات Excel الأصلية، بل البيانات والتاريخ داخل قاعدة واحدة مضغوطة. احتفظ بالملفات الأصلية منفصلة."));
        Page.Children.Add(Ui.Text("تنبيه أمان: القاعدة والنسخة غير مشفرتين. احفظهما وانقلهما عبر حساب أو وسيط محمي. بصمة الملف تكشف التلف والتغيّر، ولا تثبت هوية المرسل."));
        Page.Children.Add(Ui.Text("مساحات العمل المحفوظة", 20)); var paths = await Task.Run(() => Directory.GetDirectories(workspace.Workspaces).Where(p => Guid.TryParseExact(Path.GetFileName(p), "N", out _) && File.Exists(Path.Combine(p, "payroll.db"))).ToArray()); foreach (var path in paths) { var p = path; Page.Children.Add(Ui.Button((p == workspace.DirectoryPath ? "الحالية • " : "فتح • ") + Path.GetFileName(p), () => SwitchWorkspace(p))); }
    }
    private async void RecoverWorkspace()
    {
        if (busy) return;
        var picker = new OpenFileDialog { Filter = "قاعدة مساحة العمل (payroll.db)|payroll.db", InitialDirectory = workspace.Workspaces };
        if (Ui.ShowNativeDialog(() => picker.ShowDialog(this)) != true) return;
        var path = Path.GetDirectoryName(picker.FileName)!;
        if (Path.GetDirectoryName(Path.GetFullPath(path)) != Path.GetFullPath(workspace.Workspaces) || !Guid.TryParseExact(Path.GetFileName(path), "N", out _)) { Ui.Message(this, "اختر قاعدة من مجلد مساحات هذا التطبيق فقط؛ لم تُقرأ بيانات التطبيق القديم."); return; }
        if (Ui.Message(this, "فتح قاعدة البيانات المحددة كمساحة عمل نشطة؟ لن يُحذف أي مجلد محفوظ.", true) != MessageBoxResult.Yes) return;
        await Run(async () => { await Task.Run(() => { workspace.EnsureSessionLock(); LocalPayrollStore.ValidateDatabase(picker.FileName); workspace.Switch(path); }); ready = true; await Reload(); });
    }
    private async void SwitchWorkspace(string path) { if (busy || path == workspace.DirectoryPath) return; if (Ui.Message(this, "فتح مساحة العمل المحفوظة؟ ستبقى المساحة الحالية وبياناتها دون حذف.", true) != MessageBoxResult.Yes) return; await Run(async () => { await Task.Run(() => { LocalPayrollStore.ValidateDatabase(Path.Combine(path, "payroll.db")); workspace.Switch(path); }); await Reload(); }); }
    private async void BackupClick()
    {
        if (busy) return; var picker = new SaveFileDialog { Filter = "نسخة محلية (*.salarybackup)|*.salarybackup", FileName = $"رواتب-{DateTime.Now:yyyy-MM-dd-HHmmss}.salarybackup", OverwritePrompt = true }; if (Ui.ShowNativeDialog(() => picker.ShowDialog(this)) != true) return;
        if (File.Exists(picker.FileName)) { Ui.Message(this, "اختر اسماً جديداً؛ لن تُستبدل النسخ الاحتياطية الموجودة."); return; }
        await Run(async () => { await Task.Run(() => BackupService.Create(workspace.Store, picker.FileName, cancellation!.Token)); Ui.Message(this, "حُفظت النسخة وفُحصت سلامتها. انقل هذا الملف للحاسبة الأخرى.\nالنسخة غير مشفرة؛ استخدم وسيطاً محمياً. البصمة تكشف التلف ولا تثبت هوية المرسل.\n" + picker.FileName); }, true);
    }
    private async void RestoreClick()
    {
        if (busy) return; var picker = new OpenFileDialog { Filter = "نسخة محلية (*.salarybackup)|*.salarybackup" }; if (Ui.ShowNativeDialog(() => picker.ShowDialog(this)) != true) return;
        await Run(async () => { await Task.Run(workspace.EnsureSessionLock); var review = await Task.Run(() => BackupService.Inspect(picker.FileName, ready ? workspace.Store : null, cancellation!.Token)); if (Ui.Message(this, $"{review.RelationshipMessage}\n\nتاريخ النسخة: {review.Manifest.CreatedAt.LocalDateTime:g}\nحجم القاعدة: {review.Manifest.DatabaseBytes / 1048576d:N2} MB\n\nلا دمج تلقائياً. ستُفتح نسخة مستقلة وتبقى المساحة الحالية محفوظة. ليست نسخة آيفون.\nالنسخة غير مشفرة. تحقق من مصدرها؛ البصمة لا تثبت هوية المرسل.\nهل تريد الاسترجاع وفتح النسخة؟", true) != MessageBoxResult.Yes) return; var restored = await Task.Run(() => BackupService.RestoreAsNewWorkspace(picker.FileName, workspace.Workspaces, review.Digest, cancellation!.Token)); cancellation!.Token.ThrowIfCancellationRequested(); CancelOperation.Visibility = Visibility.Collapsed; await Task.Run(() => workspace.Switch(restored)); ready = true; await Reload(); Ui.Message(this, "استُرجعت النسخة إلى مساحة جديدة. المساحة السابقة متاحة من الإعدادات."); }, true);
    }
}
