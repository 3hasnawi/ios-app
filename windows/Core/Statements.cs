using System.Globalization;
using System.Net;
using System.Text;
using ClosedXML.Excel;

namespace AnbarPayroll;

public record Sheet(string Name,string Title,string[] Columns,List<object?[]> Rows,int AmountColumn,decimal Total,bool Annual=false,bool ShowTotal=true,bool Command=false);
public record Statement(string Header,List<Sheet> Sheets,string Person="",string Rank="",string Agency="",string Reference="",int PaidMonths=0,bool Partial=false)
{
    private static readonly StringComparer ArabicOrder=StringComparer.Create(CultureInfo.GetCultureInfo("ar-IQ"),false);
    public static Statement Entities(IEnumerable<Deduction> input,string header,bool summary)
    {
        var sheets=new List<Sheet>();
        foreach(var group in input.Where(r=>r.Amount.HasValue).GroupBy(r=>(r.Period,r.Month,r.Agency)).OrderBy(g=>g.Key.Period).ThenBy(g=>g.Key.Agency,ArabicOrder)){
            var rows=group.OrderBy(r=>r.Name,ArabicOrder).Select((r,i)=>new object?[]{i+1,r.Rank,r.Name,r.Amount,r.Reference}).ToList();
            sheets.Add(new(group.Key.Agency,"قائمة الاستقطاعات لـ شهر "+Arabic.Eastern(group.Key.Month)+" - "+group.Key.Agency,["ت","الرتبة","الاسم الكامل","المبلغ","رقم وتاريخ الاضبارة"],rows,3,group.Sum(r=>r.Amount!.Value)));
        }
        if(sheets.Count==0)throw new InvalidOperationException("لا توجد مبالغ ضمن الاختيار.");
        if(summary&&sheets.Count>1){
            var rows=sheets.Select((s,i)=>new object?[]{i+1,s.Title,s.Rows.Count,s.Total}).ToList();
            sheets.Insert(0,new("الخلاصة","خلاصة استقطاعات الجهات",["ت","جهة التنفيذ والفترة","عدد الاستقطاعات","المجموع"],rows,3,sheets.Sum(s=>s.Total)));
        }
        return new(header,sheets);
    }
    public static Statement Employee(IEnumerable<Deduction> input,IReadOnlyList<MonthInfo> months,string person,string agency,string reference,int start,int end,string header)
    {
        if(start<=0||end<start||end/100-start/100>=100)throw new InvalidOperationException("راجع بداية الفترة ونهايتها.");
        var matching=input.Where(r=>Arabic.Match(r.Name)==Arabic.Match(person)&&r.Agency==agency&&(reference==""||Arabic.CaseKey(r.Reference)==Arabic.CaseKey(reference))).ToList();
        var rows=matching.Where(r=>r.Period>=start&&r.Period<=end).ToList();
        var periods=months.Where(m=>m.Key>=start&&m.Key<=end).GroupBy(m=>m.Key).ToList();
        if(periods.Any(g=>g.Count()>1))throw new InvalidDataException("يوجد أكثر من ملف محفوظ للشهر نفسه. راجع الأشهر قبل إصدار الكشف.");
        var sources=periods.ToDictionary(g=>g.Key,g=>g.Single().Verified);
        var sums=new List<Sheet>();bool partial=matching.Any(r=>r.Period==0);int paid=0;
        for(int y=start/100;y<=end/100;y++){
            decimal total=0;var annual=new List<object?[]>();
            for(int m=1;m<=12;m++){
                int period=y*100+m;object? value=null;
                if(period>=start&&period<=end){
                    if(!sources.GetValueOrDefault(period)){value="غير متحقق";partial=true;}
                    else {
                        var matched=rows.Where(r=>r.Period==period).ToList();
                        var amounts=matched.Where(r=>r.Amount.HasValue).ToList();
                        var statuses=matched.Select(r=>r.Status).Where(s=>s!="").Distinct().ToList();
                        if(matched.Any(r=>r.Amount==null&&r.Status==""))throw new InvalidDataException("يوجد استقطاع بلا مبلغ أو حالة معروفة.");
                        if(amounts.Count>0){decimal sum=amounts.Sum(r=>r.Amount!.Value);paid++;total+=sum;value=sum;if(statuses.Count>0)value=Arabic.Money(sum)+" / "+string.Join(" / ",statuses);}
                        else value=statuses.Count>0?string.Join(" / ",statuses):"—";
                    }
                }
                annual.Add([Arabic.Months[m-1],value]);
            }
            sums.Add(new(y.ToString(),"كشف استقطاعات",["الشهر",y.ToString()],annual,1,total,true));
        }
        return new(header,sums,person,rows.OrderByDescending(r=>r.Period).Select(r=>r.Rank).FirstOrDefault()??"",agency,
            reference!=""?reference:string.Join(" | ",rows.Select(r=>r.Reference).Where(s=>s!="").Distinct()),paid,partial);
    }
    static string Display(object? v)=>WebUtility.HtmlEncode(v switch{decimal d=>Arabic.Money(d),_=>v?.ToString()??""});
    public bool Landscape=>Person!=""||Sheets.Count==1&&Sheets[0].Command;
    public string Html()
    {
        if(Sheets.Count==1&&Sheets[0].Command){
            var command=Sheets[0];var result=new StringBuilder("<!doctype html><html dir='rtl' lang='ar'><meta charset='utf-8'><style>@page{size:A4 landscape;margin:0}body{font:bold 14pt Arial;margin:73pt auto 0;width:581pt;text-align:center}h1{font:inherit;margin:0 0 17pt}table{width:100%;border-collapse:collapse}td,th{height:17.2pt;border:1pt solid black;padding:0 2pt;font-size:12pt}th,td:first-child{background:#ff0}*{-webkit-print-color-adjust:exact}</style><h1>");
            result.Append(Display(command.Title)+"</h1><table><thead><tr>");
            foreach(var col in command.Columns)result.Append("<th>"+Display(col)+"</th>");result.Append("</tr></thead><tbody>");
            foreach(var row in command.Rows){result.Append("<tr>");for(int i=0;i<row.Length;i++){
                if(row[i]==null)continue;result.Append(i==2&&row[3]==null?"<td colspan='4'>":"<td>");result.Append(Arabic.Eastern(Display(row[i]))+"</td>");}result.Append("</tr>");}
            return result.Append("</tbody></table></html>").ToString();
        }
        var b=new StringBuilder("<!doctype html><html dir='rtl' lang='ar'><meta charset='utf-8'><style>");
        b.Append("body{background:#e9eeec;margin:0;color:#172b26;font:12pt Arial}section{box-sizing:border-box;background:white;max-width:900px;margin:18px auto;padding:25px;break-after:page}section:last-child{break-after:auto}table{border-collapse:collapse;width:100%}th,td{box-sizing:border-box;border:1pt solid #9cb8d4;text-align:center;padding:2pt 4pt;font:bold 11.5pt Arial;height:20pt}th,.sum{background:#deebf7}tbody tr:nth-child(even){background:#f3f7fb}.org{background:white;border:0;font-size:14pt}.title{background:#1f4f78;color:white;font-size:14pt}.bands{display:grid;grid-template-columns:repeat(4,25%);margin:12pt 0 7pt}.bands td{height:22.1pt;font-size:12pt}.bands th,.banner{background:#1c3b4f;color:white}.banner,.info{display:flex;justify-content:space-around;padding:6pt;border:1pt solid #a6b8c4}.info{background:#e6eff5}.warn{color:#aa5200}p{margin:5pt}thead{display:table-header-group}tr{break-inside:avoid}td{overflow-wrap:anywhere}td.num{white-space:nowrap;direction:ltr;overflow-wrap:normal;font-variant-numeric:tabular-nums}@page{size:A4 ");
        b.Append(Person==""?"portrait;margin:28pt 28pt 32pt":"landscape;margin:18pt 22pt 32pt");
        string footer=Person==""?"( قيادة شرطة محافظة الانبار - قسم الحسابات )":"كشف الاستقطاعات";
        var stamp=Arabic.Eastern(DateTime.Now.ToString("yyyy/MM/dd HH:mm",CultureInfo.InvariantCulture));
        b.Append("; @bottom-right{content:'"+footer+"';font:bold 8.5pt Arial;color:#444}@bottom-center{content:'صفحة ' counter(page,arabic-indic) ' من ' counter(pages,arabic-indic);font:8.5pt Arial;color:#444}@bottom-left{content:'"+stamp+"';font:8.5pt Arial;color:#444}");
        b.Append("}@media print{*{-webkit-print-color-adjust:exact;print-color-adjust:exact}body,section{background:white;margin:0;padding:0;max-width:none}}</style>");
        void Table(Sheet s,bool entity){
            b.Append("<table>");
            if(s.Columns.Length==5 && s.AmountColumn==3)b.Append("<colgroup><col style='width:6%'><col style='width:13%'><col style='width:31%'><col style='width:18%'><col style='width:32%'></colgroup>");
            b.Append("<thead>");
            if(entity)b.Append($"<tr><th class='org' colspan='{s.Columns.Length}'>{Display(Header)}</th></tr><tr><th class='title' colspan='{s.Columns.Length}'>{Display(s.Title)}</th></tr>");
            b.Append("<tr>");foreach(var c in s.Columns)b.Append("<th>"+Display(c)+"</th>");b.Append("</tr></thead><tbody>");
            foreach(var row in s.Rows){b.Append("<tr>");foreach(var value in row)b.Append((value is decimal?"<td class=num>":"<td>")+Display(value)+"</td>");b.Append("</tr>");}
            if(!s.ShowTotal){b.Append("</tbody></table>");return;}
            b.Append($"<tr class='sum'><td colspan='{s.AmountColumn}'>المجموع</td><td class='num'>{Display(s.Total)}</td>");
            for(int i=s.AmountColumn+1;i<s.Columns.Length;i++)b.Append("<td></td>");b.Append("</tr></tbody></table>");
        }
        if(Person==""){foreach(var s in Sheets){b.Append("<section>");Table(s,true);b.Append("</section>");}}
        else for(int i=0;i<Sheets.Count;i+=4){
            b.Append("<section><div class='banner'><b>قيادة شرطة محافظة الأنبار / قسم الحسابات</b><b>كشف استقطاعات</b></div>");
            b.Append($"<table class='identity'><tr><th>الاسم الكامل</th><td colspan='3'>{Display(Person)}</td></tr><tr><th>الرتبة</th><td>{Display(Rank)}</td><th>جهة الاستقطاع</th><td>{Display(Agency)}</td></tr><tr><th>عدد الأشهر</th><td>{PaidMonths}</td><th>رقم وتاريخ الإضبارة</th><td>{Display(Reference)}</td></tr></table><div class='bands'>");
            foreach(var s in Sheets.Skip(i).Take(4))Table(s,false);b.Append("</div>");
            if(i+4>=Sheets.Count){b.Append($"<div class='banner'>{(Partial?"مجموع القيود المتوفرة":"المجموع الكلي للاستقطاعات")}　{Display(Sheets.Sum(s=>s.Total))}</div>");if(Partial)b.Append("<p class='warn'>توجد أشهر غير متحققة؛ المجموع للقيود المتوفرة فقط.</p>");b.Append("<p>التدقيق</p>");}b.Append("</section>");
        }
        return b.Append("</html>").ToString();
    }
    public void Excel(string file)
    {
        const decimal maxExcel=999_999_999_999_999m;
        if(Sheets.Any(s=>s.Rows.Count>1_048_570||s.Total>maxExcel||s.Rows.SelectMany(r=>r).Any(v=>v is string text&&text.Length>32_767||v is decimal amount&&(Math.Abs(amount)>maxExcel||(decimal)(double)amount!=amount))))
            throw new InvalidDataException("تعذّر تمثيل جميع بيانات الكشف بدقة في Excel.");
        using var book=new XLWorkbook();
        foreach(var s in Sheets){
            var stem=string.Concat(s.Name.Select(c=>"[]:*?/\\".Contains(c)?' ':c)).Trim(' ','\'');
            if(stem.Length==0)stem="كشف";stem=stem[..Math.Min(31,stem.Length)];var name=stem;
            for(int i=2;book.Worksheets.Contains(name);i++){var end=" "+i;name=stem[..Math.Min(stem.Length,31-end.Length)]+end;}
            var w=book.Worksheets.Add(name);w.RightToLeft=true;int width=s.Columns.Length;
            void Heading(int row,string title){w.Range(row,1,row,width).Merge().FirstCell().SetValue(title);}
            Heading(1,Header);Heading(2,s.Title);int head=3;
            if(s.Annual){Heading(3,Person+"　"+Rank+"　"+Agency);Heading(4,"رقم وتاريخ الإضبارة　"+Reference);head=5;}
            for(int j=0;j<width;j++)w.Cell(head,j+1).SetValue(s.Columns[j]);int row=head+1;
            foreach(var values in s.Rows){
                for(int j=0;j<values.Length;j++){var c=w.Cell(row,j+1);switch(values[j]){case decimal d:c.SetValue((double)d);break;case int n:c.SetValue(n);break;default:c.SetValue(values[j]?.ToString()??"");break;}}
                w.Row(row).Height=s.Annual?22.1:Math.Max(20,20*values.Select((v,index)=>v is string t?(int)Math.Ceiling(t.Length/(double)(index==3||index==1?14:32)):1).Max());row++;
            }
            if(s.ShowTotal){
            if(s.AmountColumn>1)w.Range(row,1,row,s.AmountColumn).Merge();w.Cell(row,1).SetValue("المجموع");
            var amount=w.Cell(row,s.AmountColumn+1);if(s.Annual)amount.SetValue((double)s.Total);else amount.FormulaA1=$"SUM({amount.Address.ColumnLetter}{head+1}:{amount.Address.ColumnLetter}{row-1})";
            } else row--;
            if(s.Annual){Heading(row+1,(Partial?"مجموع القيود المتوفرة":"المجموع الكلي للاستقطاعات")+"　"+Arabic.Money(Sheets.Sum(a=>a.Total)));}
            var used=w.RangeUsed()!;used.Style.Font.FontName="Arial";used.Style.Font.FontSize=s.Annual?12:11.5;used.Style.Font.Bold=true;used.Style.Alignment.WrapText=true;used.Style.Alignment.Vertical=XLAlignmentVerticalValues.Center;
            w.Range(head,1,row,width).Style.Border.InsideBorder=XLBorderStyleValues.Thin;w.Range(head,1,row,width).Style.Border.OutsideBorder=XLBorderStyleValues.Thin;
            w.Range(2,1,2,width).Style.Fill.BackgroundColor=XLColor.FromHtml("#1F4F78");w.Range(2,1,2,width).Style.Font.FontColor=XLColor.White;
            w.Range(head,1,head,width).Style.Fill.BackgroundColor=XLColor.FromHtml("#DEEBF7");w.Column(s.AmountColumn+1).Style.NumberFormat.Format="#,##0.###############";
            for(int j=1;j<=width;j++)w.Column(j).Width=s.Annual?34:j==1?7:j==3||j==5?39:20;
            if(s.Command){
                w.Range(1,1,1,width).FirstCell().Clear();w.Range(2,1,2,width).Style.Fill.BackgroundColor=XLColor.White;w.Range(2,1,2,width).Style.Font.FontColor=XLColor.Black;
                w.Range(head,1,head,width).Style.Fill.BackgroundColor=XLColor.Yellow;w.Range(head+1,1,row,1).Style.Fill.BackgroundColor=XLColor.Yellow;
                w.Range(head+3,3,head+3,6).Merge();w.Columns(3,6).Width=13;w.Column(2).Width=44;w.Column(7).Width=31;
            }
            w.PageSetup.Footer.Center.AddText("صفحة ");w.PageSetup.Footer.Center.AddText(XLHFPredefinedText.PageNumber);w.PageSetup.Footer.Center.AddText(" من ");w.PageSetup.Footer.Center.AddText(XLHFPredefinedText.NumberOfPages);
            w.PageSetup.Footer.Right.AddText("قيادة شرطة محافظة الانبار / قسم الحسابات");
            w.SheetView.FreezeRows(head);w.PageSetup.PaperSize=XLPaperSize.A4Paper;w.PageSetup.PageOrientation=s.Annual||s.Command?XLPageOrientation.Landscape:XLPageOrientation.Portrait;w.PageSetup.FitToPages(1,0);w.PageSetup.SetRowsToRepeatAtTop(1,head);
        }
        book.SaveAs(file);
    }
}
