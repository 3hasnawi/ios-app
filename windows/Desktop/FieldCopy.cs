using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;

namespace AnbarPayroll.Desktop;

internal static class FieldCopy
{
    const string CopyField = "نسخ قيمة الحقل";

    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.ContextMenuOpeningEvent,
            new ContextMenuEventHandler(OpenMenu));
        EventManager.RegisterClassHandler(typeof(Window), Keyboard.PreviewKeyDownEvent,
            new KeyEventHandler(CopyShortcut));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent,
            new RoutedEventHandler(AddToEditorMenu));
    }

    internal static string? ValueAt(DependencyObject? source)
    {
        string? text = null;
        for (var item = source; item != null; item = ElementTree.Parent(item))
        {
            switch (item)
            {
                case PasswordBox or HwndHost:
                    return null;
                case TextBox field:
                    return field.Text;
                case DataGridCell cell:
                    return (cell.Content as TextBlock)?.Text ?? text;
                case ComboBoxItem:
                    return text;
                case ComboBox combo:
                    return text ?? DisplayNumerals.Format(combo.Text);
                case DatePicker date:
                    return date.Text;
                case TextBlock label when text == null && !label.FontFamily.Source.Contains("Icons", StringComparison.OrdinalIgnoreCase):
                    text = label.Text;
                    break;
            }
        }
        return text;
    }

    static void OpenMenu(object sender, ContextMenuEventArgs args)
    {
        if (args.OriginalSource is not DependencyObject source) return;
        // القائمة الأصلية للمحرر تتضمن القص واللصق، وبعض الأزرار لها خياراتها الخاصة.
        for (var item = source; item != null; item = ElementTree.Parent(item))
            if (item is TextBoxBase or PasswordBox or HwndHost || ContextMenuService.GetContextMenu(item) != null) return;

        var value = ValueAt(source);
        if (string.IsNullOrEmpty(value)) return;

        var owner = (Window)sender;
        var menu = new ContextMenu {
            FlowDirection = FlowDirection.RightToLeft,
            PlacementTarget = source as UIElement ?? owner,
            Placement = args.CursorLeft < 0 ? PlacementMode.Bottom : PlacementMode.MousePoint
        };
        var copy = new MenuItem { Header = CopyField };
        copy.Click += (_,_) => Write(value, owner);
        menu.Items.Add(copy);
        args.Handled = true;
        menu.IsOpen = true;
    }

    static void AddToEditorMenu(object sender, RoutedEventArgs args)
    {
        var menu = (ContextMenu)sender;
        if (menu.ItemsSource != null || menu.PlacementTarget is not TextBox field || menu.Items.OfType<MenuItem>().Any(i => Equals(i.Header, CopyField))) return;
        var copy = new MenuItem { Header = CopyField };
        copy.Click += (_,_) => Write(field.Text, Window.GetWindow(field));
        var divider = new Separator();
        copy.IsEnabled = field.Text.Length > 0;
        menu.Items.Insert(0, copy);
        menu.Items.Insert(1, divider);
        // يمكن أن يُعاد فتح قائمة مخصصة للحقل نفسه بعد تغيير النص.
        menu.Closed += Cleanup;
        void Cleanup(object? sender, RoutedEventArgs e)
        {
            menu.Closed -= Cleanup;
            menu.Items.Remove(divider);
            menu.Items.Remove(copy);
        }
    }

    static void CopyShortcut(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.C || args.KeyboardDevice.Modifiers != ModifierKeys.Control ||
            args.OriginalSource is not DependencyObject target) return;

        // ينسخ المحرر الجزء المحدد بالطريقة المعتادة.
        for (var item = target; item != null; item = ElementTree.Parent(item))
            if (item is TextBox { SelectionLength: > 0 } or PasswordBox or HwndHost) return;

        var value = ValueAt(target);
        if (value == null && target is DataGrid grid && grid.CurrentCell.IsValid)
            value = (grid.CurrentColumn?.GetCellContent(grid.CurrentItem) as TextBlock)?.Text;
        if (value == null && Mouse.DirectlyOver is DependencyObject hovered && Window.GetWindow(hovered) == sender)
            value = ValueAt(hovered);
        if (value == null) return;
        args.Handled = true;
        if (value.Length > 0) Write(value, (Window)sender);
    }

    static void Write(string value, Window? owner)
    {
        if (value.Length == 0) return;
        try { Clipboard.SetText(value, TextDataFormat.UnicodeText); }
        catch (ExternalException) {
            const string message = "تعذّر النسخ إلى الحافظة. أعد المحاولة.";
            if (owner != null) MessageBox.Show(owner, message, CopyField);
            else MessageBox.Show(message, CopyField);
        }
    }
}
