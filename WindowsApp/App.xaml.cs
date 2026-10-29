using Microsoft.UI.Xaml;
namespace SalaryWindows;
public partial class App : Application
{
    private Window? window;
    private static void Diagnostic(string message)
    {
        var path = Environment.GetEnvironmentVariable("SALARY_STARTUP_DIAGNOSTIC");
        if (string.IsNullOrWhiteSpace(path)) return;
        try { File.AppendAllText(path, message + Environment.NewLine); } catch { }
    }
    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Diagnostic("Unhandled: " + args.ExceptionObject);
        UnhandledException += (_, args) => Diagnostic("XAML: " + args.Exception);
        Diagnostic("Before application initialization");
        InitializeComponent();
        Diagnostic("After application initialization");
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try { var source = Environment.GetEnvironmentVariable("SALARY_XAML_DIAG_PATH"); if (!string.IsNullOrEmpty(source)) XamlDiagnostic.Run(source, Diagnostic); Diagnostic("Before window initialization"); window = new MainWindow(); Diagnostic("Before window activation"); window.Activate(); Diagnostic("Window activated"); }
        catch (Exception e) { Diagnostic("Launch error: " + e); throw; }
    }
}
