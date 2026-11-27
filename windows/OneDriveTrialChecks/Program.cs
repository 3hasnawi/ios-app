using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AnbarPayroll.Desktop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var directory = Directory.CreateTempSubdirectory("onedrive-screen-");
        var window = new OneDriveTrialWindow(false, directory.FullName) { FlowDirection = FlowDirection.RightToLeft };
        window.ContentRendered += async (_, _) =>
        {
            int code = 1;
            try
            {
                var panel = (DockPanel)window.Content;
                var viewer = panel.Children.OfType<WebView2>().Single();
                var elapsed = Stopwatch.StartNew();
                const string ready = """
                    (() => {
                        const button = document.getElementById("login");
                        return !!button && button.textContent.includes("تسجيل الدخول") && !button.disabled
                            && button.getBoundingClientRect().width > 0
                            && document.getElementById("device").textContent.includes("ويندوز")
                            && document.getElementById("status").textContent.includes("سجّل الدخول");
                    })()
                    """;
                while (true)
                {
                    if (!viewer.IsVisible || elapsed.Elapsed > TimeSpan.FromSeconds(90))
                        throw new InvalidOperationException("No usable sign-in screen: " + panel.Children.OfType<TextBlock>().Single().Text);
                    if (viewer.CoreWebView2 != null && await viewer.ExecuteScriptAsync(ready) == "true") break;
                    await Task.Delay(200);
                }
                if (args.Length > 0)
                {
                    using var output = File.Create(args[0]);
                    await viewer.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,output);
                }
                Console.WriteLine("WebView2 rendered the enabled sign-in button and read local state through the native bridge.");
                code = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { window.Close(); application.Shutdown(code); }
        };
        return application.Run(window);
    }
}
