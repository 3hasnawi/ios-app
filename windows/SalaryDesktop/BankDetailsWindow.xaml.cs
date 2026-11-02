using System.Globalization;
using System.IO;
using System.Windows;
using SalaryDesktop.Core;
namespace SalaryDesktop;
public sealed record BankDetailsInput(string Name, string Iban, BankManualAmounts Amounts, string Branch, string LocalAccount);
/// بيانات الحساب (الاسم والفرع والحساب المحلي وIBAN كما في الآيفون) ومبالغ الشهر اليدوية؛ focus يضع المؤشر على المبلغ المطلوب تعديله.
public partial class BankDetailsWindow : Window
{
    public BankDetailsInput? Result { get; private set; }
    public BankDetailsWindow(string month, BankAccountDetails account, BankManualAmounts amounts, string? focus = null)
    {
        InitializeComponent(); Heading.Text = "بيانات الحساب والمبالغ اليدوية"; ManualTitle.Text = "المبالغ اليدوية · " + month;
        PayerText.Text = account.Name; BranchText.Text = account.Branch; LocalText.Text = account.LocalAccount; IbanText.Text = account.Iban;
        var numbers = CultureInfo.GetCultureInfo("en-US");
        LeaveText.Text = amounts.AccumulatedLeave.ToString("N0", numbers); RewardsText.Text = amounts.EmployeeRewards.ToString("N0", numbers); SingleText.Text = amounts.SingleList.ToString("N0", numbers);
        var target = focus switch { "leave" => LeaveText, "rewards" => RewardsText, "single" => SingleText, _ => PayerText };
        Loaded += (_, _) => { target.Focus(); target.SelectAll(); };
    }
    private static decimal Amount(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var amount = PayrollSpreadsheet.ParseMoney(text);
        if (amount is null || amount < 0 || amount != decimal.Truncate(amount.Value)) throw new InvalidDataException("أدخل المبالغ بالدينار كأرقام صحيحة وغير سالبة.");
        return amount.Value;
    }
    private void Save(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = PayerText.Text.Trim(); var iban = TransferPreparation.NormalizeAccount(IbanText.Text);
            if (name.Length == 0 || name.Length > 200 || name.Any(c => char.IsControl(c) || c == '|') || !TransferPreparation.ValidIban(iban)) throw new InvalidDataException("راجع اسم الحساب وIBAN العراقي الكامل.");
            if (BranchText.Text.Trim().Length == 0) throw new InvalidDataException("اكتب فرع المصرف.");
            Result = new(name, iban, new BankManualAmounts(Amount(LeaveText.Text), Amount(RewardsText.Text), Amount(SingleText.Text)), BranchText.Text.Trim(), LocalText.Text.Trim());
            DialogResult = true;
        }
        catch (InvalidDataException ex) { Status.Text = ex.Message; }
    }
}
