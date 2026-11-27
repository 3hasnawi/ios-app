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
        Check(slip.PixelWidth==1170 && slip.PixelHeight>Render().PixelHeight && pixels.Where((_,i)=>i%4==3).All(a=>a==255),"opaque full-size payslip");
        Check(pixels.SequenceEqual(Bytes(Render(line,stopped))),"inactive deduction excluded from the actual image");
        Check(!pixels.SequenceEqual(Bytes(Render(line with{Amount=1250}))),"amount changes the exported image");
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(slip));
        using(var output=File.Create(Path.Combine(folder,"payslip.png")))encoder.Save(output);
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
