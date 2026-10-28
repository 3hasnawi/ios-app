using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Net;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace SalaryWindows;
public sealed partial class MainWindow : Window
{
    private BackupData? data;
    private CancellationTokenSource? searchCancellation;
    public MainWindow() { InitializeComponent(); }
    private void Home_Click(object sender, RoutedEventArgs e) { HomePanel.Visibility = Visibility.Visible; SearchPanel.Visibility = Visibility.Collapsed; PageTitle.Text = "الرئيسية"; }
    private void SearchPage_Click(object sender, RoutedEventArgs e) { HomePanel.Visibility = Visibility.Collapsed; SearchPanel.Visibility = Visibility.Visible; PageTitle.Text = "البحث"; }
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var url = new TextBox { Header = "عنوان السيرفر المحلي", PlaceholderText = "http://192.168.1.10:8765", FlowDirection = FlowDirection.LeftToRight };
        var token = new PasswordBox { Header = "رمز الوصول", FlowDirection = FlowDirection.LeftToRight };
        var content = new StackPanel { Spacing = 12, FlowDirection = FlowDirection.RightToLeft };
        content.Children.Add(new TextBlock { Text = "اقرأ آخر نسخة من السيرفر، أو اختر ملف نسخة محفوظاً. لا تُرفع أي تغييرات في هذه المرحلة.", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(url); content.Children.Add(token);
        var dialog = new ContentDialog { Title = "فتح نسخة احتياطية", Content = content, PrimaryButtonText = "تنزيل من السيرفر", SecondaryButtonText = "اختيار ملف", CloseButtonText = "إلغاء", XamlRoot = Content.XamlRoot };
        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.None) return;
        string? path = null; bool temporary = false;
        try
        {
            StatusText.Text = "جارٍ قراءة النسخة…";
            if (choice == ContentDialogResult.Primary)
            {
                if (!Uri.TryCreate(url.Text.Trim(), UriKind.Absolute, out var uri)) throw new InvalidOperationException("عنوان السيرفر غير صالح.");
                path = await BackupReader.Download(uri, token.Password); temporary = true;
            }
            else
            {
                var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".zip"); InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                var file = await picker.PickSingleFileAsync();
                if (file == null) { StatusText.Text = "لم تُختر نسخة."; return; }
                path = file.Path;
            }
            var loaded = await Task.Run(() => BackupReader.Read(path));
            data = loaded;
            MonthPicker.ItemsSource = loaded.Manifest.Months;
            MonthPicker.SelectedIndex = loaded.Manifest.Months.Length > 0 ? 0 : -1;
            StatusText.Text = "تم فتح النسخة للقراءة فقط · أعد تنزيلها للحصول على أحدث البيانات";
            UpdateHome(); SearchChanged(null!, null!);
        }
        catch (Exception ex)
        {
            StatusText.Text = data == null ? "لم تُفتح نسخة." : "تعذّر فتح النسخة الجديدة؛ تبقى البيانات السابقة معروضة.";
            var message = ex is HttpRequestException ? "تعذّر الاتصال بالسيرفر أو لم يقبل رمز الوصول." : ex.Message;
            await new ContentDialog { Title = "تعذّر فتح النسخة", Content = message, CloseButtonText = "حسناً", XamlRoot = Content.XamlRoot }.ShowAsync();
        }
        finally { token.Password = ""; if (temporary && path != null) { try { File.Delete(path); } catch { } } }
    }
    private void MonthChanged(object sender, SelectionChangedEventArgs e) { UpdateHome(); SearchChanged(null!, null!); }
    private void UpdateHome()
    {
        if (data == null || MonthPicker.SelectedItem is not string month) return;
        var employees = data.Employees.Where(x => x.Month == month).ToList();
        NetText.Text = employees.Sum(x => x.Net ?? 0).ToString("N0") + " د.ع";
        CountText.Text = $"عدد الموظفين: {employees.Count:N0}";
        var counts = new int[4]; foreach (var employee in employees) counts[Category(employee.Rank ?? "")]++;
        CategoriesText.Text = $"ضباط {counts[0]:N0}   ·   موظفون {counts[1]:N0}   ·   مراتب {counts[2]:N0}   ·   عقود {counts[3]:N0}";
    }
    private async void SearchChanged(object sender, TextChangedEventArgs e)
    {
        searchCancellation?.Cancel();
        if (data == null || MonthPicker.SelectedItem is not string month || string.IsNullOrWhiteSpace(SearchBox.Text)) { SearchResults.ItemsSource = null; return; }
        var source = new CancellationTokenSource();
        searchCancellation = source;
        var snapshot = data;
        var q = Normalize(SearchBox.Text);
        try
        {
            await Task.Delay(250, source.Token);
            var results = await Task.Run(() => {
                var people = snapshot.Employees.Where(x => x.Month == month && Normalize(x.Name ?? "").Contains(q)).Take(100)
                    .Select(x => $"{x.Name}   ·   {x.Rank}   ·   {x.Workplace}   ·   صافي: {(x.Net.HasValue ? x.Net.Value.ToString("N0") : "غير متوفر")}");
                var entities = snapshot.Deductions.Where(x => x.Month == month && Normalize(x.Title ?? "").Contains(q)).GroupBy(x => x.Title).Take(100)
                    .Select(g => $"جهة: {g.Key}   ·   مجموع الاستقطاع: {g.Sum(x => x.Amount ?? 0):N0}   ·   عدد السجلات: {g.Count():N0}");
                return people.Concat(entities).ToList();
            }, source.Token);
            if (source.IsCancellationRequested) return;
            SearchResults.ItemsSource = results;
            SearchStatus.Text = "النتائج من الشهر المحدد؛ تظهر أول 100 نتيجة لكل نوع.";
        }
        catch (OperationCanceledException) { }
    }
    private static int Category(string rank)
    {
        var key = string.Join(" ", Normalize(rank).Replace('.', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (key.Split(' ').Any(w => w == "عقد" || w == "عقود")) return 3;
        if (key.StartsWith("موظف")) return 1;
        string[] officers = ["لواء", "فريق", "فريق اول", "عميد", "عقيد", "مقدم", "رائد", "نقيب", "ملازم اول", "م اول", "ملازم"];
        return officers.Contains(key) ? 0 : 2;
    }
    private static string Normalize(string value)
    {
        var chars = new System.Text.StringBuilder();
        foreach (var c in value.ToLowerInvariant())
        {
            if ((c >= '\u064b' && c <= '\u065f') || c == '\u0670' || c == '\u0640') continue;
            chars.Append(c switch { 'آ' or 'أ' or 'إ' or 'ٱ' => 'ا', 'ة' => 'ه', 'ى' or 'ئ' => 'ي', 'ؤ' => 'و', _ => c });
        }
        return string.Join(" ", chars.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
