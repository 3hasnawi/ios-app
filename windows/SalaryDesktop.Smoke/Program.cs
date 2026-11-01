using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    [STAThread]
    private static int Main()
    {
        var directory = Path.Combine(Path.GetTempPath(), "salary-ui-smoke-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "fictional.zip"); Fixture(path); var snapshot = BackupReader.Read(path);
        var application = new SalaryDesktop.App(); application.InitializeComponent(); application.StartupUri = null; application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var exit = 1;
        application.Startup += async (_, _) => {
            MainWindow? main = null;
            try
            {
                main = new MainWindow(); typeof(MainWindow).GetMethod("SetSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [snapshot]); main.Show();
                await Task.Delay(500);
                async Task Navigate(string tag)
                {
                    var button = Children<Button>(main).Single(b => Equals(b.Tag, tag)); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(800);
                }
                var table = (DataGrid)main.FindName("DataTable");
                await Navigate("payroll"); Check(table.Items.Count == 1, "payroll page did not show the synthetic employee");
                await Navigate("deductions"); Check(table.Items.Count == 1, "deductions page did not show the synthetic entry");
                await Navigate("staff"); await Task.Delay(1200); Check(table.Items.Count == 1 && table.Items[0] is StaffMember, "staff page did not load its synthetic record");
                await Navigate("bank"); Check(((FrameworkElement)main.FindName("BankPanel")).Visibility == Visibility.Visible, "bank page was not visible");
                await Navigate("transfer"); Check(((FrameworkElement)main.FindName("TransferPanel")).Visibility == Visibility.Visible, "transfer page was not visible");
                var staff = new StaffDetailsWindow(StaffRecords.Read(snapshot)[0]) { Owner = main }; staff.Show(); Check(((DataGrid)staff.FindName("Fields")).Items.Count == 1, "staff details were not displayed"); staff.Close();
                var names = NameLists.Prepare(snapshot, TransferPreparation.Prepare(snapshot, "أيلول 2026"));
                var lists = new NameListsWindow("أيلول 2026", names, () => true) { Owner = main }; lists.Show(); Check(((ComboBox)lists.FindName("ListPicker")).Items.Count == 1, "name list page did not open"); lists.Close();
                var annual = new AnnualImportWindow(snapshot) { Owner = main }; annual.Show(); Check(annual.FindName("Choose") is Button, "annual import page did not open"); annual.Close();
                var bank = new BankDetailsWindow("أيلول 2026", "دافع وهمي", "IQ06TEST123000000000001", new BankManualAmounts(1, 2, 3)) { Owner = main }; bank.Show(); Check(((TextBox)bank.FindName("LeaveText")).Text == "1", "bank settings did not display manual amount"); bank.Close();
                var review = SalaryReview.Prepare(snapshot, "أيلول 2026", StaffRecords.Read(snapshot)); var audit = new SalaryReviewWindow(review) { Owner = main }; audit.Show(); Check(audit.FindName("KindPicker") is ComboBox, "salary review page did not open"); audit.Close();
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
