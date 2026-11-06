using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
namespace SalaryLocal.App;
internal static class Ui
{
    internal static void EnsureUnlocked() => SessionLockUi.RequireUnlocked();
    internal static Action CaptureUnlockedSession() => SessionLockUi.CaptureUnlockedSession();
    internal static bool? ShowNativeDialog(Func<bool?> show)
    {
        EnsureUnlocked();
        using var scope = SessionLockUi.BeginNativeDialog();
        var validate = CaptureUnlockedSession();
        var result = show();
        try { validate(); return result; }
        catch (OperationCanceledException) { return false; }
    }
    internal static MessageBoxResult Message(Window? owner, string text, bool confirm = false)
    {
        // نافذة WPF قابلة للستر؛ الإغلاق أو القفل لا يمنحان موافقة ضمنية.
        var result = confirm ? MessageBoxResult.No : MessageBoxResult.OK;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(Text(text));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(buttons);
        var window = new Window {
            Title = "الرواتب المحلية", FlowDirection = FlowDirection.RightToLeft,
            Width = Math.Min(620, SystemParameters.WorkArea.Width - 30),
            Height = Math.Min(400, SystemParameters.WorkArea.Height - 30),
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        if (owner?.IsVisible == true) window.Owner = owner;
        var cancel = Button(confirm ? "لا" : "موافق", () => window.Close());
        cancel.IsDefault = true; cancel.IsCancel = true; buttons.Children.Add(cancel);
        if (confirm)
        {
            var validate = CaptureUnlockedSession();
            buttons.Children.Add(Button("نعم", () => {
                try { validate(); result = MessageBoxResult.Yes; }
                catch (OperationCanceledException) { result = MessageBoxResult.No; }
                window.Close();
            }));
        }
        window.ShowDialog();
        return result;
    }
    internal static TextBlock Text(string text, double size = 14) => new() { Text = text, FontSize = size, Margin = new Thickness(0, 5, 0, 9), TextWrapping = TextWrapping.Wrap };
    internal static Button Button(string title, Action action) { var b = new Button { Content = title }; b.Click += (_, _) => action(); return b; }
    internal static Border Card(UIElement content) => new() { Style = (Style)Application.Current.FindResource("Card"), Child = content };
    internal static Window Dialog(Window owner, string title, UIElement content, double width = 800, double height = 600)
    {
        var w = new Window { Owner = owner, Title = title, Content = content, Width = Math.Min(width, SystemParameters.WorkArea.Width - 30), Height = Math.Min(height, SystemParameters.WorkArea.Height - 30), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        w.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); }; return w;
    }
}
