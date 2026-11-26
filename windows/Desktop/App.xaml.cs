using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace AnbarPayroll.Desktop;
public partial class App : Application
{
    private Mutex? mutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool capture=e.Args.Length==3&&e.Args[0]=="--capture";
        string root=capture?e.Args[1]:Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AnbarPayroll");
        if(!capture){mutex=new Mutex(true,@"Local\AnbarPayroll.Desktop",out var created);if(!created){MessageBox.Show("برنامج الحسابات مفتوح بالفعل.");Shutdown();return;}}
        var window=new MainWindow(new Store(root));MainWindow=window;
        if(capture)window.Loaded+=async (_,_)=>{try{await window.Capture(e.Args[2]);Shutdown(0);}catch(Exception ex){Console.Error.WriteLine(ex);Shutdown(1);}};
        window.Show();
    }
}
