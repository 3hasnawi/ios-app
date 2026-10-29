using SkiaSharp;
using SkiaSharp.HarfBuzz;
using System.Globalization;
using System.Text.RegularExpressions;
namespace SalaryWindows;
internal sealed record SlipLine(string Title, string Note, double? Amount);
internal sealed record SlipData(string Name,string Rank,string Workplace,string Month,double? Net,List<SlipLine> Lines)
{
    public static SlipData From(BackupData data, Employee employee)
    {
        var key=PayrollMath.Key(employee.Name);
        if (string.IsNullOrEmpty(key) || data.Employees.Count(x=>x.Month==employee.Month && PayrollMath.Key(x.Name)==key)!=1)
            throw new InvalidDataException("الاسم متكرر أو غير مكتمل في هذا الشهر؛ لا يمكن ربط الاستقطاعات بالقسيمة بأمان.");
        var staff=data.Staff.Where(x=>PayrollMath.Key(x.Name)==key).Take(2).ToList();
        var rank=string.IsNullOrWhiteSpace(employee.Rank) && staff.Count==1 ? staff[0].Rank : employee.Rank;
        var workplace=string.IsNullOrWhiteSpace(employee.Workplace) && staff.Count==1 ? staff[0].Workplace : employee.Workplace;
        var lines=data.Deductions.Where(x=>x.Month==employee.Month && PayrollMath.Key(x.Name)==key)
            .Select(x=>new SlipLine(x.Title,x.Note??"",x.Amount)).ToList();
        if (lines.Any(x=>x.Amount.HasValue && !double.IsFinite(x.Amount.Value)) || (employee.Net.HasValue && !double.IsFinite(employee.Net.Value)))
            throw new InvalidDataException("يوجد مبلغ غير صالح؛ لم تُنشأ قسيمة ناقصة.");
        return new(employee.Name,rank??"",workplace??"",employee.Month,employee.Net,lines);
    }
}
internal static class PayslipImage
{
    public static void Write(string path, SlipData data,string? fontPath=null)
    {
        fontPath ??=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts),"arial.ttf");
        if(!File.Exists(fontPath)) throw new InvalidOperationException("خط Arial غير موجود؛ لم تُنشأ القسيمة.");
        using var typeface=SKTypeface.FromFile(fontPath) ?? throw new InvalidDataException("خط القسيمة غير صالح.");
        using var shaper=new SKShaper(typeface);
        using var text=new SKPaint { Typeface=typeface,TextSize=14,Color=SKColors.Black,TextAlign=SKTextAlign.Right,IsAntialias=true };
        using var green=new SKPaint { Color=new SKColor(15,61,46) }; using var paper=new SKPaint { Color=new SKColor(255,253,247) };
        using var band=new SKPaint { Color=new SKColor(243,239,226) }; using var border=new SKPaint { Color=new SKColor(218,214,203),Style=SKPaintStyle.Stroke,StrokeWidth=.6f };
        const float width=390, margin=16, right=width-margin, tableWidth=width-margin*2;
        List<string> Wrap(string value,float max)
        {
            var lines=new List<string>(); string current="";
            foreach(var word in (value??"").Replace('\r',' ').Replace('\n',' ').Split(' ',StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate=current.Length==0?word:current+" "+word;
                if(shaper.Shape(candidate,text).Width<=max) { current=candidate;continue; }
                if(current.Length>0) { lines.Add(current);current=""; }
                foreach(var ch in word) { candidate=current+ch; if(current.Length>0 && shaper.Shape(candidate,text).Width>max) { lines.Add(current);current=""; } current+=ch; }
            }
            if(current.Length>0) lines.Add(current); if(lines.Count==0) lines.Add(""); return lines;
        }
        var information=new[]{("الاسم",data.Name),("الرتبة",data.Rank),("محل العمل",data.Workplace)}.Where(x=>!string.IsNullOrWhiteSpace(x.Item2))
            .Select(x=>(x.Item1,Lines:Wrap(x.Item2,tableWidth-112-20))).ToList();
        var renderedLines=data.Lines.Select(x=>(Item:x,Title:Wrap(x.Title,tableWidth-112-20),Notes:string.IsNullOrEmpty(x.Note)?new List<string>():Wrap(x.Note,tableWidth-112-20),
            Amount:Wrap(x.Amount?.ToString("#,##0.##",CultureInfo.InvariantCulture)??"غير متوفر",100))).ToList();
        float infoHeight=information.Sum(x=>Math.Max(36,x.Lines.Count*19+16));
        float deductionsHeight=data.Lines.Count==0?36:renderedLines.Sum(x=>Math.Max(36,Math.Max(x.Title.Count+x.Notes.Count,x.Amount.Count)*19+16))+36;
        float height=56+16+infoHeight+14+36+deductionsHeight+14+66+16;
        if(height>6000 || height<1) throw new InvalidDataException("بيانات القسيمة طويلة جداً؛ لم تُنشأ صورة ناقصة.");
        var tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var bitmap=new SKBitmap((int)(width*2),(int)Math.Ceiling(height*2)))
            using(var canvas=new SKCanvas(bitmap))
            {
                canvas.Scale(2);canvas.DrawRect(0,0,width,height,paper);canvas.DrawRect(0,0,width,56,green);
                void Text(string value,float x,float y)
                {
                    value=string.Concat(value.Select(c=>c>='٠'&&c<='٩'?(char)('0'+c-'٠'):c>='۰'&&c<='۹'?(char)('0'+c-'۰'):c=='٫'?'.':c=='٬'?',':c=='٪'?'%':c));
                    foreach(Match m in Regex.Matches(value,@"[A-Za-z0-9][A-Za-z0-9.,:/%+\-]*(?:\s+[A-Za-z0-9][A-Za-z0-9.,:/%+\-]*)*|[^A-Za-z0-9]+"))
                    { canvas.DrawShapedText(shaper,m.Value,x,y,text);x-=shaper.Shape(m.Value,text).Width; }
                }
                text.Color=SKColors.White; text.TextSize=17; Text("قسيمة راتب",right,34);
                text.TextSize=13; foreach(var (v,i) in Wrap(data.Month,180).Select((s,i)=>(s,i))) { if(i>1) throw new InvalidDataException("اسم الشهر طويل جداً."); Text(v,190,26+i*17); }
                text.Color=SKColors.Black;text.TextSize=14;float y=72;
                foreach(var item in information)
                {
                    var h=Math.Max(36,item.Lines.Count*19+16);canvas.DrawRect(right-112,y,112,h,border);canvas.DrawRect(margin,y,tableWidth-112,h,border);
                    Text(item.Item1,right-8,y+23);for(int i=0;i<item.Lines.Count;i++) Text(item.Lines[i],right-120,y+23+i*19);y+=h;
                }
                y+=14;canvas.DrawRect(margin,y,tableWidth,36,band);canvas.DrawRect(margin,y,112,36,border);canvas.DrawRect(margin+112,y,tableWidth-112,36,border);
                Text("الاستقطاع",right-8,y+23);Text("المبلغ",right-(tableWidth-112)-8,y+23);y+=36;
                void DrawLine(List<string> title,List<string> notes,List<string> amounts,float rowHeight)
                {
                    canvas.DrawRect(right-(tableWidth-112),y,tableWidth-112,rowHeight,border);canvas.DrawRect(margin,y,112,rowHeight,border);
                    int index=0;foreach(var part in title.Concat(notes)) Text(part,right-8,y+23+index++*19);
                    for(int i=0;i<amounts.Count;i++)Text(amounts[i],margin+104,y+23+i*19);
                    y+=rowHeight;
                }
                if(data.Lines.Count==0) DrawLine(["لا توجد استقطاعات"],[],["—"],36);
                else
                {
                    foreach(var row in renderedLines) DrawLine(row.Title,row.Notes,row.Amount,Math.Max(36,Math.Max(row.Title.Count+row.Notes.Count,row.Amount.Count)*19+16));
                    canvas.DrawRect(margin,y,tableWidth,36,band);canvas.DrawRect(margin,y,112,36,border);canvas.DrawRect(margin+112,y,tableWidth-112,36,border);
                    Text("المجموع",right-8,y+23);
                    var total=data.Lines.All(x=>x.Amount.HasValue)?data.Lines.Sum(x=>x.Amount!.Value):(double?)null;
                    if(total.HasValue && !double.IsFinite(total.Value)) throw new InvalidDataException("مجموع الاستقطاعات غير صالح؛ لم تُنشأ القسيمة.");
                    var sum=total?.ToString("#,##0.##",CultureInfo.InvariantCulture)??"غير متوفر";
                    if(Wrap(sum,100).Count>1) throw new InvalidDataException("مجموع الاستقطاعات لا يتسع في القسيمة؛ لم تُنشأ صورة ناقصة.");
                    Text(sum,margin+104,y+23);y+=36;
                }
                y+=14;using var netBand=new SKPaint { Color=new SKColor(230,239,233) };canvas.DrawRoundRect(new SKRect(margin,y,right,y+66),8,8,netBand);
                text.Color=new SKColor(15,61,46);text.TextSize=16;Text("صافي الراتب",right-10,y+26);
                text.TextSize=21;var net=data.Net?.ToString("#,##0.##",CultureInfo.InvariantCulture)??"غير متوفر";
                if(shaper.Shape(net,text).Width>tableWidth-20) throw new InvalidDataException("صافي الراتب لا يتسع في القسيمة؛ لم تُنشأ صورة ناقصة.");
                Text(net,right-10,y+53);
                using var image=SKImage.FromBitmap(bitmap); using var png=image.Encode(SKEncodedImageFormat.Png,100) ?? throw new IOException("تعذّر إنشاء صورة القسيمة.");
                using var output=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None);png.SaveTo(output);
            }
            File.Move(tmp,path,true);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }
}
