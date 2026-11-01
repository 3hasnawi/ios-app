using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SalaryDesktop.Core;

namespace SalaryDesktop;

/// كشف استقطاعات موظف لجهة واحدة عبر الأشهر (مثل «كشف الاستقطاعات» في الآيفون): اختيار الموظف ثم الجهة ثم الفترة،
/// وتفاصيل كل شهر ومجموعها، مع معاينة الكشف المعتمد PDF وحفظه وطباعته.
public partial class EmployeeDeductionWindow : Window
{
    public sealed record MonthOption(int Key, string Label);
    private readonly BackupSnapshot source;
    private readonly string? initialEntity;
    private IReadOnlyList<DeductionPerson>? people;
    private DeductionPerson? person;
    private EmployeeDeductionStatement? statement;
    private readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private int ticket;
    private bool syncing;

    public EmployeeDeductionWindow(BackupSnapshot source, DeductionPerson? person = null, string? entity = null)
    {
        InitializeComponent();
        this.source = source; initialEntity = entity;
        debounce.Tick += (_, _) => { debounce.Stop(); RunSearch(); };
        Loaded += async (_, _) =>
        {
            if (person is not null) await SelectPerson(person);
            else Query.Focus();
            people = await Task.Run(() => EmployeeDeductions.People(source));
            if (Query.Text.Length > 0) RunSearch();
        };
    }

    private void CloseWindow(object sender, RoutedEventArgs e) => Close();

    // ——— الموظف ———
    private void QueryChanged(object sender, TextChangedEventArgs e)
    {
        QueryHint.Visibility = Query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        debounce.Stop(); debounce.Start();
    }
    private void RunSearch()
    {
        var text = Query.Text.Trim();
        if (text.Length < 2) { Results.ItemsSource = null; Results.Visibility = Visibility.Collapsed; SearchStatus.Text = "اكتب اسم الموظف بوضوح لتضييق النتائج."; return; }
        if (people is null) { SearchStatus.Text = "جارٍ تجهيز قائمة الأسماء…"; return; }
        var found = EmployeeDeductions.Search(people, text);
        Results.ItemsSource = found;
        Results.Visibility = found.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchStatus.Text = found.Count == 0 ? "لا توجد نتائج." : found.Count >= 100 ? "أول ١٠٠ نتيجة؛ اكتب الاسم كاملاً لتضييقها." : "";
    }
    private async void ResultChosen(object sender, SelectionChangedEventArgs e)
    {
        if (Results.SelectedItem is DeductionPerson chosen) await SelectPerson(chosen);
    }
    private void ChangePerson(object sender, RoutedEventArgs e)
    {
        PersonCard.Visibility = Visibility.Collapsed; SearchPanel.Visibility = Visibility.Visible;
        Query.Focus(); Query.SelectAll();
    }

    private async Task SelectPerson(DeductionPerson chosen)
    {
        person = chosen;
        PersonName.Text = chosen.Name; PersonDetail.Text = chosen.Detail;
        PersonDetail.Visibility = chosen.Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PersonCard.Visibility = Visibility.Visible; SearchPanel.Visibility = Visibility.Collapsed;
        Results.SelectedItem = null;
        var previous = EntityPicker.SelectedItem as string ?? initialEntity;
        Clear();
        syncing = true;
        try { EntityPicker.ItemsSource = null; EntityPicker.IsEnabled = false; } finally { syncing = false; }
        var my = ++ticket;
        var entities = await Task.Run(() => EmployeeDeductions.Entities(source, chosen.Key));
        if (my != ticket) return;
        EntityPicker.ItemsSource = entities; EntityPicker.IsEnabled = entities.Count > 0;
        EntityStatus.Text = entities.Count == 0 ? "لا توجد جهات استقطاع لهذا الموظف." : $"{entities.Count:N0} جهة";
        // نفس الجهة السابقة إن وُجدت، أو الجهة الوحيدة.
        EntityPicker.SelectedItem = previous is not null && entities.Contains(previous) ? previous : entities.Count == 1 ? entities[0] : null;
    }

    // ——— الجهة والفترة ———
    private async void EntityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncing) return;
        Clear();
        syncing = true;
        try { FromPicker.ItemsSource = ToPicker.ItemsSource = null; FromPicker.IsEnabled = ToPicker.IsEnabled = false; } finally { syncing = false; }
        PeriodStatus.Text = "";
        if (person is not { } who || EntityPicker.SelectedItem is not string entity) return;
        var my = ++ticket;
        var available = await Task.Run(() => EmployeeDeductions.AvailableMonths(source, who.Key, entity));
        if (my != ticket) return;
        var options = available.Keys.Select(k => new MonthOption(k, EmployeeDeductions.Label(k))).ToArray();
        syncing = true;
        try
        {
            FromPicker.ItemsSource = options; ToPicker.ItemsSource = options;
            FromPicker.IsEnabled = ToPicker.IsEnabled = options.Length > 0;
            FromPicker.SelectedIndex = options.Length > 0 ? 0 : -1; ToPicker.SelectedIndex = options.Length - 1;
        }
        finally { syncing = false; }
        PeriodStatus.Text = (options.Length == 0 ? "لا توجد أشهر مفهومة لهذه الجهة. " : "")
            + (available.UnrecognizedCount > 0 ? "توجد أشهر بأسماء غير مفهومة لا تظهر في الاختيار؛ راجع أسماء الأشهر المستوردة." : "");
        await Compute();
    }

    private async void PeriodChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncing) return;
        syncing = true;
        try
        {
            // البداية لا تتجاوز النهاية: تتحرك الأخرى معها كما في الآيفون.
            if (FromPicker.SelectedItem is MonthOption from && ToPicker.SelectedItem is MonthOption to && from.Key > to.Key)
            {
                if (ReferenceEquals(sender, FromPicker)) ToPicker.SelectedItem = from; else FromPicker.SelectedItem = to;
            }
        }
        finally { syncing = false; }
        await Compute();
    }

    private void Clear()
    {
        ticket++;
        statement = null; Rows.ItemsSource = null; Warning.Text = ""; RowsCount.Text = "";
        TotalLabel.Text = "مجموع الاستقطاعات"; TotalText.Text = "—"; EntityText.Text = "";
        EmptyText.Visibility = Visibility.Visible; Spinner.Visibility = Visibility.Collapsed;
        PreviewButton.IsEnabled = PrintButton.IsEnabled = SaveButton.IsEnabled = false;
    }

    private async Task Compute()
    {
        Clear();
        if (person is not { } who || EntityPicker.SelectedItem is not string entity || FromPicker.SelectedItem is not MonthOption from || ToPicker.SelectedItem is not MonthOption to) return;
        var my = ticket;
        EmptyText.Visibility = Visibility.Collapsed; Spinner.Visibility = Visibility.Visible;
        try
        {
            var result = await Task.Run(() => EmployeeDeductions.Statement(source, who, entity, from.Key, to.Key));
            if (my != ticket) return;
            statement = result;
            Rows.ItemsSource = result.Rows;
            TotalLabel.Text = result.Incomplete ? "مجموع القيود المتوفرة" : "مجموع الاستقطاعات";
            TotalText.Text = SalaryReportPdf.Money(result.Total);
            EntityText.Text = result.Entity;
            SubHeading.Text = $"{result.Person.Name} · {from.Label} — {to.Label}";
            RowsCount.Text = $"{result.Rows.Count:N0} شهر";
            Warning.Text = result.Incomplete ? "بعض الأشهر غير مستوردة أو غير متحققة أو أسماؤها غير مفهومة؛ المجموع للقيود المتوفرة فقط." : "";
            PreviewButton.IsEnabled = PrintButton.IsEnabled = SaveButton.IsEnabled = true;
        }
        catch (InvalidDataException ex) { if (my == ticket) { Warning.Text = ex.Message; EmptyText.Visibility = Visibility.Visible; } }
        finally { if (my == ticket) Spinner.Visibility = Visibility.Collapsed; }
    }

    // ——— PDF ———
    private Func<ReportAssets, byte[]>? Maker()
    {
        if (statement is not { } report) return null;
        var logo = PersonWindow.HeaderLogo();
        return assets => EmployeeDeductionPdf.Generate(report, assets, logo, DateTime.Now);
    }
    private async void PreviewPdf(object sender, RoutedEventArgs e) { if (Maker() is { } make) await Exports.Preview(this, statement!.FileName, make); }
    private async void SavePdf(object sender, RoutedEventArgs e) { if (Maker() is { } make) await Exports.SavePdf(this, statement!.FileName, make); }
    private async void PrintPdf(object sender, RoutedEventArgs e) { if (Maker() is { } make) await Exports.Print(this, statement!.FileName, make); }
}
