using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using A=SalaryDesktop.Core;

namespace AnbarPayroll.Desktop;

public partial class MainWindow
{
    sealed record HomeReview(string Key,string Title,string Detail,string Icon,Action Open);
    static string ReviewTitle(string kind) => kind switch {
        "جديد في الرواتب"=>"الموظفون الجدد", "خارج من الرواتب"=>"الخارجون من الرواتب",
        "تغيّر الصافي"=>"تغيّر صافي الراتب", "تغيّرت الرتبة"=>"تغيّر الرتبة", "تغيّر الحساب"=>"تغيّر الحساب المصرفي",
        "عدد الاطفال"=>"تغيّر عدد الأطفال", "الحالة الزوجية"=>"تغيّر الزوجية", "اسم الشهادة"=>"تغيّر الشهادة",
        "IBAN فارغ" or "IBAN مكرر" or "IBAN يحتاج مراجعة"=>"IBAN",
        "حساب الصافي"=>"خطأ في الحساب", "رقم مكرر"=>"الرقم الوظيفي / الموحد",
        "راتب بلا ملاك"=>"لهم راتب وليسوا في الملاك", "ملاك بلا راتب"=>"في الملاك بلا راتب",
        "رتبة مختلفة عن الملاك"=>"الرتبة مختلفة عن الملاك", _=>kind
    };

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
            items.Add(new("deduction-check","مقارنة الاستقطاعات تحتاج مراجعة","تحتاج المقارنة إلى شهرين متحققين من الاستقطاعات.","\uE7BA",()=>Activate(AgencyPage)));
        if(!hasSalary)
            items.Add(new("salary-missing","تدقيق الرواتب غير مكتمل",month.Name+"　·　فتح إعدادات الاستيراد","\uE7BA",()=>Activate("الإعدادات")));
        else if(audit==null||audit.EmployeeCount==0||audit.PreviousMonth==null)
            items.Add(new("salary-check","تدقيق الرواتب غير مكتمل",month.Name+"　·　فتح تدقيق الرواتب","\uE7BA",()=>Activate(AuditName)));
        if(audit!=null){
            foreach(var group in audit.Rows.GroupBy(r=>ReviewTitle(r.Kind)).OrderByDescending(g=>g.Count()).ThenBy(g=>g.Key,StringComparer.Ordinal)){
                var kind=group.Key;
                items.Add(new("salary|"+kind,kind,Arabic.Money(group.Count())+" حالة　·　"+month.Name,"\uE8AB",()=>{auditFilter=kind;Activate(AuditName);}));
            }
        }

        host.Children.Clear();
        if(items.Count==0){host.Children.Add(Text("لا توجد حالات تستدعي المراجعة لهذا الشهر.",16));return;}
        var grid=new UniformGrid{Columns=1,HorizontalAlignment=HorizontalAlignment.Stretch};
        grid.SizeChanged+=(_,_)=>grid.Columns=Math.Clamp((int)(grid.ActualWidth/234),1,4);
        host.Children.Add(grid);
        foreach(var item in items){
            bool change=item.Key.StartsWith("salary|");
            var card=ButtonFor(item.Title,()=>Guard(item.Open));
            AutomationProperties.SetAutomationId(card,"HomeReview-"+item.Key);
            AutomationProperties.SetHelpText(card,item.Detail);
            card.HorizontalContentAlignment=HorizontalAlignment.Stretch;
            card.MinHeight=82;card.Padding=new Thickness(12);card.Margin=new Thickness(4);
            card.Background=BrushOf(change?"#F1F7F4":"#FBF6EB");
            card.BorderBrush=BrushOf(change?"#C9DBD1":"#E3D6BA");
            var content=new DockPanel();
            var icon=Glyph(item.Icon,change?"#26634F":"#855E23");
            icon.Margin=new Thickness(5);DockPanel.SetDock(icon,Dock.Right);content.Children.Add(icon);
            var detail=Text(item.Detail,13);detail.Margin=new Thickness(0,5,0,0);
            content.Children.Add(Column(Text(item.Title,17,true),detail));
            card.Content=content;grid.Children.Add(card);
        }
    }
}
