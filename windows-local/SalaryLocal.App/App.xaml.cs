using System.Windows;
namespace SalaryLocal.App;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) => { Ui.Message(null, "حدث خطأ غير متوقع. توقفت العملية؛ راجع البيانات قبل إعادة المحاولة.\n" + args.Exception.Message); args.Handled = true; };
        base.OnStartup(e);
    }
}
