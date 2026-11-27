using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Identity.Client;

namespace AnbarPayroll.Desktop;

public partial class MainWindow
{
    sealed record SyncConflict(string key,string ticket,string local,string remote);
    sealed record SyncChoice(string ticket,string side);
    sealed record SyncResult(string status,string? error,string? file,string? transaction,bool apply,SyncConflict[]? conflicts);
    readonly Dictionary<string,SyncChoice> syncChoices=new();
    SyncConflict[] syncConflicts=[];
    IPublicClientApplication? account;
    Window? syncDialog;
    StackPanel? syncPanel;
    bool syncing;
    string syncStatus="المزامنة متوقفة";
    string SyncDir=>Path.Combine(store.Root,"OneDriveWorkspace");
    string EnabledPath=>Path.Combine(SyncDir,"enabled");
    string AccountPath=>Path.Combine(SyncDir,"account");
    bool SyncEnabled=>File.Exists(EnabledPath);
    void StartWorkspaceSync()
    {
        var timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(90)};
        async Task Automatic(){
            if(!initializing && IsActive && !busy && syncDialog==null && syncConflicts.Length==0 &&
               (page=="الرئيسية" || page=="الإعدادات") && SyncEnabled)await WorkspaceRun();
        }
        timer.Tick+=async (_,_)=>await Automatic();
        Activated+=async (_,_)=>await Automatic();
        ContentRendered+=async (_,_)=>await Automatic();
        Closed+=(_,_)=>timer.Stop();
        timer.Start();
    }
    IPublicClientApplication MicrosoftAccount()
    {
        if(account!=null)return account;
        Directory.CreateDirectory(SyncDir);
        var app=PublicClientApplicationBuilder.Create("986d159c-3325-4480-af32-3572c0c9f3c9")
            .WithAuthority("https://login.microsoftonline.com/consumers").WithRedirectUri("http://localhost").Build();
        string file=Path.Combine(SyncDir,"session.dpapi");
        app.UserTokenCache.SetBeforeAccess(a=>{
            if(File.Exists(file))a.TokenCache.DeserializeMsalV3(ProtectedData.Unprotect(File.ReadAllBytes(file),null,DataProtectionScope.CurrentUser),true);
        });
        app.UserTokenCache.SetAfterAccess(a=>{
            if(!a.HasStateChanged)return;
            var temp=file+Guid.NewGuid().ToString("N");
            try{
                File.WriteAllBytes(temp,ProtectedData.Protect(a.TokenCache.SerializeMsalV3(),null,DataProtectionScope.CurrentUser));
                File.Move(temp,file,true);
            }finally{File.Delete(temp);}
        });
        return account=app;
    }
    void OpenWorkspaceSync()
    {
        syncPanel=new StackPanel{Margin=new Thickness(16)};
        syncDialog=new Window{Owner=this,Title="مزامنة OneDrive",FlowDirection=FlowDirection.RightToLeft,
            WindowStartupLocation=WindowStartupLocation.CenterOwner,Width=570,Height=480,
            Content=new ScrollViewer{Content=syncPanel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}};
        syncDialog.PreviewKeyDown+=(_,e)=>{if(e.Key==System.Windows.Input.Key.Escape)syncDialog.Close();};
        DrawWorkspaceSync();syncDialog.ShowDialog();syncDialog=null;syncPanel=null;
    }
    void DrawWorkspaceSync()
    {
        if(syncPanel==null)return;
        syncPanel.Children.Clear();
        bool linked=File.Exists(AccountPath);
        syncPanel.Children.Add(Text(syncStatus,17,true));
        var last=Path.Combine(SyncDir,"last");
        if(File.Exists(last))syncPanel.Children.Add(Text("آخر مزامنة　"+File.GetLastWriteTime(last).ToString("g"),14));
        syncPanel.Children.Add(ButtonFor(linked?"إعادة ربط الحساب":"تسجيل الدخول إلى Microsoft",()=>_=WorkspaceRun(login:true),"\uE77B"));
        if(linked){
            syncPanel.Children.Add(ButtonFor(SyncEnabled?"إيقاف المزامنة التلقائية":"تفعيل المزامنة التلقائية",()=>{
                if(SyncEnabled){File.Delete(EnabledPath);syncStatus="المزامنة متوقفة";}
                else{File.WriteAllText(EnabledPath,"1");_=WorkspaceRun();}
                DrawWorkspaceSync();
            },"\uE895"));
            if(SyncEnabled)syncPanel.Children.Add(ButtonFor("مزامنة الآن",()=>_=WorkspaceRun(),"\uE895"));
            syncPanel.Children.Add(ButtonFor("تسجيل الخروج",()=>_=WorkspaceRun(logout:true),"\uE8AC"));
        }
        foreach(var row in syncConflicts){
            var key=JsonSerializer.Deserialize<string[]>(row.key)??[];
            syncPanel.Children.Add(Text(string.Join(" · ",key.Skip(1)),16,true));
            foreach(var side in new[]{"local","remote"}){
                var selected=syncChoices.GetValueOrDefault(row.key)==new SyncChoice(row.ticket,side);
                string value=side=="local"?"هذا الجهاز　"+row.local:"OneDrive　"+row.remote;
                syncPanel.Children.Add(ButtonFor(value,()=>{syncChoices[row.key]=new(row.ticket,side);DrawWorkspaceSync();},selected?"\uE73E":"\uEA3A"));
            }
        }
        syncPanel.IsEnabled=!syncing;
    }
    static async Task<SyncResult> CallWorkspace(Dictionary<string,object> args)
    {
        using var process=new Process{StartInfo=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"payroll-sync.exe")){
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,
            StandardInputEncoding=new UTF8Encoding(false),StandardOutputEncoding=Encoding.UTF8}};
        process.Start();
        var output=process.StandardOutput.ReadToEndAsync();
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(args));
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        var reply=JsonSerializer.Deserialize<SyncResult>(await output)??throw new IOException("تعذّر تشغيل المزامنة.");
        if(reply.error!=null)throw new IOException(reply.error);
        return reply;
    }
    async Task WorkspaceRun(bool login=false,bool logout=false)
    {
        if(syncing || busy || ((App)Application.Current).DeviceLock.SyncLocked || (!login&&!logout&&!SyncEnabled))return;
        syncing=true;string? exported=null;
        try {
            syncStatus="جارٍ الاتصال بـ OneDrive…";DrawWorkspaceSync();
            var app=MicrosoftAccount();
            var current=File.Exists(AccountPath)?await app.GetAccountAsync(File.ReadAllText(AccountPath)):null;
            if(logout){
                if(current!=null)await app.RemoveAsync(current);
                File.Delete(EnabledPath);File.Delete(AccountPath);File.Delete(Path.Combine(SyncDir,"session.dpapi"));
                syncChoices.Clear();syncConflicts=[];syncStatus="تم تسجيل الخروج";return;
            }
            string[] scopes=["https://graph.microsoft.com/Files.ReadWrite.AppFolder"];
            var auth=login?await app.AcquireTokenInteractive(scopes).WithUseEmbeddedWebView(false).WithPrompt(Prompt.SelectAccount).ExecuteAsync():
                           await app.AcquireTokenSilent(scopes,current).ExecuteAsync();
            if(login){File.WriteAllText(AccountPath,auth.Account.HomeAccountId.Identifier);File.WriteAllText(EnabledPath,"1");}
            var stamp=store.WorkspaceStamp;
            var args=new Dictionary<string,object>{{"action","prepare"},{"dir",SyncDir},{"token",auth.AccessToken},{"stamp",stamp},{"choices",syncChoices},{"empty",store.Active==null}};
            var reply=await CallWorkspace(args);
            if(reply.status=="export"){
                exported=Path.Combine(SyncDir,"export-"+Guid.NewGuid().ToString("N")+".zip");
                stamp=await Task.Run(()=>store.ExportWorkspace(exported));
                args["local"]=exported;args["stamp"]=stamp;reply=await CallWorkspace(args);
            }
            if(reply.status=="conflict"){syncConflicts=reply.conflicts??[];syncStatus="راجع القيود المتعارضة في إعدادات OneDrive.";return;}
            if(reply.status=="waiting"){syncStatus="بانتظار بيانات للمزامنة";return;}
            if(reply.status=="ready" && reply.file is {} file){
                await Task.Run(()=>store.ValidateWorkspace(file));
                void Check(){if(store.WorkspaceStamp!=stamp || busy || ((App)Application.Current).DeviceLock.SyncLocked)throw new IOException("تغيّرت البيانات أو أُغلق التطبيق. ستُعاد المحاولة.");}
                Check();args["action"]="publish";args["transaction"]=reply.transaction!;reply=await CallWorkspace(args);
                Check();if(reply.status!="committed")throw new IOException("تعذّر تأكيد المزامنة.");
                if(reply.apply){
                    if(syncDialog==null && (!IsActive || page!="الرئيسية"&&page!="الإعدادات"))throw new IOException("ستكتمل المزامنة عند العودة إلى الرئيسية.");
                    busy=true;IsEnabled=false;
                    try{stamp=await Task.Run(()=>store.ApplyWorkspace(file,stamp));Reload();ShowPage(page);}
                    finally{busy=false;IsEnabled=true;}
                }
                args["action"]="ack";args["stamp"]=stamp;reply=await CallWorkspace(args);
            }
            if(reply.status!="synced")throw new IOException("تعذّر إكمال المزامنة.");
            File.WriteAllText(Path.Combine(SyncDir,"last"),"1");syncConflicts=[];syncChoices.Clear();syncStatus="البيانات متزامنة";
        }catch(MsalUiRequiredException){syncStatus="تحتاج Microsoft إلى تسجيل الدخول مجدداً.";}
        catch(Exception ex){syncStatus=ex is MsalException?"تعذّر تسجيل الدخول إلى Microsoft.":ex.Message;}
        finally{if(exported!=null)File.Delete(exported);syncing=false;DrawWorkspaceSync();if(syncDialog==null)Status.Text=syncStatus;}
    }
}
