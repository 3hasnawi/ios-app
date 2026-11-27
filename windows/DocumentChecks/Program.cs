using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using AnbarPayroll.Desktop;
using SkiaSharp;
using UglyToad.PdfPig;
using A=SalaryDesktop.Core;

internal static class Program
{
    static void Check(bool value,string label){if(!value)throw new InvalidDataException(label);Console.WriteLine("PASS "+label);}

    [STAThread]
    static void Main(string[] args){
        var folder=args.Single();Directory.CreateDirectory(folder);
        const string month="تشرين الثاني 2026";
        A.Employee employee=new(month,"نور الاختبار","موظف","قسم تجريبي",15000,null,null,null);
        var line=new A.Deduction(month,employee.Name,"جهة تجريبية",250,"ملاحظة تجريبية",employee.Rank);
        var stopped=line with{Amount=null,Note="⟦حالة الاستقطاع:موقوف⟧"};
        BitmapSource Render(params A.Deduction[] lines)=>PayslipImage.Render(new(month,[employee],lines));
        static byte[] Bytes(BitmapSource image){
            var data=new byte[image.PixelWidth*image.PixelHeight*4];image.CopyPixels(data,image.PixelWidth*4,0);return data;
        }
        var slip=Render(line);var pixels=Bytes(slip);
        Check(slip.PixelWidth==1170 && slip.PixelHeight>Render().PixelHeight,"full-size payslip with a deduction and total");
        var translucent=pixels.Where((_,i)=>i%4==3).Count(a=>a!=255);
        Check(translucent==0,$"opaque payslip including edges ({translucent} translucent pixels)");

        int headerPixel=4*(10*slip.PixelWidth+10);
        Check(pixels[headerPixel]<100 && pixels[headerPixel+2]<80,"green payslip title band");
        Check(pixels.SequenceEqual(Bytes(Render(line,stopped))),"inactive deduction excluded from the actual image");
        Check(!pixels.SequenceEqual(Bytes(Render(line with{Amount=1250}))),"amount changes the exported image");
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(slip));
        using(var output=File.Create(Path.Combine(folder,"payslip.png")))encoder.Save(output);
        var names=new[]{"أحمد سامر محمود","باسم علي حسن","حسن فاضل ناصر","خالد سعد كريم","رائد عماد علي","سامي كاظم حسن","عادل حامد سالم","علي حسن ناصر","محمد ناصر علي","مصطفى سعد سالم","نزار حامد فاضل","يوسف سامر كريم"};
        var rows=names.Select((name,i)=>new[]{(i+1).ToString(),i%2==0?"مفوض":"شرطي",name,"","قسم تجريبي"}).ToArray();
        var general=new A.ReportSheet(A.ReportSheet.DefaultHeader,"قائمة أسماء ورواتب تجريبية",
            [new("ت",5,A.ReportColumnKind.Serial),new("الرتبة",11,A.ReportColumnKind.Text),
             new("الاسم الكامل",34,A.ReportColumnKind.Text),new("المبلغ",14,A.ReportColumnKind.Amount),
             new("محل العمل",30,A.ReportColumnKind.Text)],rows,names.Select((_,i)=>473000m+i*10000m).ToArray()).Fitted();
        var fonts=Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var assets=new A.ReportAssets(File.ReadAllBytes(Path.Combine(fonts,"arial.ttf")),File.ReadAllBytes(Path.Combine(fonts,"arialbd.ttf")),null);
        File.WriteAllBytes(Path.Combine(folder,"general-report.pdf"),A.ReportPdf.Generate([general],assets,DateTime.UnixEpoch));

        static byte[] Page(int w,int h){
            using var bitmap=new SKBitmap(w,h);bitmap.Erase(SKColors.Green);
            using var image=SKImage.FromBitmap(bitmap);using var encoded=image.Encode(SKEncodedImageFormat.Png,100);return encoded.ToArray();
        }
        var portrait=Page(120,160);var landscape=Page(160,120);
        var pages=new Queue<byte[]>([portrait,landscape,portrait]);
        var pdf=ArchiveScanner.Scan(pages.Dequeue,()=>MessageBoxResult.Yes)!;
        using(var doc=PdfDocument.Open(pdf)){
            Check(doc.NumberOfPages==3 && doc.GetPage(1).Height>doc.GetPage(1).Width && doc.GetPage(2).Width>doc.GetPage(2).Height,"three scanned pages with portrait and landscape orientation");
        }
        using(var doc=PdfDocument.Open(ArchiveScanner.Scan(()=>portrait,()=>MessageBoxResult.No)!))Check(doc.NumberOfPages==1,"finish after first page");
        Check(ArchiveScanner.Scan(()=>portrait,()=>MessageBoxResult.Cancel)==null,"cancel discards all pages");
        Check(ArchiveScanner.Scan(()=>null,()=>throw new InvalidOperationException())==null,"device cancellation");
    }
}
