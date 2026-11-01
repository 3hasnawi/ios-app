using System.Windows;
using SalaryDesktop.Core;
namespace SalaryDesktop;
public partial class TransferCategoryWindow : Window
{
    public TransferGroup SelectedGroup => Category.SelectedIndex switch { 0 => TransferGroup.Rafidain, 1 => TransferGroup.OtherBanks, 2 => TransferGroup.Anbar, _ => TransferGroup.Unresolved };
    public TransferCategoryWindow(TransferEntry entry)
    {
        InitializeComponent(); SourceTitle.Text = "الجهة: " + entry.Title + " · " + (entry.Account?.Branch ?? "حساب غير محفوظ");
        Category.SelectedIndex = entry.Group switch { TransferGroup.Rafidain => 0, TransferGroup.OtherBanks => 1, TransferGroup.Anbar => 2, _ => 3 };
    }
    private void Save(object sender, RoutedEventArgs e) => DialogResult = true;
}
