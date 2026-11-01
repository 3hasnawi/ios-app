using System.Windows;
using SalaryDesktop.Core;
namespace SalaryDesktop;
public partial class AccountImportReviewWindow : Window
{
    public AccountImportReviewWindow(AccountImportPreview preview)
    {
        InitializeComponent(); Accounts.ItemsSource = preview.Accounts; Problems.ItemsSource = preview.Problems;
        Apply.IsEnabled = preview.Accounts.Count > 0 && preview.Problems.Count == 0;
        Notice.Text = preview.Problems.Count == 0 ? $"{preview.Accounts.Count:N0} حساب صالح. راجع البيانات قبل الحفظ؛ لن تُحذف الحسابات السابقة." : $"توجد {preview.Problems.Count:N0} مشكلة. صحح ملف Excel وأعد استيراده؛ لن يُحفظ أي تغيير حتى تصحيح جميع الأخطاء.";
    }
    private void Save(object sender, RoutedEventArgs e) => DialogResult = true;
}
