using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;

namespace AnbarPayroll.Desktop;

internal sealed class ReportWindow : Window
{
    private readonly Statement report;
    private readonly WebView2 viewer=new();
    private readonly Button pdf=new(){Content="تصدير PDF",Margin=new Thickness(4),IsEnabled=false};
    private readonly TextBlock status=new(){Margin=new Thickness(10),VerticalAlignment=VerticalAlignment.Center};
    private readonly string folder=Path.Combine(Path.GetTempPath(),"AnbarPayroll",Guid.NewGuid().ToString("N"));
    private readonly TaskCompletionSource<bool> ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool closed;
    public ReportWindow(Statement model)
    {
        SetResourceReference(StyleProperty,typeof(Window));
        report=model;Title="معاينة الكشف";Width=1000;Height=760;MinWidth=620;MinHeight=450;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;FlowDirection=FlowDirection.RightToLeft;
        var root=new DockPanel();var toolbar=new WrapPanel{Margin=new Thickness(8)};
        var excel=new Button{Content="حفظ Excel",Margin=new Thickness(4)};
        excel.Click+=async (_,_)=>await Save(false);pdf.Click+=async (_,_)=>await Save(true);
        toolbar.Children.Add(pdf);toolbar.Children.Add(excel);toolbar.Children.Add(status);
        DockPanel.SetDock(toolbar,Dock.Top);root.Children.Add(toolbar);root.Children.Add(viewer);Content=root;
        Loaded+=async (_,_)=>await LoadPreview();
        Closed+=(_,_)=>{closed=true;viewer.Dispose();if(Directory.Exists(folder))try{Directory.Delete(folder,true);}catch(IOException){}};
    }
    async Task LoadPreview()
    {
        try{
            status.Text="جارٍ إعداد المعاينة…";Directory.CreateDirectory(folder);
            var file=Path.Combine(folder,"statement.html");await File.WriteAllTextAsync(file,await Task.Run(report.Html));
            var profile=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AnbarPayroll","WebView");
            var env=await CoreWebView2Environment.CreateAsync(userDataFolder:profile);if(closed)return;
            await viewer.EnsureCoreWebView2Async(env);if(closed)return;
            var web=viewer.CoreWebView2;web.Settings.IsScriptEnabled=false;web.Settings.AreDevToolsEnabled=false;web.Settings.IsWebMessageEnabled=false;
            web.Settings.AreBrowserAcceleratorKeysEnabled=false;
            // نفتح من قائمة العارض أمر النسخ وحده، ويأتي النص من خلية الكشف المعروضة.
            web.ContextMenuRequested+=(_,args)=>{
                var items=args.MenuItems;
                for(int i=items.Count-1;i>=0;i--)
                    if(items[i].Name!="copy")items.RemoveAt(i);
                if(items.Count==0)args.Handled=true;
            };
            await web.AddScriptToExecuteOnDocumentCreatedAsync("""
                document.addEventListener('contextmenu', event => {
                    const cell = event.target.closest?.('td, th');
                    const selection = window.getSelection();
                    if (!cell || !selection || (!selection.isCollapsed && selection.containsNode(cell, true))) return;
                    const range = document.createRange();
                    range.selectNodeContents(cell);
                    selection.removeAllRanges();
                    selection.addRange(range);
                });
                """);
            if(closed)return;
            var uri=new Uri(file);
            web.NavigationStarting+=(_,e)=>e.Cancel=!uri.Equals(new Uri(e.Uri));
            web.NewWindowRequested+=(_,e)=>e.Handled=true;
            web.NavigationCompleted+=(_,e)=>{ready.TrySetResult(e.IsSuccess);pdf.IsEnabled=e.IsSuccess;status.Text=e.IsSuccess?"معاينة قبل التصدير":"تعذّر فتح المعاينة.";};
            web.Navigate(uri.AbsoluteUri);
        }catch(Exception e){ready.TrySetException(e);if(!closed)status.Text="تعذّر فتح عارض PDF. "+e.Message;}
    }
    internal async Task PrintPdf(string path)
    {
        if(!await ready.Task.WaitAsync(TimeSpan.FromSeconds(30)))throw new IOException("تعذّرت معاينة الكشف.");
        var settings=viewer.CoreWebView2.Environment.CreatePrintSettings();
                settings.Orientation=report.Landscape?CoreWebView2PrintOrientation.Landscape:CoreWebView2PrintOrientation.Portrait;
                settings.PageWidth=8.2677;settings.PageHeight=11.6929;settings.ShouldPrintBackgrounds=true;settings.ShouldPrintHeaderAndFooter=false;
                if(!await viewer.CoreWebView2.PrintToPdfAsync(path,settings))throw new IOException("تعذّر إنشاء PDF.");
    }
    async Task Save(bool isPdf)
    {
        string ext=isPdf?"pdf":"xlsx";
        var d=new SaveFileDialog{FileName=report.Person==""?report.Sheets[0].Name:"كشف استقطاعات "+report.Person,Filter=ext.ToUpperInvariant()+"|*."+ext,DefaultExt=ext};
        if(d.ShowDialog(this)!=true)return;
        string temp=Path.Combine(Path.GetDirectoryName(d.FileName)!,"."+Guid.NewGuid().ToString("N")+"."+ext);
        IsEnabled=false;
        try{
            if(isPdf)await PrintPdf(temp);
            else await Task.Run(()=>report.Excel(temp));
            File.Move(temp,d.FileName,true);status.Text="تم حفظ الكشف.";
        }catch(Exception e){MessageBox.Show(this,e.Message,"تعذّر تصدير الكشف");}
        finally{IsEnabled=true;if(File.Exists(temp))File.Delete(temp);}
    }
}
