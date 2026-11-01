using System.Windows;

namespace SalaryDesktop;

/// تعيين كلمة سر البرنامج أو تغييرها؛ التغيير يتطلب الحالية.
public partial class PasswordWindow : Window
{
    private readonly bool change, verifyOnly;
    public string? Password { get; private set; }
    /// change: يتطلب كلمة السر الحالية؛ verifyOnly: تأكيد كلمة السر الحالية فقط (لإيقاف القفل).
    public PasswordWindow(bool change, bool verifyOnly = false)
    {
        InitializeComponent(); this.change = change || verifyOnly; this.verifyOnly = verifyOnly;
        if (!this.change) { CurrentLabel.Visibility = Visibility.Collapsed; Current.Visibility = Visibility.Collapsed; }
        if (verifyOnly) NewFields.Visibility = Visibility.Collapsed;
        Loaded += (_, _) => (this.change ? Current : First).Focus();
    }
    private void Save(object sender, RoutedEventArgs e)
    {
        if (change && !Locker.Check(Current.Password)) { Error.Text = "كلمة السر الحالية غير صحيحة."; return; }
        if (verifyOnly) { DialogResult = true; return; }
        if (First.Password.Length < 4) { Error.Text = "كلمة السر قصيرة؛ استعمل 4 أحرف أو أرقام على الأقل."; return; }
        if (First.Password != Second.Password) { Error.Text = "كلمتا السر غير متطابقتين."; return; }
        Password = First.Password; DialogResult = true;
    }
}
