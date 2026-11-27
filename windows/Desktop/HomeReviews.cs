using System.Windows;
using System.Windows.Controls;
using A=SalaryDesktop.Core;

namespace AnbarPayroll.Desktop;

public partial class MainWindow
{
    enum ReviewSection { Payroll, Deductions }
    sealed record HomeReview(ReviewSection Section,string Key,string Title,string Detail,Action Open);
    static string ReviewTitle(string kind) => kind switch {
        "جديد في الرواتب"=>"الموظفون الجدد", "خارج من الرواتب"=>"الخارجون من الرواتب",
        "تغيّر الصافي"=>"تغيّر صافي الراتب", "تغيّرت الرتبة"=>"تغيّر الرتبة", "تغيّر الحساب"=>"تغيّر الحساب المصرفي",
        "عدد الاطفال"=>"تغيّر عدد الأطفال", "الحالة الزوجية"=>"تغيّر الزوجية", "اسم الشهادة"=>"تغيّر الشهادة",
        "IBAN فارغ" or "IBAN مكرر" or "IBAN يحتاج مراجعة"=>"IBAN",
        "استقطاع بلا راتب"=>"استقطاعات بلا راتب", "مجموع الاستقطاعات"=>"مطابقة المجاميع",
        "حساب الصافي"=>"خطأ في الحساب", "رقم مكرر"=>"الرقم الوظيفي / الموحد",
        "راتب بلا ملاك"=>"لهم راتب وليسوا في الملاك", "ملاك بلا راتب"=>"في الملاك بلا راتب",
        "رتبة مختلفة عن الملاك"=>"الرتبة مختلفة عن الملاك", _=>kind
    };

    Task homeLoad=Task.CompletedTask;
    sealed record ReviewData(List<Review> Alerts,bool Salary,DeductionCardData Deductions);
    (string? Revision,string Month,Task<ReviewData> Result)? homeReviewCache;
    Task<ReviewData> HomeReviewData(string? revision,MonthInfo month,MonthInfo? previous)
    {
        if(homeReviewCache is {} cached && cached.Revision==revision && cached.Month==month.Name && !cached.Result.IsFaulted)return cached.Result;
        var task=Task.Run(()=>new ReviewData(store.Reviews(month),store.HasPayroll(month.Name),store.ReviewDeductionEntries(month,previous)));
        homeReviewCache=(revision,month.Name,task);
        return task;
    }

    void AgencyReview(Review[] entities)
    {
        var selected=entities[0];
        if(entities.Length>1){
            var list=Choices(entities.Select(r=>r.Agency));
            if(!Form(selected.Title,Labeled("جهة التنفيذ",list,360)))return;
            selected=entities[list.SelectedIndex];
        }
        ShowPage(AgencyPage,selected);
    }

    void DeductionReviewDetails(string title,List<DeductionReviewLine> rows)
    {
        var table=GridView(("المنتسب","Name",1.4),("الجهة","Agency",1.4),("رقم وتاريخ الإضبارة","Reference",1.3),("السابق","Before",1),("الحالي","After",1));
        if(rows.Any(r=>r.Copies>1))table.Columns.Add(new DataGridTextColumn{Header="عدد القيود",Binding=new System.Windows.Data.Binding("Copies")});
        table.ItemsSource=rows.OrderByDescending(r=>Math.Abs(r.After-(r.Before??0))).ThenBy(r=>r.Name).ToList();
        new Window{Owner=this,Title=title,Width=Math.Min(930,SystemParameters.WorkArea.Width*.9),
            Height=Math.Min(550,SystemParameters.WorkArea.Height*.85),WindowStartupLocation=WindowStartupLocation.CenterOwner,
            FlowDirection=FlowDirection.RightToLeft,Content=table}.ShowDialog();
    }

    List<HomeReview> HomeReviewItems(MonthInfo month,IReadOnlyList<Review> alerts,bool hasSalary,A.SalaryReviewResult? audit,DeductionCardData deductions)
    {
        var items=new List<HomeReview>();
        foreach(var group in alerts.GroupBy(r=>r.Title)){
            var entities=group.ToArray();
            var detail="عدد الجهات "+Arabic.Money(entities.Length);
            items.Add(new(ReviewSection.Deductions,"agency|"+group.Key,group.Key,detail,()=>AgencyReview(entities)));
        }
        if(deductions.Blocked)
            items.Add(new(ReviewSection.Deductions,"deduction-check","مقارنة الاستقطاعات تحتاج مراجعة","تحتاج المقارنة إلى شهرين متحققين من الاستقطاعات.",()=>Activate(AgencyPage)));
        foreach(var (title,rows) in deductions.Rows)
            items.Add(new(ReviewSection.Deductions,"deduction|"+title,title,"عدد الحالات "+Arabic.Money(rows.Count),()=>DeductionReviewDetails(title,rows)));
        if(!hasSalary)
            items.Add(new(ReviewSection.Payroll,"salary-missing","تدقيق الرواتب غير مكتمل",month.Name+"　·　فتح إعدادات الاستيراد",()=>Activate("الإعدادات")));
        else if(audit==null||audit.EmployeeCount==0||audit.PreviousMonth==null)
            items.Add(new(ReviewSection.Payroll,"salary-check","تدقيق الرواتب غير مكتمل",month.Name+"　·　فتح تدقيق الرواتب",()=>Activate(AuditName)));
        if(audit!=null){
            foreach(var group in audit.Rows.GroupBy(r=>ReviewTitle(r.Kind)).OrderByDescending(g=>g.Count()).ThenBy(g=>g.Key,StringComparer.Ordinal)){
                var kind=group.Key;
                items.Add(new(ReviewSection.Payroll,"salary|"+kind,kind,"عدد الحالات "+Arabic.Money(group.Count()),()=>{auditFilter=kind;Activate(AuditName);}));
            }
        }

        return items.OrderBy(item=>item.Key switch {
            "salary-missing" or "salary-check" or "deduction-check"=>0,
            "salary|IBAN" or "salary|خطأ في الحساب" or "salary|مطابقة المجاميع"
                or "deduction|تكرار محتمل في الاستقطاعات"=>1,
            "salary|تغيّر الحساب المصرفي" or "salary|الخارجون من الرواتب"
                or "deduction|الاستقطاعات المتوقفة"=>2,
            _ when item.Key.StartsWith("agency|",StringComparison.Ordinal)=>3,
            "salary|تغيّر صافي الراتب" or "deduction|تغيّر مبلغ الاستقطاع (نفس الجهة والإضبارة)"=>4,
            _=>5
        }).ToList();
    }
}
