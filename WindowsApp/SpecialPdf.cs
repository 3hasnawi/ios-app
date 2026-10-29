using System.Globalization;
using System.Text.RegularExpressions;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
namespace SalaryWindows;
internal static class SpecialPdf
{
    private sealed record LayoutRow(List<string>[] Cells,float Height,List<SpecialExcel.Cell> Values,int SheetRow);
    private sealed record Page(SpecialExcel.Sheet Sheet,List<LayoutRow> Rows,List<string> TitleLines,float[] Widths);
    public static void Write(string path,BackupData data,string month,string title,string? fontPath=null)
    {
        var sheets=SpecialExcel.Build(data,month,title);
        fontPath ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts),"arial.ttf");
        if(!File.Exists(fontPath)) throw new InvalidOperationException("خط Arial غير موجود؛ لم يُنشأ الكشف.");
        using var font=SKTypeface.FromFile(fontPath) ?? throw new InvalidDataException("خط Arial غير صالح.");
        using var shaper=new SKShaper(font);
        using var paint=new SKPaint { Typeface=font,TextSize=10,Color=SKColors.Black,TextAlign=SKTextAlign.Right,IsAntialias=true };
        using var border=new SKPaint { Color=SKColors.Black,Style=SKPaintStyle.Stroke,StrokeWidth=.5f };
        using var yellow=new SKPaint { Color=new SKColor(255,255,0) }; using var gray=new SKPaint { Color=new SKColor(217,217,217) };
        using var red=new SKPaint { Color=new SKColor(255,124,128) }; using var green=new SKPaint { Color=new SKColor(146,208,80) };
        var watermarkPath=Path.Combine(AppContext.BaseDirectory,"Resources","WatermarkLogo.png"); using var watermark=File.Exists(watermarkPath)?SKBitmap.Decode(watermarkPath):null;
        using var watermarkPaint=new SKPaint { Color=SKColors.White.WithAlpha(26),FilterQuality=SKFilterQuality.Medium };
        const float pageWidth=595,pageHeight=842,margin=32,width=531,baselineHeight=14;
        List<string> Wrap(string text,float max)
        {
            var lines=new List<string>(); var current="";
            foreach(var word in (text??"").Replace('\r',' ').Replace('\n',' ').Split(' ',StringSplitOptions.RemoveEmptyEntries))
            {
                var next=current.Length==0?word:current+" "+word;
                if(shaper.Shape(next,paint).Width<=max) { current=next; continue; }
                if(current.Length>0) { lines.Add(current); current=""; }
                foreach(var c in word) { next=current+c; if(current.Length>0 && shaper.Shape(next,paint).Width>max) { lines.Add(current);current=""; } current+=c; }
            }
            if(current.Length>0) lines.Add(current); if(lines.Count==0) lines.Add(""); return lines;
        }
        var pages=new List<Page>();
        foreach(var sheet in sheets)
        {
            var totalWidth=sheet.Widths.Sum(); var widths=sheet.Widths.Select(w=>(float)(w/totalWidth*width)).ToArray();
            paint.TextSize=12; var titleLines=Wrap(sheet.Title,width-8); if(titleLines.Count>4) throw new InvalidDataException("عنوان الكشف طويل ولا يتسع في الصفحة.");
            var titleHeight=Math.Max(30,titleLines.Count*16+6); var headerEnd=margin+titleHeight+4+24; paint.TextSize=10;
            var page=new Page(sheet,[],titleLines,widths); pages.Add(page); float y=headerEnd;
            var sheetRow=2;
            foreach(var row in sheet.Rows)
            {
                sheetRow++;
                var lines=new List<string>[widths.Length];
                for(int i=0;i<lines.Length;i++)
                {
                    var cell=i<row.Count?row[i]:new SpecialExcel.Cell(Text:"");
                    var value=cell.Number.HasValue?cell.Number.Value.ToString("#,##0.##",CultureInfo.InvariantCulture):cell.Text??"";
                    lines[i]=Wrap(value,widths[i]-8);
                }
                var h=Math.Max(20,lines.Max(x=>x.Count)*baselineHeight+6);
                if(headerEnd+h>pageHeight-40) throw new InvalidDataException("أحد السجلات طويل ولا يتسع في الصفحة؛ لم يُصدر كشف ناقص.");
                if(y+h>pageHeight-40) { page=new Page(sheet,[],titleLines,widths); pages.Add(page); y=headerEnd; }
                page.Rows.Add(new(lines,h,row,sheetRow)); y+=h;
            }
        }
        var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var doc=SKDocument.CreatePdf(temporary) ?? throw new IOException("تعذر إنشاء ملف PDF."))
            {
                for(int pageIndex=0;pageIndex<pages.Count;pageIndex++)
                {
                    var p=pages[pageIndex]; var canvas=doc.BeginPage(pageWidth,pageHeight);
                    if(watermark!=null) { float side=pageWidth*.62f, h=side*watermark.Height/Math.Max(1,watermark.Width); canvas.DrawBitmap(watermark,new SKRect(pageWidth/2-side/2,pageHeight/2-h/2,pageWidth/2+side/2,pageHeight/2+h/2),watermarkPaint); }
                    void Text(string text,float x,float y)
                    {
                        text=string.Concat(text.Select(c=>c>='٠'&&c<='٩'?(char)('0'+c-'٠'):c>='۰'&&c<='۹'?(char)('0'+c-'۰'):c=='٫'?'.':c=='٬'?',':c=='٪'?'%':c));
                        foreach(Match m in Regex.Matches(text,@"[A-Za-z0-9][A-Za-z0-9.,:/%+\-]*(?:\s+[A-Za-z0-9][A-Za-z0-9.,:/%+\-]*)*|[^A-Za-z0-9]+"))
                        { canvas.DrawShapedText(shaper,m.Value,x,y,paint); x-=shaper.Shape(m.Value,paint).Width; }
                    }
                    paint.TextSize=12; var titleHeight=Math.Max(30,p.TitleLines.Count*16+6); float y=margin;
                    if(p.Sheet.Tab!=null) { canvas.DrawRect(margin,y,width,titleHeight,yellow); canvas.DrawRect(margin,y,width,titleHeight,border); }
                    foreach(var line in p.TitleLines) { Text(line,margin+width-4,y+14); y+=16; }
                    y=margin+titleHeight+4; paint.TextSize=10; float x=margin+width;
                    for(int i=0;i<p.Sheet.Headers.Length;i++)
                    {
                        canvas.DrawRect(x-p.Widths[i],y,p.Widths[i],24,gray); canvas.DrawRect(x-p.Widths[i],y,p.Widths[i],24,border);
                        var label=Wrap(p.Sheet.Headers[i],p.Widths[i]-8);
                        if(label.Count>2) throw new InvalidDataException("عنوان عمود لا يتسع في الكشف.");
                        for(int j=0;j<label.Count;j++) Text(label[j],x-4,y+10+j*11);
                        x-=p.Widths[i];
                    }
                    y+=24;
                    foreach(var row in p.Rows)
                    {
                        x=margin+width;
                        for(int c=0;c<row.Cells.Length;c++)
                        {
                            var merged=p.Sheet.Merges.Select(m=>Regex.Match(m,@"^([A-Z])(\d+):([A-Z])(\d+)$"))
                                .FirstOrDefault(m=>m.Success && int.Parse(m.Groups[2].Value)==row.SheetRow && int.Parse(m.Groups[4].Value)==row.SheetRow && m.Groups[1].Value[0]-'A'==c);
                            int end=merged==null?c:merged.Groups[3].Value[0]-'A';
                            var spanWidth=0f; for(int j=c;j<=end;j++) spanWidth+=p.Widths[j];
                            int style=c<row.Values.Count?row.Values[c].Style:4;
                            if(style==5) canvas.DrawRect(x-spanWidth,y,spanWidth,row.Height,red);
                            else if(style==6) canvas.DrawRect(x-spanWidth,y,spanWidth,row.Height,green);
                            else if(style==2) canvas.DrawRect(x-spanWidth,y,spanWidth,row.Height,gray);
                            canvas.DrawRect(x-spanWidth,y,spanWidth,row.Height,border);
                            for(int i=0;i<row.Cells[c].Count;i++) Text(row.Cells[c][i],x-4,y+13+i*baselineHeight);
                            x-=spanWidth; c=end;
                        }
                        y+=row.Height;
                    }
                    canvas.DrawLine(margin,pageHeight-26,margin+width,pageHeight-26,border); paint.TextSize=8;
                    Text("( قيادة شرطة محافظة الانبار - قسم الحسابات )",margin+width,pageHeight-10);
                    Text($"صفحة {pageIndex+1} من {pages.Count}",pageWidth/2+40,pageHeight-10);
                    Text(DateTime.Now.ToString("yyyy/MM/dd HH:mm"),margin+100,pageHeight-10);
                    doc.EndPage();
                }
                doc.Close();
            }
            File.Move(temporary,path,true);
        }
        finally { try { File.Delete(temporary); } catch { } }
    }
}
