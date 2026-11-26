using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using A=SalaryDesktop.Core;

namespace AnbarPayroll.Desktop;

public partial class MainWindow
{
    sealed record HomeReview(string Key,string Title,string Detail,string Icon,Action Open);
    string? reviewMonth,reviewRevision,selectedReviewKey;
    bool reviewRotationPaused;
    Task homeLoad=Task.CompletedTask;

    void AgencyReview(Review[] entities)
    {
        var selected=entities[0];
        if(entities.Length>1){
            var list=Choices(entities.Select(r=>r.Agency));
            if(!Form(selected.Title,Labeled("جهة التنفيذ",list,360)))return;
            selected=entities[list.SelectedIndex];
        }
        backPage="الرئيسية";
        ShowPage(AgencyPage,selected);
    }

    void ShowReviewCards(Panel host,MonthInfo month,IReadOnlyList<Review> alerts,bool hasSalary,A.SalaryReviewResult? audit,MonthInfo? previous)
    {
        var items=new List<HomeReview>();
        foreach(var group in alerts.GroupBy(r=>r.Title)){
            var entities=group.ToArray();
            var detail=entities.Length==1?entities[0].Agency:Arabic.Money(entities.Length)+" جهة";
            items.Add(new("agency|"+group.Key,group.Key,detail+"　·　"+month.Name,"\uEC06",()=>AgencyReview(entities)));
        }
        if(!month.Verified||previous==null||!previous.Verified)
            items.Add(new("deduction-check","مقارنة الاستقطاعات غير مكتملة","تحتاج المقارنة إلى شهرين متحققين من الاستقطاعات.","\uE7BA",()=>Activate(AgencyPage)));
        if(!hasSalary)
            items.Add(new("salary-missing","رواتب الشهر غير متوفرة",month.Name+"　·　فتح إعدادات الاستيراد","\uE7BA",()=>Activate("الإعدادات")));
        else if(audit==null||audit.EmployeeCount==0||audit.PreviousMonth==null)
            items.Add(new("salary-check","مقارنة الرواتب غير مكتملة",month.Name+"　·　فتح تدقيق الرواتب","\uE7BA",()=>Activate(AuditName)));
        if(audit!=null){
            foreach(var group in audit.Rows.GroupBy(r=>r.Kind).OrderByDescending(g=>g.Count()).ThenBy(g=>g.Key,StringComparer.Ordinal)){
                var kind=group.Key;
                items.Add(new("salary|"+kind,kind,Arabic.Money(group.Count())+" حالة　·　"+month.Name,"\uE8AB",()=>{auditFilter=kind;Activate(AuditName);}));
            }
        }

        host.Children.Clear();
        if(items.Count==0){host.Children.Add(Text("لا توجد حالات تستدعي المراجعة لهذا الشهر.",16));return;}
        int index=items.FindIndex(item=>item.Key==selectedReviewKey);
        if(index<0)index=0;
        var surface=new StackPanel();
        host.Children.Add(surface);
        var card=ButtonFor("فتح حالة المراجعة",()=>Guard(items[index].Open));
        AutomationProperties.SetAutomationId(card,"HomeReviewCard");
        card.HorizontalContentAlignment=HorizontalAlignment.Stretch;
        card.MinHeight=138;card.Padding=new Thickness(18);card.Margin=new Thickness(0,8,0,6);
        card.Background=BrushOf("#FAF6EC");card.BorderBrush=BrushOf("#D7C7A0");surface.Children.Add(card);
        var position=Text("",13,true);
        void Draw(){
            var item=items[index];selectedReviewKey=item.Key;
            position.Text=(index+1)+" من "+items.Count;
            var title=Text(item.Title,21,true);var detail=Text(item.Detail,16);detail.Margin=new Thickness(0,6,0,0);
            var content=new DockPanel();
            var symbol=Glyph(item.Icon,"#835F24");symbol.FontSize=27;symbol.Margin=new Thickness(0,0,12,0);
            DockPanel.SetDock(symbol,Dock.Left);content.Children.Add(symbol);
            content.Children.Add(Column(title,detail));card.Content=content;
            AutomationProperties.SetName(card,item.Title+" · "+item.Detail);
        }
        Draw();
        if(items.Count==1)return;

        void Move(int step){index=(index+step+items.Count)%items.Count;Draw();}
        var navigation=Bar(ButtonFor("السابق",()=>Move(-1)),position,ButtonFor("التالي",()=>Move(1)));
        navigation.HorizontalAlignment=HorizontalAlignment.Center;position.Margin=new Thickness(8,0,8,0);
        surface.Children.Add(navigation);
        if(!SystemParameters.ClientAreaAnimation||SystemParameters.HighContrast)return;
        var timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(8)};
        timer.Tick+=(_,_)=>{
            if(IsActive && surface.IsVisible && !surface.IsMouseOver && !surface.IsKeyboardFocusWithin && !reviewRotationPaused)Move(1);
        };
        var pause=new Button{Margin=new Thickness(3)};
        void UpdatePause(){
            pause.Content=reviewRotationPaused?"استئناف التبديل":"إيقاف التبديل";
            if(surface.IsLoaded&&!reviewRotationPaused)timer.Start();else timer.Stop();
        }
        pause.Click+=(_,_)=>{reviewRotationPaused=!reviewRotationPaused;UpdatePause();};
        navigation.Children.Add(pause);UpdatePause();
        surface.Loaded+=(_,_)=>UpdatePause();
        surface.Unloaded+=(_,_)=>timer.Stop();
    }
}
