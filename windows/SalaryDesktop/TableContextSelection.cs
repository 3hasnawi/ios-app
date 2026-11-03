using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace SalaryDesktop;
/// يضمن أن إجراءات القائمة اليمنى تخص الصف الذي نُقر عليه، مع إبقاء التحديد المتعدد إذا كان الصف محدداً.
internal static class TableContextSelection
{
    private static DataGridRow? FindRow(DataGrid grid, DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is DataGridRow row) return row;
            if (ReferenceEquals(node, grid)) return null;
            node = node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }
    public static void Select(DataGrid grid, MouseButtonEventArgs e)
    {
        if (FindRow(grid, e.OriginalSource as DependencyObject) is not { } row) return;
        if (!row.IsSelected) { grid.SelectedItems.Clear(); row.IsSelected = true; grid.CurrentItem = row.Item; }
    }
    public static void Guard(DataGrid grid, ContextMenuEventArgs e)
    {
        // طلب لوحة المفاتيح يستخدم التحديد الحالي؛ طلب الفأرة يجب أن يكون فوق صف فعلي.
        if (e.CursorLeft < 0) { if (grid.SelectedItem is null) e.Handled = true; return; }
        if (FindRow(grid, grid.InputHitTest(Mouse.GetPosition(grid)) as DependencyObject) is null) e.Handled = true;
    }
}
