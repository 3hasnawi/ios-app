using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Win32;
using Windows.Security.Credentials.UI;

namespace AnbarPayroll.Desktop;

// يبقى اختيار القفل للحساب الحالي على هذه الحاسبة، خارج مجلد البيانات والنسخة المحمولة.
internal sealed class DeviceLock
{
    const string Key=@"HKEY_CURRENT_USER\Software\AnbarPayroll", Value="DeviceLockSeconds";
    internal static readonly int[] Delays=[-1,0,60,300,900];
    internal int Delay {get;private set;}
    readonly Dictionary<Window,(UIElement Body,Visibility Visibility,string Title)> covered=[];
    Window? gate;
    bool verifying, foreground, running, forced;
    long? leftAt;
    int session;

    readonly Func<Window,Task<string?>> verify;
    readonly Action<int> save;
    public DeviceLock():this(ReadDelay(),Verify,seconds=>Registry.SetValue(Key,Value,seconds,RegistryValueKind.DWord)) {}
    internal DeviceLock(int delay,Func<Window,Task<string?>> verify,Action<int> save)
    {
        Delay=delay;
        this.verify=verify;
        this.save=save;
    }

    static int ReadDelay()
    {
        var stored=Registry.GetValue(Key,Value,null);
        return stored switch {null=>-1,int n when Delays.Contains(n)=>n,_=>throw new InvalidDataException("تعذرت قراءة إعداد قفل التطبيق.")};
    }

    static async Task<string?> Verify(Window owner)
    {
        if(!OperatingSystem.IsWindowsVersionAtLeast(10,0,22000))
            return "يتطلب قفل Windows Hello داخل التطبيق ويندوز 11 أو أحدث.";
        try {
            var result=await UserConsentVerifierInterop.RequestVerificationForWindowAsync(
                new WindowInteropHelper(owner).EnsureHandle(),"تحقق برمز الجهاز PIN أو البصمة لفتح تطبيق الرواتب أو تغيير إعداد القفل.");
            return result switch {
                UserConsentVerificationResult.Verified=>null,
                UserConsentVerificationResult.Canceled=>"أُلغي التحقق.",
                UserConsentVerificationResult.NotConfiguredForUser or UserConsentVerificationResult.DeviceNotPresent=>
                    "أعدّ رمز PIN أو البصمة من إعدادات ويندوز ← الحسابات ← خيارات تسجيل الدخول.",
                UserConsentVerificationResult.DisabledByPolicy=>"عطّلت سياسة الجهاز التحقق عبر Windows Hello.",
                _=>"تعذّر التحقق. أعد المحاولة من Windows Hello."
            };
        }catch(Exception){return "تعذّر فتح Windows Hello. تحقّق من خيارات تسجيل الدخول في ويندوز.";}
    }

    public async Task<string?> Change(Window owner,int seconds)
    {
        if(!Delays.Contains(seconds))throw new ArgumentOutOfRangeException(nameof(seconds));
        if(gate!=null || verifying || covered.Count!=0)return "افتح التطبيق أولاً.";
        verifying=true;
        int started=session;
        try {
            var error=await verify(owner);
            if(error!=null || started!=session)return error??"أعد التحقق بعد فتح جلسة ويندوز.";
            save(seconds);
            Delay=seconds;
            forced=false;
            if(foreground || seconds<0)Reveal();
            else Conceal();
            return null;
        }finally{
            verifying=false;
            ResumeSoon();
        }
    }

    public bool Start()
    {
        var app=Application.Current;
        SystemEvents.SessionSwitch+=SessionChanged;
        app.Exit+=(_,_)=>SystemEvents.SessionSwitch-=SessionChanged;
        if(Delay>=0 && !Prompt(null))return false;
        foreground=true;
        app.Deactivated+=(_,_)=>LeftApplication();
        app.Activated+=(_,_)=>ReturnedToApplication();
        EventManager.RegisterClassHandler(typeof(Window),FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender,e)=>{
                if(sender is Window w && ReferenceEquals(e.OriginalSource,w) && leftAt.HasValue)Cover(w);
            }));
        running=true;
        return true;
    }

    internal void LeftApplication()
    {
        foreground=false;
        if(Delay>=0)Conceal();
    }

    internal void ReturnedToApplication()
    {
        foreground=true;
        ResumeSoon();
    }

    void SessionChanged(object sender,SessionSwitchEventArgs args)
    {
        if(args.Reason!=SessionSwitchReason.SessionLock)return;
        Application.Current?.Dispatcher.InvokeAsync(()=>{
            session++;
            if(Delay>=0){forced=true;Conceal();}
        });
    }

    void Cover(Window window)
    {
        if(window==gate || covered.ContainsKey(window) || window.Content is not UIElement body)return;
        covered.Add(window,(body,body.Visibility,window.Title));
        body.SetCurrentValue(UIElement.VisibilityProperty,Visibility.Hidden);
        window.SetCurrentValue(Window.TitleProperty,"تطبيق الرواتب مقفل");
    }

    void Conceal()
    {
        leftAt??=Stopwatch.GetTimestamp();
        foreach(Window w in Application.Current.Windows)Cover(w);
    }

    void Reveal()
    {
        foreach(var (window,saved) in covered){
            saved.Body.SetCurrentValue(UIElement.VisibilityProperty,saved.Visibility);
            window.SetCurrentValue(Window.TitleProperty,saved.Title);
        }
        covered.Clear();leftAt=null;
    }

    void ResumeSoon()
    {
        if(running && leftAt.HasValue)
            Application.Current.Dispatcher.InvokeAsync(Resume,System.Windows.Threading.DispatcherPriority.Input);
    }

    void Resume()
    {
        if(!foreground || verifying || gate!=null || leftAt is not {} since)return;
        if(Delay>=0 && (forced || Stopwatch.GetElapsedTime(since)>=TimeSpan.FromSeconds(Delay))){
            var windows=Application.Current.Windows.OfType<Window>();
            var owner=windows.FirstOrDefault(w=>w.IsActive)??windows.LastOrDefault(w=>w.IsVisible && w.IsEnabled);
            if(owner==null)return;
            if(!Prompt(owner)){Application.Current.Shutdown();return;}
        }
        forced=false;
        if(foreground)Reveal();
    }

    bool Prompt(Window? owner)
    {
        var message=new TextBlock{Text="استخدم رمز الجهاز PIN أو البصمة المسجّلة في Windows Hello.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,16)};
        var unlock=new Button{Content="فتح باستخدام Windows Hello",IsDefault=true,Padding=new Thickness(14,8,14,8)};
        var panel=new StackPanel{Margin=new Thickness(20)};
        panel.Children.Add(new TextBlock{Text="تطبيق الرواتب مقفل",FontSize=20,FontWeight=FontWeights.Bold});
        panel.Children.Add(message);panel.Children.Add(unlock);
        var dialog=new Window{Title="قفل التطبيق",Owner=owner,Width=360,SizeToContent=SizeToContent.Height,
            ResizeMode=ResizeMode.NoResize,WindowStartupLocation=owner==null?WindowStartupLocation.CenterScreen:WindowStartupLocation.CenterOwner,Content=panel};
        gate=dialog;
        bool approved=false;
        unlock.Click+=async (_,_)=>{
            unlock.IsEnabled=false;
            int started=session;
            var error=await verify(dialog);
            if(!dialog.IsVisible)return;
            if(error==null && started==session){approved=true;dialog.DialogResult=true;return;}
            message.Text=error??"أعد التحقق بعد فتح جلسة ويندوز.";
            unlock.IsEnabled=true;
        };
        try {return dialog.ShowDialog()==true && approved;}
        finally{gate=null;}
    }
}
