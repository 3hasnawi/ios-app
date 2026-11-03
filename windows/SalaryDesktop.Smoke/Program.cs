using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private static void Entry(ZipArchive zip, string name, byte[] value)
    {
        var entry = zip.CreateEntry(name); using var stream = entry.Open(); stream.Write(value);
    }
    /// صفحة كتاب وهمية مرسومة (بلا نص حقيقي) لمعاينات الأرشيف.
    internal static byte[] FictionalPage(int seed)
    {
        using var bitmap = new SkiaSharp.SKBitmap(1240, 1754); using var canvas = new SkiaSharp.SKCanvas(bitmap); canvas.Clear(SkiaSharp.SKColors.White);
        using var ink = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(60, 70, 80), IsAntialias = true };
        using var head = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(28, 110, 91), IsAntialias = true };
        using var stamp = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(40, 80, 170, 150), IsAntialias = true, Style = SkiaSharp.SKPaintStyle.Stroke, StrokeWidth = 8 };
        canvas.DrawRect(120, 110, 1000, 26, head); canvas.DrawRect(820, 170, 300, 18, ink); canvas.DrawRect(120, 170, 260, 18, ink);
        for (int i = 0; i < 16 + seed % 5; i++) canvas.DrawRect(120 + (i % 4 == 0 ? 0 : 60), 320 + i * 62, i % 4 == 3 ? 560 : 940, 14, ink);
        canvas.DrawCircle(330, 1480, 110, stamp); canvas.DrawRect(820, 1460, 260, 14, ink);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap); using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 88); return data.ToArray();
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
                var rank = ranks[i % ranks.Length];
                // مبالغ وهمية متسقة: الاستحقاق = الاسمي + المخصصات، والصافي = الاستحقاق − الاستقطاعات.
                decimal nominal = 430_000 + i * 2_500, risk = 100_000, married = i % 3 == 0 ? 50_000 : 0, extra = m * 5_000, gross = nominal + risk + married + extra;
                decimal pension = nominal / 10, fund = rank == "عقود" ? 2000 : 4000, health = 3500 * (1 + i % 4), tax = 12_000 + i * 150, totalDeductions = pension + fund + health + tax, net = gross - totalDeductions;
                var items = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, decimal> { ["صندوق الشهداء"] = fund, ["الرعاية الصحية"] = health, ["الضريبة"] = tax });
                var detailMap = new Dictionary<string, string> { ["طريقة الدفع"] = i % 5 == 0 ? "كاش" : "بطاقة", ["الاسمي"] = nominal.ToString("0"), ["الخطورة"] = risk.ToString("0"), ["الرقم الموحد"] = (90_000 + i).ToString(),
                    ["الاستحقاق"] = gross.ToString("0"), ["توقيفات تقاعدية"] = pension.ToString("0"), ["صندوق الشهداء"] = fund.ToString("0"), ["الرعاية الصحية"] = health.ToString("0"), ["الضريبة"] = tax.ToString("0"),
                    ["مجموع الاستقطاعات"] = totalDeductions.ToString("0"), ["الدرجة الوظيفية"] = $"{1 + i % 9}/{1 + i % 5}" };
                if (married > 0) detailMap["الزوجية"] = married.ToString("0");
                if (extra > 0) detailMap["اضافية"] = extra.ToString("0");
                if (i % 5 != 0) { detailMap["IBAN"] = $"IQ00TEST{i:D3}0000000000000"; detailMap["توطين المصرف"] = i % 2 == 0 ? "الرافدين فرع الرمادي" : "الرشيد فرع الأنبار"; }
                var details = JsonSerializer.SerializeToUtf8Bytes(detailMap);
                employees.Add(new { month = months[m], name, rank, workplace = places[i % places.Length], net, items, details, rawColumns = (byte[]?)null });
                if (i % 3 != 2) deductions.Add(new { month = months[m], name, title = entities[i % entities.Length], amount = 25_000m + (i % 6) * 12_500m, note = $"اضبارة {1200 + i} في 2026/{1 + i % 9}/5", rank });
                if (m == 0)
                {
                    var sections = JsonSerializer.SerializeToUtf8Bytes(new[] { new { title = "الوظيفية", fields = new[] { new { label = "الرتبة", value = rank }, new { label = "محل العمل", value = places[i % places.Length] } } } });
                    staff.Add(new { name, mother = $"{mothers[i % mothers.Length]} {third[(i + 2) % third.Length]}", stat = (5000 + i).ToString(), unified = (90_000 + i).ToString(), rank, status = i % 9 == 0 ? "متقاعد" : "مستمر بالخدمة", workplace = places[i % places.Length], details = sections });
                }
            }
        var accounts = entities.Select((entity, i) => new { entity, iban = $"IQ00TEST{i:D3}000000000000", branch = entity.Contains("الرشيد") ? "مصرف الرشيد / فرع الأنبار" : "مصرف الرافدين / فرع الرمادي", swift = "TESTIQBAAAA", localAccount = (string?)null }).ToArray();
        var books = new[] { new { id = Guid.Parse("11111111-1111-1111-1111-111111111111"), title = "إشعار تحويل وهمي", month = "أيلول 2026", pages = 1, entity = (string?)"شركة عراق الجوال" }, new { id = Guid.Parse("22222222-2222-2222-2222-222222222222"), title = "كتاب وهمي للمراجعة", month = "آب 2026", pages = 2, entity = (string?)null } };
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Entry(zip, "manifest.json", JsonSerializer.Serialize(new { version = 5, months, employees = employees.Count, deductions = deductions.Count, staff = staff.Count, accounts = accounts.Length, books = books.Length }));
        Entry(zip, "months.jsonl", Lines(months.Select(month => new { name = month, employeeCount = 64, deductionCount = deductions.Count / 2 })));
        Entry(zip, "employees.jsonl", Lines(employees)); Entry(zip, "deductions.jsonl", Lines(deductions)); Entry(zip, "staff.jsonl", Lines(staff));
        Entry(zip, "accounts.jsonl", Lines(accounts)); Entry(zip, "books/index.json", JsonSerializer.Serialize(books));
        foreach (var book in books) Entry(zip, $"books/{book.id.ToString().ToUpperInvariant()}.pdf", ArchivePdf.FromImages(Enumerable.Range(0, book.pages).Select(i => new ArchiveImage(FictionalPage(i + book.pages))).ToArray()));
        Entry(zip, "settings.json", JsonSerializer.Serialize(new Dictionary<string, string> { ["bankReconciliations"] = Convert.ToBase64String(BankReconciliation.LedgerPlist(FictionalPeriods())) }));
    }
    /// فترتا مطابقة وهميتان: آب معتمدة، وأيلول قيد العمل (حركتان غير مؤشرتين).
    internal static BankPeriod[] FictionalPeriods()
    {
        BankMovement M(int month, int day, string text, long amount, bool credit, bool matched, bool funding = false) =>
            new(Guid.NewGuid(), new DateOnly(2026, month, day), text, amount, credit ? MovementDirection.Credit : MovementDirection.Debit, IsFunding: funding, Matched: matched);
        var committee = new BankCommittee(DefaultsInitialized: true);
        var august = new BankPeriod(Guid.NewGuid(), "آب 2026", new DateOnly(2026, 8, 3), new DateOnly(2026, 9, 2), 0, "عضو وهمي أول", "عضو وهمي ثان", null, null,
            [M(8, 3, "تمويل رواتب آب", 1_812_400_000, true, true, true), M(8, 5, "تحويل مجموعة الرافدين", 1_050_000_000, false, true), M(8, 6, "صرف الرواتب كاش", 212_000_000, false, true),
             M(8, 9, "تحويل مصرف وهمي أ", 255_000_000, false, true), M(8, 20, "الضريبة", 295_400_000, false, true)], committee);
        var september = new BankPeriod(Guid.NewGuid(), "أيلول 2026", new DateOnly(2026, 9, 3), new DateOnly(2026, 10, 2), 0, "عضو وهمي أول", "عضو وهمي ثان", null, null,
            [M(9, 3, "تمويل رواتب أيلول", 1_846_250_500, true, true, true), M(9, 5, "تحويل مجموعة الرافدين", 1_071_450_500, false, true), M(9, 6, "صرف الرواتب كاش", 214_600_000, false, true),
             M(9, 8, "تحويل مصرف وهمي أ", 260_000_000, false, true), M(9, 9, "شركة اتصالات وهمية", 25_000_000, false, false), M(9, 12, "الرعاية الصحية", 18_300_000, false, true),
             M(9, 20, "الضريبة", 41_210_000, false, false)], committee);
        return [BankReconciliation.Confirm(august, new DateTime(2026, 9, 4, 8, 0, 0, DateTimeKind.Utc)), september];
    }

    // جهاز الفحص قد تكون شاشته صغيرة؛ النافذة الأكبر منها تُقص، فتُصغّر قبل التصوير.
    private static T Fit<T>(T window, double width = 0, double height = 0) where T : Window
    {
        var area = SystemParameters.WorkArea;
        if (width > 0) window.Width = width; if (height > 0) window.Height = height;
        if (!double.IsNaN(window.Width)) window.Width = Math.Min(window.Width, area.Width - 8);
        if (!double.IsNaN(window.Height)) window.Height = Math.Min(window.Height, area.Height - 8);
        window.MinWidth = Math.Min(window.MinWidth, area.Width - 8); window.MinHeight = Math.Min(window.MinHeight, area.Height - 8);
        window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = area.Left; window.Top = area.Top;
        return window;
    }

    // صورة لمحتوى النافذة كما تُعرض (يشمل اتجاه اليمين لليسار)، للمراجعة البصرية فقط.
    private static void Shot(Window window, string name)
    {
        var folder = Environment.GetEnvironmentVariable("SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(folder)) return;
        Directory.CreateDirectory(folder);
        window.UpdateLayout();
        // أول عنصر في قالب النافذة بحجم منطقة العرض؛ والنافذة نفسها تحمل تحويل اتجاه اليمين لليسار، فتُرسم هي لا محتواها.
        var client = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
        int width = (int)Math.Ceiling(client.ActualWidth), height = (int)Math.Ceiling(client.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(folder, name + ".png")); encoder.Save(file);
    }
    private static void ShotElement(FrameworkElement element, string name)
    {
        var folder = Environment.GetEnvironmentVariable("SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(folder)) return;
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
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
                var families = Fonts.GetFontFamilies(new Uri("pack://application:,,,/SalaryDesktop;component/Fonts/")).ToArray();
                foreach (var option in AppFonts.Options.Where(o => !o.Installed))
                    Check(families.Any(f => f.Source.Contains("#" + option.Family, StringComparison.Ordinal)), "embedded font family missing: " + option.Family);
                foreach (var familyName in new[] { "Alexandria", "Readex Pro", "Rubik" })
                {
                    var family = families.First(f => f.Source.EndsWith("#" + familyName, StringComparison.Ordinal));
                    var sizes = family.GetTypefaces().Select(t => t.Weight.ToOpenTypeWeight()).ToHashSet();
                    Check(sizes.SetEquals([400, 500, 600]), familyName + " font weights are missing");
                }
                Check(AppFonts.Find("times").Family == "Times New Roman" && AppFonts.Find("times").Installed, "Windows Times New Roman option missing");
                foreach (var key in new[] { "alexandria", "readex", "rubik", "times" })
                {
                    AppFonts.Apply(key, "latin");
                    Check(AppFonts.CurrentFont == key && Application.Current.Resources["AppFont"] is FontFamily selected && selected.Source.Contains(AppFonts.Find(key).Family, StringComparison.Ordinal), "font selection did not apply: " + key);
                }
                AppFonts.Apply(AppFonts.DefaultFont, "latin");
                AppFonts.ApplyScale(120);
                Check(AppFonts.InterfaceScale == 120 && ((FrameworkElement)main.Content).LayoutTransform.Value.M11 == 1.2, "interface scale did not apply to the main window");
                AppFonts.ApplyScale(999);
                Check(AppFonts.InterfaceScale == 100 && ((FrameworkElement)main.Content).LayoutTransform.Value.IsIdentity, "invalid scale was not reset to the default");
                var kufi = families.FirstOrDefault(f => f.Source.EndsWith("#Noto Kufi Arabic", StringComparison.Ordinal)) ?? families.First(f => f.Source.Contains("#Noto Kufi Arabic", StringComparison.Ordinal));
                var weights = kufi.GetTypefaces().Select(t => t.Weight.ToOpenTypeWeight()).ToHashSet();
                Check(weights.SetEquals([400, 500, 600]), "Noto Kufi Arabic (Regular, Medium, SemiBold) weights: " + string.Join(",", weights));
                var escapeDialog = new Window { Owner = main, Width = 200, Height = 120, Content = new TextBox() };
                bool keepOpen = true;
                escapeDialog.Closing += (_, e) => { if (keepOpen) e.Cancel = true; };
                escapeDialog.Show(); await Task.Delay(150);
                void PressEscape(Window window)
                {
                    var source = PresentationSource.FromVisual(window) ?? throw new InvalidOperationException("dialog has no presentation source");
                    window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
                }
                PressEscape(escapeDialog);
                Check(escapeDialog.IsVisible, "Escape bypassed the window's closing guard");
                keepOpen = false; PressEscape(escapeDialog);
                Check(!escapeDialog.IsVisible && main.IsVisible, "Escape did not close only the secondary window");
                await Task.Delay(300);
                Check(((Button)main.FindName("StartFromExcelButton")).Visibility == Visibility.Visible, "standalone start was not visible on empty data");
                typeof(MainWindow).GetMethod("SetSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [snapshot]);
                await Task.Delay(500);
                var preferences = typeof(MainWindow).Assembly.GetType("SalaryDesktop.UiPreferences")!;
                var currentPreferences = preferences.GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null)!;
                var closeToTray = currentPreferences.GetType().GetProperty("CloseToTray")!;
                var oldCloseToTray = closeToTray.GetValue(currentPreferences);
                closeToTray.SetValue(currentPreferences, true);
                var closingArgs = new System.ComponentModel.CancelEventArgs();
                typeof(MainWindow).GetMethod("WindowClosing", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [main, closingArgs]);
                closeToTray.SetValue(currentPreferences, oldCloseToTray);
                Check(!closingArgs.Cancel && ((FrameworkElement)main.FindName("CloseToTrayBox")).Visibility == Visibility.Collapsed, "old close-to-tray preference blocked exit");
                Check(((Button)main.FindName("StartFromExcelButton")).Visibility != Visibility.Visible, "standalone start remained visible over existing data");
                async Task Navigate(string tag)
                {
                    var name = tag switch { "home" => "NavHome", "payroll" => "NavPayroll", "deductions" => "NavDeductions", "staff" => "NavStaff", "archive" => "NavArchive", "groups" => "NavGroups", "bank" => "NavBank", "transfer" => "NavTransfer", "accounts" => "NavAccounts", "settings" => "NavSettings", _ => throw new InvalidOperationException("unknown page") };
                    var item = (RadioButton)main.FindName(name); item.IsChecked = true; await Task.Delay(800);
                    Check(item.IsChecked == true, $"navigation item {tag} was not selected");
                }
                var table = (DataGrid)main.FindName("DataTable");
                var backupIndicator = (Button)main.FindName("BackupIndicatorButton");
                var themeIndicator = (Button)main.FindName("ThemeToggle");
                var bellIndicator = (Button)main.FindName("BellButton");
                var backupPosition = backupIndicator.TranslatePoint(new Point(0, 0), main);
                var themePosition = themeIndicator.TranslatePoint(new Point(0, 0), main);
                var bellPosition = bellIndicator.TranslatePoint(new Point(0, 0), main);
                Check(backupIndicator.Width == 38 && themeIndicator.Width == 38 && bellIndicator.Width == 38, "header icons should have equal fixed width");
                Check(MainWindow.DescribeBackupIndicator(true, true, "error", true, true, true, null).Text.Contains("جارٍ"), "backup indicator must show progress first");
                Check(MainWindow.DescribeBackupIndicator(false, true, null, true, true, false, null).Text.Contains("أحدث"), "backup indicator did not show a newer server copy");
                Check(MainWindow.DescribeBackupIndicator(false, false, "error", true, true, false, null).Text.Contains("فشل"), "backup indicator did not show an error");
                Check(MainWindow.DescribeBackupIndicator(false, false, null, true, true, true, null).Text.Contains("تعديلات"), "backup indicator did not show pending changes");
                Check(MainWindow.DescribeBackupIndicator(false, false, null, false, true, false, null).Text.Contains("لا توجد بيانات"), "backup indicator did not explain missing data");
                Check(MainWindow.DescribeBackupIndicator(false, false, null, true, false, false, null).Text.Contains("إعدادات"), "backup indicator did not explain missing configuration");
                Check(MainWindow.DescribeBackupIndicator(false, false, null, true, true, false, DateTime.Now).Text.Contains("آخر نسخة"), "backup indicator did not show the last backup");
                await Navigate("payroll");
                Check((backupIndicator.TranslatePoint(new Point(0, 0), main) - backupPosition).Length < 0.5 && (themeIndicator.TranslatePoint(new Point(0, 0), main) - themePosition).Length < 0.5 && (bellIndicator.TranslatePoint(new Point(0, 0), main) - bellPosition).Length < 0.5, "header icons moved during navigation");
                Check(((Button)main.FindName("GlobalSearchButton")).Visibility == Visibility.Collapsed, "fixed header still displayed the search bar");
                backupIndicator.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(300);
                Check(((FrameworkElement)main.FindName("SettingsPanel")).Visibility == Visibility.Visible, "backup indicator did not open backup settings");
                await Navigate("payroll"); Check(table.Items.Count == 1 && table.Items[0] is PayrollRow { Payment: "كاش" }, "payroll page did not show the synthetic employee");
                Check(main.Content is Grid { ColumnDefinitions.Count: 3 } && double.IsNaN(table.RowHeight) && table.ColumnHeaderHeight == 44, "workspace columns or grid density do not match the design");
                var netColumn = (DataGridTextColumn)table.Columns.Single(c => c.SortMemberPath == "Net");
                var deductionColumn = (DataGridTextColumn)table.Columns.Single(c => c.SortMemberPath == "TotalDeductions");
                Check(ReferenceEquals(netColumn.ElementStyle, application.FindResource("AccountingNetText")) &&
                      ReferenceEquals(deductionColumn.ElementStyle, application.FindResource("AccountingDeductionText")), "payroll accounting columns did not receive their styles");
                table.UpdateLayout();
                var netText = netColumn.GetCellContent(table.Items[0]) as TextBlock ?? throw new InvalidOperationException("net cell was not realized");
                var oldTheme = Themes.Current;
                Themes.Apply("dark");
                Check(Equals(netText.Foreground, application.FindResource("Primary")), "net text did not follow the dark theme");
                Themes.Apply(oldTheme);
                table.SelectedIndex = 0; await Task.Delay(150);
                var inspector = (Border)main.FindName("InspectorDrawer");
                Check(Grid.GetColumn(inspector) == 1 && Panel.GetZIndex(inspector) > 0, "inspector did not overlay the workspace");
                Check(inspector.Visibility == Visibility.Visible && ((TextBlock)main.FindName("InspectorTitleText")).Text == ((PayrollRow)table.Items[0]).Name, "inspector did not show the selected payroll record");
                Check(((ItemsControl)main.FindName("InspectorFieldsList")).Items.Count > 0, "inspector did not display record fields");
                Check(((TextBlock)main.FindName("InspectorAmountLabel")).Text == "صافي الراتب المستحق" && ((TextBlock)main.FindName("InspectorNetAmountText")).Text.Contains("500", StringComparison.Ordinal), "inspector payroll amount was not labeled as net pay");
                Check(((Button)main.FindName("InspectorSecondaryActionBtn")).Visibility == Visibility.Visible, "payroll detail action was not available");
                ((RadioButton)main.FindName("InspectorTabBank")).IsChecked = true;
                Check(((ItemsControl)main.FindName("InspectorFieldsList")).Items.Count > 0, "inspector tab lost the selected record");
                var originalRows = table.ItemsSource;
                table.ItemsSource = new[] { PayrollRows.Row(snapshot.Employees[0] with { Net = 500.125m }) };
                table.SelectedIndex = 0; await Task.Delay(150);
                Check(((TextBlock)main.FindName("InspectorNetAmountText")).Text.Contains("500.125", StringComparison.Ordinal), "inspector rounded a fractional accounting amount");
                ((RadioButton)main.FindName("InspectorTabSummary")).IsChecked = true;
                Check(((TextBlock)main.FindName("InspectorNetAmountText")).Text.Contains("500.125", StringComparison.Ordinal), "inspector tab change rounded an accounting amount");
                table.ItemsSource = originalRows; table.SelectedIndex = 0; await Task.Delay(150);
                var source = PresentationSource.FromVisual(main) ?? throw new InvalidOperationException("main window has no input source");
                var escape = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                main.RaiseEvent(escape);
                Check(escape.Handled && inspector.Visibility == Visibility.Collapsed && table.SelectedIndex == 0, "escape did not close the inspector while preserving selection");
                var filterHost = (StackPanel)main.FindName("FilterHost");
                Check(Children<ComboBox>(filterHost).Count() == 3 && Children<ComboBox>(filterHost).First().Items.Count == 2, "payroll quick filters were not built");
                Check(((TextBlock)main.FindName("TotalsShown")).Text.Contains("500", StringComparison.Ordinal), "payroll totals row did not show the net sum");
                Children<ComboBox>(filterHost).Last().SelectedIndex = 1; await Task.Delay(700); Check(table.Items.Count == 1, "payment filter hid the matching row");
                table.Columns.Single(c => c.SortMemberPath == "Name").Width = new DataGridLength(210);
                typeof(MainWindow).GetMethod("PersistColumnWidths", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, ["payroll"]);
                await Navigate("deductions"); await Navigate("payroll");
                Check(table.Columns.Single(c => c.SortMemberPath == "Name").Width.IsAbsolute && Math.Abs(table.Columns.Single(c => c.SortMemberPath == "Name").Width.Value - 210) < .1, "manually chosen column width was not restored");
                await Navigate("deductions"); Check(table.Items.Count == 1, "deductions page did not show the synthetic entry");
                var deductionAmount = (DataGridTextColumn)table.Columns.Single(c => c.SortMemberPath == "Amount");
                Check(ReferenceEquals(deductionAmount.ElementStyle, application.FindResource("AccountingDeductionText")), "deduction amount did not receive its accounting style");
                table.SelectedIndex = 0; await Task.Delay(150);
                Check(inspector.Visibility == Visibility.Collapsed && ((MenuItem)main.FindName("RowEmployeeDeductionMenu")).Visibility == Visibility.Visible && ((Button)main.FindName("DeductionCompareButton")).Visibility == Visibility.Visible, "deductions should expose direct actions without opening the inspector");
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
                reconciliation.Show(); Check(reconciliation.FindName("PeriodList") is ListBox, "reconciliation page did not open"); reconciliation.Close();
                var staff = new StaffDetailsWindow(StaffRecords.Read(snapshot)[0]) { Owner = main }; staff.Show(); Check(((DataGrid)staff.FindName("Fields")).Items.Count == 1, "staff details were not displayed"); staff.Close();
                var names = NameLists.Prepare(snapshot, TransferPreparation.Prepare(snapshot, "أيلول 2026"));
                var lists = new NameListsWindow("أيلول 2026", names, () => true) { Owner = main }; lists.Show(); Check(((ComboBox)lists.FindName("ListPicker")).Items.Count == 1, "name list page did not open"); lists.Close();
                var initial = new PayrollImportWindow(null, "") { Owner = main }; initial.Show(); Check(initial.FindName("Choose") is Button, "standalone monthly import did not open"); initial.Close();
                var refImport = new ReferenceImportWindow() { Owner = main }; refImport.Show(); Check(refImport.FindName("Choose") is Button, "reference import did not open"); refImport.Close();
                var annual = new AnnualImportWindow(snapshot) { Owner = main }; annual.Show(); Check(annual.FindName("Choose") is Button, "annual import page did not open"); annual.Close();
                var bank = new BankDetailsWindow("أيلول 2026", new BankAccountDetails("دافع وهمي", "فرع وهمي", "123456", "IQ06TEST123000000000001"), new BankManualAmounts(1, 2, 3)) { Owner = main }; bank.Show(); Check(((TextBox)bank.FindName("LeaveText")).Text == "1", "bank settings did not display manual amount"); bank.Close();
                var review = SalaryReview.Prepare(snapshot, "أيلول 2026", StaffRecords.Read(snapshot)); var audit = new SalaryReviewWindow(review) { Owner = main }; audit.Show(); Check(audit.FindName("CategoryList") is ListBox, "salary review page did not open"); audit.Close();
                var profile = PersonProfiles.Build(snapshot, "منتسب وهمي", StaffRecords.Read(snapshot));
                var person = new PersonWindow(snapshot, profile, "أيلول 2026") { Owner = main }; person.Show(); await Task.Delay(300);
                Check(((TextBlock)person.FindName("NetText")).Text == "500" && ((DataGrid)person.FindName("HistoryTable")).Items.Count == 1, "member profile did not show the payslip and deductions"); person.Close();
                var search = new SearchWindow(snapshot, Task.FromResult<IReadOnlyList<StaffMember>?>(StaffRecords.Read(snapshot))) { Owner = main }; search.Show(); await Task.Delay(800);
                Check(((Border)search.FindName("ResultsContainer")).Visibility == Visibility.Collapsed, "empty search should stay compact");
                var compactSearchHeight = search.ActualHeight;
                ((TextBox)search.FindName("Query")).Text = "123"; await Task.Delay(300);
                Check(((ListBox)search.FindName("Results")).Items.Count == 1, "global search did not find the member by unified number");
                Check(((Border)search.FindName("ResultsContainer")).Visibility == Visibility.Visible && search.ActualHeight > compactSearchHeight, "search window did not expand to show its result");
                ((TextBox)search.FindName("Query")).Text = "لا توجد نتيجة وهمية"; await Task.Delay(200);
                Check(((ListBox)search.FindName("Results")).Items.Count == 0 && ((Border)search.FindName("ResultsContainer")).Visibility == Visibility.Collapsed, "empty results did not collapse");
                search.Close();
                await Navigate("home");
                Check(SalaryDesktop.App.UnhandledCount == 0, "an unexpected UI error was reported");
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SCREENSHOT_DIR")))
                {
                    var richPath = Path.Combine(directory, "rich.zip"); RichFixture(richPath); var rich = BackupReader.Read(richPath);
                    var navNames = new[] { "NavHome", "NavDeductions", "NavBank", "NavTransfer", "NavArchive", "NavSettings" };
                    Check(navNames.All(n => ((FrameworkElement)main.FindName(n)).IsVisible) && new[] { "NavPayroll", "NavStaff", "NavGroups", "NavAccounts" }.All(n => !((FrameworkElement)main.FindName(n)).IsVisible), "sidebar did not show the requested navigation items");
                    var compareWindow = Fit(new DeductionComparisonWindow(rich, "أيلول 2026", () => true) { Owner = main }, 1100, 720);
                    compareWindow.Show(); await Task.Delay(400);
                    ((Button)compareWindow.FindName("CompareRunButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var comparisonGrid = (DataGrid)compareWindow.FindName("Rows");
                    for (int i = 0; i < 50 && comparisonGrid.Items.Count == 0; i++) await Task.Delay(100);
                    Check(comparisonGrid.Items.Count > 0, "deduction comparison did not show rows for two populated months");
                    Shot(compareWindow, "49-deduction-comparison");
                    ((ComboBox)compareWindow.FindName("ToMonth")).SelectedIndex = 0; await Task.Delay(100);
                    Check(comparisonGrid.Items.Count == 0, "changing comparison months left stale results visible");
                    compareWindow.Close();
                    main.WindowState = WindowState.Normal; Fit(main, 1460, 860);
                    Console.WriteLine($"Screen work area: {SystemParameters.WorkArea.Width}x{SystemParameters.WorkArea.Height}");
                    typeof(MainWindow).GetMethod("SetSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [rich]);
                    await Task.Delay(1200);
                    await Navigate("accounts");
                    table.SelectedIndex = 0; await Task.Delay(200);
                    var inspectorList = (ItemsControl)main.FindName("InspectorFieldsList");
                    var editableBranch = inspectorList.Items.Cast<InspectorFieldItem>().Single(f => f.RawPropertyName == "Branch");
                    Check(editableBranch.IsEditable && inspectorList.Items.Cast<InspectorFieldItem>().Single(f => f.RawPropertyName == "Entity").IsEditable == false, "inspector must edit only authorized account fields");
                    var priorBranch = editableBranch.Value;
                    editableBranch.EditableValue = "مصرف وهمي جديد";
                    Check(((Button)main.FindName("InspectorInlineSaveBtn")).Visibility == Visibility.Visible, "inspector did not show the pending save action");
                    Shot(main, "34-inspector-inline-edit");
                    table.SelectedIndex = 1; await Task.Delay(100); table.SelectedIndex = 0; await Task.Delay(100);
                    Check(inspectorList.Items.Cast<InspectorFieldItem>().Single(f => f.RawPropertyName == "Branch").EditableValue == "مصرف وهمي جديد", "switching rows discarded an unsaved draft");
                    var inspectorFilter = (TextBox)main.FindName("InspectorFilterBox");
                    inspectorFilter.Text = "IBAN";
                    Check(inspectorList.Items.Count == 1 && ((InspectorFieldItem)inspectorList.Items[0]).RawPropertyName == "Iban", "inspector search did not filter fields");
                    inspectorFilter.Text = "";
                    ((Button)main.FindName("InspectorDiscardBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(inspectorList.Items.Cast<InspectorFieldItem>().Single(f => f.RawPropertyName == "Branch").EditableValue == priorBranch && ((Button)main.FindName("InspectorInlineSaveBtn")).Visibility == Visibility.Collapsed, "discard did not restore the stored account");
                    var drawer = (FrameworkElement)main.FindName("InspectorDrawer");
                    var headerAboveDrawer = (FrameworkElement)main.FindName("HeaderGrid");
                    Check(drawer.Visibility == Visibility.Visible && drawer.TranslatePoint(new Point(0, 0), main).Y >= headerAboveDrawer.TranslatePoint(new Point(0, headerAboveDrawer.ActualHeight), main).Y, "inspector drawer covered the fixed header");
                    var stableBackupPosition = backupIndicator.TranslatePoint(new Point(0, 0), main);
                    var stableThemePosition = themeIndicator.TranslatePoint(new Point(0, 0), main);
                    var stableBellPosition = bellIndicator.TranslatePoint(new Point(0, 0), main);
                    foreach (var (tag, name) in new[] { ("home", "01-home"), ("payroll", "02-payroll"), ("deductions", "03-deductions"), ("staff", "04-staff"), ("archive", "05-archive"), ("groups", "06-groups"), ("bank", "07-bank"), ("transfer", "08-transfer"), ("accounts", "09-accounts"), ("settings", "10-settings") })
                    {
                        await Navigate(tag); if (tag is "staff" or "bank" or "transfer" or "archive") await Task.Delay(1200);
                        Check((backupIndicator.TranslatePoint(new Point(0, 0), main) - stableBackupPosition).Length < 0.5 && (themeIndicator.TranslatePoint(new Point(0, 0), main) - stableThemePosition).Length < 0.5 && (bellIndicator.TranslatePoint(new Point(0, 0), main) - stableBellPosition).Length < 0.5, "header icons moved on page " + tag);
                        var header = (FrameworkElement)main.FindName("HeaderGrid");
                        var subtitle = (TextBlock)main.FindName("PageSubtitle");
                        var subtitleBottom = subtitle.TranslatePoint(new Point(0, subtitle.ActualHeight), header).Y;
                        Check(subtitleBottom <= header.ActualHeight + 0.5, $"header clipped its subtitle on page {tag}: bottom {subtitleBottom:F1}, header {header.ActualHeight:F1}, subtitle height {subtitle.ActualHeight:F1}");
                        Shot(main, name);
                        if (tag == "deductions")
                        {
                            table.UpdateLayout();
                            var clickedRow = table.ItemContainerGenerator.ContainerFromIndex(1) as DataGridRow ?? throw new InvalidOperationException("second deduction row not realized");
                            table.SelectedIndex = 0;
                            var rightClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent, Source = clickedRow };
                            typeof(MainWindow).GetMethod("SelectContextRow", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [table, rightClick]);
                            Check(ReferenceEquals(table.SelectedItem, clickedRow.Item), "right-click actions were not targeted to the clicked deduction");
                            var menu = table.ContextMenu ?? throw new InvalidOperationException("deduction context menu missing");
                            menu.PlacementTarget = table; menu.IsOpen = true; await Task.Delay(300);
                            Check(((MenuItem)main.FindName("RowEmployeeDeductionMenu")).IsEnabled, "employee deduction statement action was not enabled");
                            ShotElement(menu, "50-deduction-context"); menu.IsOpen = false;
                        }
                        if (tag == "archive") Check(((ListBox)main.FindName("ArchiveList")).Items.Count == 2, "archive list did not show the books");
                    }
                    await Navigate("payroll"); ((TextBox)main.FindName("SearchBox")).Text = "علي"; await Task.Delay(1200); Shot(main, "11-payroll-search");
                    var detail = Fit(new EmployeeDetailsWindow(rich.Employees[3], rich) { Owner = main }); detail.Show(); await Task.Delay(500); Shot(detail, "12-employee-details"); detail.Close();
                    var import = Fit(new PayrollImportWindow(rich, "أيلول 2026") { Owner = main }); import.Show(); await Task.Delay(500); Shot(import, "13-payroll-import"); import.Close();
                    var ibanAudit = Fit(new IbanAuditWindow(IbanAudit.Prepare(rich, "أيلول 2026")) { Owner = main });
                    ibanAudit.Show(); await Task.Delay(500);
                    var ibanRows = (DataGrid)ibanAudit.FindName("Rows");
                    Check(ibanRows.Items.Count == rich.Employees.Count(e => e.Month == "أيلول 2026"), "IBAN audit did not display the selected month's payroll");
                    Shot(ibanAudit, "41-iban-audit");
                    var missingFilter = Children<RadioButton>(ibanAudit).Single(r => Equals(r.Tag, "Missing"));
                    missingFilter.IsChecked = true;
                    Check(ibanRows.Items.Cast<IbanAuditRow>().All(r => r.Status == IbanAuditStatus.Missing), "IBAN audit missing filter showed another status");
                    ibanAudit.Close();
                    var richReview = Fit(new SalaryReviewWindow(SalaryReview.Prepare(rich, "أيلول 2026", StaffRecords.Read(rich))) { Owner = main }); richReview.Show(); await Task.Delay(500); Shot(richReview, "14-salary-review"); richReview.Close();
                    var richGroups = SalaryItemReports.Groups(rich, "أيلول 2026"); var richItems = Fit(new SalaryItemsWindow(rich, "أيلول 2026", richGroups) { Owner = main }); richItems.Show(); await Task.Delay(300);
                    ((DataGrid)richItems.FindName("Groups")).SelectedItem = richGroups.First(g => g.Title == "صندوق الشهداء"); await Task.Delay(900);
                    Check(((FrameworkElement)richItems.FindName("SpecialPanel")).Visibility == Visibility.Visible, "fund report buttons were not offered"); Shot(richItems, "15-salary-items"); richItems.Close();
                    // الكشوف الخاصة بصفحتها (مثل الآيفون): الملخص والأسماء.
                    foreach (var (kind, shot) in new[] { ("fund", "39-special-fund"), ("food", "39-special-food"), ("health", "39-special-health") })
                    {
                        if (richGroups.FirstOrDefault(g => SalaryItemsWindow.Special(g.Title) == kind) is not { } specialGroup) { Console.WriteLine("No fixture item for " + kind); continue; }
                        var specialWindow = Fit(new SpecialReportWindow(rich, "أيلول 2026", kind, specialGroup) { Owner = main }); specialWindow.Show(); await Task.Delay(1200);
                        Check(((FrameworkElement)specialWindow.FindName("Actions")).IsEnabled, "special report page did not finish loading: " + kind);
                        var reportButtons = Children<Button>((FrameworkElement)specialWindow.FindName("Actions")).ToArray();
                        foreach (var (label, key) in new[] { ("Excel", "ExcelActionGlyph"), ("PDF", "PdfActionGlyph") })
                        {
                            var button = reportButtons.Single(b => Equals(b.Content, label));
                            Check(Children<TextBlock>(button).Any(t => t.Text == (string)Application.Current.FindResource(key)), "report action icon missing: " + label);
                        }
                        Shot(specialWindow, shot); specialWindow.Close();
                    }
                    // البحث الشامل، ملف المنتسب، التصفية والمجاميع، وكشوف PDF/Excel المعتمدة للمراجعة البصرية.
                    var richStaff = StaffRecords.Read(rich);
                    var richSearch = Fit(new SearchWindow(rich, Task.FromResult<IReadOnlyList<StaffMember>?>(richStaff)) { Owner = main }); richSearch.Show(); await Task.Delay(900);
                    Shot(richSearch, "16-global-search-empty");
                    ((TextBox)richSearch.FindName("Query")).Text = "علي محمد"; await Task.Delay(300); Shot(richSearch, "16-global-search"); richSearch.Close();
                    var richPerson = Fit(new PersonWindow(rich, PersonProfiles.Build(rich, rich.Employees[3].Name, richStaff), "أيلول 2026") { Owner = main }); richPerson.Show(); await Task.Delay(800); Shot(richPerson, "17-person-payslip");
                    var tabs = Children<TabControl>(richPerson).First();
                    tabs.SelectedIndex = 1; await Task.Delay(400); Shot(richPerson, "18-person-deductions");
                    tabs.SelectedIndex = 3; await Task.Delay(400); Shot(richPerson, "19-person-changes"); richPerson.Close();
                    await Navigate("payroll"); ((TextBox)main.FindName("SearchBox")).Text = ""; await Task.Delay(900);
                    var richTable = (DataGrid)main.FindName("DataTable");
                    foreach (var column in richTable.Columns.Where(c => c.SortMemberPath is "Unified" or "TotalDeductions")) column.Visibility = Visibility.Visible;
                    Children<ComboBox>((StackPanel)main.FindName("FilterHost")).First().SelectedIndex = 1; await Task.Delay(900);
                    richTable.SelectedItems.Clear(); foreach (var row in richTable.Items.Cast<object>().Take(4)) richTable.SelectedItems.Add(row); await Task.Delay(300);
                    Check(((TextBlock)main.FindName("TotalsSelected")).Text.Contains("المحدد", StringComparison.Ordinal), "selection totals were not shown");
                    Shot(main, "20-payroll-filter-totals");
                    foreach (var column in richTable.Columns.Where(c => c.SortMemberPath is "Unified" or "TotalDeductions")) column.Visibility = Visibility.Collapsed;
                    await Navigate("deductions"); Children<ComboBox>((StackPanel)main.FindName("FilterHost")).First().SelectedIndex = 1; await Task.Delay(900); Shot(main, "21-deductions-entity-filter");
                    await Navigate("home");
                    var folder = Environment.GetEnvironmentVariable("SCREENSHOT_DIR")!;
                    var assets = ReportAssets.Load(Path.GetFullPath("SalaryApp/Assets.xcassets/WatermarkLogo.imageset/WatermarkLogo.png"));
                    var stamp = new DateTime(2026, 10, 1);
                    var payrollRows = PayrollRows.Build(rich.Employees.Where(e => e.Month == "أيلول 2026"));
                    var tableSheet = TableReport.Build(ReportSheet.HeaderFor(rich), "رواتب شهر أيلول ٢٠٢٦", ["الاسم", "الرتبة", "محل العمل", "طريقة الدفع", "الصافي"], 4,
                        payrollRows.Select(r => new[] { r.Name, r.Rank, r.Workplace, r.Payment, "" }).ToArray(), payrollRows.Select(r => r.Net).ToArray());
                    File.WriteAllBytes(Path.Combine(folder, "22-table-export.pdf"), ReportPdf.Generate([tableSheet], assets, stamp));
                    File.WriteAllBytes(Path.Combine(folder, "22-table-export.xlsx"), XlsxReport.Generate(tableSheet));
                    var entitySheet = StandardReports.EntityDeductions(rich, "أيلول 2026", "شركة عراق الجوال");
                    File.WriteAllBytes(Path.Combine(folder, "23-entity-list.pdf"), ReportPdf.Generate([entitySheet], assets, stamp));
                    var month = PersonProfiles.Build(rich, rich.Employees[3].Name, richStaff).Months.First(m => m.Month == "أيلول 2026");
                    File.WriteAllBytes(Path.Combine(folder, "24-payslip.pdf"), PayslipPdf.Generate(ReportSheet.HeaderFor(rich), month.Employee!.Name, month.Employee.Rank, month.Employee.Workplace, month.Month, PersonProfiles.Payslip(month), assets, stamp));
                    var homeLogo = Path.Combine(AppContext.BaseDirectory, "Assets", "HomeLogo.png");
                    File.WriteAllBytes(Path.Combine(folder, "24-salary-report.pdf"), SalaryReportPdf.Generate([SalaryReports.Make(month, false, false)], assets, File.Exists(homeLogo) ? File.ReadAllBytes(homeLogo) : null, stamp));
                    // الاستيراد الموحّد ومعاينة PDF داخل البرنامج (ملفات وهمية).
                    var candidates = ImportDetector.Order([
                        new ImportCandidate(@"C:\بيانات\نسخة الرواتب.zip", ImportKind.Backup, null, "يُفحص فهرسها قبل فتحها؛ ملف الآيفون الأصلي لا يتغير."),
                        new ImportCandidate(@"C:\بيانات\رواتب أيلول 2026.xlsx", ImportKind.Payroll, "أيلول 2026", "رواتب واستقطاعات · الشهر من اسم الملف: أيلول 2026"),
                        new ImportCandidate(@"C:\بيانات\استقطاعات 2026.xlsx", ImportKind.Annual, null, "يُقسّم إلى أشهر حسب عمود الشهر وسنة اسم الملف."),
                        new ImportCandidate(@"C:\بيانات\حسابات الجهات.xlsx", ImportKind.Accounts, null, "يُضاف الجديد ويُحدّث الموجود دون حذف الجهات الغائبة."),
                        new ImportCandidate(@"C:\بيانات\ملاحظات.docx", ImportKind.Unknown, null, "الامتداد غير مدعوم؛ الملفات المقبولة: نسخة ZIP أو ملف Excel ‏(xlsx).")]);
                    var center = Fit(new ImportCenterWindow(candidates, true) { Owner = main }); center.Show(); await Task.Delay(500); Shot(center, "37-import-center"); center.Close();
                    var previewBytes = SalaryReportPdf.Generate([SalaryReports.Make(month, false, false)], assets, File.Exists(homeLogo) ? File.ReadAllBytes(homeLogo) : null, stamp);
                    var preview = Fit(new PdfPreviewWindow("تقرير راتب - " + month.Employee.Name, previewBytes, "تقرير راتب") { Owner = main }, 980, 860); preview.Show(); await Task.Delay(2500);
                    Check(((ItemsControl)preview.FindName("Pages")).Items.Count == 1, "in-app PDF preview did not render the page"); Shot(preview, "38-pdf-preview"); preview.Close();
                    // كشف استقطاعات موظف لجهة واحدة عبر الأشهر.
                    var sampleDeduction = rich.Deductions.First(d => d.Amount is not null);
                    var statementWindow = Fit(new EmployeeDeductionWindow(rich, new DeductionPerson(PersonProfiles.Key(sampleDeduction.Name), sampleDeduction.Name, sampleDeduction.Rank), sampleDeduction.Title) { Owner = main }, 1120, 780);
                    statementWindow.Show(); await Task.Delay(1800);
                    Check(((ItemsControl)statementWindow.FindName("Rows")).Items.Count > 0, "employee deduction statement rows were not shown"); Shot(statementWindow, "41-employee-deductions"); statementWindow.Close();
                    var scanFolder = Path.Combine(directory, "scan"); Directory.CreateDirectory(scanFolder);
                    var scanFiles = new[] { Path.Combine(scanFolder, "Scan_0001.jpg"), Path.Combine(scanFolder, "Scan_0002.jpg") };
                    for (int i = 0; i < scanFiles.Length; i++) File.WriteAllBytes(scanFiles[i], FictionalPage(i + 3));
                    var archiveAdd = Fit(new ArchiveBookWindow(ArchiveBookMode.Add, ["أيلول 2026", "آب 2026"], ["شركة عراق الجوال", "مصرف الرافدين"], "أيلول 2026", files: scanFiles) { Owner = main }, 1040, 720);
                    archiveAdd.Show(); await Task.Delay(1500);
                    Check(((ItemsControl)archiveAdd.FindName("PageList")).Items.Count == 2 && ((TextBox)archiveAdd.FindName("TitleBox")).Text.Length == 0, "archive import did not load scanned pages or named a scanner file");
                    ((TextBox)archiveAdd.FindName("TitleBox")).Text = "كتاب وهمي بشأن الاستقطاع"; await Task.Delay(200);
                    Shot(archiveAdd, "45-archive-add"); archiveAdd.Close();
                    var ledger = Fit(new ReconciliationWindow(rich, "تشرين الاول 2026", () => true, BankReconciliation.ReadFromBackup(rich).Single(p => !p.IsConfirmed).Id) { Owner = main }, 1340, 820);
                    ledger.Show(); await Task.Delay(1500);
                    Check(((ListBox)ledger.FindName("PeriodList")).Items.Count == 2 && ((DataGrid)ledger.FindName("Lines")).Items.Count == 7, "ledger did not show periods and movements");
                    Shot(ledger, "46-ledger");
                    ((ListBox)ledger.FindName("PeriodList")).SelectedIndex = 1; await Task.Delay(700); Shot(ledger, "47-ledger-confirmed"); ledger.Close();
                    var confirmedSample = BankReconciliation.ReadFromBackup(rich).Single(p => p.IsConfirmed);
                    var confirmWindow = new ReconciliationConfirmWindow(BankReconciliation.Unconfirm(confirmedSample), ReconciliationReview.Prepare(BankReconciliation.Unconfirm(confirmedSample), null, null, null), true) { Owner = main };
                    confirmWindow.Show(); await Task.Delay(400); Shot(confirmWindow, "48-ledger-confirm"); confirmWindow.Close();
                    var periodEditor = Fit(new ReconciliationEditor(null, "أيلول 2026", confirmedSample) { Owner = main }, 560, 720); periodEditor.Show(); await Task.Delay(400); Shot(periodEditor, "49-period-editor"); periodEditor.Close();
                    // أمانات الرواتب ببيانات وهمية: الجدول بالمرشحات، وقيد بدفعة جزئية بلا رقم كتاب ولا تاريخ.
                    var trustStore = new LocalSalaryTrustStore(Path.Combine(directory, "salary-trust"));
                    SalaryTrustPayment Pay(long amount, string number, string date, string note) => new(Guid.NewGuid().ToString(), amount, number, date, note);
                    SalaryTrustEntry Trust(string name, int month, long amount, string status, bool legacy = false, bool review = false, params SalaryTrustPayment[] payments) =>
                        new(Guid.NewGuid().ToString(), name, "شرطي", "قسم وهمي", 2026, month, amount, status, "", legacy ? "صرف" : review ? "قطع علاقة تقاعد" : "", legacy, review, payments);
                    var partialTrust = Trust("منتسب وهمي أول", 10, 750000, "لم يراجع", false, false, Pay(250000, "", "", "صرف جزئي بلا كتاب"), Pay(100000, "كتاب 15", "2026-10-20", ""));
                    var fictionalTrust = new SalaryTrustLedger(1, [partialTrust, Trust("منتسب وهمي ثان", 10, 820000, "إجازة"), Trust("منتسب وهمي ثالث", 9, 640000, "", true),
                        Trust("منتسب وهمي رابع", 8, 910000, "قطع علاقة", false, true), Trust("منتسب وهمي خامس", 9, 560000, "سفر", false, false, Pay(560000, "كتاب 9", "", ""))], true);
                    trustStore.Save(rich, trustStore.Load(rich), fictionalTrust);
                    var trustWindow = Fit(new SalaryTrustWindow(rich, trustStore, () => true) { Owner = main }, 1200, 800); trustWindow.Show(); await Task.Delay(800);
                    var trustTable = (DataGrid)trustWindow.FindName("Table");
                    Check(trustTable.Items.Count == 5 && new[] { "محل العمل", "ملاحظات", "حالة الاستيراد", "تفاصيل الدفعات", "عناوين الكتب" }.All(h => trustTable.Columns.Any(c => Equals(c.Header, h))), "salary trust details were not shown"); Shot(trustWindow, "43-salary-trust");
                    var trustEntryWindow = Fit(new SalaryTrustEntryWindow(trustWindow, trustWindow.Latest(partialTrust.Id)!, rich.Books) { Owner = trustWindow }, 760, 820); trustEntryWindow.Show(); await Task.Delay(600);
                    Shot(trustEntryWindow, "44-salary-trust-entry"); trustEntryWindow.Close(); trustWindow.Close();
                    // الرئيسية: الخطوة التالية والفئات والأدوات، ولا تذكير حفظ لأن النسخة الوهمية بلا تعديلات محلية.
                    Check(((TextBlock)main.FindName("NextTitle")).Text.Length > 0 && ((FrameworkElement)main.FindName("NextButton")).Visibility == Visibility.Visible, "home next step was not shown");
                    Check(((ItemsControl)main.FindName("HomeCategories")).Items.Count == 4, "home categories were not shown");
                    Check(((ItemsControl)main.FindName("HomeTools")).Items.Count == 8, "home tools were not shown");
                    // القائمة الجانبية الثابتة: الإعدادات زر في بطاقة الحالة، والشارة على كشف البنك.
                    Check(((FrameworkElement)main.FindName("NavSettings")).IsVisible && ((FrameworkElement)main.FindName("SidebarBackup")).IsVisible, "sidebar status card was not shown");
                    Check(((FrameworkElement)main.FindName("BackupReminder")).Visibility == Visibility.Collapsed, "backup reminder shown without local changes");
                    var mainTrustStore = (LocalSalaryTrustStore)typeof(MainWindow).GetField("trustStore", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
                    mainTrustStore.Save(rich, mainTrustStore.Load(rich), fictionalTrust);
                    ((Button)main.FindName("NavTrustButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(700);
                    var embeddedTrust = (SalaryTrustWindow)typeof(MainWindow).GetField("trustController", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
                    var embeddedTable = (DataGrid)embeddedTrust.FindName("Table");
                    Check(((FrameworkElement)main.FindName("TrustPanel")).IsVisible && embeddedTable.IsVisible && embeddedTable.Items.Count == 5 && !embeddedTrust.IsVisible && Window.GetWindow(embeddedTable) == main, "salary trust was not embedded in the main window");
                    ((TextBox)embeddedTrust.FindName("SearchBox")).Text = "منتسب وهمي أول"; await Task.Delay(100);
                    Check(embeddedTable.Items.Count == 1, "embedded trust search did not filter records");
                    ((TextBox)embeddedTrust.FindName("SearchBox")).Text = ""; await Task.Delay(100);
                    Shot(main, "51-salary-trust-embedded");
                    await Navigate("home");
                    // الوضع الداكن.
                    Themes.Apply("dark"); await Task.Delay(500);
                    Check(((SolidColorBrush)Application.Current.Resources["Page"]).Color == (Color)ColorConverter.ConvertFromString("#0F1918"), "dark theme was not applied");
                    Shot(main, "25-dark-home");
                    await Navigate("payroll"); await Task.Delay(600); Shot(main, "26-dark-payroll");
                    var darkPerson = Fit(new PersonWindow(rich, PersonProfiles.Build(rich, rich.Employees[3].Name, richStaff), "أيلول 2026") { Owner = main }); darkPerson.Show(); await Task.Delay(700); Shot(darkPerson, "27-dark-person"); darkPerson.Close();
                    var darkSearch = Fit(new SearchWindow(rich, Task.FromResult<IReadOnlyList<StaffMember>?>(richStaff)) { Owner = main }); darkSearch.Show(); await Task.Delay(900);
                    ((TextBox)darkSearch.FindName("Query")).Text = "الرافدين"; await Task.Delay(300); Shot(darkSearch, "28-dark-search"); darkSearch.Close();
                    Themes.Apply("light"); await Navigate("home"); await Task.Delay(800);
                    // معاينة تصميم تذكير الحفظ فقط (النسخة الوهمية بلا تعديلات محلية فعلية).
                    ((FrameworkElement)main.FindName("BackupReminder")).Visibility = Visibility.Visible;
                    ((TextBlock)main.FindName("BackupReminderText")).Text = "أشهر مستوردة، حسابات وروابط على هذا الجهاز فقط · آخر نسخة شاملة: قبل يومين";
                    ((FrameworkElement)main.FindName("UpdateCard")).Visibility = Visibility.Visible;
                    ((TextBlock)main.FindName("UpdateTitle")).Text = "إصدار أحدث متاح · 1.0.99";
                    // جرس الإشعارات: عدّاد أحمر على الجرس، ثم اللوحة مفتوحة بكل التنبيهات.
                    await Task.Delay(1000);
                    Check(((FrameworkElement)main.FindName("BellBadge")).Visibility == Visibility.Visible && ((TextBlock)main.FindName("BellCount")).Text != "0", "notification bell badge was not shown");
                    Shot(main, "29-bell");
                    ((TextBlock)main.FindName("ServerNewerText")).Text = "رفع جهاز آخر نسخة جديدة اليوم 21:15 (14.2 ميغابايت). استرجعها قبل النسخ من ويندوز.";
                    ((FrameworkElement)main.FindName("ServerNewerCard")).Visibility = Visibility.Visible;
                    ((FrameworkElement)main.FindName("NotificationPanel")).Visibility = Visibility.Visible;
                    await Task.Delay(300); Shot(main, "29-backup-reminder");
                    ((FrameworkElement)main.FindName("ServerNewerCard")).Visibility = Visibility.Collapsed;
                    ((FrameworkElement)main.FindName("NotificationPanel")).Visibility = Visibility.Collapsed;
                    ((FrameworkElement)main.FindName("UpdateCard")).Visibility = Visibility.Collapsed;
                    // القفل: طبقة تغطي النوافذ المفتوحة وتعطّل محتواها حتى إدخال كلمة السر.
                    Locker.Enable("1234"); Locker.Lock(); await Task.Delay(400);
                    Check(Locker.IsLocked && !((UIElement)main.Content).IsEnabled, "lock did not cover the main window");
                    Shot(main, "30-lock");
                    Check(!Locker.TryUnlock("0000", out _) && Locker.IsLocked, "wrong password unlocked the application");
                    Check(Locker.TryUnlock("1234", out _) && !Locker.IsLocked && ((UIElement)main.Content).IsEnabled, "correct password did not unlock");
                    Locker.Disable();
                    await Navigate("settings"); await Task.Delay(500);
                    ((FrameworkElement)main.FindName("LockEnabledBox")).BringIntoView(); await Task.Delay(400); Shot(main, "31-settings-security");
                    // سيرفر النسخ المحلي: الرمز يُحفظ مشفّراً (DPAPI) ويُقرأ كما هو، ثم لقطة لوحة الإعدادات بعنوان وهمي.
                    var fictionalToken = new string('t', 40);
                    Check(MainWindow.SaveServerToken(fictionalToken) && MainWindow.ServerToken == fictionalToken, "server token was not protected and restored");
                    var servicePicker = (ListBox)main.FindName("BackupServicePicker");
                    servicePicker.SelectedIndex = 1; await Task.Delay(300);
                    Check(((FrameworkElement)main.FindName("ServerPanel")).Visibility == Visibility.Visible, "server settings panel was not shown");
                    ((TextBox)main.FindName("ServerAddressBox")).Text = "http://192.168.1.50:8765";
                    ((FrameworkElement)main.FindName("ServerPanel")).BringIntoView(); await Task.Delay(400); Shot(main, "42-settings-server");
                    servicePicker.SelectedIndex = 0; MainWindow.SaveServerToken(""); await Task.Delay(200);
                    // الخط والأرقام من الإعدادات: Cairo مع أرقام هندية ثم العودة إلى الافتراضي.
                    ((FrameworkElement)main.FindName("FontPicker")).BringIntoView(); await Task.Delay(400); Shot(main, "32-settings-font");
                    var scalePicker = (ComboBox)main.FindName("InterfaceScalePicker");
                    scalePicker.SelectedItem = scalePicker.Items.Cast<ComboBoxItem>().Single(i => Equals(i.Tag, "120")); await Task.Delay(400);
                    Check(AppFonts.InterfaceScale == 120, "interface scale setting did not apply");
                    ((FrameworkElement)main.FindName("FontPicker")).BringIntoView(); await Task.Delay(400); Shot(main, "46-settings-font-large");
                    scalePicker.SelectedItem = scalePicker.Items.Cast<ComboBoxItem>().Single(i => Equals(i.Tag, "100")); await Task.Delay(300);
                    Check(AppFonts.InterfaceScale == 100, "interface scale setting did not return to default");
                    AppFonts.Apply("alexandria", "latin"); await Task.Delay(300); Shot(main, "47-settings-font-alexandria");
                    AppFonts.Apply("times", "latin"); await Task.Delay(300); Shot(main, "48-settings-font-times");
                    AppFonts.Apply("cairo", "arabic"); await Task.Delay(300);
                    Check(((FontFamily)Application.Current.Resources["AppFont"]).Source.Contains("#Cairo", StringComparison.Ordinal) && NumberSubstitution.GetSubstitution(main) == NumberSubstitutionMethod.Traditional, "font and digits were not applied");
                    await Navigate("payroll"); await Task.Delay(700); Shot(main, "33-font-cairo-eastern");
                    AppFonts.Apply(AppFonts.DefaultFont, "latin"); await Task.Delay(300);
                    Check(NumberSubstitution.GetSubstitution(main) == NumberSubstitutionMethod.European, "latin digits were not restored");
                    // ملفات التحويل: كل تبويب بإجراءاته فقط.
                    await Navigate("transfer"); await Task.Delay(1200);
                    Check(((FrameworkElement)main.FindName("GroupCard")).Visibility == Visibility.Visible && ((FrameworkElement)main.FindName("IndividualCard")).Visibility == Visibility.Collapsed, "Rafidain tab showed the wrong actions");
                    ((ListBox)main.FindName("TransferGroupPicker")).SelectedIndex = 2; await Task.Delay(1200);
                    Check(((FrameworkElement)main.FindName("GroupCard")).Visibility == Visibility.Collapsed && ((FrameworkElement)main.FindName("IndividualCard")).Visibility == Visibility.Visible, "Anbar tab showed the wrong actions");
                    Shot(main, "34-transfer-anbar");
                    ((ListBox)main.FindName("TransferGroupPicker")).SelectedIndex = 3; await Task.Delay(1200);
                    Check(((FrameworkElement)main.FindName("UnresolvedCard")).Visibility == Visibility.Visible && ((FrameworkElement)main.FindName("ReferencesCard")).Visibility == Visibility.Collapsed, "unclassified tab showed the wrong actions");
                    Shot(main, "35-transfer-unresolved");
                    ((ListBox)main.FindName("TransferGroupPicker")).SelectedIndex = 0;
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
        // ينهي العملية بالنتيجة مباشرة حتى لا يغيّرها تنظيف كائنات ويندوز (معاينة PDF) بعد إغلاق الواجهة.
        Console.WriteLine($"Smoke exit code: {exit}"); Console.Out.Flush();
        Environment.Exit(exit);
        return exit;
    }
}
