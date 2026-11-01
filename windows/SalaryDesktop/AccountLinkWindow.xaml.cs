using System.Windows;
using System.Windows.Controls;
using SalaryDesktop.Core;

namespace SalaryDesktop;

public partial class AccountLinkWindow : Window
{
    private readonly IReadOnlyList<ExecutionAccount> accounts;
    public ExecutionAccount? SelectedAccount { get; private set; }
    public AccountLinkWindow(TransferEntry entry, IReadOnlyList<ExecutionAccount> accounts)
    {
        InitializeComponent(); this.accounts = accounts;
        SourceTitle.Text = "جهة الاستقطاع: " + entry.Title;
        Accounts.ItemsSource = accounts.OrderBy(a => a.Entity).ToArray();
        if (entry.Account is not null) Accounts.SelectedItem = entry.Account;
    }
    private void Filter(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized || accounts is null) return;
        var words = PayrollAnalysis.Normalize(Search.Text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Accounts.ItemsSource = accounts.Where(a => { var text = PayrollAnalysis.Normalize($"{a.Entity} {a.Branch} {a.Iban} {a.Swift}"); return words.All(text.Contains); }).OrderBy(a => a.Entity).ToArray();
    }
    private void Choose(object sender, RoutedEventArgs e)
    {
        if (Accounts.SelectedItem is not ExecutionAccount account) { Ui.Message(this, "اختر حساب الجهة أولاً.", "ربط الحساب", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        SelectedAccount = account; DialogResult = true;
    }
}
