using System.Globalization;
using System.Windows;
using SalaryDesktop.Core;

namespace SalaryDesktop;

public partial class ContractOverrideWindow : Window
{
    public decimal? Result { get; private set; }
    public ContractOverrideWindow(BankSummary summary)
    {
        InitializeComponent();
        MonthText.Text = summary.Month;
        CalculatedText.Text = summary.CalculatedContractContribution.ToString("N0", CultureInfo.GetCultureInfo("en-US")) + " د.ع";
        WarningText.Text = summary.ContractOverrideStale ? "تغير أساس العقود منذ الحفظ السابق؛ راجع المبلغ وأعد اعتماده." : "";
        AmountText.Text = (summary.ManualContractOverride?.Total ?? summary.CalculatedContractContribution).ToString("N0", CultureInfo.GetCultureInfo("en-US"));
        Loaded += (_, _) => { AmountText.Focus(); AmountText.SelectAll(); };
    }
    private void Save(object sender, RoutedEventArgs e)
    {
        var amount = PayrollSpreadsheet.ParseMoney(AmountText.Text);
        if (amount is null || amount < 0 || amount > 9_007_199_254_740_991m || amount != decimal.Truncate(amount.Value))
        { StatusText.Text = "أدخل مبلغاً صحيحاً غير سالب بالدينار."; return; }
        Result = amount; DialogResult = true;
    }
    private void Reset(object sender, RoutedEventArgs e) { Result = null; DialogResult = true; }
}
