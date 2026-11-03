using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace SalaryDesktop;

/// يلتقط الأخطاء غير المتوقعة حتى لا يُغلق البرنامج فجأة. عمليات الحفظ ذرية، فالخطأ يوقف العملية ولا يغيّر البيانات المحفوظة.
public partial class App : Application
{
    internal static string LogFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SalaryDesktop", "Logs");
    /// فحص الواجهة على GitHub لا يملك من يغلق نافذة الرسالة؛ يُكتب الخطأ في السجل فقط ويُعدّ.
    internal static bool Unattended => string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);
    public static int UnhandledCount { get; private set; }

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, e) => { Log(e.Exception); e.SetObserved(); };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) Log(ex); };
    }

    /// المظهر المحفوظ يُطبّق قبل فتح أي نافذة.
    private static Mutex? instance;
    private static EventWaitHandle? showSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        // نسخة واحدة فقط لكل مستخدم؛ تشغيله مرة ثانية ينشّط النافذة الموجودة.
        if (!Unattended)
        {
            instance = new Mutex(true, @"Local\SalaryDesktop.Instance", out var first);
            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SalaryDesktop.Show");
            if (!first) { showSignal.Set(); Environment.Exit(0); return; }
            ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.ShowFromTray()), null, Timeout.Infinite, false);
            // إيقاف ويندوز أو برنامج التثبيت يسمح بإغلاق البرنامج.
            SessionEnding += (_, _) => (MainWindow as MainWindow)?.AllowExit();
        }
        Themes.Apply(UiPreferences.Current.Theme ?? "light");
        AppFonts.Apply(UiPreferences.Current.Font, UiPreferences.Current.Digits);
        AppFonts.ApplyScale(UiPreferences.Current.InterfaceScale);
        DialogStyleHelper.AttachFluentDialogBehavior();
        ScreenFit.Register();
        SmoothWheel.Register();
        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        UnhandledCount++;
        var path = Log(e.Exception);
        e.Handled = true;
        if (Unattended) { Console.Error.WriteLine("Unhandled UI exception: " + e.Exception); return; }
        var text = "حدث خطأ غير متوقع وأُوقفت العملية. البيانات المحفوظة لم تتغير.\n\n" + e.Exception.Message +
            (path is null ? "" : "\n\nسُجّلت التفاصيل في:\n" + path);
        var owner = this.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? MainWindow;
        const MessageBoxOptions rtl = MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign;
        if (owner is { IsLoaded: true }) MessageBox.Show(owner, text, "تطبيق الرواتب", MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.OK, rtl);
        else MessageBox.Show(text, "تطبيق الرواتب", MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.OK, rtl);
    }

    /// سجل محلي بالأخطاء فقط (نوع الخطأ ومكانه)، بلا بيانات الرواتب.
    internal static string? Log(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(LogFolder);
            var path = Path.Combine(LogFolder, $"errors-{DateTime.Now:yyyy-MM}.log");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception}\n\n", new UTF8Encoding(false));
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
