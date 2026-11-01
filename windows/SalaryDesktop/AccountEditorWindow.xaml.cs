using System.Windows;
using SalaryDesktop.Core;
namespace SalaryDesktop;
public partial class AccountEditorWindow : Window
{
    private readonly ExecutionAccount original;
    public ExecutionAccount? Result { get; private set; }
    public AccountEditorWindow(ExecutionAccount account)
    {
        InitializeComponent(); original = account; Entity.Text = account.Entity; Branch.Text = account.Branch; Iban.Text = account.Iban; Swift.Text = account.Swift; Local.Text = account.LocalAccount ?? "";
    }
    private void Save(object sender, RoutedEventArgs e)
    {
        Result = original with { Branch = Branch.Text, Iban = Iban.Text, Swift = Swift.Text, LocalAccount = Local.Text }; DialogResult = true;
    }
}
