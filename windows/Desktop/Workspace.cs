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
    Button PreviewButton(string title,Func<Task<byte[]>> prepare,Func<bool>? current=null,string caption="معاينة الكشف")
    {
        Button button = ButtonFor(caption,()=>{}, "\uE890");
        button.Click += async (_,_) => {
            button.IsEnabled = false;
            try {
                var bytes = await prepare();
                if (button.IsLoaded && (current?.Invoke() ?? true))
                    new LetterPreview(bytes,title){Owner=Window.GetWindow(button)}.ShowDialog();
            } catch (Exception ex) {
                if (button.IsLoaded) Error(ex);
            } finally {
                button.IsEnabled = true;
            }
        };
        return button;
    }
    async Task SaveFile(string name,string ext,Func<byte[]> generate,Action? record=null,Action? finish=null)
    {
        var dialog=new SaveFileDialog{Filter=ext+"|*."+ext,FileName=name,DefaultExt=ext};if(dialog.ShowDialog(this)!=true)return;
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
    void SearchAll()
    {
        var dock=new DockPanel();var input=new TextBox();var title=Text("ابحث بالاسم أو اسم الأم أو الرقم الإحصائي أو الجهة أو الإضبارة",15);
        var top=Column(title,FilterBar(("بحث في الرواتب والملاك والاستقطاعات والأمانات",input,1)));DockPanel.SetDock(top,Dock.Top);dock.Children.Add(top);
        var list=GridView(("المصدر","Kind",.65),("الاسم","Name",1.5),("الشهر","Month",.9),("التفاصيل","Info",1.9));dock.Children.Add(list);CanvasArea.Children.Add(dock);
        int serial=0;input.TextChanged+=async (_,_)=>{var q=input.Text;int current=++serial;await Task.Delay(180);if(serial!=current||page!=SearchName)return;
            try{var hits=await Task.Run(()=>store.SearchPeople(q));if(current!=serial||page!=SearchName)return;list.ItemsSource=hits.Take(300);Status.Text=hits.Count>300?"تظهر أول 300 نتيجة. ضيّق البحث.":hits.Count+" نتيجة";}catch(Exception e){Error(e);}};
        list.MouseDoubleClick+=async (_,_)=>{if(list.SelectedItem is Store.SearchResult hit)await OpenPerson(hit);};input.Focus();
    }
    Task workPageLoad=Task.CompletedTask;
    async Task WorkPage(string key)
    {
        if(key==TransfersName){TransferHome();return;}
        if(key==SearchName){SearchAll();return;}
        if(store.Active==null){CanvasArea.Children.Add(Text("افتح نسخة الآيفون أو استورد ملف Excel من الإعدادات."));return;}
        var month=SelectedMonth?.Name??"";var revision=store.Active;var loading=Text("جارٍ إعداد البيانات…",16);CanvasArea.Children.Add(loading);
        try{
            if(key==AuditName){
                string? initial=auditFilter;auditFilter=null;var result=await Audit(month);
                if(page!=key||revision!=store.Active||(SelectedMonth?.Name??"")!=month)return;
                CanvasArea.Children.Remove(loading);
                var dock=new DockPanel();var filter=Choices(new[]{"كل الحالات"}.Concat(result.Rows.Select(r=>ReviewTitle(r.Kind)).Distinct()));if(initial!=null)filter.SelectedItem=initial;
                var head=FilterBar(("حالة المراجعة",filter,0),("",ButtonFor("الرئيسية",()=>ShowPage("الرئيسية")),0));DockPanel.SetDock(head,Dock.Top);dock.Children.Add(head);
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
                RefundHome(saved.Source,month,saved.Batches);
                return;
            }
            var snapshot=await Task.Run(()=>key==TrustName?store.Snapshot():store.Snapshot(month));
            if(!CanvasArea.Children.Contains(loading)||page!=key||revision!=store.Active||(SelectedMonth?.Name??"")!=month)return;
            CanvasArea.Children.Remove(loading);
            if(key is CurrentTransfers or DeferredTransfers){TransferDesk(snapshot,month,key==DeferredTransfers);return;}
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
        }catch(Exception e){if(page==key){CanvasArea.Children.Clear();CanvasArea.Children.Add(Text(e.Message,16));}}
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
        var top=Column(FilterBar(("بحث عن منتسب",q,1)));DockPanel.SetDock(top,Dock.Top);dock.Children.Add(top);dock.Children.Add(g);CanvasArea.Children.Add(dock);
        var byPerson=ledger.Entries.GroupBy(e=>Arabic.Match(e.Name)).ToDictionary(k=>k.Key,k=>k.OrderByDescending(e=>e.Year*100+e.Month).ToArray());
        void Draw(){g.ItemsSource=byPerson.Where(p=>p.Key.Contains(Arabic.Match(q.Text))).Select(p=>new TrustPerson(p.Key,p.Value[0].Name,p.Value.Length,Arabic.Money(p.Value.Sum(e=>e.Amount)),Arabic.Money(A.SalaryTrust.Outstanding(p.Value)))).ToList();}
        q.TextChanged+=(_,_)=>Draw();Draw();
        g.MouseDoubleClick+=(_,_)=>{if(g.SelectedItem is not TrustPerson p)return;var grid=GridView(("الشهر","Month",1),("المبلغ","Amount",1),("الحالة","Status",1),("تاريخ الصرف","Date",1),("الملاحظات","Note",1.6));
            grid.Width=720;grid.Height=320;grid.ItemsSource=byPerson[p.Key].Select(e=>new{Month=A.SalaryTrust.MonthLabel(e.Year,e.Month),Amount=Arabic.Money(e.Amount),Status=e.ImportedStatus,Date=e.ImportedPaymentDate??string.Join("، ",e.Payments.Select(x=>x.BookDate)),Note=string.Join(" · ",new[]{e.CaseStatus,e.Note}.Where(x=>x.Length>0).Distinct())});Form(p.Name,grid,Text("المجموع　"+p.Total),PreviewButton(TrustName+" - "+p.Name,()=>Task.Run(()=>A.SalaryTrustReports.Pdf(Sheet(byPerson[p.Key],p.Name),Fonts(),DateTime.Now))));};
        A.ReportSheet Sheet(A.SalaryTrustEntry[] entries,string query)=>A.SalaryTrustReports.Sheet(ledger,entries,A.ReportSheet.HeaderFor(s),"كل الأشهر","كل الحالات",query);
        A.SalaryTrustEntry[] Selected(string query)=>ledger.Entries.Where(e=>Arabic.Match(e.Name).Contains(Arabic.Match(query))).ToArray();
        top.Children.Add(Bar(PreviewButton(TrustName,()=>{var query=q.Text;var entries=Selected(query);return Task.Run(()=>A.SalaryTrustReports.Pdf(Sheet(entries,query),Fonts(),DateTime.Now));}),ButtonFor("تصدير Excel",()=>{var query=q.Text;var entries=Selected(query);_=SaveFile(TrustName,"xlsx",()=>A.SalaryTrustReports.Excel(Sheet(entries,query),entries));})));
    }
    record TrustPerson(string Key,string Name,int Count,string Total,string Remaining);
}
