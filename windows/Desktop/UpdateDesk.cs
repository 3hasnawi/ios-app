using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AnbarPayroll.Desktop;

public partial class MainWindow
{
    static Version InstalledVersion{
        get{var v=typeof(MainWindow).Assembly.GetName().Version!;return new(v.Major,v.Minor,v.Build);}
    }
    static HttpClient UpdateClient(){
        var client=new HttpClient{MaxResponseContentBufferSize=8*1024*1024,Timeout=TimeSpan.FromSeconds(30)};
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AnbarPayroll/"+InstalledVersion);
        return client;
    }
    static readonly WindowsUpdates Updates=new(UpdateClient());
    readonly CancellationTokenSource updateLifetime=new();
    WindowsRelease? newerRelease;
    Task<WindowsRelease?>? checking;
    public void StartUpdateChecks()
    {
        var timer=new DispatcherTimer{Interval=TimeSpan.FromHours(6)};
        timer.Tick+=(_,_)=>_=CheckForUpdate(false);timer.Start();
        Closed+=(_,_)=>{timer.Stop();updateLifetime.Cancel();};
        _=CheckForUpdate(false);
    }
    async Task CheckForUpdate(bool manual)
    {
        if(manual)Status.Text="جارٍ التحقق من إصدارات ويندوز…";
        var check=checking??=Updates.LatestAfter(InstalledVersion,updateLifetime.Token);
        try{
            var result=await check;
            if(updateLifetime.IsCancellationRequested)return;
            newerRelease=result;UpdateNotice.Visibility=result==null?Visibility.Collapsed:Visibility.Visible;
            if(result!=null)UpdateNotice.Content=$"تحديث ويندوز {WindowsRelease.DisplayVersion(result.Version)} متاح　·　عرض التغييرات والتثبيت";
            if(manual){
                Status.Text=result==null?$"تستخدم أحدث إصدار ويندوز منشور ({WindowsRelease.DisplayVersion(InstalledVersion)}).":"يتوفر تحديث لويندوز.";
                if(result!=null)ShowUpdate(result);
            }
        }catch(Exception ex){
            if(manual&&!updateLifetime.IsCancellationRequested)Status.Text="تعذّر التحقق من التحديث. تحقق من اتصال الإنترنت وأعد المحاولة.";
            Debug.WriteLine(ex.Message);
        }finally{if(ReferenceEquals(checking,check))checking=null;}
    }
    void OpenUpdate(object sender,RoutedEventArgs args){if(newerRelease is {} release)ShowUpdate(release);}
    void ShowUpdate(WindowsRelease release)
    {
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(updateLifetime.Token);
        var dialog=new Window{Owner=this,Title="تحديث برنامج الحسابات",Width=490,Height=370,MinWidth=320,MinHeight=230,WindowStartupLocation=WindowStartupLocation.CenterOwner,FlowDirection=FlowDirection.RightToLeft};
        var notes=Text(release.Notes.Trim(),15);var status=Text("احفظ تعديلاتك قبل التثبيت. سيُغلق التطبيق بعد اكتمال التنزيل.",14);
        var progress=new ProgressBar{Minimum=0,Maximum=100,Height=8,Visibility=Visibility.Collapsed,Margin=new Thickness(2,8,2,8)};
        var install=ButtonFor("تنزيل وتثبيت",()=>{},"\uE896",true);var back=ButtonFor("لاحقاً",dialog.Close);
        var dock=new DockPanel{Margin=new Thickness(14)};
        var header=Text("إصدار ويندوز "+WindowsRelease.DisplayVersion(release.Version)+"　·　"+(release.Bytes/1048576d).ToString("N1")+" ميغابايت",18,true);
        DockPanel.SetDock(header,Dock.Top);dock.Children.Add(header);
        var actions=Column(status,progress,Bar(install,back));DockPanel.SetDock(actions,Dock.Bottom);dock.Children.Add(actions);
        dock.Children.Add(new ScrollViewer{Content=notes,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(2,12,2,12)});dialog.Content=dock;
        dialog.Closed+=(_,_)=>cancel.Cancel();
        install.Click+=async (_,_)=>{
            install.IsEnabled=false;progress.Visibility=Visibility.Visible;
            back.Content="إلغاء التنزيل";status.Text="جارٍ تنزيل المثبّت…";progress.Value=0;
            var token=cancel.Token;
            try{
                var file=await Updates.Download(release,Path.Combine(store.Root,"updates"),new Progress<int>(p=>progress.Value=p),token);
                token.ThrowIfCancellationRequested();
                using var installer=Process.Start(new ProcessStartInfo(file){UseShellExecute=true})??throw new IOException("تعذّر فتح المثبّت.");
                Application.Current.Shutdown();
            }catch(OperationCanceledException) when(token.IsCancellationRequested){}
            catch(Exception ex){if(!token.IsCancellationRequested){status.Text="تعذّر إكمال التحديث. "+ex.Message;install.IsEnabled=true;back.Content="إغلاق";}}
        };
        dialog.ShowDialog();
    }
}
