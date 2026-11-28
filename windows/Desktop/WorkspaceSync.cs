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
    sealed record SyncConflict(string key,string ticket,string local,string remote);
    sealed record SyncChoice(string ticket,string side);
    sealed record HistoryPoint(string id,long at,string kind,string[]? notes)
    {
        public string DateLabel=>DateTimeOffset.FromUnixTimeMilliseconds(at).ToLocalTime().ToString("G");
        public string Label=>notes is {Length:>0}?string.Join(" · ",notes.Select(n=>n=="import"?"قبل الاستيراد":"قبل الحذف")):kind switch {"beforeRestore"=>"قبل الاستعادة","restore"=>"بعد الاستعادة",_=>"مزامنة"};
    }
    sealed record HistoryPreview(int staff,int employees,int refunds,decimal net,decimal deductions);
    readonly List<HistoryPoint> historyPoints=[];
    string? historyHead,historyCursor;
    HistoryPoint? historySelected;
    HistoryPreview? historyPreview;
    long? historyUntil;
    bool showingHistory;
    async Task MarkWorkspacePoint(string reason){
        if(File.Exists(AccountPath))await CallWorkspace(new(){{"dir",SyncDir},{"action","note"},{"reason",reason}});
    }
    sealed record SyncResult(string status,string? error,string? file,string? transaction,bool apply,SyncConflict[]? conflicts, string? head,string? cursor,HistoryPoint[]? points,HistoryPreview? preview);
    readonly Dictionary<string,SyncChoice> syncChoices=new();
    SyncConflict[] syncConflicts=[];
    IPublicClientApplication? account;
    Window? syncDialog;
    StackPanel? syncPanel;
    bool syncing;
    string syncStatus="المزامنة يدوية";
    TextBlock? dialogStatus;
    ProgressBar? dialogProgress;
    bool manualTransfer;
    string SyncDir=>Path.Combine(store.Root,"OneDriveWorkspace");
    string AccountPath=>Path.Combine(SyncDir,"account");

    sealed record LocalCheckpoint(string? Stamp);
    bool HasPendingUpload()
    {
        if(store.Active==null)return false;
        try {
            var state=JsonSerializer.Deserialize<LocalCheckpoint>(File.ReadAllBytes(Path.Combine(SyncDir,"checkpoint.json")));
            return state?.Stamp!=store.WorkspaceStamp;
        } catch(IOException){return true;}
          catch(JsonException){return true;}
          catch(UnauthorizedAccessException){return true;}
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
        showingHistory=false;
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
        syncDialog=new Window{Owner=this,Title="مزامنة OneDrive",FlowDirection=FlowDirection.RightToLeft,
            WindowStartupLocation=WindowStartupLocation.CenterOwner,Width=570,Height=480,Content=layout};
        syncDialog.PreviewKeyDown+=(_,e)=>{if(e.Key==System.Windows.Input.Key.Escape)syncDialog.Close();};
        ShowSyncProgress(SyncOperationProgress.IsIndeterminate?null:SyncOperationProgress.Value);
        DrawWorkspaceSync();syncDialog.ShowDialog();syncDialog=null;syncPanel=null;dialogProgress=null;dialogStatus=null;
    }
    void DrawWorkspaceSync()
    {
        if(syncPanel==null)return;
        dialogStatus!.Text=DisplayNumerals.Format(syncStatus);
        syncPanel.Children.Clear();
        if(showingHistory){DrawWorkspaceHistory();syncPanel.IsEnabled=!syncing;return;}
        bool linked=File.Exists(AccountPath);
        if(linked){
            syncPanel.Children.Add(ButtonFor("مزامنة الآن",()=>_=WorkspaceRun(),"\uE895"));
            if(!syncing && HasPendingUpload())syncPanel.Children.Add(Text("تعديلات محلية بانتظار الرفع",14,true));
            var last=Path.Combine(SyncDir,"last");
            if(File.Exists(last))syncPanel.Children.Add(Text("آخر مزامنة ناجحة　"+File.GetLastWriteTime(last).ToString("g"),14));
            syncPanel.Children.Add(ButtonFor("سجل المزامنة والاستعادة",()=>{
                showingHistory=true;historyUntil=null;_=WorkspaceRun(action:"history");
            },"\uE81C"));
            syncPanel.Children.Add(ButtonFor("تسجيل الخروج",()=>_=WorkspaceRun(logout:true),"\uE8AC"));
        }
        syncPanel.Children.Add(ButtonFor(linked?"إعادة ربط الحساب":"تسجيل الدخول إلى Microsoft",()=>_=WorkspaceRun(login:true),"\uE77B"));
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
    void DrawWorkspaceHistory()
    {
        var panel=syncPanel!;
        panel.Children.Add(ButtonFor("الرجوع إلى المزامنة",()=>{showingHistory=false;DrawWorkspaceSync();},"\uE72B"));
        var until=historyUntil is {} limit?DateTimeOffset.FromUnixTimeMilliseconds(limit).LocalDateTime:DateTime.Now;
        var date=new DatePicker{SelectedDate=historyUntil.HasValue?until.Date:null,MinWidth=135,ToolTip="تاريخ النقطة"};
        var time=new TextBox{Text=until.ToString("HH:mm"),MinWidth=64,ToolTip="الوقت المحلي بصيغة 24 ساعة"};
        var filter=new WrapPanel();
        filter.Children.Add(Text("حتى",15,true));filter.Children.Add(date);filter.Children.Add(time);
        filter.Children.Add(ButtonFor("عرض النقاط",()=>{
            historyUntil=null;
            if(date.SelectedDate is {} day){
                if(!TimeSpan.TryParse(time.Text,out var clock)||clock<TimeSpan.Zero||clock>=TimeSpan.FromDays(1)){
                    MessageBox.Show(syncDialog!,"أدخل الوقت بصيغة 24 ساعة، مثلاً 14:30.");return;
                }
                historyUntil=new DateTimeOffset(day.Date+clock).ToUnixTimeMilliseconds()+59_999;
            }
            _=WorkspaceRun(action:"history");
        },"\uE721"));
        panel.Children.Add(filter);
        if(historySelected is {} point && historyPreview is {} preview){
            panel.Children.Add(Text("معاينة　"+point.DateLabel,17,true));
            panel.Children.Add(Text($"عدد الملاك　{preview.staff:N0}　 ·　 قيود الرواتب　{preview.employees:N0}　 ·　 دفعات الإعادة　{preview.refunds:N0}",15));
            panel.Children.Add(Text($"مجموع صافي الرواتب　{preview.net:#,0.##} د.ع",15));
            panel.Children.Add(Text($"مجموع الاستقطاعات　{preview.deductions:#,0.##} د.ع",15));
            if(point.id!=historyHead)panel.Children.Add(ButtonFor("استعادة هذه النقطة",()=>{
                if(MessageBox.Show(syncDialog!,"سيُحفظ الوضع الحالي قبل الاستعادة. ستصل البيانات المستعادة إلى بقية أجهزتك عند المزامنة. هل تريد استعادة نقطة "+point.DateLabel+"؟","استعادة نقطة مزامنة",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)==MessageBoxResult.Yes)
                    _=WorkspaceRun(action:"restore",point:point);
            },"\uE777"));
        }
        foreach(var row in historyPoints){
            panel.Children.Add(ButtonFor(row.DateLabel+"　 ·　"+(row.id==historyHead?"الحالية · "+row.Label:row.Label),
                ()=>_=WorkspaceRun(action:"preview",point:row),"\uE890"));
        }
        if(historyCursor!=null)panel.Children.Add(ButtonFor("عرض الأقدم",()=>_=WorkspaceRun(action:"history",more:true),"\uE70D"));
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
                    string label=phase=="1"?"رفع التعديلات":"استلام وتجميع النسخة";
                    ShowSyncProgress(size>0?(double)received/size:null);
                    SetSyncStatus($"{label} · {received/1048576d:N1} من {size/1048576d:N1} ميغابايت");
                    continue;
                }
                if(line!="transfer true" && line!="transfer false")continue;
                active=line=="transfer true";
                ShowSyncProgress();
                if(!active)SetSyncStatus("جارٍ إكمال المزامنة…");
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
    async Task WorkspaceRun(bool login=false,bool logout=false,string action="prepare",HistoryPoint? point=null,bool more=false)
    {
        if(syncing || busy || ((App)Application.Current).DeviceLock.SyncLocked || (!login&&!logout&&!File.Exists(AccountPath)))return;
        manualTransfer=!login && !logout && (action=="prepare" || action=="restore");
        ShowSyncProgress();
        syncing=true;string? exported=null;
        try {
            SetSyncStatus("جارٍ الاتصال بـ OneDrive…");DrawWorkspaceSync();
            var app=MicrosoftAccount();
            var current=File.Exists(AccountPath)?await app.GetAccountAsync(File.ReadAllText(AccountPath)):null;
            if(logout){
                if(current!=null)await app.RemoveAsync(current);
                File.Delete(AccountPath);File.Delete(Path.Combine(SyncDir,"session.dpapi"));
                syncChoices.Clear();syncConflicts=[];historyPoints.Clear();historyPreview=null;historySelected=null;historyHead=null;historyCursor=null;syncStatus="تم تسجيل الخروج";return;
            }
            string[] scopes=["https://graph.microsoft.com/Files.ReadWrite.AppFolder"];
            var auth=login?await app.AcquireTokenInteractive(scopes).WithUseEmbeddedWebView(false).WithPrompt(Prompt.SelectAccount).ExecuteAsync():
                           await app.AcquireTokenSilent(scopes,current).ExecuteAsync();
            if(login){File.WriteAllText(AccountPath,auth.Account.HomeAccountId.Identifier);syncStatus="الحساب مرتبط";return;}
            var stamp=store.WorkspaceStamp;
            var args=new Dictionary<string,object>{{"action",action},{"dir",SyncDir},{"token",auth.AccessToken},{"stamp",stamp},{"choices",syncChoices},{"empty",store.Active==null}};
            if(historyHead!=null)args["head"]=historyHead;
            if(point!=null)args["revision"]=point.id;
            if(action=="history"){
                if(more && historyCursor!=null)args["cursor"]=historyCursor;
                if(!more){historyPoints.Clear();historyCursor=null;}
                if(historyUntil.HasValue)args["until"]=historyUntil.Value;
            }
            if(action=="history" || action=="preview"){historySelected=null;historyPreview=null;}
            var reply=await CallWorkspace(args);
            if(reply.status=="history"){
                historyPoints.AddRange(reply.points??[]);historyHead=reply.head;historyCursor=reply.cursor;
                syncStatus=historyPoints.Count==0?"لا توجد نقاط مزامنة لهذه الفترة":"سجل المزامنة";return;
            }
            if(reply.status=="preview"){
                historyPreview=reply.preview;historySelected=point;syncStatus="معاينة نقطة المزامنة";return;
            }
            if(reply.status=="export"){
                SetSyncStatus("تجهيز التعديلات المحلية…");
                exported=Path.Combine(SyncDir,"export-"+Guid.NewGuid().ToString("N")+".zip");
                stamp=await Task.Run(()=>store.ExportWorkspace(exported));
                args["local"]=exported;args["stamp"]=stamp;reply=await CallWorkspace(args);
            }
            if(reply.status=="conflict"){syncConflicts=reply.conflicts??[];syncStatus="اختر القيمة المطلوبة لكل قيد مختلف، ثم اضغط مزامنة الآن.";return;}
            if(reply.status=="ready"){syncConflicts=[];DrawWorkspaceSync();}
            if(reply.status=="waiting"){syncStatus="بانتظار بيانات للمزامنة";return;}
            if(reply.status=="ready" && reply.file is {} file){
                if(reply.apply && syncDialog==null && (!IsActive || page!="الرئيسية"&&page!="الإعدادات")){
                    syncStatus="توجد تعديلات جاهزة. افتح إعدادات OneDrive واضغط مزامنة الآن.";return;
                }
                SetSyncStatus("جارٍ التحقق من النسخة…");
                using var verified = await Task.Run(()=>store.PrepareWorkspace(file,reply.apply));
                void Check(){if(store.WorkspaceStamp!=stamp || busy || ((App)Application.Current).DeviceLock.SyncLocked)throw new IOException("تغيّرت البيانات أو أُغلق التطبيق. اضغط مزامنة الآن مجدداً.");}
                Check();args["action"]="publish";args["transaction"]=reply.transaction!;reply=await CallWorkspace(args);
                Check();if(reply.status!="committed")throw new IOException("تعذّر تأكيد المزامنة.");
                if(reply.apply){
                    if(syncDialog==null && (!IsActive || page!="الرئيسية"&&page!="الإعدادات"))throw new IOException("افتح إعدادات OneDrive واضغط مزامنة الآن مجدداً.");
                    SetSyncStatus("جارٍ تطبيق التعديلات المستلمة…");
                    busy=true;IsEnabled=false;
                    try{stamp=await Task.Run(()=>store.ApplyWorkspace(file,stamp,appearance.Theme,verified));Reload();ShowPage(page);}
                    finally{busy=false;IsEnabled=true;}
                }
                SetSyncStatus("جارٍ تأكيد اكتمال المزامنة…");
                args["action"]="ack";args["stamp"]=stamp;reply=await CallWorkspace(args);
            }
            if(reply.status!="synced")throw new IOException("تعذّر إكمال المزامنة.");
            File.WriteAllText(Path.Combine(SyncDir,"last"),"1");syncConflicts=[];syncChoices.Clear();syncStatus="اكتملت آخر مزامنة";
            if(action=="restore"){
                historyPoints.Clear();historyCursor=null;historySelected=null;historyPreview=null;
                syncStatus="تمت الاستعادة وحُفظت نقطة للرجوع إليها.";
            }
        }catch(MsalUiRequiredException){syncStatus="تحتاج Microsoft إلى تسجيل الدخول مجدداً.";}
        catch(Exception ex){syncStatus=ex is MsalException?"تعذّر تسجيل الدخول إلى Microsoft.":ex.Message;}
        finally{
            SetSyncStatus(syncStatus);
            manualTransfer=false;ShowSyncProgress();syncing=false;
            try{if(exported!=null)File.Delete(exported);}
            catch(IOException ex){Debug.WriteLine(ex);}
            DrawWorkspaceSync();
        }
    }
}
