using System.Windows;
using System.Windows.Controls;
using A=SalaryDesktop.Core;

namespace AnbarPayroll.Desktop;
public partial class MainWindow
{
    async Task OpenPerson(Store.SearchResult hit,Action<FrameworkElement>? capture=null)
    {
        var revision=store.Active;
        try{
            var (profile,trust,members)=await Task.Run(()=>{
                var source=store.PersonSnapshot(hit.Name);
                var key=A.PersonProfiles.Key(hit.Name);
                var matches=Store.StaffForPerson(source,hit.Name).ToArray();
                var ledger=A.SalaryTrust.ReadFromBackup(source).Entries.Where(e=>A.PersonProfiles.Key(e.Name)==key).OrderByDescending(e=>e.Year*100+e.Month).ToArray();
                return (A.PersonProfiles.Build(source,hit.Name,matches),ledger,matches);
            });
            if(revision!=store.Active||(!SearchIsOpen&&page!=SearchName))return;
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
            var salaries=profile.Months.Where(m=>m.Employees.Count>0).ToArray();
            if(salaries.Length>0){
                var months=Choices(salaries.Select(m=>m.Month));if(salaries.Any(m=>m.Month==hit.Month))months.SelectedItem=hit.Month;
                var pane=new DockPanel();
                var preview=PreviewButton("تقرير راتب - "+profile.Name,()=>{
                    var month=salaries[months.SelectedIndex];
                    return Task.Run(()=>A.SalaryReportPdf.Generate([A.SalaryReports.Make(month,true,false)],Fonts(),Logo(),DateTime.Now));
                },caption:"معاينة تقرير الراتب");
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
            var deductions=profile.Months.SelectMany(m=>m.Deductions).ToArray();
            if(deductions.Length>0){
                var grid=ProfileGrid(("الشهر","Month",1),("جهة التنفيذ","Title",1.3),("المبلغ","Value",.7),("رقم وتاريخ الإضبارة","Note",1.4));
                grid.ItemsSource=deductions.Select(d=>new{d.Month,d.Title,d.Note,Value=d.Amount.HasValue?Arabic.Money(d.Amount.Value):"غير محتسب"});Tab("الاستقطاعات",grid);
            }
            if(trust.Length>0){
                var grid=ProfileGrid(("الشهر","Month",1),("المبلغ","Amount",.8),("الحالة","Status",.9),("تاريخ الصرف","Date",.9),("الملاحظات","Note",1.4));
                grid.ItemsSource=trust.Select(e=>new{Month=A.SalaryTrust.MonthLabel(e.Year,e.Month),Amount=Arabic.Money(e.Amount),Status=A.SalaryTrust.PaymentStatus(e),Date=e.ImportedPaymentDate??string.Join("، ",e.Payments.Select(p=>p.BookDate)),Note=string.Join(" · ",new[]{e.CaseStatus,e.Note}.Where(x=>x.Length>0).Distinct())});
                var body=new DockPanel();var sum=Text("مجموع الأمانات　"+Arabic.Money(trust.Sum(e=>(decimal)e.Amount)),16,true);DockPanel.SetDock(sum,Dock.Top);body.Children.Add(sum);body.Children.Add(grid);Tab("الأمانات",body);
            }
            foreach(var member in members){
                var fields=ProfileGrid(("الحقل","Label",1),("القيمة","Value",1.6));
                fields.ItemsSource=A.StaffRecords.Sections(member).SelectMany(s=>s.Fields).ToList();Tab(members.Length==1?"الملاك":"ملاك "+member.Stat,fields);
            }
            var target=hit.Kind switch{"أمانات"=>"الأمانات","استقطاع"=>"الاستقطاعات","ملاك"=>"الملاك",_=>"قسيمة الراتب"};
            foreach(TabItem item in tabs.Items)if(Equals(item.Header,target)){tabs.SelectedItem=item;break;}
            var panel=new DockPanel();var title=Text(profile.Name,21,true);DockPanel.SetDock(title,Dock.Top);
            panel.Children.Add(title);panel.Children.Add(tabs);
            var window=new Window{Owner=this,Title=profile.Name,WindowStartupLocation=WindowStartupLocation.CenterOwner,Width=900,Height=550,MinWidth=350,MinHeight=280,FlowDirection=FlowDirection.RightToLeft,Content=Box(panel)};
            if(capture==null)window.ShowDialog();else{window.Show();try{
                await window.Dispatcher.InvokeAsync(()=>window.UpdateLayout(),System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                if(salaries.Length>0&&tabs.SelectedItem is TabItem {Content:DockPanel pay}&&pay.Children.OfType<DataGrid>().FirstOrDefault() is {} grid&&grid.Columns[1].ActualWidth<240)
                    throw new InvalidOperationException("لم يأخذ حقل قيمة الراتب عرضه المخصص.");
                capture((FrameworkElement)window.Content);
            }finally{window.Close();}}
        }catch(Exception ex){if(capture!=null)throw;Error(ex);}
    }
}
