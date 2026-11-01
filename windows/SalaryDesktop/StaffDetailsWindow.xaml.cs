using System.IO;
using System.Windows;
using SalaryDesktop.Core;
namespace SalaryDesktop;
public partial class StaffDetailsWindow : Window
{
    private sealed record Field(string Section, string Label, string Value);
    public StaffDetailsWindow(StaffMember member)
    {
        InitializeComponent(); NameText.Text = member.Name;
        IdentityText.Text = string.Join(" · ", new[] { member.Rank, member.Workplace, member.Status, member.Unified.Length > 0 ? "الموحد: " + member.Unified : "", member.Stat.Length > 0 ? "الإحصائي: " + member.Stat : "", member.Mother.Length > 0 ? "الأم: " + member.Mother : "" }.Where(x => !string.IsNullOrWhiteSpace(x)));
        try { var rows = StaffRecords.Sections(member).SelectMany(section => section.Fields.Select(field => new Field(section.Title, field.Label, field.Value))).ToArray(); Fields.ItemsSource = rows; Notice.Text = rows.Length == 0 ? "لا توجد تفاصيل إضافية محفوظة لهذا السجل." : $"{rows.Length:N0} حقل محفوظ."; }
        catch (InvalidDataException ex) { Notice.Text = ex.Message; }
    }
}
