using System.Globalization;
using System.Text.Json;
using System.Windows;
using SalaryDesktop.Core;

namespace SalaryDesktop;

public partial class EmployeeDetailsWindow : Window
{
    private sealed record DetailLine(string Section, string Key, string Value);
    public EmployeeDetailsWindow(Employee employee)
    {
        InitializeComponent();
        NameText.Text = employee.Name;
        var rows = new List<DetailLine>
        {
            new("البيانات الأساسية", "الشهر", employee.Month),
            new("البيانات الأساسية", "الرتبة", employee.Rank),
            new("البيانات الأساسية", "محل العمل", employee.Workplace),
            new("البيانات الأساسية", "الصافي", employee.Net?.ToString("#,0.##", CultureInfo.GetCultureInfo("en-US")) ?? "غير متاح")
        };
        var problems = new List<string>();
        if (employee.Details is not null)
        {
            try
            {
                var details = JsonSerializer.Deserialize<Dictionary<string, string>>(employee.Details) ?? throw new JsonException();
                rows.AddRange(details.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new DetailLine("التفاصيل", p.Key, p.Value)));
            }
            catch (JsonException) { problems.Add("تعذرت قراءة بعض تفاصيل الراتب"); }
        }
        if (employee.Items is not null)
        {
            try
            {
                var items = JsonSerializer.Deserialize<Dictionary<string, decimal>>(employee.Items) ?? throw new JsonException();
                rows.AddRange(items.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new DetailLine("بنود الراتب", p.Key, p.Value.ToString("#,0.##", CultureInfo.GetCultureInfo("en-US")))));
            }
            catch (JsonException) { problems.Add("تعذرت قراءة بعض بنود الراتب"); }
        }
        DetailTable.ItemsSource = rows;
        StatusText.Text = problems.Count == 0 ? "عرض محلي للقراءة فقط." : string.Join(" · ", problems) + "؛ راجع نسخة الآيفون.";
    }
}
