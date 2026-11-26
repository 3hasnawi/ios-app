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
        var dialog=new Window{Title=title,Owner=this,SizeToContent=SizeToContent.WidthAndHeight,WindowStartupLocation=WindowStartupLocation.CenterOwner,MaxHeight=SystemParameters.WorkArea.Height*.9,FlowDirection=FlowDirection.RightToLeft};
        var stack=Column(Text(title,19,true));foreach(var f in fields)stack.Children.Add(f);
        stack.Children.Add(Bar(ButtonFor("متابعة",()=>dialog.DialogResult=true,"\uE73E",true),ButtonFor("إلغاء",()=>dialog.DialogResult=false)));
        dialog.Content=new ScrollViewer{Content=Box(stack),VerticalScrollBarVisibility=ScrollBarVisibility.Auto};return dialog.ShowDialog()==true;
    }
    async Task ImportExcel()
    {
        var dialog=new OpenFileDialog{Filter="ملفات Excel|*.xlsx"};if(dialog.ShowDialog(this)!=true)return;
        var expected=store.Active;
        var label=Path.GetFileNameWithoutExtension(dialog.FileName);
        var field=new TextBox{Text=Arabic.Period(label)>0?label:SelectedMonth?.Name??""};
        if(!Form("استيراد الرواتب والاستقطاعات",Labeled("الشهر والسنة",field,330)))return;
        try{
            string month=field.Text;var path=dialog.FileName;
            var preview=await Task.Run(()=>store.PreviewExcel(path,month));
            if(!Form("مراجعة الاستيراد",Text(month+"　·　"+preview.Employees.Count+" راتب　·　"+preview.Deductions.Count+" استقطاع"),Text("تُستبدل بيانات النوع الموجود في الملف لهذا الشهر. عند إدخال رواتب تُحفظ أحدث فترتين للرواتب.")))return;
            await Working(()=>store.ApplyExcel(preview,expected),"حُفظ الاستيراد وتحقق البرنامج من النسخة الجديدة.",()=>{Reload();ShowPage("الرئيسية");});
        }catch(Exception e){Error(e);}
    }
    string? auditFilter;
    void SearchAll()
    {
        var dock=new DockPanel();var input=new TextBox();var title=Text("ابحث بالاسم أو اسم الأم أو الرقم الإحصائي أو الجهة أو الإضبارة",15);
        var top=Column(title,Labeled("بحث في الرواتب والملاك والاستقطاعات والأمانات",input,430));DockPanel.SetDock(top,Dock.Top);dock.Children.Add(top);
        var list=GridView(("المصدر","Kind",.65),("الاسم","Name",1.5),("الشهر","Month",.9),("التفاصيل","Info",1.9));dock.Children.Add(list);CanvasArea.Children.Add(dock);
        int serial=0;input.TextChanged+=async (_,_)=>{var q=input.Text;int current=++serial;await Task.Delay(180);if(serial!=current||page!=SearchName)return;
            try{var hits=await Task.Run(()=>store.SearchPeople(q));if(current!=serial||page!=SearchName)return;list.ItemsSource=hits.Take(300);Status.Text=hits.Count>300?"تظهر أول 300 نتيجة. ضيّق البحث.":hits.Count+" نتيجة";}catch(Exception e){Error(e);}};
        list.MouseDoubleClick+=(_,_)=>{if(list.SelectedItem is not Store.SearchResult hit)return;if(hit.Kind=="أمانات")ShowPage(TrustName);else if(hit.Kind=="استقطاع"){ShowPage("بحث الاستقطاعات");search!.Text=hit.Name;}else Form(hit.Name,Text(hit.Kind+"　·　"+hit.Month),Text(hit.Info));};input.Focus();
    }
    async void WorkPage(string key)
    {
        if(key==SearchName){SearchAll();return;}
        if(store.Active==null){CanvasArea.Children.Add(Text("افتح نسخة الآيفون أو استورد ملف Excel من الإعدادات."));return;}
        var month=SelectedMonth?.Name??"";var revision=store.Active;var loading=Text("جارٍ إعداد البيانات…",16);CanvasArea.Children.Add(loading);
        try{
            if(key==AuditName){
                string? initial=auditFilter;auditFilter=null;var result=await Audit(month);
                if(page!=key||revision!=store.Active||SelectedMonth?.Name!=month)return;
                CanvasArea.Children.Remove(loading);
                var dock=new DockPanel();var filter=Choices(new[]{"كل الحالات"}.Concat(result.Rows.Select(r=>r.Kind).Distinct()));if(initial!=null)filter.SelectedItem=initial;
                var head=Bar(Labeled("حالة المراجعة",filter,250),ButtonFor("الرئيسية",()=>ShowPage("الرئيسية")));DockPanel.SetDock(head,Dock.Top);dock.Children.Add(head);
                var list=GridView(("الحالة","Kind",1),("المنتسب","Name",1.4),("التفاصيل","Detail",3),("السابق","Before",.8),("الحالي","After",.8));
                void Draw(){list.ItemsSource=result.Rows.Where(r=>filter.SelectedIndex<=0||r.Kind==(string)filter.SelectedItem).ToList();}
                filter.SelectionChanged+=(_,_)=>Draw();Draw();dock.Children.Add(list);CanvasArea.Children.Add(dock);Status.Text=string.Join("　·　",result.Notes.Prepend(result.Rows.Count+" حالة"));return;
            }
            var snapshot=await Task.Run(()=>key==TrustName?store.Snapshot():store.Snapshot(month));
            if(page!=key||revision!=store.Active||SelectedMonth?.Name!=month)return;
            CanvasArea.Children.Remove(loading);
            if(key==TransfersName){TransferDesk(snapshot,month);return;}
            if(key==TrustName){TrustDesk(snapshot);return;}
            if(key==HealthName||key==FundName){SpecialDesk(snapshot,month,key==HealthName);return;}
            var report=await Task.Run(()=>key==FundingName?WorkReports.Funding(snapshot,month):WorkReports.Command(snapshot,month));
            if(page!=key||revision!=store.Active||SelectedMonth?.Name!=month)return;
            var content=Column(Text(key+"　·　"+month,20,true));
            foreach(var sheet in report.Sheets){content.Children.Add(Text(sheet.Title,16,true));foreach(var row in sheet.Rows)content.Children.Add(Bar(Text(row[1]?.ToString()??"",14),Text(Arabic.Money((decimal)row[sheet.AmountColumn]!),16,true)));}
            content.Children.Add(ButtonFor("معاينة وتصدير",()=>new ReportWindow(report){Owner=this}.ShowDialog(),"\uE890",true));
            CanvasArea.Children.Add(new ScrollViewer{Content=Box(content),VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        }catch(Exception e){if(page==key){CanvasArea.Children.Clear();CanvasArea.Children.Add(Text(e.Message,16));}}
    }
    void SpecialDesk(A.BackupSnapshot s,string month,bool health)
    {
        var items=s.Employees.Where(e=>e.Month==month).SelectMany(e=>e.Items==null?[]:System.Text.Json.JsonSerializer.Deserialize<Dictionary<string,decimal>>(e.Items)!.Keys.AsEnumerable())
            .Distinct().Where(t=>health?A.HealthCareReport.Applies(t):A.FundReport.Applies(t)).ToArray();
        if(items.Length==0){CanvasArea.Children.Add(Text("لم يُعثر على بند لهذا الكشف في رواتب الشهر."));return;}
        var picker=Choices(items);var dock=new DockPanel();var top=Column(Labeled("بند الرواتب",picker,330));DockPanel.SetDock(top,Dock.Top);dock.Children.Add(top);
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
            actions.Children.Add(ButtonFor("تصدير PDF",()=>_=SaveFile(name,"pdf",pdf)));actions.Children.Add(ButtonFor("تصدير Excel",()=>_=SaveFile(name,"xlsx",excel)));
        }
        picker.SelectionChanged+=(_,_)=>Guard(Draw);Draw();
    }
    void TrustDesk(A.BackupSnapshot s)
    {
        var ledger=A.SalaryTrust.ReadFromBackup(s);
        var dock=new DockPanel();var q=new TextBox();var g=GridView(("الاسم","Name",2),("عدد القيود","Count",1),("المجموع","Total",1),("المتبقي المؤكد","Remaining",1));
        var top=Column(Labeled("بحث عن منتسب",q,300));DockPanel.SetDock(top,Dock.Top);dock.Children.Add(top);dock.Children.Add(g);CanvasArea.Children.Add(dock);
        var byPerson=ledger.Entries.GroupBy(e=>Arabic.Match(e.Name)).ToDictionary(k=>k.Key,k=>k.OrderByDescending(e=>e.Year*100+e.Month).ToArray());
        void Draw(){g.ItemsSource=byPerson.Where(p=>p.Key.Contains(Arabic.Match(q.Text))).Select(p=>new TrustPerson(p.Key,p.Value[0].Name,p.Value.Length,Arabic.Money(p.Value.Sum(e=>e.Amount)),Arabic.Money(A.SalaryTrust.Outstanding(p.Value)))).ToList();}
        q.TextChanged+=(_,_)=>Draw();Draw();
        g.MouseDoubleClick+=(_,_)=>{if(g.SelectedItem is not TrustPerson p)return;var grid=GridView(("الشهر","Month",1),("المبلغ","Amount",1),("الحالة","Status",1),("تاريخ الصرف","Date",1),("الملاحظات","Note",1.6));
            grid.Width=720;grid.Height=320;grid.ItemsSource=byPerson[p.Key].Select(e=>new{Month=A.SalaryTrust.MonthLabel(e.Year,e.Month),Amount=Arabic.Money(e.Amount),Status=e.ImportedStatus,Date=e.ImportedPaymentDate??string.Join("، ",e.Payments.Select(x=>x.BookDate)),Note=string.Join(" · ",new[]{e.CaseStatus,e.Note}.Where(x=>x.Length>0).Distinct())});Form(p.Name,grid,Text("المجموع　"+p.Total));};
        A.ReportSheet Sheet(A.SalaryTrustEntry[] entries)=>A.SalaryTrustReports.Sheet(ledger,entries,A.ReportSheet.HeaderFor(s),"كل الأشهر","كل الحالات","");
        A.SalaryTrustEntry[] Selected()=>ledger.Entries.Where(e=>Arabic.Match(e.Name).Contains(Arabic.Match(q.Text))).ToArray();
        top.Children.Add(Bar(ButtonFor("تصدير PDF",()=>{var entries=Selected();_=SaveFile(TrustName,"pdf",()=>A.SalaryTrustReports.Pdf(Sheet(entries),Fonts(),DateTime.Now));}),ButtonFor("تصدير Excel",()=>{var entries=Selected();_=SaveFile(TrustName,"xlsx",()=>A.SalaryTrustReports.Excel(Sheet(entries),entries));})));
    }
    record TrustPerson(string Key,string Name,int Count,string Total,string Remaining);
}
