using System.Windows;
using System.Windows.Controls;
using A=SalaryDesktop.Core;

namespace AnbarPayroll.Desktop;
public partial class MainWindow
{
    async Task OpenPerson(Store.SearchResult hit,Action<FrameworkElement>? capture=null,Func<bool>? current=null)
    {
        var revision=store.Active;
        var window=new Window{Owner=this,Title=hit.Name,WindowStartupLocation=WindowStartupLocation.CenterOwner,
            Width=Math.Min(900,SystemParameters.WorkArea.Width*.94),Height=Math.Min(550,SystemParameters.WorkArea.Height*.94),
            MinWidth=350,MinHeight=280,FlowDirection=FlowDirection.RightToLeft,
            Content=Box(Column(Text(hit.Name,21,true),Text("جارٍ فتح ملف المنتسب…")))};
        async Task Load(){
            var profile=await Task.Run(()=>store.EmployeeDetails(hit.Name));
            if(!window.IsVisible)return;
            if(current?.Invoke()==false || revision!=store.Active || (!SearchIsOpen&&page!=SearchName)){window.Close();return;}
            var ledger=profile.Trust;var trust=ledger.Entries;var header=profile.Header;
            var members=profile.Staff;var deductions=profile.Deductions;
            var tabs=new TabControl();
            DataGrid ProfileGrid(params (string Title,string Field,double Width)[] columns){
                var grid=GridView(columns);
                double weights=columns.Sum(c=>c.Width);
                grid.SizeChanged+=(_,_)=>{
                    double available=Math.Max(0,grid.ActualWidth-24);
                    for(int i=0;i<columns.Length;i++)grid.Columns[i].Width=new DataGridLength(Math.Floor(available*columns[i].Width/weights));
                };
                return grid;
            }
            void Tab(string title,UIElement body)=>tabs.Items.Add(new TabItem{Header=title,Content=body});
            var salaries=profile.Months;
            if(salaries.Length>0){
                var months=Choices(salaries.Select(m=>m.Month));if(SelectedMonth is {} selected && salaries.Any(m=>m.Month==selected.Name))months.SelectedItem=selected.Name;
                var pane=new DockPanel();
                var preview=PreviewButton("تقرير راتب - "+profile.Name,()=>{
                    var month=salaries[months.SelectedIndex];
                    return Task.Run(()=>A.SalaryReportPdf.Generate([A.SalaryReports.Make(month,true,false)],Fonts(),Logo(),DateTime.Now));
                },caption:"معاينة تقرير الراتب",printAndSave:true);
                var actions=Bar(months,preview);DockPanel.SetDock(actions,Dock.Top);pane.Children.Add(actions);
                var table=ProfileGrid(("البيان","Title",1),("القيمة","Value",1.4));pane.Children.Add(table);
                void Draw(){
                    var m=salaries[months.SelectedIndex];
                    preview.IsEnabled=m.Employee!=null;
                    if(m.Employee is not {} employee){table.ItemsSource=new[]{new A.ProfileLine("تشابه أسماء","أكثر من راتب بالاسم نفسه في هذا الشهر")};return;}
                    var slip=A.PersonProfiles.Payslip(m);var lines=new List<A.ProfileLine>{new("الرتبة",employee.Rank),new("محل العمل",employee.Workplace)};
                    lines.AddRange(slip.Info);lines.AddRange(slip.Allowances);if(slip.Gross!=null)lines.Add(slip.Gross);
                    lines.AddRange(slip.Deductions);if(slip.TotalDeductions!=null)lines.Add(slip.TotalDeductions);
                    lines.Add(new("صافي الراتب",slip.Net is {} net?Arabic.Money(net):"غير متوفر"));
                    table.ItemsSource=lines;
                }
                months.SelectionChanged+=(_,_)=>Draw();Draw();Tab("قسيمة الراتب",pane);
            }
            if(deductions.Count>0){
                var grid=ProfileGrid(("الشهر","Month",1),("جهة التنفيذ","Agency",1.3),("المبلغ","Value",.7),("رقم وتاريخ الإضبارة","Reference",1.4));
                grid.ItemsSource=deductions;
                var preview=ButtonFor("كشف استقطاعات المنتسب",()=>Guard(()=>OpenEmployee(grid.SelectedItem as Deduction??deductions[0],Window.GetWindow(grid),deductions)),"\uE890");
                var pane=new DockPanel();DockPanel.SetDock(preview,Dock.Top);pane.Children.Add(preview);pane.Children.Add(grid);
                Tab("الاستقطاعات",pane);
            }
            if(trust.Count>0){
                var grid=ProfileGrid(("الشهر","Month",1),("المبلغ","Amount",.8),("الحالة","Status",.9),("تاريخ الصرف","Date",.9),("الملاحظات","Note",1.4));
                grid.ItemsSource=trust.Select(e=>new{Month=A.SalaryTrust.MonthLabel(e.Year,e.Month),Amount=Arabic.Money(e.Amount),Status=A.SalaryTrust.PaymentStatus(e),Date=e.ImportedPaymentDate??string.Join("، ",e.Payments.Select(p=>p.BookDate)),Note=string.Join(" · ",new[]{e.CaseStatus,e.Note}.Where(x=>x.Length>0).Distinct())});
                var body=new DockPanel();var sum=Text("مجموع الأمانات　"+Arabic.Money(trust.Sum(e=>(decimal)e.Amount)),16,true);string reportName="كشف أمانات الرواتب - "+profile.Name;
                A.ReportSheet Sheet()=>A.SalaryTrustReports.Sheet(ledger,trust,header,"كل الأشهر","كل الحالات",profile.Name);
                var actions=Bar(sum,PreviewButton(reportName,()=>Task.Run(()=>A.SalaryTrustReports.Pdf(Sheet(),Fonts(),DateTime.Now)),printAndSave:true),
                    ButtonFor("تصدير Excel",()=>_=SaveFile(reportName,"xlsx",()=>A.SalaryTrustReports.Excel(Sheet(),trust),owner:Window.GetWindow(grid))));
                DockPanel.SetDock(actions,Dock.Top);body.Children.Add(actions);body.Children.Add(grid);Tab("أمانات الرواتب",body);
            }
            foreach(var member in members){
                var fields=ProfileGrid(("الحقل","Label",1),("القيمة","Value",1.6));
                var basics=new A.StaffField[]{new("الرتبة",member.Rank),new("محل العمل",member.Workplace),new("الحالة",member.Status),
                    new("اسم الأم",member.Mother),new("الرقم الإحصائي",member.Stat),new("الرقم الموحد",member.Unified)};
                fields.ItemsSource=basics.Concat(A.StaffRecords.Sections(member).SelectMany(s=>s.Fields))
                    .Where(f=>!string.IsNullOrWhiteSpace(f.Value)).DistinctBy(f=>(Arabic.Key(f.Label),f.Value)).ToList();
                var preview=PreviewButton("تقرير الملاك - "+member.Name,()=>Task.Run(()=>A.StaffReportPdf.Generate(member,Fonts(),Logo(),DateTime.Now)),
                    caption:"معاينة تقرير الملاك",printAndSave:true);
                var pane=new DockPanel();DockPanel.SetDock(preview,Dock.Top);pane.Children.Add(preview);pane.Children.Add(fields);
                Tab(members.Count==1?"الملاك":"ملاك "+member.Stat,pane);
            }
            var target=hit.Payroll?"قسيمة الراتب":hit.Deductions?"الاستقطاعات":hit.Staff?"الملاك":"أمانات الرواتب";
            foreach(TabItem item in tabs.Items)if(Equals(item.Header,target)){tabs.SelectedItem=item;break;}
            var panel=new DockPanel();var title=Text(profile.Name,21,true);DockPanel.SetDock(title,Dock.Top);
            panel.Children.Add(title);panel.Children.Add(tabs);
            window.Title=profile.Name;window.Content=Box(panel);
            if(capture!=null){
                await window.Dispatcher.InvokeAsync(()=>window.UpdateLayout(),System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                if(salaries.Length>0&&tabs.SelectedItem is TabItem {Content:DockPanel pay}&&pay.Children.OfType<DataGrid>().FirstOrDefault() is {} grid&&grid.Columns[1].ActualWidth<240)
                    throw new InvalidOperationException("لم يأخذ حقل قيمة الراتب عرضه المخصص.");
                capture((FrameworkElement)window.Content);
            }
        }
        if(capture!=null){
            window.Show();
            try{await Load();}finally{window.Close();}
        }else{
            window.ContentRendered+=OnFirstRender;
            async void OnFirstRender(object? sender,EventArgs args){
                window.ContentRendered-=OnFirstRender;
                try{await Load();}
                catch(Exception ex){if(window.IsVisible)window.Content=Box(Column(Text(hit.Name,21,true),Text("تعذّر فتح الملف. "+ex.Message)));}
            }
            window.ShowDialog();
        }
    }
}
