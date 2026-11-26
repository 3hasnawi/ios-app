using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace AnbarPayroll.Desktop;

internal static class ElementTree
{
    internal static DependencyObject? Parent(DependencyObject item)=>item switch{
        Visual or Visual3D=>VisualTreeHelper.GetParent(item),
        FrameworkContentElement text=>text.Parent??ContentOperations.GetParent(text),
        ContentElement content=>ContentOperations.GetParent(content),
        _=>LogicalTreeHelper.GetParent(item)
    };
}
