using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SalaryDesktop;
using SalaryDesktop.Core;

static class Program
{
    private static void Entry(ZipArchive zip, string name, string value)
    {
        var entry = zip.CreateEntry(name); using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)); writer.Write(value);
    }
    private static string Lines<T>(IEnumerable<T> rows) => string.Concat(rows.Select(r => JsonSerializer.Serialize(r) + "\n"));
    private static void Fixture(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Entry(zip, "manifest.json", JsonSerializer.Serialize(new { version = 5, months = new[] { "أيلول 2026" }, employees = 1, deductions = 1, staff = 1, accounts = 0, books = 0 }));
        Entry(zip, "months.jsonl", Lines(new[] { new { name = "أيلول 2026", employeeCount = 1, deductionCount = 1 } }));
        var details = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["طريقة الدفع"] = "كاش", ["الرقم الموحد"] = "123", ["مجموع استقطاعات المصارف"] = "100" });
        Entry(zip, "employees.jsonl", Lines(new[] { new { month = "أيلول 2026", name = "منتسب وهمي", rank = "موظف", workplace = "قسم وهمي", net = 500m, items = (byte[]?)null, details, rawColumns = (byte[]?)null } }));
        Entry(zip, "deductions.jsonl", Lines(new[] { new { month = "أيلول 2026", name = "منتسب وهمي", title = "جهة وهمية", amount = 100m, note = "", rank = "موظف" } }));
        var sections = JsonSerializer.SerializeToUtf8Bytes(new[] { new { title = "بيانات وهمية", fields = new[] { new { label = "حقل وهمي", value = "قيمة وهمية" } } } });
        Entry(zip, "staff.jsonl", Lines(new[] { new { name = "منتسب وهمي", mother = "", stat = "", unified = "123", rank = "موظف", status = "مستمر", workplace = "قسم وهمي", details = sections } }));
        Entry(zip, "accounts.jsonl", ""); Entry(zip, "books/index.json", "[]"); Entry(zip, "settings.json", "{}");
    }
    private static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match;
            foreach (var descendant in Children<T>(child)) yield return descendant;
        }
    }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }

    // بيانات وهمية أوسع لصور التصميم فقط: أسماء مركبة من قوائم عامة وحسابات اختبار.
    private static void RichFixture(string path)
    {
        string[] first = ["علي", "حسن", "كريم", "عمر", "سعد", "أحمد", "خالد", "مازن", "يوسف", "فراس"];
        string[] second = ["محمد", "عبد الله", "جاسم", "حميد", "صالح", "ناصر", "عادل", "رعد"];
        string[] third = ["حسين", "جبار", "كاظم", "سلمان", "عباس", "فاضل"];
        string[] mothers = ["فاطمة", "زينب", "مريم", "خديجة", "سعاد", "نجلاء", "هدى"];
        string[] ranks = ["ملازم", "نقيب", "رائد", "موظف", "شرطي", "عريف", "نائب ضابط", "عقود"];
        string[] places = ["قسم الحسابات", "مديرية المرور", "قسم الإدارة", "شرطة الرمادي", "قسم النجدة", "قسم التدريب"];
        string[] entities = ["مصرف الرافدين / فرع الرمادي", "مصرف الرشيد / فرع الأنبار", "صندوق الإسكان فرع الأنبار", "شركة عراق الجوال", "مصرف الرافدين/فرع الانبار", "المصرف الأهلي العراقي", "مصرف الشرق الأوسط"];
        string[] months = ["آب 2026", "أيلول 2026"];
        var employees = new List<object>(); var deductions = new List<object>(); var staff = new List<object>();
        for (int m = 0; m < months.Length; m++)
            for (int i = 0; i < 64; i++)
            {
                var name = $"{first[i % first.Length]} {second[(i / 3) % second.Length]} {third[(i / 7) % third.Length]}";
                var rank = ranks[i % ranks.Length]; var net = 650_000m + (i * 37_500m % 900_000m) + m * 5_000m;
                var items = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, decimal> { ["صندوق الشهداء"] = rank == "عقود" ? 2000 : 4000, ["الرعاية الصحية"] = 3500 * (1 + i % 4), ["الضريبة"] = 12_000 + i * 150 });
                var details = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["طريقة الدفع"] = i % 5 == 0 ? "كاش" : "بطاقة", ["الاسمي"] = (430_000 + i * 2_500).ToString(), ["الرقم الموحد"] = (90_000 + i).ToString() });
                employees.Add(new { month = months[m], name, rank, workplace = places[i % places.Length], net, items, details, rawColumns = (byte[]?)null });
                if (i % 3 != 2) deductions.Add(new { month = months[m], name, title = entities[i % entities.Length], amount = 25_000m + (i % 6) * 12_500m, note = $"اضبارة {1200 + i} في 2026/{1 + i % 9}/5", rank });
                if (m == 0)
                {
                    var sections = JsonSerializer.SerializeToUtf8Bytes(new[] { new { title = "الوظيفية", fields = new[] { new { label = "الرتبة", value = rank }, new { label = "محل العمل", value = places[i % places.Length] } } } });
                    staff.Add(new { name, mother = $"{mothers[i % mothers.Length]} {third[(i + 2) % third.Length]}", stat = (5000 + i).ToString(), unified = (90_000 + i).ToString(), rank, status = i % 9 == 0 ? "متقاعد" : "مستمر بالخدمة", workplace = places[i % places.Length], details = sections });
                }
            }
        var accounts = entities.Select((entity, i) => new { entity, iban = $"IQ00TEST{i:D3}000000000000", branch = entity.Contains("الرشيد") ? "مصرف الرشيد / فرع الأنبار" : "مصرف الرافدين / فرع الرمادي", swift = "TESTIQBAAAA", localAccount = (string?)null }).ToArray();
        var books = new[] { new { id = Guid.Parse("11111111-1111-1111-1111-111111111111"), title = "إشعار تحويل وهمي", month = "أيلول 2026", pages = 1, entity = "شركة عراق الجوال" }, new { id = Guid.Parse("22222222-2222-2222-2222-222222222222"), title = "كتاب وهمي للمراجعة", month = "آب 2026", pages = 2, entity = (string?)null } };
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Entry(zip, "manifest.json", JsonSerializer.Serialize(new { version = 5, months, employees = employees.Count, deductions = deductions.Count, staff = staff.Count, accounts = accounts.Length, books = books.Length }));
        Entry(zip, "months.jsonl", Lines(months.Select(month => new { name = month, employeeCount = 64, deductionCount = deductions.Count / 2 })));
        Entry(zip, "employees.jsonl", Lines(employees)); Entry(zip, "deductions.jsonl", Lines(deductions)); Entry(zip, "staff.jsonl", Lines(staff));
        Entry(zip, "accounts.jsonl", Lines(accounts)); Entry(zip, "books/index.json", JsonSerializer.Serialize(books));
        foreach (var book in books) Entry(zip, $"books/{book.id.ToString().ToUpperInvariant()}.pdf", "%PDF-1.4\n% fictional\n");
        Entry(zip, "settings.json", "{}");
    }

    // صورة لمحتوى النافذة كما تُعرض (يشمل اتجاه اليمين لليسار)، للمراجعة البصرية فقط.
    private static void Shot(Window window, string name, int mode = 0)
    {
        var folder = Environment.GetEnvironmentVariable("SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(folder)) return;
        Directory.CreateDirectory(folder);
        window.UpdateLayout();
        var root = (FrameworkElement)window.Content;
        int width = (int)Math.Ceiling(root.ActualWidth), height = (int)Math.Ceiling(root.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        if (mode == 1)
        {
            var flow = window.FlowDirection; window.FlowDirection = FlowDirection.LeftToRight; root.FlowDirection = FlowDirection.RightToLeft; window.UpdateLayout();
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) dc.DrawRectangle(window.Background ?? Brushes.White, null, new Rect(0, 0, width, height));
            bitmap.Render(visual); bitmap.Render(root);
            root.ClearValue(FrameworkElement.FlowDirectionProperty); window.FlowDirection = flow; window.UpdateLayout();
        }
        else if (mode == 2)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(window.Background ?? Brushes.White, null, new Rect(0, 0, width, height));
                dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, width, height));
            }
            bitmap.Render(visual);
        }
        else bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(folder, name + ".png")); encoder.Save(file);
    }
    [STAThread]
    private static int Main()
    {
        var directory = Path.Combine(Path.GetTempPath(), "salary-ui-smoke-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "fictional.zip"); Fixture(path); var snapshot = BackupReader.Read(path);
        var application = new SalaryDesktop.App(); application.InitializeComponent(); application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var exit = 1;
        application.Startup += async (_, _) => {
            MainWindow? main = null;
            try
            {
                await application.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                main = application.MainWindow as MainWindow ?? throw new InvalidOperationException("application did not create its main window");
                await Task.Delay(300);
                Check(((Button)main.FindName("StartFromExcelButton")).Visibility == Visibility.Visible, "standalone start was not visible on empty data");
                typeof(MainWindow).GetMethod("SetSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [snapshot]);
                await Task.Delay(500);
                Check(((Button)main.FindName("StartFromExcelButton")).Visibility != Visibility.Visible, "standalone start remained visible over existing data");
                async Task Navigate(string tag)
                {
                    var item = Children<RadioButton>(main).Single(b => Equals(b.Tag, tag)); item.IsChecked = true; await Task.Delay(800);
                    Check(item.IsChecked == true, $"navigation item {tag} was not selected");
                }
                var table = (DataGrid)main.FindName("DataTable");
                await Navigate("payroll"); Check(table.Items.Count == 1, "payroll page did not show the synthetic employee");
                await Navigate("deductions"); Check(table.Items.Count == 1, "deductions page did not show the synthetic entry");
                await Navigate("staff"); await Task.Delay(1200); Check(table.Items.Count == 1 && table.Items[0] is StaffMember, "staff page did not load its synthetic record");
                await Navigate("bank"); Check(((FrameworkElement)main.FindName("BankPanel")).Visibility == Visibility.Visible, "bank page was not visible");
                await Navigate("transfer"); Check(((FrameworkElement)main.FindName("TransferPanel")).Visibility == Visibility.Visible, "transfer page was not visible");
                var employeeDetails = new EmployeeDetailsWindow(snapshot.Employees[0], snapshot) { Owner = main }; employeeDetails.Show();
                Check(((DataGrid)employeeDetails.FindName("DetailTable")).Items.Count >= 5, "employee details did not display payroll and deduction data"); employeeDetails.Close();
                var duplicateSource = snapshot with { Employees = [snapshot.Employees[0], snapshot.Employees[0]] };
                var duplicateDetails = new EmployeeDetailsWindow(snapshot.Employees[0], duplicateSource) { Owner = main }; duplicateDetails.Show();
                Check(((TextBlock)duplicateDetails.FindName("StatusText")).Text.Contains("مكرر", StringComparison.Ordinal), "duplicate employee identity was not flagged"); duplicateDetails.Close();
                var itemGroups = SalaryItemReports.Groups(snapshot, "أيلول 2026"); var items = new SalaryItemsWindow(snapshot, "أيلول 2026", itemGroups) { Owner = main }; items.Show();
                Check(items.FindName("Groups") is DataGrid, "salary item report page did not open"); items.Close();
                var linkStore = new LocalTransferLinks(Path.Combine(directory, "links")); var linkState = linkStore.Load(snapshot);
                var settings = new IndividualSettingsWindow(snapshot, linkState, linkStore, "food_company", () => true, "أيلول 2026", null, "IQ76TEST123000000000002") { Owner = main };
                settings.Show(); Check(((TextBox)settings.FindName("AccountText")).Text.Length > 0, "individual letter settings did not open"); settings.Close();
                var reconciliation = new ReconciliationWindow(snapshot, "أيلول 2026", () => true) { Owner = main };
                reconciliation.Show(); Check(reconciliation.FindName("Periods") is ComboBox, "reconciliation page did not open"); reconciliation.Close();
                var staff = new StaffDetailsWindow(StaffRecords.Read(snapshot)[0]) { Owner = main }; staff.Show(); Check(((DataGrid)staff.FindName("Fields")).Items.Count == 1, "staff details were not displayed"); staff.Close();
                var names = NameLists.Prepare(snapshot, TransferPreparation.Prepare(snapshot, "أيلول 2026"));
                var lists = new NameListsWindow("أيلول 2026", names, () => true) { Owner = main }; lists.Show(); Check(((ComboBox)lists.FindName("ListPicker")).Items.Count == 1, "name list page did not open"); lists.Close();
                var initial = new PayrollImportWindow(null, "") { Owner = main }; initial.Show(); Check(initial.FindName("Choose") is Button, "standalone monthly import did not open"); initial.Close();
                var refImport = new ReferenceImportWindow() { Owner = main }; refImport.Show(); Check(refImport.FindName("Choose") is Button, "reference import did not open"); refImport.Close();
                var annual = new AnnualImportWindow(snapshot) { Owner = main }; annual.Show(); Check(annual.FindName("Choose") is Button, "annual import page did not open"); annual.Close();
                var bank = new BankDetailsWindow("أيلول 2026", "دافع وهمي", "IQ06TEST123000000000001", new BankManualAmounts(1, 2, 3)) { Owner = main }; bank.Show(); Check(((TextBox)bank.FindName("LeaveText")).Text == "1", "bank settings did not display manual amount"); bank.Close();
                var review = SalaryReview.Prepare(snapshot, "أيلول 2026", StaffRecords.Read(snapshot)); var audit = new SalaryReviewWindow(review) { Owner = main }; audit.Show(); Check(audit.FindName("KindPicker") is ComboBox, "salary review page did not open"); audit.Close();
                await Navigate("home");
                Check(SalaryDesktop.App.UnhandledCount == 0, "an unexpected UI error was reported");
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SCREENSHOT_DIR")))
                {
                    var richPath = Path.Combine(directory, "rich.zip"); RichFixture(richPath); var rich = BackupReader.Read(richPath);
                    main.Width = 1460; main.Height = 860; main.WindowState = WindowState.Normal;
                    typeof(MainWindow).GetMethod("SetSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [rich]);
                    await Task.Delay(1200);
                    Shot(main, "00-render-window", 0); Shot(main, "00-render-flipped", 1); Shot(main, "00-render-brush", 2);
                    foreach (var (tag, name) in new[] { ("home", "01-home"), ("payroll", "02-payroll"), ("deductions", "03-deductions"), ("staff", "04-staff"), ("archive", "05-archive"), ("groups", "06-groups"), ("bank", "07-bank"), ("transfer", "08-transfer"), ("accounts", "09-accounts"), ("settings", "10-settings") })
                    {
                        await Navigate(tag); if (tag is "staff" or "bank" or "transfer") await Task.Delay(1200); Shot(main, name);
                    }
                    await Navigate("payroll"); ((TextBox)main.FindName("SearchBox")).Text = "علي"; await Task.Delay(1200); Shot(main, "11-payroll-search");
                    var detail = new EmployeeDetailsWindow(rich.Employees[3], rich) { Owner = main }; detail.Show(); await Task.Delay(500); Shot(detail, "12-employee-details"); detail.Close();
                    var import = new PayrollImportWindow(rich, "أيلول 2026") { Owner = main }; import.Show(); await Task.Delay(500); Shot(import, "13-payroll-import"); import.Close();
                    var richReview = new SalaryReviewWindow(SalaryReview.Prepare(rich, "أيلول 2026", StaffRecords.Read(rich))) { Owner = main }; richReview.Show(); await Task.Delay(500); Shot(richReview, "14-salary-review"); richReview.Close();
                    var richItems = new SalaryItemsWindow(rich, "أيلول 2026", SalaryItemReports.Groups(rich, "أيلول 2026")) { Owner = main }; richItems.Show(); await Task.Delay(500); Shot(richItems, "15-salary-items"); richItems.Close();
                    Check(SalaryDesktop.App.UnhandledCount == 0, "an unexpected UI error was reported while capturing screens");
                    Console.WriteLine("Windows design screenshots saved.");
                }
                Console.WriteLine("Windows UI smoke passed using fictional backup data."); exit = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine("Windows UI smoke failed: " + ex); }
            finally { main?.Close(); application.Shutdown(); }
        };
        application.Run();
        try { Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return exit;
    }
}
