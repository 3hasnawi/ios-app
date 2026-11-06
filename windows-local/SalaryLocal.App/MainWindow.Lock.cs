using System.IO;
using System.Windows;
using System.Windows.Controls;
using SalaryLocal.Core;

namespace SalaryLocal.App;

public partial class MainWindow
{
    private SessionLockUi? sessionLock;
    // يُستدعى بعد InitializeComponent وقبل إظهار النافذة، خارج النسخة الاحتياطية.
    private void InitializeSessionLock()
    {
        sessionLock = new SessionLockUi(this, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SalaryLocal", "session-lock.json"));
        sessionLock.Unlocked += () => { if (!ready && !busy) LoadedWindow(this, new RoutedEventArgs()); };
        Closed += (_, _) => sessionLock.Dispose();
    }
    private void AddSessionLockSettings(StackPanel target)
    {
        if (sessionLock is null) return;
        var panel = new StackPanel();
        panel.Children.Add(Ui.Text("قفل الجلسة المحلية", 21));
        panel.Children.Add(Ui.Text(LocalSessionLock.Warning));
        panel.Children.Add(Ui.Text("تُقفل عند التشغيل وبعد الخمول. القفل خاص بهذه الحاسبة ولا يُنقل مع النسخة. الملفات المفتوحة في برامج أخرى والمهام التي وصلت للطابعة خارج نطاقه. استخدم Win+L عند ترك الجهاز."));
        panel.Children.Add(Ui.Button("إعداد كلمة السر ومدة الخمول", () => sessionLock.ShowSettings()));
        panel.Children.Add(Ui.Button("قفل الآن", () => sessionLock.LockNow()));
        target.Children.Add(Ui.Card(panel));
    }
}
