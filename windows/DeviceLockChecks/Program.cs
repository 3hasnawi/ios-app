using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AnbarPayroll.Desktop;

internal static class Program
{
    static void Check(bool condition,string name)
    {
        if(!condition)throw new InvalidOperationException(name);
        Console.WriteLine("PASS "+name);
    }

    [STAThread]
    static int Main()
    {
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var closed=new DeviceLock(0,_=>throw new InvalidOperationException(),_=>throw new InvalidOperationException());
        app.Dispatcher.InvokeAsync(()=>app.Windows.OfType<Window>().Single().Close(),DispatcherPriority.ApplicationIdle);
        Check(!closed.Start(),"closing the startup gate denies access");
        int saved=0;
        string? rejection="إلغاء";
        var protector=new DeviceLock(0,_=>Task.FromResult<string?>(rejection),delay=>saved=delay);
        app.Dispatcher.InvokeAsync(()=>{
            var dialog=app.Windows.OfType<Window>().Single();
            var controls=((StackPanel)dialog.Content).Children;
            var unlock=controls.OfType<Button>().Single();
            unlock.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(dialog.IsVisible && controls.OfType<TextBlock>().Last().Text==rejection,"rejected verification keeps the startup gate visible");
            rejection=null;
            unlock.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        },DispatcherPriority.ApplicationIdle);
        Check(protector.Start(),"verified startup permits opening the workspace");

        var body=new TextBlock{Text="fictional payroll"};
        var main=new Window{Content=body,Title="workspace"};
        app.MainWindow=main;
        main.Show();
        rejection="إلغاء";
        Check(protector.Change(main,-1).GetAwaiter().GetResult()!=null && protector.Delay==0 && saved==0,"failed verification cannot disable protection");
        rejection=null;
        Check(protector.Change(main,60).GetAwaiter().GetResult()==null && saved==60,"verified timeout change");
        var reportBody=new TextBlock{Text="fictional report"};
        var report=new Window{Content=reportBody,Owner=main,Title="report"};
        report.Show();
        protector.LeftApplication();
        Check(!body.IsVisible && !reportBody.IsVisible && report.Title!="report","background hides main and owned document");
        protector.ReturnedToApplication();
        var frame=new DispatcherFrame();
        app.Dispatcher.InvokeAsync(()=>frame.Continue=false,DispatcherPriority.ApplicationIdle);
        Dispatcher.PushFrame(frame);
        Check(body.IsVisible && reportBody.IsVisible && report.Title=="report","return within grace period restores open documents");
        Check(protector.Change(main,0).GetAwaiter().GetResult()==null,"enable immediate relock");
        protector.LeftApplication();
        protector.ReturnedToApplication();
        var resumed=new DispatcherFrame();
        app.Dispatcher.InvokeAsync(()=>{
            var dialog=app.Windows.OfType<Window>().Single(w=>w!=main && w!=report);
            Check(!body.IsVisible && !reportBody.IsVisible,"immediate return keeps documents hidden until verification");
            ((StackPanel)dialog.Content).Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            resumed.Continue=false;
        },DispatcherPriority.ApplicationIdle);
        Dispatcher.PushFrame(resumed);
        Check(body.IsVisible && reportBody.IsVisible,"verified resume preserves open documents");
        Check(protector.Change(main,-1).GetAwaiter().GetResult()==null && saved==-1,"verified disable");
        protector.LeftApplication();
        Check(body.IsVisible && reportBody.IsVisible,"disabled lock preserves normal behavior");
        app.Shutdown();
        return 0;
    }
}
