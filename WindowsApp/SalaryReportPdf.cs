using System.Globalization;
using System.Text.RegularExpressions;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
namespace SalaryWindows;
internal static class SalaryReportPdf
{
    public static void Write(string path,SalaryReportData report,string? fontPath=null)
    {
        fontPath ??=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts),"arial.ttf");
        if(!File.Exists(fontPath)) throw new InvalidOperationException("خط Arial غير موجود؛ لم يُنشأ التقرير.");
        using var font=SKTypeface.FromFile(fontPath) ?? throw new InvalidDataException("خط التقرير غير صالح.");
        using var shaper=new SKShaper(font);
        using var paint=new SKPaint { Typeface=font,TextSize=10,Color=SKColors.Black,TextAlign=SKTextAlign.Right,IsAntialias=true };
        using var line=new SKPaint { Color=new SKColor(140,153,173),Style=SKPaintStyle.Stroke,StrokeWidth=.5f };
        using var head=new SKPaint { Color=new SKColor(219,230,245) };using var soft=new SKPaint { Color=new SKColor(237,242,250) };
        using var watermarkPaint=new SKPaint { Color=SKColors.White.WithAlpha(26),FilterQuality=SKFilterQuality.Medium };
        var resources=Path.Combine(AppContext.BaseDirectory,"Resources");
        using var logo=File.Exists(Path.Combine(resources,"HomeLogo.png"))?SKBitmap.Decode(Path.Combine(resources,"HomeLogo.png")):null;
        using var watermark=File.Exists(Path.Combine(resources,"WatermarkLogo.png"))?SKBitmap.Decode(Path.Combine(resources,"WatermarkLogo.png")):null;
        const float pageWidth=595,pageHeight=842,margin=30,width=535,gap=16,rowHeight=15.5f; float half=(width-gap)/2;
        int tableRows=Math.Max(Math.Max(report.Entitlements.Count,report.Deductions.Count),report.MissingDetails?4:1);
        float startY=238;float tableEnd=startY+18+rowHeight*(tableRows+2)+10;float netY=tableEnd, infoY=netY+38;
        int bankRows=1+Math.Max(1,report.Banks.Count)+(report.Banks.Count>1?1:0);
        float notesY=Math.Max(infoY+18+report.Info.Count*(rowHeight+1),infoY+18+bankRows*(rowHeight+1))+10;
        if(notesY+34>pageHeight-38) throw new InvalidDataException("بيانات التقرير لا تتسع في صفحة A4 واحدة؛ لم يُنشأ تقرير ناقص.");
        var tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var doc=SKDocument.CreatePdf(tmp) ?? throw new IOException("تعذر إنشاء التقرير."))
            {
                var canvas=doc.BeginPage(pageWidth,pageHeight);
                if(watermark!=null) { float side=pageWidth*.62f,h=side*watermark.Height/Math.Max(1,watermark.Width);canvas.DrawBitmap(watermark,new SKRect(pageWidth/2-side/2,pageHeight/2-h/2,pageWidth/2+side/2,pageHeight/2+h/2),watermarkPaint); }
                if(logo!=null) canvas.DrawBitmap(logo,new SKRect(pageWidth/2-43,18,pageWidth/2+43,104));
                string NormalizeNumbers(string s)=>string.Concat(s.Select(c=>c>='٠'&&c<='٩'?(char)('0'+c-'٠'):c>='۰'&&c<='۹'?(char)('0'+c-'۰'):c=='٫'?'.':c=='٬'?',':c=='٪'?'%':c));
                List<string> Runs(string s)=>Regex.Matches(NormalizeNumbers(s),@"[A-Za-z0-9][A-Za-z0-9.,:/%+\-]*(?:\s+[A-Za-z0-9][A-Za-z0-9.,:/%+\-]*)*|[^A-Za-z0-9]+").Select(m=>m.Value).ToList();
                float Measure(string value) => Runs(value).Sum(x=>shaper.Shape(x,paint).Width);
                void Draw(string value,SKRect rect,float desired=10,bool center=false)
                {
                    value=value??""; if(value.Contains('\n')||value.Contains('\r')) throw new InvalidDataException("أحد حقول التقرير متعدد الأسطر ولا يتسع؛ لم يُنشأ تقرير ناقص.");
                    paint.TextSize=desired; var available=rect.Width-8;
                    while(paint.TextSize>6.5f && Measure(value)>available) paint.TextSize-=.5f;
                    if(Measure(value)>available) throw new InvalidDataException("أحد حقول التقرير طويل ولا يتسع؛ لم يُنشأ تقرير ناقص.");
                    var x=center?rect.MidX+Measure(value)/2:rect.Right-4;var y=rect.MidY+paint.TextSize*.34f;
                    foreach(var run in Runs(value)) { canvas.DrawShapedText(shaper,run,x,y,paint); x-=shaper.Shape(run,paint).Width; }
                }
                string Money(double? n)
                {
                    if(n.HasValue && !double.IsFinite(n.Value)) throw new InvalidDataException("يوجد مجموع غير صالح؛ لم يُنشأ التقرير.");
                    return n?.ToString("#,##0.##",CultureInfo.InvariantCulture)??"—";
                }
                void Cell(SKRect rect,string value,SKPaint? fill=null,float size=9.5f,bool center=false)
                { if(fill!=null) canvas.DrawRect(rect,fill);canvas.DrawRect(rect,line);Draw(value,rect,size,center); }
                Draw("جمهورية العراق",new SKRect(395,26,565,44),12,true);
                Draw("وزارة الداخلية",new SKRect(395,46,565,64),12,true);
                Draw("قيادة شرطة محافظة الانبار",new SKRect(395,66,565,84),10.5f,true);
                var monthParts=report.Month.Split(' ',StringSplitOptions.RemoveEmptyEntries);
                bool hasYear=monthParts.Length>1 && monthParts[^1].All(char.IsDigit);
                var monthName=hasYear?string.Join(" ",monthParts[..^1]):report.Month;
                Draw("الشهر : "+monthName,new SKRect(30,70,180,86),10);
                Draw("السنة : "+(hasYear?monthParts[^1]:"—"),new SKRect(30,88,180,104),10);
                Draw("برنامج الرواتب الالكتروني الموحد",new SKRect(margin,110,565,128),12.5f,true);
                Draw("تقرير راتب",new SKRect(margin,128,565,148),14,true);
                void Pair(float y,string label,string value,string label2="",string value2="")
                {
                    Draw(label+"  "+(string.IsNullOrWhiteSpace(value)?"—":value),new SKRect(300,y,565,y+20),10);
                    if(label2.Length>0) Draw(label2+"  "+(string.IsNullOrWhiteSpace(value2)?"—":value2),new SKRect(30,y,295,y+20),10);
                    canvas.DrawLine(margin,y+20,565,y+20,line);
                }
                Pair(156,"ندرج أدناه تفاصيل راتب",report.Rank,"/",report.Name);
                Pair(182,"منسوب",report.Agency,"مديرية",report.Directorate);
                Pair(208,"قسم",report.Section);
                void Table(float x,string title,string header,List<SalaryLine> lines,string totalLabel,double? total)
                {
                    Draw(title,new SKRect(x,startY,x+half,startY+16),11.5f,true);
                    float y=startY+18; var labelWidth=half*.6f;
                    void Row(string label,string value,SKPaint? fill=null)
                    {
                        Cell(new SKRect(x+half-labelWidth,y,x+half,y+rowHeight),label,fill);
                        Cell(new SKRect(x,y,x+half-labelWidth,y+rowHeight),value,fill,9.5f,true);y+=rowHeight;
                    }
                    Row(header,"المبلغ",head);
                    for(int i=0;i<tableRows;i++) Row(i<lines.Count?lines[i].Label:"",i<lines.Count?Money(lines[i].Value):"");
                    Row(totalLabel,Money(total),soft);
                }
                Table(565-half,"الراتب المستحق","اسم المخصصات",report.Entitlements,"الاستحقاق الكلي",report.EntitlementTotal);
                Table(margin,"الاستقطاعات","اسم الاستقطاع",report.Deductions,"مجموع الاستقطاعات",report.DeductionTotal);
                if(report.MissingDetails) Draw("أعد استيراد ملف الرواتب لإظهار المخصصات",new SKRect(565-half,startY+35,565,startY+55),8,true);
                canvas.DrawRect(margin,netY,width,26,soft);canvas.DrawRect(margin,netY,width,26,line);
                Draw("صافي الراتب",new SKRect(300,netY,540,netY+26),13,true);Draw(Money(report.Net),new SKRect(55,netY,295,netY+26),13,true);
                Draw("بيانات الموظف",new SKRect(565-half,infoY,565,infoY+16),11.5f,true);Draw("استقطاعات المصارف",new SKRect(margin,infoY,margin+half,infoY+16),11.5f,true);
                float iy=infoY+18, ix=565-half;
                foreach(var (label,value) in report.Info)
                {
                    Cell(new SKRect(ix+half*.55f,iy,ix+half,iy+rowHeight+1),label);
                    Cell(new SKRect(ix,iy,ix+half*.55f,iy+rowHeight+1),value,null,9,true);iy+=rowHeight+1;
                }
                float by=infoY+18;float[] bw=[half*.42f,half*.33f,half*.25f];
                void BankRow(string[] values,SKPaint? fill=null)
                { float x=margin+half;for(int i=0;i<3;i++){Cell(new SKRect(x-bw[i],by,x,by+rowHeight+1),values[i],fill,9,i!=0);x-=bw[i];}by+=rowHeight+1; }
                BankRow(["المصرف","الفرع","المبلغ"],head);
                if(report.Banks.Count==0) BankRow(["لا توجد","",""]);
                else { foreach(var b in report.Banks) BankRow([b.Bank,b.Branch,Money(b.Amount)]); if(report.Banks.Count>1) BankRow(["المجموع","",report.Banks.All(b=>b.Amount.HasValue)?Money(report.Banks.Sum(b=>b.Amount!.Value)):"غير متوفر"],soft); }
                canvas.DrawRect(margin,notesY,width,34,line);Draw("الملاحظات",new SKRect(495,notesY,565,notesY+34),10);
                if(report.Notes.Length>0) Draw(report.Notes,new SKRect(margin+4,notesY,490,notesY+34),9.5f);
                canvas.DrawLine(margin,pageHeight-26,565,pageHeight-26,line);Draw("( قيادة شرطة محافظة الانبار - قسم الحسابات )",new SKRect(315,pageHeight-24,565,pageHeight-8),8);
                Draw("صفحة 1 من 1",new SKRect(230,pageHeight-24,360,pageHeight-8),8,true);Draw(DateTime.Now.ToString("yyyy/MM/dd HH:mm"),new SKRect(30,pageHeight-24,170,pageHeight-8),8,true);
                doc.EndPage();doc.Close();
            }
            File.Move(tmp,path,true);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }
}
