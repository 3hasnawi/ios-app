using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using A=SalaryDesktop.Core;

namespace AnbarPayroll.Desktop;

internal static class PayslipImage
{
    static SolidColorBrush Color(byte r,byte g,byte b)=>new(System.Windows.Media.Color.FromRgb(r,g,b));
    internal static BitmapSource Render(A.ProfileMonth month){
        var employee=month.Employee??throw new InvalidDataException("اختر راتباً واحداً لهذا الشهر.");
        var green=Color(15,61,46);var paper=Color(255,253,247);var rule=Color(227,223,209);var band=Color(243,239,226);
        TextBlock Text(string value,double size=14,bool bold=false,Brush? ink=null)=>new(){
            Text=value,FontFamily=new FontFamily("Arial"),FontSize=size,FontWeight=bold?FontWeights.Bold:FontWeights.Normal,
            TextWrapping=TextWrapping.Wrap,Foreground=ink??Brushes.Black,VerticalAlignment=VerticalAlignment.Center};
        string Money(decimal? value)=>value.HasValue?Arabic.Eastern(A.ReportSheet.Money(value.Value)):"—";
        Grid Pair(string title,string value,bool narrow,Brush background,bool bold=false,bool framed=true,Brush? ink=null,string note=""){
            var row=new Grid{Background=background};
            var fixedWidth=new GridLength(112);var stretch=new GridLength(1,GridUnitType.Star);
            row.ColumnDefinitions.Add(new(){Width=narrow?fixedWidth:stretch});row.ColumnDefinitions.Add(new(){Width=narrow?stretch:fixedWidth});
            for(int i=0;i<2;i++){
                var stack=new StackPanel{VerticalAlignment=VerticalAlignment.Center};
                stack.Children.Add(Text(i==0?title:value,14,bold||narrow&&title=="الاسم"&&i==1,i==1 && title=="المجموع"?Brushes.DarkRed:ink));
                if(i==0 && note.Length>0)stack.Children.Add(Text(note,11,false,Brushes.DimGray));
                var cell=new Border{Child=stack,Padding=new Thickness(10,8,10,8),BorderBrush=rule,
                    BorderThickness=framed?new Thickness(A.ReportTableStyle.BorderWidth):new Thickness(0)};
                Grid.SetColumn(cell,i);row.Children.Add(cell);
            }
            return row;
        }
        var body=new StackPanel{Margin=new Thickness(16)};
        var method=A.PersonProfiles.Details(employee).GetValueOrDefault("طريقة الدفع","").Trim();
        var key=A.PayrollAnalysis.Normalize(method);
        string payment=key.Contains("بطاق")||key.Contains("كارت")||key.Contains("ماستر")?"بطاقة":key.Contains("كاش")||key.Contains("نقد")?"كاش":method.Length==0?"غير محدد":method;
        foreach(var (label,value) in new[]{("الاسم",employee.Name),("الرتبة",employee.Rank),("محل العمل",employee.Workplace),("نوع الدفع",payment)})
            if(!string.IsNullOrWhiteSpace(value))body.Children.Add(Pair(label,value,true,paper));
        var table=new StackPanel{Margin=new Thickness(0,14,0,14)};body.Children.Add(table);
        table.Children.Add(Pair("الاستقطاع","المبلغ",false,band,true));
        var lines=month.Deductions.Where(d=>!A.PayrollAnalysis.IsInactive(d)).OrderBy(d=>d.Title,StringComparer.Create(new("ar-IQ"),false)).ToArray();
        foreach(var d in lines)table.Children.Add(Pair(d.Title,Money(d.Amount),false,paper,note:d.Note));
        table.Children.Add(lines.Length==0?Pair("لا توجد استقطاعات","—",false,paper):
            Pair("المجموع",Money(lines.Sum(d=>d.Amount??0)),false,band,true));
        var net=new DockPanel();var total=Text(Money(employee.Net),26,true,green);DockPanel.SetDock(total,Dock.Left);
        net.Children.Add(total);net.Children.Add(Text("صافي الراتب",16,true,green));
        body.Children.Add(new Border{Child=net,Background=Color(237,243,236),Padding=new Thickness(14,12,14,12),CornerRadius=new CornerRadius(8)});
        var header=new DockPanel();var date=Text(Arabic.Eastern(month.Month),15,false,Brushes.White);DockPanel.SetDock(date,Dock.Left);
        header.Children.Add(date);header.Children.Add(Text("قسيمة راتب",18,true,Brushes.White));
        var card=new StackPanel{Width=390,Background=paper,FlowDirection=FlowDirection.RightToLeft};
        card.Children.Add(new Border{Child=header,Padding=new Thickness(18,14,18,14),Background=green});
        card.Children.Add(body);card.Measure(new Size(390,double.PositiveInfinity));
        int height=checked((int)Math.Ceiling(card.DesiredSize.Height));
        if(height*3L>32767)throw new InvalidDataException("القسيمة أطول من حدود الصورة. استخدم تقرير الراتب PDF.");
        // أصل الصورة باتجاه ثابت حتى يبقى تحويل الاتجاه العربي داخل مساحة الرسم.
        var surface=new Border{Width=390,Height=height,Child=card,Background=paper,FlowDirection=FlowDirection.LeftToRight};
        surface.Measure(new Size(390,height));surface.Arrange(new Rect(0,0,390,height));surface.UpdateLayout();
        var image=new RenderTargetBitmap(1170,height*3,288,288,PixelFormats.Pbgra32);image.Render(surface);image.Freeze();return image;
    }
    public static void Save(A.ProfileMonth month,Window owner){
        var dialog=new SaveFileDialog{Filter="صورة PNG|*.png",FileName=A.ReportSheet.FileName("قسيمة راتب - "+month.Employee?.Name+" - "+month.Month)};
        if(dialog.ShowDialog(owner)!=true)return;
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(Render(month)));
        using var bytes=new MemoryStream();encoder.Save(bytes);
        var temp=dialog.FileName+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllBytes(temp,bytes.ToArray());File.Move(temp,dialog.FileName,true);}finally{File.Delete(temp);}
    }
}
