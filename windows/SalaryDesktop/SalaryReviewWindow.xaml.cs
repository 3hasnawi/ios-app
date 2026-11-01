using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SalaryDesktop.Core;

namespace SalaryDesktop;

public partial class SalaryReviewWindow : Window
{
    private sealed record Row(string Kind, string Name, string Detail, string Before, string After);
    private readonly SalaryReviewResult result;
    // نص البحث الموحّد يُحسب مرة واحدة لكل ملاحظة بدل كل ضغطة مفتاح.
    private readonly string[] keys;
    private readonly DispatcherTimer delay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    public SalaryReviewWindow(SalaryReviewResult result)
    {
        InitializeComponent(); this.result = result;
        keys = result.Rows.Select(r => PayrollAnalysis.Normalize(r.Name + " " + r.Detail)).ToArray();
        Heading.Text = "تدقيق أولي · " + result.Month;
        Notes.Text = string.Join("\n", result.Notes);
        delay.Tick += (_, _) => { delay.Stop(); ApplyFilter(); };
        KindPicker.ItemsSource = new[] { "كل الملاحظات" }.Concat(result.Rows.Select(r => r.Kind).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)).ToArray();
        KindPicker.SelectedIndex = 0; ApplyFilter(); Closed += (_, _) => delay.Stop();
    }
    private void FilterChanged(object sender, SelectionChangedEventArgs e) { if (IsInitialized) ApplyFilter(); }
    private void SearchChanged(object sender, TextChangedEventArgs e) { if (IsInitialized) { delay.Stop(); delay.Start(); } }
    private void ApplyFilter()
    {
        if (Rows is null || Count is null || result is null || keys is null) return;
        var kind = KindPicker.SelectedItem as string ?? "كل الملاحظات"; var query = PayrollAnalysis.Normalize(Search.Text);
        string Display(decimal? n) => n?.ToString("N0", CultureInfo.GetCultureInfo("en-US")) ?? "—";
        var filtered = result.Rows.Where((r, i) => (kind == "كل الملاحظات" || r.Kind == kind) && (query.Length == 0 || keys[i].Contains(query, StringComparison.Ordinal))).Select(r => new Row(r.Kind, r.Name, r.Detail, Display(r.Before), Display(r.After))).ToArray();
        Rows.ItemsSource = filtered; Count.Text = $"المعروض: {filtered.Length:N0} · الموظفون: {result.EmployeeCount:N0}";
    }
}
