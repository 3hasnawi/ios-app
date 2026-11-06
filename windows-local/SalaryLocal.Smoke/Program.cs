using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SalaryLocal.App;
using SalaryLocal.Core;
namespace SalaryLocal.Smoke;
internal static class Program
{
    private static readonly List<object> Captures = [];
    private static readonly List<string> Errors = [];
    private static string output = "", expectedDialog = "";
    private static MainWindow main = null!;
    private static bool completed;
    private static readonly DateTime Started = DateTime.UtcNow;
    [STAThread]
    private static int Main()
    {
        try
        {
            if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("SALARYLOCAL_UI_TEST") != "1" || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
                throw new InvalidOperationException("Requires Windows ephemeral GitHub Actions runner, GITHUB_ACTIONS=true and SALARYLOCAL_UI_TEST=1.");
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SalaryLocal");
            if (Directory.Exists(root) || File.Exists(root)) throw new InvalidOperationException("Refusing existing SalaryLocal profile; use a fresh ephemeral Windows runner.");
            output = Path.GetFullPath(Environment.GetEnvironmentVariable("SCREENSHOT_DIR") ?? throw new InvalidOperationException("SCREENSHOT_DIR required."));
            if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new InvalidOperationException("Screenshot destination must be empty.");
            Directory.CreateDirectory(output);
            var app = new SalaryLocal.App.App();
            app.InitializeComponent();
            // نحافظ على موارد التطبيق ومسار بدء التشغيل الحقيقي في تجميعة التطبيق.
            app.StartupUri = new Uri("/SalaryLocal;component/MainWindow.xaml", UriKind.Relative);
            app.DispatcherUnhandledException += (_, e) => { Errors.Add(e.Exception.ToString()); e.Handled = true; };
            var watcher = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            watcher.Tick += (_, _) =>
            {
                foreach (Window window in app.Windows.Cast<Window>().ToArray())
                {
                    if (window is MainWindow || !window.IsVisible || window.Title == expectedDialog) continue;
                    Errors.Add("Unexpected dialog: " + window.Title + " | " + string.Join(" | ", Descendants<TextBlock>(window).Select(t => t.Text)));
                    window.Close();
                }
                if ((DateTime.UtcNow - Started).TotalMinutes > 4) { Errors.Add("Global timeout."); WriteManifest(); Environment.Exit(1); }
            };
            app.Startup += (_, _) => app.Dispatcher.BeginInvoke(new Action(async () =>
            {
                try { watcher.Start(); await Run(); completed = true; }
                catch (Exception e) { Errors.Add(e.ToString()); }
                finally { watcher.Stop(); WriteManifest(); app.Shutdown(Errors.Count == 0 && completed ? 0 : 1); }
            }), DispatcherPriority.ApplicationIdle);
            return app.Run();
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    private static async Task Run()
    {
        await Until(() => Application.Current.MainWindow is MainWindow, "main window startup");
        main = (MainWindow)Application.Current.MainWindow;
        await Ready();
        var workspace = Field<object>("workspace");
        var store = (LocalPayrollStore)workspace.GetType().GetProperty("Store")!.GetValue(workspace)!;
        await Task.Run(() => Fixture.Seed(store));
        var fixtureRevision = store.GetStatus().RevisionId;
        await (Task)Invoke("Reload")!;
        await Ready();
        var nav = Field<StackPanel>("Navigation").Children.Cast<Button>().ToArray();
        if (nav.Length != 15) throw new InvalidOperationException("Expected all 15 navigation menus.");
        int number = 0;
        foreach (var button in nav)
        {
            button.BringIntoView(); button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Ready();
            if (Field<string>("section") != (string)button.Tag) throw new InvalidOperationException("Navigation mismatch.");
            if (button.Tag is "employees" or "deductions" && Field<DataGrid>("Table").Items.Count != 4) throw new InvalidOperationException("Missing synthetic table rows.");
            await Capture(main, $"{++number:00}-{button.Tag}", button.Content.ToString()!, false);
        }
        await Navigate("employees");
        Field<DataGrid>("Table").SelectedIndex = 0;
        await Dialog("16-person", "ملف المنتسب", () => Invoke("OpenDetails", main, null));
        await Navigate("bank");
        await Dialog("17-bank-amounts", "إضافات البنك", () => Click("إضافات البنك اليدوية"));
        await Dialog("18-contract-amount", "تعديل العقود", () => Click("مراجعة وتعديل مبلغ العقود"));
        await Dialog("19-payer", "بيانات الحساب الدافع", () => Click("الحساب الدافع"));
        await Navigate("reports");
        await Dialog("20-export-options", "التصدير والطباعة", () => Descendants<Button>(Field<StackPanel>("Page")).First(b => b.Content?.ToString()?.Contains("جهة تنفيذ وهمية TEST") == true).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)));
        await Navigate("home");
        await Ready();
        if (store.GetStatus().RevisionId != fixtureRevision) throw new InvalidOperationException("UI navigation unexpectedly changed stored data.");
    }
    private static async Task Navigate(string section)
    {
        Field<StackPanel>("Navigation").Children.Cast<Button>().Single(b => (string)b.Tag == section).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Ready();
    }
    private static void Click(string text) => Descendants<Button>(Field<StackPanel>("Page")).Single(b => b.Content?.ToString() == text).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static async Task Dialog(string id, string title, Action open)
    {
        expectedDialog = title;
        // تُشغّل النافذة الفعلية في دورة الموزع، ويستمر الالتقاط داخل دورة ShowDialog المتداخلة.
        var operation = main.Dispatcher.BeginInvoke(open);
        Window? dialog = null;
        try
        {
            await Until(() => (dialog = Application.Current.Windows.Cast<Window>().FirstOrDefault(w => w != main && w.Title == title && w.IsVisible)) != null, title);
            if (title == "ملف المنتسب") await Until(() => Descendants<TextBlock>(dialog!).Any(t => t.Text.StartsWith("جاهز • الربط", StringComparison.Ordinal)), "person detail ready");
            await Capture(dialog!, id, title, true);
            dialog!.Close();
            await operation.Task;
            await Ready();
        }
        finally { if (dialog?.IsVisible == true) dialog.Close(); expectedDialog = ""; }
    }
    private static async Task Ready()
    {
        await Until(() => Field<bool>("ready") && !Field<bool>("busy"), "ready/not busy");
        await Task.Delay(300);
        CheckErrors();
        if (!Field<TextBlock>("Status").Text.StartsWith("جاهز", StringComparison.Ordinal)) throw new InvalidOperationException("Non-ready status: " + Field<TextBlock>("Status").Text);
    }
    private static async Task Until(Func<bool> condition, string label)
    {
        var timer = Stopwatch.StartNew();
        while (!condition()) { CheckErrors(); if (timer.Elapsed.TotalSeconds > 35) throw new TimeoutException(label); await Task.Delay(75); }
        CheckErrors();
    }
    private static void CheckErrors() { if (Errors.Count > 0) throw new InvalidOperationException("Runtime error: " + Errors[0]); }
    private static T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
    private static object? Invoke(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, args);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T found) yield return found;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    private static async Task Capture(Window window, string id, string label, bool modal)
    {
        window.Activate(); window.UpdateLayout();
        await Task.Delay(450);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        CheckErrors();
        var file = Path.Combine(output, id + ".png");
        string method = "CopyFromScreen: actual Windows desktop window pixels", fallback = "";
        var handle = new WindowInteropHelper(window).Handle;
        try
        {
            if (GetForegroundWindow() != handle || !GetWindowRect(handle, out var rect)) throw new InvalidOperationException("No foreground desktop window.");
            var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
            if (rect.Left < bounds.Left || rect.Top < bounds.Top || rect.Right > bounds.Right || rect.Bottom > bounds.Bottom) throw new InvalidOperationException("Window extends beyond desktop.");
            using var bitmap = new System.Drawing.Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap)) graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size);
            var colors = new HashSet<int>();
            for (int y = 0; y < bitmap.Height; y += 12) for (int x = 0; x < bitmap.Width; x += 12) colors.Add(bitmap.GetPixel(x, y).ToArgb());
            if (colors.Count < 12) throw new InvalidOperationException("Desktop capture appears blank.");
            bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception e)
        {
            fallback = e.Message;
            method = "RenderTargetBitmap: actual running WPF visual tree, NOT a desktop screenshot; excludes OS window chrome";
            var visual = (FrameworkElement)window.Content;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(file); encoder.Save(stream);
        }
        Captures.Add(new { file = Path.GetFileName(file), label, method, fallbackReason = fallback, utc = DateTime.UtcNow, width = window.ActualWidth, height = window.ActualHeight, ready = Field<bool>("ready"), mainBusy = Field<bool>("busy"), modal, noRuntimeError = Errors.Count == 0, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))) });
        Console.WriteLine($"CAPTURE {id}: {label} | {method}");
        WriteManifest();
    }
    private static void WriteManifest()
    {
        if (output.Length == 0) return;
        var manifest = new { passed = completed && Errors.Count == 0, startedUtc = Started, finishedUtc = DateTime.UtcNow, sourceCommit = Environment.GetEnvironmentVariable("SOURCE_COMMIT") ?? Environment.GetEnvironmentVariable("GITHUB_SHA"), appVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString(), runtime = Environment.Version.ToString(), os = Environment.OSVersion.VersionString, syntheticOnly = true, fixture = "8 payroll, 8 deductions across two months; 4 staff; TEST invalid account; one archive PDF, trust entry and reconciliation period. No real inputs.", navigation = "Actual WPF Button.Click events; selected person via actual handler; App.InitializeComponent and StartupUri.", limitations = "No printing, exports saved to disk, bank submission, scanner hardware or production-data validation. Invalid TEST bank accounts intentionally produce audit warnings. Modal dialogs can leave mainBusy=true until dismissed. Fallback images are runtime WPF renders, not desktop screenshots.", captures = Captures, errors = Errors };
        File.WriteAllText(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }
}
