using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace AnbarPayroll.Desktop;

internal static class WindowPlacement
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor,Work; public int Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window,uint flag);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window,out NativeRect bounds);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);

    public static void OnLoaded(object sender,RoutedEventArgs args)
    {
        var window=(Window)sender;
        if(!ReferenceEquals(args.OriginalSource,window))return;
        var hwnd=new WindowInteropHelper(window).Handle;
        var source=HwndSource.FromHwnd(hwnd);
        if(source==null)return;
        double minWidth=window.MinWidth,minHeight=window.MinHeight;
        double maxWidth=window.MaxWidth,maxHeight=window.MaxHeight;
        bool autoSize=window.SizeToContent!=SizeToContent.Manual;
        var lastMonitor=MonitorFromWindow(hwnd,2);
        void Fit()
        {
            if(!window.IsVisible||window.WindowState!=WindowState.Normal)return;
            var info=new MonitorInfo{Size=Marshal.SizeOf<MonitorInfo>()};
            lastMonitor=MonitorFromWindow(hwnd,2);
            if(!GetMonitorInfo(lastMonitor,ref info)||!GetWindowRect(hwnd,out var bounds))return;
            var area=info.Work;
            var dpi=VisualTreeHelper.GetDpi(window);
            int width=area.Right-area.Left,height=area.Bottom-area.Top;
            window.MinWidth=Math.Min(minWidth,width/dpi.DpiScaleX);
            window.MinHeight=Math.Min(minHeight,height/dpi.DpiScaleY);
            if(autoSize){
                window.MaxWidth=Math.Min(maxWidth,width/dpi.DpiScaleX);
                window.MaxHeight=Math.Min(maxHeight,height/dpi.DpiScaleY);
            }
            // إحداثيات الشاشة وإطار النافذة بوحدات البكسل الفعلية وعلى الشاشة نفسها.
            int w=Math.Min(bounds.Right-bounds.Left,width),h=Math.Min(bounds.Bottom-bounds.Top,height);
            int x=Math.Clamp(bounds.Left,area.Left,area.Right-w),y=Math.Clamp(bounds.Top,area.Top,area.Bottom-h);
            if(x!=bounds.Left||y!=bounds.Top||w!=bounds.Right-bounds.Left||h!=bounds.Bottom-bounds.Top)
                SetWindowPos(hwnd,IntPtr.Zero,x,y,w,h,0x14); // SWP_NOZORDER | SWP_NOACTIVATE
        }
        bool queued=false;
        void QueueFit(){
            if(queued)return;
            queued=true;
            window.Dispatcher.InvokeAsync(()=>{queued=false;Fit();},DispatcherPriority.Loaded);
        }
        IntPtr MonitorChanges(IntPtr handle,int message,IntPtr wParam,IntPtr lParam,ref bool handled){
            bool changedWorkArea=message==0x001A&&wParam.ToInt64()==47;
            bool movedToAnotherScreen=message==0x0232&&MonitorFromWindow(hwnd,2)!=lastMonitor;
            if(message is 0x02E0 or 0x007E || changedWorkArea||movedToAnotherScreen)QueueFit();
            return IntPtr.Zero;
        }
        source.AddHook(MonitorChanges);
        window.StateChanged+=(_,_)=>{if(window.WindowState==WindowState.Normal)QueueFit();};
        Fit();
    }
}
