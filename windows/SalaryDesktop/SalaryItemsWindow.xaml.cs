using System.IO;
using System.Windows;
using System.Windows.Controls;
using SalaryDesktop.Core;
namespace SalaryDesktop;
public partial class SalaryItemsWindow : Window
{
    private readonly BackupSnapshot source; private readonly string month; private int request; private bool closed; private SalaryItemGroup? selected;
    public SalaryItemsWindow(BackupSnapshot source, string month, IReadOnlyList<SalaryItemGroup> groups, string? special = null)
    {
        InitializeComponent(); this.source = source; this.month = month; Heading.Text = "بنود الرواتب · " + month; Groups.ItemsSource = groups;
        Status.Text = groups.Count == 0 ? "لا توجد بنود محفوظة لهذا الشهر." : "اختر بنداً لعرض تفاصيله."; Closed += (_, _) => { closed = true; request++; };
        // فتح كشف خاص من الرئيسية: يُختار بنده مباشرة، أو تُعرض رسالة إن لم يوجد في الشهر.
        if (special is not null)
        {
            if (groups.FirstOrDefault(g => Special(g.Title) == special) is { } match) Groups.SelectedItem = match;
            else Status.Text = special switch { "fund" => "لا يوجد بند صندوق الشهداء", "health" => "لا يوجد بند الرعاية الصحية", _ => "لا يوجد بند عقد الإطعام" } + " في رواتب هذا الشهر.";
        }
    }
    private async void GroupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Groups.SelectedItem is not SalaryItemGroup group || Rows is null) return;
        var ticket = ++request; Rows.ItemsSource = null; Status.Text = "جارٍ مراجعة تفاصيل البند…";
        selected = null; ListPreview.IsEnabled = ListExcel.IsEnabled = ListPdf.IsEnabled = ListPrint.IsEnabled = false; SpecialPanel.Visibility = Visibility.Collapsed;
        try
        {
            var rows = await Task.Run(() => SalaryItemReports.Rows(source, month, group));
            if (closed || request != ticket) return;
            Rows.ItemsSource = rows; Status.Text = $"{rows.Count:N0} سجل · المجموع يطابق ملخص البند.";
            selected = group; ListPreview.IsEnabled = ListExcel.IsEnabled = ListPdf.IsEnabled = ListPrint.IsEnabled = true;
            var special = Special(group.Title);
            SpecialPanel.Visibility = special is null ? Visibility.Collapsed : Visibility.Visible;
            SpecialTitle.Text = special switch { "fund" => "كشف صندوق الشهداء حسب الفئة:", "food" => "كشف عقد الإطعام:", "health" => "اشتراكات الرعاية الصحية:", _ => "" };
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException)
        { if (!closed && request == ticket) Status.Text = "تعذّر عرض البند: " + ex.Message; }
    }

    /// الكشوف الخاصة بأشكالها المعتمدة حسب اسم البند (مثل الآيفون).
    public static string? Special(string title) => FundReport.Applies(title) ? "fund" : FoodContractReport.Applies(title) ? "food" : HealthCareReport.Applies(title) ? "health" : null;
    private string ListName => selected is null ? "" : $"قائمة {selected.Title} - {month}";
    private async void PreviewList(object sender, RoutedEventArgs e) { if (selected is { } g) await Exports.Preview(this, ListName, assets => ReportPdf.Generate([StandardReports.ItemList(source, month, g.Title)], assets, DateTime.Now)); }
    private async void ExportListExcel(object sender, RoutedEventArgs e) { if (selected is { } g) await Exports.SaveExcel(this, ListName, () => XlsxReport.Generate(StandardReports.ItemList(source, month, g.Title))); }
    private async void ExportListPdf(object sender, RoutedEventArgs e) { if (selected is { } g) await Exports.SavePdf(this, ListName, assets => ReportPdf.Generate([StandardReports.ItemList(source, month, g.Title)], assets, DateTime.Now)); }
    private async void PrintList(object sender, RoutedEventArgs e) { if (selected is { } g) await Exports.Print(this, ListName, assets => ReportPdf.Generate([StandardReports.ItemList(source, month, g.Title)], assets, DateTime.Now)); }
    private (string Name, Func<byte[]> Excel, Func<ReportAssets, byte[]> Pdf)? SpecialFiles()
    {
        if (selected is not { } group) return null;
        var title = group.Title;
        return Special(title) switch
        {
            "fund" => (FundReport.FileName(month), () => FundReport.Excel(FundReport.Summary(source, month, title), FundReport.Title(source, month)),
                assets => FundReport.Pdf(FundReport.Summary(source, month, title), FundReport.Title(source, month), assets, DateTime.Now)),
            "food" => (FoodContractReport.FileName(month), () => FoodContractReport.Excel(FoodContractReport.Rows(source, month, title), FoodContractReport.Title(source, month)),
                assets => FoodContractReport.Pdf(FoodContractReport.Rows(source, month, title), FoodContractReport.Title(source, month), assets, DateTime.Now)),
            "health" => (HealthCareReport.FileName(month), () => HealthCareReport.Excel(HealthCareReport.Build(source, month, title), HealthCareReport.Title(source, month, false), HealthCareReport.Title(source, month, true)),
                assets => HealthCareReport.Pdf(HealthCareReport.Build(source, month, title), HealthCareReport.Title(source, month, false), HealthCareReport.Title(source, month, true), assets, DateTime.Now)),
            _ => null
        };
    }
    private void OpenSpecial(object sender, RoutedEventArgs e)
    {
        if (selected is { } group && Special(group.Title) is { } kind) new SpecialReportWindow(source, month, kind, group) { Owner = this }.ShowDialog();
    }
    private async void PreviewSpecial(object sender, RoutedEventArgs e) { if (SpecialFiles() is { } f) await Exports.Preview(this, f.Name, f.Pdf); }
    private async void ExportSpecialExcel(object sender, RoutedEventArgs e) { if (SpecialFiles() is { } f) await Exports.SaveExcel(this, f.Name, f.Excel); }
    private async void ExportSpecialPdf(object sender, RoutedEventArgs e) { if (SpecialFiles() is { } f) await Exports.SavePdf(this, f.Name, f.Pdf); }
    private async void PrintSpecial(object sender, RoutedEventArgs e) { if (SpecialFiles() is { } f) await Exports.Print(this, f.Name, f.Pdf); }
}
