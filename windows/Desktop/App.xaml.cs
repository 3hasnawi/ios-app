using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Input;

namespace AnbarPayroll.Desktop;
public partial class App : Application
{
    private Mutex? mutex;
    static void CloseOnEscape(object sender, KeyEventArgs e)
    {
        var window = (Window)sender;
        if (e.Key != Key.Escape || e.KeyboardDevice.Modifiers != ModifierKeys.None || window.Owner == null || !window.IsActive)
            return;
        e.Handled = true;
        // نغلق بعد انتهاء حدث لوحة المفاتيح حتى لا نتخلص من عارض PDF أثناء معالجته للحدث.
        if (!e.IsRepeat)
            window.Dispatcher.InvokeAsync(() => {
                if (window.IsActive && window.IsEnabled && window.IsVisible) window.Close();
            });
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        EventManager.RegisterClassHandler(typeof(Window),FrameworkElement.LoadedEvent,new RoutedEventHandler(WindowPlacement.OnLoaded));
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.TextBox),System.Windows.Controls.TextBox.TextChangedEvent,
            new System.Windows.Controls.TextChangedEventHandler(Appearance.InputDigits));
        EventManager.RegisterClassHandler(typeof(Window),System.Windows.Input.Mouse.PreviewMouseWheelEvent,
            new System.Windows.Input.MouseWheelEventHandler(PrecisionScroll.VerticalWheel));
        EventManager.RegisterClassHandler(typeof(Window),Keyboard.KeyDownEvent,new KeyEventHandler(CloseOnEscape));
        bool capture=e.Args.Length==3&&e.Args[0]=="--capture";
        string root=capture?e.Args[1]:Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AnbarPayroll");
        if(!capture){mutex=new Mutex(true,@"Local\AnbarPayroll.Desktop",out var created);if(!created){MessageBox.Show("برنامج الحسابات مفتوح بالفعل.");Shutdown();return;}}
        var window=new MainWindow(new Store(root));MainWindow=window;
        if(capture)window.Loaded+=async (_,_)=>{try{await window.Capture(e.Args[2]);Shutdown(0);}catch(Exception ex){File.WriteAllText(Path.Combine(e.Args[2],"capture-error.txt"),ex.ToString());Shutdown(1);}};
        window.Show();
        if(!capture)window.StartUpdateChecks();
    }
}
