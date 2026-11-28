using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Globalization;
using Microsoft.Identity.Client;

namespace AnbarPayroll.Desktop;

public partial class MainWindow
{
    sealed record HistoryPoint(string id,long at)
    {
        public string DateLabel=>DateTimeOffset.FromUnixTimeMilliseconds(at).ToLocalTime().ToString("G");
    }
    readonly List<HistoryPoint> historyPoints=[];
    string? historyHead;
    bool cloudUpload, cloudFailed;
    async Task MarkWorkspacePoint(string reason){
        if(File.Exists(AccountPath))await CallWorkspace(new(){{"dir",SyncDir},{"action","note"},{"reason",reason}});
    }
    sealed record SyncResult(string status,string? error,string? warning,string? file,string? transaction,string? head,HistoryPoint[]? points);
    IPublicClientApplication? account;
    Window? syncDialog;
    StackPanel? syncPanel;
    bool syncing;
    string syncStatus="نقل النسخ يدوي";
    TextBlock? dialogStatus;
    ProgressBar? dialogProgress;
    bool manualTransfer;
    string SyncDir=>Path.Combine(store.Root,"OneDriveWorkspace");
    string AccountPath=>Path.Combine(SyncDir,"account");

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
    void OpenWorkspaceSync(bool upload)
    {
        if(busy || syncing || syncDialog!=null)return;
        cloudUpload=upload;
        cloudFailed=false;
        historyPoints.Clear();
        syncStatus="جارٍ الاتصال بـ OneDrive…";
        syncPanel=new StackPanel{Margin=new Thickness(16)};
        var layout=new DockPanel();
        var header=new StackPanel{Margin=new Thickness(16,12,16,0)};
        dialogStatus=Text(syncStatus,17,true);
        dialogProgress=new ProgressBar{Height=5,Margin=new Thickness(0,6,0,0)};
        header.Children.Add(dialogStatus);
        header.Children.Add(dialogProgress);
        DockPanel.SetDock(header,Dock.Top);
        layout.Children.Add(header);
        layout.Children.Add(new ScrollViewer{Content=syncPanel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        syncDialog=new Window{Owner=this,Title=upload?"رفع نسخة · OneDrive":"استيراد نسخة · OneDrive",FlowDirection=FlowDirection.RightToLeft,
            WindowStartupLocation=WindowStartupLocation.CenterOwner,Width=480,SizeToContent=SizeToContent.Height,MaxHeight=460,Content=layout};
        syncDialog.PreviewKeyDown+=(_,e)=>{if(e.Key==System.Windows.Input.Key.Escape)syncDialog.Close();};
        ShowSyncProgress(SyncOperationProgress.IsIndeterminate?null:SyncOperationProgress.Value);
        syncDialog.ContentRendered+=async (_,_)=>await WorkspaceRun(upload?"backup-upload":"history");
        syncDialog.ShowDialog();syncDialog=null;syncPanel=null;dialogProgress=null;dialogStatus=null;
    }
    void DrawWorkspaceSync()
    {
        if(syncPanel==null)return;
        dialogStatus!.Text=DisplayNumerals.Format(syncStatus);
        syncPanel.Children.Clear();
        if(syncing)return;
        if(!cloudUpload){
            foreach(var point in historyPoints){
                var label=point.id==historyHead?"أحدث نسخة":"النسخة السابقة";
                syncPanel.Children.Add(ButtonFor(label+"　·　"+point.DateLabel,()=>{
                    if(MessageBox.Show(syncDialog!,"ستُستبدل بيانات هذه الحاسبة، بما فيها الدفعات، ببيانات نسخة "+point.DateLabel+"، هل تريد المتابعة؟",
                        "استيراد نسخة",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)==MessageBoxResult.Yes)
                        _=WorkspaceRun("backup-download",point);
                },"\uE896"));
            }
        }
        if(cloudFailed){
            string action=cloudUpload?"backup-upload":"history";
            syncPanel.Children.Add(ButtonFor("إعادة المحاولة",()=>_=WorkspaceRun(action),"\uE895"));
            syncPanel.Children.Add(ButtonFor("إعادة ربط الحساب",()=>_=WorkspaceRun(action,reconnect:true),"\uE77B"));
        }
    }
    void SetSyncStatus(string message)
    {
        syncStatus=message;
        if(dialogStatus!=null)dialogStatus.Text=DisplayNumerals.Format(message);
        if(manualTransfer || syncDialog==null)Status.Text=DisplayNumerals.Format(message);
    }
    void ShowSyncProgress(double? fraction=null)
    {
        foreach(var bar in new[]{SyncOperationProgress,dialogProgress}){
            if(bar==null)continue;
            bar.Visibility=manualTransfer?Visibility.Visible:Visibility.Collapsed;
            bar.IsIndeterminate=manualTransfer && !fraction.HasValue;
            bar.Maximum=1;
            bar.Value=Math.Clamp(fraction??0,0,1);
        }
    }
    async Task ReadTransferEvents(StreamReader events)
    {
        bool active=false;
        try{
            while(await events.ReadLineAsync() is {} line){
                if(line.Split(' ') is ["progress", var phase, var done, var total] &&
                    long.TryParse(done,NumberStyles.None,CultureInfo.InvariantCulture,out var received) &&
                    long.TryParse(total,NumberStyles.None,CultureInfo.InvariantCulture,out var size)){
                    string label=phase=="1"?"رفع النسخة":"تنزيل النسخة";
                    ShowSyncProgress(size>0?(double)received/size:null);
                    SetSyncStatus($"{label} · {received/1048576d:N1} من {size/1048576d:N1} ميغابايت");
                    continue;
                }
                if(line!="transfer true" && line!="transfer false")continue;
                active=line=="transfer true";
                ShowSyncProgress();
                if(!active)SetSyncStatus("جارٍ إكمال النقل…");
                SyncTransferIndicator.Visibility=active?Visibility.Visible:Visibility.Hidden;
                var rotation=active && SystemParameters.ClientAreaAnimation
                    ? new DoubleAnimation(0,360,TimeSpan.FromSeconds(1.3)){RepeatBehavior=RepeatBehavior.Forever}
                    : null;
                SyncTransferRotation.BeginAnimation(RotateTransform.AngleProperty,rotation);
            }
        }finally{
            // قد ينتهي محرك النقل قبل إرسال إشارة التوقف.
            if(active){
                SyncTransferRotation.BeginAnimation(RotateTransform.AngleProperty,null);
                SyncTransferIndicator.Visibility=Visibility.Hidden;
            }
        }
    }
    async Task<SyncResult> CallWorkspace(Dictionary<string,object> args)
    {
        using var process=new Process{StartInfo=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"payroll-sync.exe")){
            Arguments="--transfer-events",UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
            StandardInputEncoding=new UTF8Encoding(false),StandardOutputEncoding=Encoding.UTF8}};
        process.Start();
        var output=process.StandardOutput.ReadToEndAsync();
        var events=ReadTransferEvents(process.StandardError);
        try{
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(args));
        }finally{
            process.StandardInput.Close();
            await events;
        }
        await process.WaitForExitAsync();
        var reply=JsonSerializer.Deserialize<SyncResult>(await output)??throw new IOException("تعذّر تشغيل المزامنة.");
        if(reply.error!=null)throw new IOException(reply.error);
        return reply;
    }
    async Task<AuthenticationResult> BackupLogin(bool reconnect)
    {
        var app=MicrosoftAccount();
        string[] scopes=["https://graph.microsoft.com/Files.ReadWrite.AppFolder"];
        var current=File.Exists(AccountPath)?await app.GetAccountAsync(File.ReadAllText(AccountPath)):null;
        if(current!=null && !reconnect){
            try{return await app.AcquireTokenSilent(scopes,current).ExecuteAsync();}
            catch(MsalUiRequiredException){ /* انتهت الجلسة المحفوظة. */ }
        }
        var result=await app.AcquireTokenInteractive(scopes).WithUseEmbeddedWebView(false).WithPrompt(Prompt.SelectAccount).ExecuteAsync();
        File.WriteAllText(AccountPath,result.Account.HomeAccountId.Identifier);
        return result;
    }
    async Task WorkspaceRun(string action,HistoryPoint? point=null,bool reconnect=false)
    {
        if(syncing || busy || ((App)Application.Current).DeviceLock.SyncLocked)return;
        manualTransfer=action=="backup-upload" || action=="backup-download";
        ShowSyncProgress();
        cloudFailed=false;
        if(action=="history"){historyPoints.Clear();historyHead=null;}
        syncing=true;string? exported=null, received=null;
        try {
            SetSyncStatus("جارٍ الاتصال بـ OneDrive…");DrawWorkspaceSync();
            var auth=await BackupLogin(reconnect);
            if(syncDialog==null || ((App)Application.Current).DeviceLock.SyncLocked)return;
            var stamp=store.WorkspaceStamp;
            var args=new Dictionary<string,object>{{"action",action},{"dir",SyncDir},{"token",auth.AccessToken},{"stamp",stamp},{"recentBackups",true}};
            if(point!=null){args["head"]=historyHead!;args["revision"]=point.id;}
            if(action=="backup-upload"){
                SetSyncStatus("تجهيز النسخة الاحتياطية الكاملة…");
                exported=Path.Combine(SyncDir,"export-"+Guid.NewGuid().ToString("N")+".zip");
                stamp=await Task.Run(()=>store.ExportBackup(exported));
                SetSyncStatus("التحقق من النسخة قبل الرفع…");
                await Task.Run(()=>store.ValidateWorkspace(exported));
                if(store.WorkspaceStamp!=stamp || ((App)Application.Current).DeviceLock.SyncLocked)throw new IOException("تغيّرت البيانات أثناء التجهيز. أعد الرفع.");
                args["local"]=exported;args["stamp"]=stamp;
            }
            var reply=await CallWorkspace(args);
            if(reply.status=="history"){
                historyPoints.AddRange(reply.points??[]);historyHead=reply.head;
                syncStatus=historyPoints.Count==0?"لا توجد نسخة محفوظة على OneDrive.":"اختر النسخة المراد استيرادها.";return;
            }
            if(action=="backup-download" && reply.status=="downloaded" && reply.file is {} file){
                received=file;
                SetSyncStatus("جارٍ التحقق من النسخة المستلمة…");
                using var verified=await Task.Run(()=>store.PrepareWorkspace(file,true));
                if(syncDialog==null || store.WorkspaceStamp!=stamp || busy || ((App)Application.Current).DeviceLock.SyncLocked)
                    throw new IOException("تغيّرت البيانات أو أُغلقت نافذة الاستيراد. أعد الاستيراد.");
                SetSyncStatus("جارٍ استيراد بيانات النسخة…");
                busy=true;IsEnabled=false;
                try{stamp=await Task.Run(()=>store.ApplyWorkspace(file,stamp,appearance.Theme,verified));Reload();ShowPage(page);}
                finally{busy=false;IsEnabled=true;}
                reply=await CallWorkspace(new(){{"action","backup-ack"},{"dir",SyncDir},{"stamp",stamp},{"transaction",reply.transaction!}});
            }
            if(action=="backup-upload" && reply.status=="uploaded")syncStatus=reply.warning??"اكتمل رفع النسخة الكاملة";
            else if(action=="backup-download" && reply.status=="imported")syncStatus="اكتمل استيراد النسخة على هذا الجهاز";
            else throw new IOException("تعذّر إكمال نقل النسخة.");
            if(action=="backup-download")historyPoints.Clear();
        }
        catch(Exception ex){cloudFailed=true;syncStatus=ex is MsalException?"تعذّر تسجيل الدخول إلى Microsoft.":ex.Message;}
        finally{
            SetSyncStatus(syncStatus);
            manualTransfer=false;ShowSyncProgress();syncing=false;
            try{if(received!=null)File.Delete(received);if(exported!=null)File.Delete(exported);}
            catch(IOException ex){Debug.WriteLine(ex);}
            DrawWorkspaceSync();
        }
    }
}
