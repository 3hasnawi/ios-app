using System.Globalization;
using System.IO;
using System.Windows;
using SalaryDesktop.Core;

namespace SalaryDesktop;

/// الكشوف الخاصة (صندوق الشهداء، عقد الإطعام، الرعاية الصحية) كما في صفحة البند في الآيفون:
/// بطاقة المجموع، ثم الملخص الخاص بالكشف (حسب الفئة / حسب المبلغ وتفاصيل النسبة / المبالغ الشاذة والعائلات)، ثم الأسماء،
/// مع معاينة الكشف المعتمد وتصديره Excel وPDF وطباعته.
public partial class SpecialReportWindow : Window
{
    public sealed record FundView(string Name, string Count, string Rate, string Total);
    public sealed record FoodView(string Count, string Amount, string Total);
    public sealed record LineView(string Label, string Value, FontWeight Weight, string Note = "");
    public sealed record NameView(string Index, string Rank, string Name, string Amount, string Extra);

    private readonly BackupSnapshot source; private readonly string month; private readonly string kind; private readonly SalaryItemGroup group;
    private static readonly CultureInfo Numbers = CultureInfo.GetCultureInfo("en-US");
    private static string Money(decimal value) => value.ToString("#,0.##", Numbers);

    public SpecialReportWindow(BackupSnapshot source, string month, string kind, SalaryItemGroup group)
    {
        InitializeComponent();
        this.source = source; this.month = month; this.kind = kind; this.group = group;
        Heading.Text = kind switch { "fund" => "صندوق الشهداء", "food" => "عقد الإطعام", _ => "الرعاية الصحية" };
        Title = Heading.Text + " · " + month;
        SubHeading.Text = $"{group.Title} · {month}";
        KindIcon.Text = kind switch { "fund" => "", "food" => "", _ => "" };
        Loaded += async (_, _) => await Load();
    }

    private async Task Load()
    {
        try
        {
            var people = await Task.Run(() => SalaryItemReports.Rows(source, month, group));
            TotalText.Text = Money(people.Sum(p => p.Amount));
            CountText.Text = $"{people.Count:N0} منتسب";
            switch (kind)
            {
                case "fund":
                {
                    var rows = await Task.Run(() => FundReport.Summary(source, month, group.Title));
                    FundRows.ItemsSource = rows.Select(r => new FundView(r.Category.Name, r.Count.ToString("N0", Numbers), Money(r.Category.Rate), Money(r.Total))).ToArray();
                    FundCount.Text = rows.Sum(r => r.Count).ToString("N0", Numbers); FundTotal.Text = Money(rows.Sum(r => r.Total));
                    FundCard.Visibility = Visibility.Visible;
                    ShowNames(people);
                    break;
                }
                case "food":
                {
                    var rows = await Task.Run(() => FoodContractReport.Rows(source, month, group.Title));
                    var total = rows.Sum(r => r.Total); var split = new FoodContractReport.Split(total);
                    FoodRows.ItemsSource = rows.Select(r => new FoodView(r.Count.ToString("N0", Numbers), Money(r.Amount), Money(r.Total))).ToArray();
                    FoodCount.Text = rows.Sum(r => r.Count).ToString("N0", Numbers); FoodTotal.Text = Money(total);
                    SplitRows.ItemsSource = new[]
                    {
                        new LineView("مجموع الاستقطاع", Money(split.Total), FontWeights.Normal),
                        new LineView("حصة صندوق الشهداء ٣٪", Money(split.Fund), FontWeights.Normal),
                        new LineView("بعد حسم حصة الصندوق", Money(split.AfterFund), FontWeights.Normal),
                        new LineView("ضريبة ١٫٨٪", Money(split.Tax), FontWeights.Normal),
                        new LineView("شركة عراق الجوال", Money(split.Company), FontWeights.SemiBold),
                    };
                    FoodCard.Visibility = SplitCard.Visibility = Visibility.Visible;
                    ShowNames(people);
                    break;
                }
                default:
                {
                    var lists = await Task.Run(() => HealthCareReport.Build(source, month, group.Title));
                    var irregular = lists.Irregular;
                    if (irregular.Count > 0)
                    {
                        IrregularTitle.Text = $"مبالغ لا تقبل القسمة على 3,500 ({irregular.Count:N0})";
                        IrregularRows.ItemsSource = irregular.Select(r => new LineView(r.Name, $"{Money(r.Amount)} ÷ 3,500 = {HealthCareReport.FamilyText(r.Amount)}", FontWeights.Normal, r.Rank)).ToArray();
                        IrregularCard.Visibility = Visibility.Visible;
                    }
                    var lines = new List<LineView>
                    {
                        new("المشتركون", lists.Subscribers.Count.ToString("N0", Numbers), FontWeights.SemiBold),
                        new("غير المشتركين (لهم رواتب)", lists.Others.Count.ToString("N0", Numbers), FontWeights.SemiBold),
                    };
                    foreach (var family in lists.Subscribers.GroupBy(r => HealthCareReport.Family(r.Amount)).OrderBy(g => g.Key))
                        lines.Add(new($"عائلة من {family.Key:N0} ({Money(family.Key * HealthCareReport.PerMember)})", family.Count().ToString("N0", Numbers), FontWeights.Normal));
                    HealthRows.ItemsSource = lines; HealthCard.Visibility = Visibility.Visible;
                    NamesTab.Header = $"المشتركون ({lists.Subscribers.Count:N0})"; OthersTab.Header = $"غير المشتركين ({lists.Others.Count:N0})"; OthersTab.Visibility = Visibility.Visible;
                    ExtraColumn.Header = "عدد افراد الاسرة";
                    Names.ItemsSource = lists.Subscribers.Select((r, i) => new NameView((i + 1).ToString(Numbers), r.Rank, r.Name, Money(r.Amount), HealthCareReport.FamilyText(r.Amount))).ToArray();
                    Others.ItemsSource = lists.Others.Select((r, i) => new NameView((i + 1).ToString(Numbers), r.Rank, r.Name, "", "")).ToArray();
                    if (irregular.Count > 0) Notice.Text = $"{irregular.Count:N0} مبلغ لا يقبل القسمة على 3,500؛ راجعها قبل الاعتماد.";
                    break;
                }
            }
            Actions.IsEnabled = true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException)
        { Notice.Text = "تعذّر إعداد الكشف: " + ex.Message; }
        finally { Loading.Visibility = Visibility.Collapsed; }
    }

    private void ShowNames(IReadOnlyList<SalaryItemRow> people)
    {
        NamesTab.Header = $"الأسماء ({people.Count:N0})";
        Names.ItemsSource = people.Select((p, i) => new NameView((i + 1).ToString(Numbers), p.Rank, p.Name, Money(p.Amount), p.Workplace)).ToArray();
    }

    /// ملفات الكشف المعتمد بنفس منشئات الآيفون المنقولة.
    private (string Name, Func<byte[]> Excel, Func<ReportAssets, byte[]> Pdf) Files() => kind switch
    {
        "fund" => (FundReport.FileName(month), () => FundReport.Excel(FundReport.Summary(source, month, group.Title), FundReport.Title(source, month)),
            assets => FundReport.Pdf(FundReport.Summary(source, month, group.Title), FundReport.Title(source, month), assets, DateTime.Now)),
        "food" => (FoodContractReport.FileName(month), () => FoodContractReport.Excel(FoodContractReport.Rows(source, month, group.Title), FoodContractReport.Title(source, month)),
            assets => FoodContractReport.Pdf(FoodContractReport.Rows(source, month, group.Title), FoodContractReport.Title(source, month), assets, DateTime.Now)),
        _ => (HealthCareReport.FileName(month), () => HealthCareReport.Excel(HealthCareReport.Build(source, month, group.Title), HealthCareReport.Title(source, month, false), HealthCareReport.Title(source, month, true)),
            assets => HealthCareReport.Pdf(HealthCareReport.Build(source, month, group.Title), HealthCareReport.Title(source, month, false), HealthCareReport.Title(source, month, true), assets, DateTime.Now)),
    };
    private async void PreviewReport(object sender, RoutedEventArgs e) { var f = Files(); await Exports.Preview(this, f.Name, f.Pdf); }
    private async void ExportExcel(object sender, RoutedEventArgs e) { var f = Files(); await Exports.SaveExcel(this, f.Name, f.Excel); }
    private async void ExportPdf(object sender, RoutedEventArgs e) { var f = Files(); await Exports.SavePdf(this, f.Name, f.Pdf); }
    private async void PrintReport(object sender, RoutedEventArgs e) { var f = Files(); await Exports.Print(this, f.Name, f.Pdf); }
    private async void PreviewNames(object sender, RoutedEventArgs e) =>
        await Exports.Preview(this, $"قائمة {group.Title} - {month}", assets => ReportPdf.Generate([StandardReports.ItemList(source, month, group.Title)], assets, DateTime.Now));
}
