using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace AnbarPayroll.Desktop;

internal static class PrecisionScroll
{
    // تجمع الإزاحات حتى إطار الرسم التالي لكيلا يؤدي كل حدث صغير إلى إعادة تخطيط الجدول.
    sealed class Offsets { public double? X; public double? Y; }
    static readonly Dictionary<ScrollViewer,Offsets> Pending=new();

    public static void VerticalWheel(object sender,MouseWheelEventArgs e)
    {
        if(e.Handled || Keyboard.Modifiers!=ModifierKeys.None || e.OriginalSource is not DependencyObject target)return;
        e.Handled=Move(target,-e.Delta/120d,false,SystemParameters.WheelScrollLines);
    }

    [DllImport("user32.dll",EntryPoint="SystemParametersInfoW")]
    static extern bool SystemParametersInfo(uint action,uint param,out uint value,uint flags);

    public static bool HorizontalWheel(Window window,IntPtr wParam,IntPtr lParam)
    {
        if(Keyboard.Modifiers!=ModifierKeys.None)return false;
        var coordinates=lParam.ToInt64();
        var point=window.PointFromScreen(new Point(unchecked((short)coordinates),unchecked((short)(coordinates>>16))));
        if(window.InputHitTest(point) is not DependencyObject target)return false;
        var delta=unchecked((short)(wParam.ToInt64()>>16));
        const uint getWheelScrollChars=0x006C;
        int chars=SystemParametersInfo(getWheelScrollChars,0,out var value,0)?unchecked((int)value):3;
        return Move(target,delta/120d,true,chars);
    }

    internal static bool Move(DependencyObject target,double notches,bool horizontal,int units)
    {
        if(units==0 || notches==0)return false;
        bool moved=false;
        for(DependencyObject? element=target;element!=null;element=ElementTree.Parent(element)){
            // العارض المضمّن والقائمة المفتوحة يستقبلان الإيماءة بطريقتهما الأصلية.
            if(element is HwndHost or ComboBox {IsDropDownOpen:true} or ComboBox {IsKeyboardFocusWithin:true})return moved;
            if(element is not ScrollViewer viewer)continue;
            var extent=horizontal?viewer.ScrollableWidth:viewer.ScrollableHeight;
            if(extent<=0)continue;
            var visibility=horizontal?viewer.HorizontalScrollBarVisibility:viewer.VerticalScrollBarVisibility;
            if(visibility==ScrollBarVisibility.Disabled)continue;
            // إزاحات بعض قوائم WPF بعدد العناصر وليست بالبكسل.
            if(viewer.CanContentScroll && !(viewer.TemplatedParent is DataGrid grid && VirtualizingPanel.GetScrollUnit(grid)==ScrollUnit.Pixel))
                return moved;
            double step=units<0?(horizontal?viewer.ViewportWidth:viewer.ViewportHeight):units*(horizontal?8d:16d);
            if(step<=0)continue;
            if(horizontal && viewer.FlowDirection==FlowDirection.RightToLeft)step=-step;
            Pending.TryGetValue(viewer,out var queued);
            var offset=horizontal?(queued?.X??viewer.HorizontalOffset):(queued?.Y??viewer.VerticalOffset);
            var distance=notches*step;
            var next=Math.Clamp(offset+distance,0,extent);
            if(next==offset)continue;
            if(queued==null){
                if(Pending.Count==0)CompositionTarget.Rendering+=Flush;
                Pending.Add(viewer,queued=new Offsets());
            }
            if(horizontal)queued.X=next;else queued.Y=next;
            moved=true;
            if(offset+distance==next)break;
            // عند بلوغ حد الجدول يصل المتبقي من الإيماءة فقط إلى الصفحة المحيطة.
            notches-=(next-offset)/step;
        }
        return moved;
    }

    static void Flush(object? sender,EventArgs e)
    {
        CompositionTarget.Rendering-=Flush;
        foreach(var (viewer,offsets) in Pending){
            if(!viewer.IsLoaded)continue;
            if(offsets.X is {} x)viewer.ScrollToHorizontalOffset(x);
            if(offsets.Y is {} y)viewer.ScrollToVerticalOffset(y);
        }
        Pending.Clear();
    }
}
