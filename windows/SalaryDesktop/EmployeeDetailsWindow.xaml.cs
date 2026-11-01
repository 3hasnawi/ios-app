using System.Globalization;
using System.Text.Json;
using System.Windows;
using SalaryDesktop.Core;

namespace SalaryDesktop;

public partial class EmployeeDetailsWindow : Window
{
    private sealed record DetailLine(string Section, string Key, string Value);
    public EmployeeDetailsWindow(Employee employee, BackupSnapshot? source = null)
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
        if (source is not null)
        {
            string Key(string name) => PayrollAnalysis.Normalize(name).Replace(" ", "", StringComparison.Ordinal);
            var key = Key(employee.Name);
            if (source.Employees.Count(e => e.Month == employee.Month && Key(e.Name) == key) != 1)
                problems.Add("الاسم مكرر؛ لم تُربط الاستقطاعات تلقائياً");
            else
            {
                foreach (var deduction in source.Deductions.Where(d => d.Month == employee.Month && Key(d.Name) == key))
                {
                    var amount = deduction.Amount?.ToString("#,0.##", CultureInfo.GetCultureInfo("en-US")) ?? "غير محتسب";
                    var note = deduction.Note;
                    if (PayrollAnalysis.IsInactive(deduction))
                    {
                        var end = note.IndexOf('⟧'); var status = end >= 0 ? note["⟦حالة الاستقطاع:".Length..end] : "موقوف";
                        note = status + (end >= 0 ? note[(end + 1)..] : "");
                    }
                    rows.Add(new("استقطاعات جهات التنفيذ", deduction.Title, amount + (string.IsNullOrWhiteSpace(note) ? "" : " · " + note.Trim())));
                }
            }
        }
        DetailTable.ItemsSource = rows;
        StatusText.Text = problems.Count == 0 ? "عرض محلي للقراءة فقط." : string.Join(" · ", problems) + "؛ راجع البيانات.";
    }
}
