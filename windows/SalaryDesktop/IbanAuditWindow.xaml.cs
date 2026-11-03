using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SalaryDesktop.Core;

namespace SalaryDesktop;
public partial class IbanAuditWindow : Window
{
    private readonly IbanAuditResult audit;
    public IbanAuditWindow(IbanAuditResult result)
    {
        InitializeComponent(); audit = result;
        Rows.ItemsSource = result.Rows;
        SummaryText.Text = $"{result.Month} · عدد الرواتب {result.Rows.Count:N0} · سليم حسابياً {result.Rows.Count(r => r.Status == IbanAuditStatus.Valid):N0} · يحتاج مراجعة {result.Rows.Count(r => r.Status is IbanAuditStatus.Invalid or IbanAuditStatus.Duplicate):N0} · بدون حساب {result.Rows.Count(r => r.Status == IbanAuditStatus.Missing):N0}";
    }
    private void FilterChanged(object sender, RoutedEventArgs e)
    {
        if (Rows?.ItemsSource is null || sender is not RadioButton { Tag: string filter }) return;
        var view = CollectionViewSource.GetDefaultView(Rows.ItemsSource);
        view.Filter = item => item is IbanAuditRow row && (filter switch { "Valid" => row.Status == IbanAuditStatus.Valid, "Warning" => row.Status is IbanAuditStatus.Invalid or IbanAuditStatus.Duplicate, "Missing" => row.Status == IbanAuditStatus.Missing, _ => true });
    }
}
