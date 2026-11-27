using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using SkiaSharp;
using A=SalaryDesktop.Core;

namespace AnbarPayroll.Desktop;

internal static class ArchiveScanner
{
    // يختار الموظف الجهاز والدقة والقص من نافذة المسح الأصلية في ويندوز.
    static byte[]? Acquire(){
        var type=Type.GetTypeFromProgID("WIA.CommonDialog")??throw new InvalidOperationException("خدمة المسح الضوئي WIA غير متاحة.");
        dynamic ui=Activator.CreateInstance(type)!;
        object? image=null,data=null;
        try{
            image=ui.ShowAcquireImage(1,0,131072,"{B96B3CAF-0728-11D3-9D7B-0000F81EF32E}",false,true,false);
            if(image==null)return null;
            data=((dynamic)image).FileData;
            return (byte[])((dynamic)data).BinaryData;
        }catch(COMException ex) when ((uint)ex.HResult==0x80210015){
            throw new InvalidOperationException("تعذّر العثور على سكنر. تحقق من توصيل الجهاز وتعريفه في ويندوز.",ex);
        }finally{
            foreach(object? component in new object?[]{data,image,ui})
                if(component!=null&&Marshal.IsComObject(component))Marshal.FinalReleaseComObject(component);
        }
    }

    public static byte[]? Scan(Window owner)=>Scan(Acquire,()=>MessageBox.Show(owner,"هل تريد مسح صفحة أخرى لهذا الكتاب؟","سكنر الأرشيف",MessageBoxButton.YesNoCancel,MessageBoxImage.Question));

    internal static byte[]? Scan(Func<byte[]?> acquire,Func<MessageBoxResult> ask){
        using var output=new MemoryStream();using var pdf=A.PdfOutput.Create(output);
        for(int page=1;page<=3;page++){
            var bytes=acquire();
            if(bytes==null)return null;
            using var bitmap=SKBitmap.Decode(bytes)??throw new IOException("تعذّرت قراءة الصورة من السكنر.");
            float w=bitmap.Width>bitmap.Height?842:595,h=bitmap.Width>bitmap.Height?595:842;
            var canvas=pdf.BeginPage(w,h);canvas.Clear(SKColors.White);
            float scale=Math.Min(w/bitmap.Width,h/bitmap.Height),width=bitmap.Width*scale,height=bitmap.Height*scale;
            canvas.DrawBitmap(bitmap,SKRect.Create((w-width)/2,(h-height)/2,width,height));pdf.EndPage();
            if(page==3)break;
            var next=ask();
            if(next==MessageBoxResult.Cancel)return null;
            if(next==MessageBoxResult.No)break;
        }
        pdf.Close();
        if(output.Length>60_000_000)throw new IOException("تجاوز حجم الكتاب 60 ميغابايت. اختر دقة أقل في السكنر.");
        return output.ToArray();
    }
}
