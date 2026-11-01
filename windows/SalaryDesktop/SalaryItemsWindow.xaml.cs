using System.IO;
using System.Windows;
using System.Windows.Controls;
using SalaryDesktop.Core;
namespace SalaryDesktop;
public partial class SalaryItemsWindow : Window
{
    private readonly BackupSnapshot source; private readonly string month; private int request; private bool closed;
    public SalaryItemsWindow(BackupSnapshot source, string month, IReadOnlyList<SalaryItemGroup> groups)
    {
        InitializeComponent(); this.source = source; this.month = month; Heading.Text = "بنود الرواتب · " + month; Groups.ItemsSource = groups;
        Status.Text = groups.Count == 0 ? "لا توجد بنود محفوظة لهذا الشهر." : "اختر بنداً لعرض تفاصيله."; Closed += (_, _) => { closed = true; request++; };
    }
    private async void GroupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Groups.SelectedItem is not SalaryItemGroup group || Rows is null) return;
        var ticket = ++request; Rows.ItemsSource = null; Status.Text = "جارٍ مراجعة تفاصيل البند…";
        try
        {
            var rows = await Task.Run(() => SalaryItemReports.Rows(source, month, group));
            if (closed || request != ticket) return;
            Rows.ItemsSource = rows; Status.Text = $"{rows.Count:N0} سجل · المجموع يطابق ملخص البند.";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException)
        { if (!closed && request == ticket) Status.Text = "تعذّر عرض البند: " + ex.Message; }
    }
}
