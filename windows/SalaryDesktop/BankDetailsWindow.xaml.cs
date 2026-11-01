using System.Globalization;
using System.IO;
using System.Windows;
using SalaryDesktop.Core;
namespace SalaryDesktop;
public sealed record BankDetailsInput(string Name, string Iban, BankManualAmounts Amounts);
public partial class BankDetailsWindow : Window
{
    public BankDetailsInput? Result { get; private set; }
    public BankDetailsWindow(string month, string payer, string iban, BankManualAmounts amounts)
    {
        InitializeComponent(); Heading.Text = "بيانات الحساب والمبالغ اليدوية · " + month; PayerText.Text = payer; IbanText.Text = iban;
        LeaveText.Text = amounts.AccumulatedLeave.ToString("N0", CultureInfo.GetCultureInfo("en-US")); RewardsText.Text = amounts.EmployeeRewards.ToString("N0", CultureInfo.GetCultureInfo("en-US")); SingleText.Text = amounts.SingleList.ToString("N0", CultureInfo.GetCultureInfo("en-US"));
    }
    private static decimal Amount(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var amount = PayrollSpreadsheet.ParseMoney(text);
        if (amount is null || amount != decimal.Truncate(amount.Value)) throw new InvalidDataException("أدخل المبالغ بالدينار كأرقام صحيحة وغير سالبة.");
        return amount.Value;
    }
    private void Save(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = PayerText.Text.Trim(); var iban = TransferPreparation.NormalizeAccount(IbanText.Text);
            if (name.Length == 0 || name.Length > 200 || name.Any(c => char.IsControl(c) || c == '|') || !TransferPreparation.ValidIban(iban)) throw new InvalidDataException("راجع اسم الجهة الدافعة وIBAN العراقي الكامل.");
            Result = new(name, iban, new BankManualAmounts(Amount(LeaveText.Text), Amount(RewardsText.Text), Amount(SingleText.Text))); DialogResult = true;
        }
        catch (InvalidDataException ex) { Ui.Message(this, ex.Message, "بيانات الحساب", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
