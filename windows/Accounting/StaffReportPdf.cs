using System.Globalization;
using SkiaSharp;

namespace SalaryDesktop.Core;

public static class StaffReportPdf
{
    // الحقول وترتيب المجموعات من بيان الملاك المعتمد في الآيفون.
    static List<StaffSection> Sections(StaffMember member)
    {
        var source=StaffRecords.Sections(member);
        string? Field(string section,params string[] names){
            var keys=names.Select(PayrollAnalysis.Normalize).ToHashSet();
            return source.Where(s=>s.Title==section).SelectMany(s=>s.Fields)
                .FirstOrDefault(f=>keys.Contains(PayrollAnalysis.Normalize(f.Label)))?.Value;
        }
        var sections=new List<StaffSection>();
        void Add(string title,params (string Label,string? Value)[] fields){
            var present=fields.Where(f=>!string.IsNullOrWhiteSpace(f.Value)).Select(f=>new StaffField(f.Label,f.Value!.Trim())).ToList();
            if(present.Count>0)sections.Add(new(title,present));
        }
        Add("المعلومات الشخصية",
            ("اسم الأم",Field("المعلومات الشخصية","اسم الأم", "اسم الام الكامل", "اسم الام")??member.Mother),
            ("التولد",Field("المعلومات الشخصية","التولد")),
            ("الحالة الزوجية",Field("المعلومات الشخصية","الحالة الزوجية")),
            ("اسم الزوجة / الزوج",Field("المعلومات الشخصية","اسم الزوجة/ الزوج", "اسم الزوجة / الزوج")),
            ("التحصيل الدراسي",Field("التحصيل الدراسي","التحصيل الدراسي")),
            ("الحالة الوظيفية",member.Status));
        Add("المعلومات الوظيفية ومكان العمل",
            ("الرتبة",member.Rank),
            ("المنصب الإداري",Field("الوظيفية","المنصب الاداري")),
            ("المنصب",Field("الوظيفية","المنصب")),
            ("العنوان الوظيفي",Field("الوظيفية","العنوان الوظيفي")),
            ("محل العمل",member.Workplace),
            ("الرقم الإحصائي",member.Stat),
            ("الرقم الموحد",member.Unified),
            ("رقم أمر المنصب",Field("الوظيفية","رقم الامر الوزاري لاسناد المنصب")),
            ("تاريخ أمر المنصب",Field("الوظيفية","تاريخ امر المنصب")),
            ("المباشرة بالمنصب",Field("الوظيفية","تاريخ المباشرة بالمنصب")));
        Add("التعيين والمباشرة",
            ("رقم أمر التعيين",Field("التعيين والمباشرة","رقم امر التعيين", "رقم الامر")),
            ("تاريخ أمر التعيين",Field("التعيين والمباشرة","تاريخ التعيين", "تاريخ الامر")),
            ("أول مباشرة بالخدمة",Field("التعيين والمباشرة","تاريخ مباشرة بالخدمة لاول مرة")),
            ("جهة التعيين",Field("الوظيفية","جهة التعيين")),
            ("تاريخ مباشرة الإعادة",Field("التعيين والمباشرة","تاريخ مباشرة بالاعادة")),
            ("رقم أمر الإعادة",Field("التعيين والمباشرة","رقم امر الاعادة للخدمة")),
            ("تاريخ أمر الإعادة",Field("التعيين والمباشرة","تاريخ امر الاعادة للخدمة")));
        Add("الترقية",
            ("شهر الترقية",Field("الترقية","شهر الترقية", "الشهر")),
            ("سنة الترقية",Field("الترقية","سنة الترقية")),
            ("رقم أمر الترقية",Field("الترقية","رقم امر الترقية")),
            ("تاريخ الترقية",Field("الترقية","تاريخ الترقية")));
        Add("السجل والملاحظات",
            ("تاريخ تحديث السجل",Field("السجل","تاريخ التحديث")),
            ("الملاحظات",Field("الملاحظات","الملاحظات")));
        return sections;
    }

    public static byte[] Generate(StaffMember member,ReportAssets assets,byte[]? logo,DateTime stamp)
    {
        var sections=Sections(member);
        using var page=new ReportCanvas(assets,stamp);
        page.BeginPage(false);
        const float margin=28,width=539,top=170;
        var border=new SKColor(198,198,198);
        const string overflow="بيانات الملاك لا تتسع في صفحة واحدة بخط مقروء.";
        List<string> Lines(SKPaint paint,string value,float room)=>value.Split('\n').SelectMany(line=>page.Lines(paint,line,room-10)).ToList();
        void Text(SKPaint paint,string value,SKRect rect,ReportCanvas.Align align=ReportCanvas.Align.Right){
            var lines=Lines(paint,value,rect.Width);
            var baseline=rect.Top+(rect.Height-lines.Count*paint.FontSpacing)/2-paint.FontMetrics.Ascent;
            foreach(var line in lines){
                page.Text(paint,line,rect.Left+1,rect.Right-1,baseline,align,true);
                baseline+=paint.FontSpacing;
            }
        }
        void Header(string value,SKRect rect,float size,bool bold,ReportCanvas.Align align=ReportCanvas.Align.Right){
            using var paint=page.Paint(size,bold);
            while(Lines(paint,value,rect.Width).Count*paint.FontSpacing>rect.Height){
                if(paint.TextSize<=6.5f)throw new InvalidDataException(overflow);
                paint.TextSize-=.5f;
            }
            Text(paint,value,rect,align);
        }
        using var emblem=logo==null?null:SKBitmap.Decode(logo);
        if(emblem!=null)page.Canvas.DrawBitmap(emblem,SKRect.Create(275.5f,24,44,44));
        Header("وزارة الداخلية\nقيادة شرطة محافظة الأنبار",SKRect.Create(335,24,230,48),10,true);
        Header("تقرير الملاك\nالتاريخ: "+stamp.ToString("MM/dd/yyyy",CultureInfo.InvariantCulture),SKRect.Create(margin,24,165,44),9,false,ReportCanvas.Align.Left);
        page.Fill(SKRect.Create(margin,78,width,.7f),SKColors.DarkGray);
        Header("بيان معلومات المنتسب",SKRect.Create(margin,83,width,25),15,true,ReportCanvas.Align.Center);
        page.Stroke(SKRect.Create(margin,112,width,48),SKColors.DarkGray,1);
        Header(member.Name,SKRect.Create(156,113,width-132,24),12,true);
        Header(string.Join(" · ",new[]{member.Rank,member.Workplace}.Where(s=>!string.IsNullOrWhiteSpace(s))),SKRect.Create(156,138,width-132,16),12,false);
        Header(member.Status,SKRect.Create(32,120,120,30),12,false,ReportCanvas.Align.Center);
        float Layout(float size,bool draw){
            using var regular=page.Paint(size,false);using var bold=page.Paint(size,true);
            float y=top;
            foreach(var section in sections){
                var title=SKRect.Create(margin,y,width,22);
                if(draw){page.Fill(title,new SKColor(240,240,240));Text(bold,section.Title,title);}
                y+=22;
                int columns=section.Title=="السجل والملاحظات"?1:2;
                float cell=width/columns,label=cell*.38f;
                foreach(var row in section.Fields.Chunk(columns)){
                    float height=row.Max(f=>Math.Max(20,10+Math.Max(Lines(regular,f.Label,label).Count*regular.FontSpacing,
                        Lines(bold,f.Value,cell-label).Count*bold.FontSpacing)));
                    if(draw){
                        for(int i=0;i<row.Length;i++){
                            float x=margin+width-(i+1)*cell;
                            var left=SKRect.Create(x,y,cell-label,height);var right=SKRect.Create(left.Right,y,label,height);
                            page.Stroke(left,border,1);page.Stroke(right,border,1);
                            Text(regular,row[i].Label,right);Text(bold,row[i].Value,left);
                        }
                    }
                    y+=height;
                    if(y>800)return y;
                }
                y+=4;
            }
            return y;
        }
        float size=10.5f;
        while(Layout(size,false)>800){
            if(size<=6.5f)throw new InvalidDataException(overflow);
            size-=.5f;
        }
        Layout(size,true);
        Header("تقرير الملاك · صفحة ١ من ١",SKRect.Create(margin,814,width,16),8,false,ReportCanvas.Align.Center);
        page.EndPage();
        return page.Finish();
    }
}
