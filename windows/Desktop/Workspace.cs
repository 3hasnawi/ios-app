using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using A=SalaryDesktop.Core;

namespace AnbarPayroll.Desktop;
public partial class MainWindow
{
    const string FundingName="المصروفات والتمويل", CommandName="استقطاعات القيادة", TrustName="أمانات الرواتب", HealthName="الرعاية الصحية", FundName="صندوق الشهداء", AuditName="تدقيق الرواتب", SearchName="البحث الشامل", TransfersName="ملفات التحويل";
    static readonly string[] WorkPages=[FundingName,CommandName,TrustName,HealthName,FundName,AuditName,SearchName,TransfersName];
    static A.ReportAssets Fonts()
    {
        string dir=Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        return new(File.ReadAllBytes(Path.Combine(dir,"arial.ttf")),File.ReadAllBytes(Path.Combine(dir,"arialbd.ttf")),null);
    }
    Button PreviewButton(string title,Func<Task<byte[]>> prepare,Func<bool>? current=null,string caption="معاينة الكشف",bool printAndSave=false)
    {
        Button button = ButtonFor(caption,()=>{}, "\uE890");
        button.Click += async (_,_) => {
            button.IsEnabled = false;
            try {
                var bytes = await prepare();
                if (button.IsLoaded && (current?.Invoke() ?? true))
                    new LetterPreview(bytes,title,printAndSave:printAndSave){Owner=Window.GetWindow(button)}.ShowDialog();
            } catch (Exception ex) {
                if (button.IsLoaded) Error(ex);
            } finally {
                button.IsEnabled = true;
            }
        };
        return button;
    }
    async Task SaveFile(string name,string ext,Func<byte[]> generate,Action? record=null,Action? finish=null,Window? owner=null)
    {
        var dialog=new SaveFileDialog{Filter=ext+"|*."+ext,FileName=name,DefaultExt=ext};if(dialog.ShowDialog(owner??this)!=true)return;
        await Working(()=>{var bytes=generate();if(bytes.Length==0)throw new InvalidDataException("الملف فارغ.");
            string temp=dialog.FileName+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{File.WriteAllBytes(temp,bytes);record?.Invoke();File.Move(temp,dialog.FileName,true);}finally{if(File.Exists(temp))File.Delete(temp);}
        },"حُفظ الملف.",finish);
    }
    async Task Working(Action action,string message,Action? finish=null)
    {
        if(busy)return;busy=true;IsEnabled=false;Status.Text="جارٍ تنفيذ العملية…";
        try{await Task.Run(action);busy=false;finish?.Invoke();Status.Text=message;}
        catch(Exception ex){Error(ex);}finally{busy=false;IsEnabled=true;}
    }
    bool Form(string title,params FrameworkElement[] fields)
    {
        var dialog=new Window{Title=title,Owner=this,SizeToContent=SizeToContent.WidthAndHeight,WindowStartupLocation=WindowStartupLocation.CenterOwner,FlowDirection=FlowDirection.RightToLeft};
        var stack=Column(Text(title,19,true));foreach(var f in fields)stack.Children.Add(f);
        stack.Children.Add(Bar(ButtonFor("متابعة",()=>dialog.DialogResult=true,"\uE73E",true),ButtonFor("إلغاء",()=>dialog.DialogResult=false)));
        dialog.Content=new ScrollViewer{Content=Box(stack),HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};return dialog.ShowDialog()==true;
    }
    string? auditFilter;
    void SearchAll(Grid? overlay=null)
    {
        var dock=new DockPanel();var input=new TextBox();var title=Text("ابحث بالاسم أو اسم الأم أو الرقم الإحصائي أو الجهة أو الإضبارة",15);
        var top=Column(title,FilterBar(("بحث في الرواتب والملاك والاستقطاعات والأمانات",input,1)));DockPanel.SetDock(top,Dock.Top);dock.Children.Add(top);
        var list=GridView(("اسم المنتسب","Name",1.4),("مصادر المطابقة","Sources",1.6));dock.Children.Add(list);
        var host=overlay??CanvasArea;
        host.Children.Add(dock);
        int serial=0;
        input.TextChanged+=async (_,_)=>{
            var q=input.Text;int current=++serial;
            list.ItemsSource=null;
            await Task.Delay(180);
            if(serial!=current||!dock.IsLoaded)return;
            try{
                var hits=await Task.Run(()=>store.SearchPeople(q));
                if(current!=serial||!dock.IsLoaded)return;
                list.ItemsSource=hits.Take(300);
                var note=string.IsNullOrWhiteSpace(q)?"ابحث بالاسم أو اسم الأم أو الرقم الإحصائي أو الجهة أو الإضبارة":
                    hits.Count>300?"تظهر أسماء أول 300 منتسب. ضيّق البحث.":$"عدد المنتسبين　{hits.Count}";
                title.Text=note;
            }catch(Exception e){if(current==serial&&dock.IsLoaded)Error(e);}
        };
        bool opening=false;
        async Task Open(Store.SearchResult hit){
            if(opening)return;
            opening=true;
            int version=serial;
            try{await OpenPerson(hit,current:()=>dock.IsLoaded && serial==version);}
            finally{opening=false;}
        }
        var refundMenu=new ContextMenu{FlowDirection=FlowDirection.RightToLeft};
        var refundAction=new MenuItem{Header="إضافة أو تعديل مبلغ في دفعة الإعادة"};
        refundMenu.Items.Add(refundAction);
        string? copyValue=null;
        var copy=new MenuItem{Header="نسخ قيمة الحقل"};refundMenu.Items.Add(copy);
        copy.Click+=(_,_)=>{if(copyValue!=null)Guard(()=>Clipboard.SetText(copyValue));};
        list.ContextMenuOpening+=(_,e)=>{
            copyValue=FieldCopy.ValueAt(e.OriginalSource as DependencyObject);copy.IsEnabled=!string.IsNullOrEmpty(copyValue);
            if(e.OriginalSource is DependencyObject el && ItemsControl.ContainerFromElement(list,el) is DataGridRow row)
                list.SelectedItem=row.Item;
            else if(e.CursorLeft>=0){e.Handled=true;return;}
            if(list.SelectedItem is not Store.SearchResult)e.Handled=true;
        };
        refundAction.Click+=async (_,_)=>{if(list.SelectedItem is Store.SearchResult hit)await SearchRefund(hit.Name,null,this);};
        list.ContextMenu=refundMenu;
        list.PreviewMouseLeftButtonUp+=async (_,e)=>{
            if(e.OriginalSource is DependencyObject element && ItemsControl.ContainerFromElement(list,element) is DataGridRow {Item:Store.SearchResult hit})
                await Open(hit);
        };
        list.PreviewKeyDown+=async (_,e)=>{
            if(e.Key==System.Windows.Input.Key.Enter && list.SelectedItem is Store.SearchResult hit){e.Handled=true;await Open(hit);}
        };
        input.Focus();
    }
    Task workPageLoad=Task.CompletedTask;
    async Task WorkPage(string key)
    {
        if(key==SearchName){SearchAll();return;}
        if(store.Active==null){CanvasArea.Children.Add(Text("استورد نسخة احتياطية أو ملف Excel من الإعدادات."));return;}
        var requestGeneration=generation;var month=SelectedMonth?.Name??"";var revision=store.Active;var loading=Text("جارٍ إعداد البيانات…",16);CanvasArea.Children.Add(loading);
        try{
            if(key==AuditName){
                string? initial=auditFilter;auditFilter=null;var result=await Audit(month);
                if(page!=key||revision!=store.Active||(SelectedMonth?.Name??"")!=month)return;
                CanvasArea.Children.Remove(loading);
                var dock=new DockPanel();var filter=Choices(new[]{"كل الحالات"}.Concat(result.Rows.Select(r=>ReviewTitle(r.Kind)).Distinct()));if(initial!=null)filter.SelectedItem=initial;
                var head=FilterBar(("حالة المراجعة",filter,0));DockPanel.SetDock(head,Dock.Top);dock.Children.Add(head);
                var list=GridView(("الحالة","Kind",1),("المنتسب","Name",1.4),("التفاصيل","Detail",3),("السابق","Before",.8),("الحالي","After",.8));
                void Draw(){list.ItemsSource=result.Rows.Where(r=>filter.SelectedIndex<=0||ReviewTitle(r.Kind)==(string)filter.SelectedItem).ToList();}
                var preview = ButtonFor("معاينة الكشف",()=>Guard(()=>{
                    var rows = list.ItemsSource.Cast<A.SalaryReviewRow>().Select(r=>new object?[]{r.Kind,r.Name,r.Detail,r.Before,r.After}).ToList();
                    var sheet = new Sheet(AuditName,AuditName+" · "+month+" · "+filter.SelectedItem,["الحالة","المنتسب","التفاصيل","السابق","الحالي"],rows,4,0,ShowTotal:false);
                    new ReportWindow(new Statement(ReportHeader(),[sheet])){Owner=this}.ShowDialog();
                }),"\uE890");
                var buttons=Bar(preview);DockPanel.SetDock(buttons,Dock.Top);dock.Children.Add(buttons);
                filter.SelectionChanged+=(_,_)=>Draw();Draw();dock.Children.Add(list);CanvasArea.Children.Add(dock);Status.Text=string.Join("　·　",result.Notes.Prepend(result.Rows.Count+" حالة"));return;
            }
            if(key==RefundTransfers){
                var saved=await Task.Run(()=>(Source:store.Snapshot(),Batches:store.Refunds()));
                if(page!=key || revision!=store.Active || !CanvasArea.Children.Contains(loading))return;
                var history=saved.Batches.Where(b=>b.ArchiveId!="draft").ToArray();
                if(history.Length>0)RefundFiles(saved.Source,month,history[0],history);
                else await OpenRefundEditor(saved.Source,month,saved.Batches.FirstOrDefault(b=>b.ArchiveId=="draft"),hasHistory:false);
                return;
            }
            if(key is CurrentTransfers or DeferredTransfers){
                var data=await LoadTransferMonth(revision!,month);
                bool held=key==DeferredTransfers;
                ContractBatch? proposal=null;string? issue=null;
                if(held && data.Batch==null){
                    try{proposal=await data.Proposal.Value;}
                    catch(System.IO.InvalidDataException error){issue=error.Message;}
                }
                if(!CanvasArea.Children.Contains(loading)||page!=key||revision!=store.Active||(SelectedMonth?.Name??"")!=month)return;
                CanvasArea.Children.Remove(loading);
                TransferDesk(data.Source,month,held,data.Batch,held&&data.Batch!=null?data.Batch.Plan(true):data.Plan,proposal,issue);
                return;
            }
            var snapshot=await Task.Run(()=>key==TrustName?store.Snapshot():store.Snapshot(month));
            if(!CanvasArea.Children.Contains(loading)||page!=key||revision!=store.Active||(SelectedMonth?.Name??"")!=month)return;
            CanvasArea.Children.Remove(loading);
            if(key==TrustName){TrustDesk(snapshot);return;}
            if(key==HealthName||key==FundName){SpecialDesk(snapshot,month,key==HealthName);return;}
            var funding=key==FundingName&&snapshot.Employees.Count>0?await Task.Run(()=>WorkReports.FundingData(snapshot,month)):null;
            if(key==FundingName&&funding==null){
                var history=Bar();FundingActions(snapshot,month,history,null);
                CanvasArea.Children.Add(Column(Text("لا توجد رواتب محفوظة لهذه الفترة."),history));return;
            }
            var report=funding?.Report(A.ReportSheet.HeaderFor(snapshot),Store.IsFundingApproved(snapshot,funding))??await Task.Run(()=>WorkReports.Command(snapshot,month));
            if(page!=key||revision!=store.Active||(SelectedMonth?.Name??"")!=month)return;
            var panel=new DockPanel();
            var reportBar=Bar(Text(month,18,true),ButtonFor("معاينة الكشف",()=>new ReportWindow(report){Owner=this}.ShowDialog(),"\uE890",true));
            if(key==FundingName)FundingActions(snapshot,month,reportBar,funding);
            DockPanel.SetDock(reportBar,Dock.Top);panel.Children.Add(reportBar);
            var tabs=new TabControl();
            foreach(var sheet in report.Sheets){
                var columns=sheet.Columns.Select((name,i)=>(name,"["+i+"]",i==1?2.1:1)).ToArray();
                var table=GridView(columns);
                table.ItemsSource=sheet.Rows.Select(row=>row.Select(v=>v is decimal amount?Arabic.Money(amount):v?.ToString()??"").ToArray()).ToList();
                tabs.Items.Add(new TabItem{Header=sheet.Name,Content=table});
            }
            panel.Children.Add(tabs);CanvasArea.Children.Add(panel);
        }catch(Exception e){if(generation==requestGeneration && page==key && revision==store.Active){CanvasArea.Children.Clear();CanvasArea.Children.Add(Text(e.Message,16));}}
    }
    void SpecialDesk(A.BackupSnapshot s,string month,bool health)
    {
        var items=s.Employees.Where(e=>e.Month==month).SelectMany(e=>e.Items==null?[]:System.Text.Json.JsonSerializer.Deserialize<Dictionary<string,decimal>>(e.Items)!.Keys.AsEnumerable())
            .Distinct().Where(t=>health?A.HealthCareReport.Applies(t):A.FundReport.Applies(t)).ToArray();
        if(items.Length==0){CanvasArea.Children.Add(Text("لم يُعثر على بند لهذا الكشف في رواتب الشهر."));return;}
        var picker=Choices(items);var dock=new DockPanel();var top=Column(FilterBar(("بند الرواتب",picker,1)));DockPanel.SetDock(top,Dock.Top);dock.Children.Add(top);
        var rows=GridView(("الاسم أو الفئة","Name",2),("الحالة أو العدد","State",1.4),("المبلغ","Value",1));dock.Children.Add(rows);CanvasArea.Children.Add(dock);
        var actions=Bar();top.Children.Add(actions);
        void Draw(){
            string item=(string)picker.SelectedItem;actions.Children.Clear();Func<byte[]> excel,pdf;string name;
            if(health){
                var data=A.HealthCareReport.Build(s,month,item);var title=A.HealthCareReport.Title(s,month,false);var other=A.HealthCareReport.Title(s,month,true);name=A.HealthCareReport.FileName(month);
                rows.ItemsSource=data.Subscribers.Select(r=>new{Name=r.Name,State=A.HealthCareReport.IsExact(r.Amount)?"مشترك":"مبلغ غير منتظم",Value=Arabic.Money(r.Amount)}).Concat(data.Others.Select(r=>new{Name=r.Name,State="غير مشترك",Value="—"})).ToList();
                excel=()=>A.HealthCareReport.Excel(data,title,other);pdf=()=>A.HealthCareReport.Pdf(data,title,other,Fonts(),DateTime.Now);
            }else{
                var data=A.FundReport.Summary(s,month,item);string title=A.FundReport.Title(s,month);name=A.FundReport.FileName(month);
                rows.ItemsSource=data.Select(r=>new{Name=r.Category.Name,State=r.Count,Value=Arabic.Money(r.Total)});excel=()=>A.FundReport.Excel(data,title);pdf=()=>A.FundReport.Pdf(data,title,Fonts(),DateTime.Now);
            }
            actions.Children.Add(PreviewButton(name,()=>Task.Run(pdf)));actions.Children.Add(ButtonFor("تصدير Excel",()=>_=SaveFile(name,"xlsx",excel)));
        }
        picker.SelectionChanged+=(_,_)=>Guard(Draw);Draw();
    }
    void TrustDesk(A.BackupSnapshot s)
    {
        var ledger=A.SalaryTrust.ReadFromBackup(s);
        var dock=new DockPanel();var q=new TextBox();var g=GridView(("الاسم","Name",2),("عدد القيود","Count",1),("المجموع","Total",1),("المتبقي المؤكد","Remaining",1));
        var periods=ledger.Entries.Select(e=>(e.Year,e.Month)).Distinct().OrderByDescending(m=>m.Year).ThenByDescending(m=>m.Month).ToArray();
        var month=Choices(new[]{"كل الأشهر"}.Concat(periods.Select(m=>A.SalaryTrust.MonthLabel(m.Year,m.Month))));
        var status=Choices(A.SalaryTrust.Filters.Select(f=>f.Title));
        var top=Column(FilterBar(("بحث عن منتسب",q,1),("الشهر",month,0),("حالة الصرف",status,0)));
        DockPanel.SetDock(top,Dock.Top);dock.Children.Add(top);dock.Children.Add(g);CanvasArea.Children.Add(dock);
        A.SalaryTrustEntry[] Visible(){
            var key=Arabic.Match(q.Text);
            var filter=A.SalaryTrust.Filters[status.SelectedIndex].Filter;
            return ledger.Entries.Where(e=>Arabic.Match(e.Name).Contains(key)&&A.SalaryTrust.Includes(filter,e)&&
                (month.SelectedIndex==0 || (e.Year,e.Month)==periods[month.SelectedIndex-1])).OrderByDescending(e=>e.Year).ThenByDescending(e=>e.Month).ToArray();
        }
        void Draw()=>g.ItemsSource=Visible().GroupBy(e=>Arabic.Match(e.Name)).Select(group=>new TrustPerson(group.Key,group.First().Name,group.Count(),
            Arabic.Money(group.Sum(e=>e.Amount)),Arabic.Money(A.SalaryTrust.Outstanding(group)))).ToList();
        q.TextChanged+=(_,_)=>Draw();month.SelectionChanged+=(_,_)=>Draw();status.SelectionChanged+=(_,_)=>Draw();Draw();
        A.ReportSheet Sheet(A.SalaryTrustEntry[] entries,string query)=>A.SalaryTrustReports.Sheet(ledger,entries,A.ReportSheet.HeaderFor(s),month.SelectedItem.ToString()!,status.SelectedItem.ToString()!,query);
        var pdf=PreviewButton(TrustName,()=>{var sheet=Sheet(Visible(),q.Text);return Task.Run(()=>A.SalaryTrustReports.Pdf(sheet,Fonts(),DateTime.Now));},printAndSave:true);
        var excel=ButtonFor("تصدير Excel",()=>{var entries=Visible();var sheet=Sheet(entries,q.Text);_=SaveFile(TrustName,"xlsx",()=>A.SalaryTrustReports.Excel(sheet,entries));});
        A.SalaryTrustEntry[] SelectedPeople(){
            var keys=g.SelectedItems.Cast<TrustPerson>().Select(p=>p.Key).ToHashSet();
            return Visible().Where(e=>keys.Contains(Arabic.Match(e.Name))).ToArray();
        }
        g.SelectionMode=DataGridSelectionMode.Extended;g.SelectionUnit=DataGridSelectionUnit.FullRow;
        var link=ButtonFor("ربط القيود الظاهرة للمحددين بكتاب",()=>Guard(()=>TrustDocument(s,SelectedPeople())),"\uE71B");link.IsEnabled=false;
        g.SelectionChanged+=(_,_)=>link.IsEnabled=g.SelectedItems.Count>0;top.Children.Add(Bar(pdf,excel,link));
        g.MouseDoubleClick+=(_,e)=>{if(e.OriginalSource is DependencyObject el && ItemsControl.ContainerFromElement(g,el) is DataGridRow{Item:TrustPerson person}){
            var entries=Visible().Where(row=>Arabic.Match(row.Name)==person.Key).ToArray();
            TrustDetails(s,person.Name,entries,Sheet(entries,person.Name));
        }};

    }
    record TrustPerson(string Key,string Name,int Count,string Total,string Remaining);
}
