using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SalaryDesktop.Core;

namespace SalaryDesktop;

/// نوافذ صغيرة لأمانات الرواتب: اختيار شهر الكاش، اختيار كتاب من الأرشيف، وتعديل بيانات كتاب دفعة.
internal static class TrustDialogs
{
    private static Window Dialog(Window owner, string title, double width, double height) => new Window
    {
        Title = title, Owner = owner, Width = width, Height = height, MinWidth = width * 0.8, MinHeight = height * 0.7,
        WindowStartupLocation = WindowStartupLocation.CenterOwner, FlowDirection = FlowDirection.RightToLeft, ShowInTaskbar = false,
    }.Themed();
    private static Window Themed(this Window window)
    {
        window.SetResourceReference(Control.FontFamilyProperty, "AppFont");
        window.SetResourceReference(Control.BackgroundProperty, "Page");
        window.SetResourceReference(Control.ForegroundProperty, "Ink");
        window.FontSize = 14; window.UseLayoutRounding = true; TextOptions.SetTextFormattingMode(window, TextFormattingMode.Ideal);
        return window;
    }
    private static TextBlock Label(string text) => new() { Text = text, Style = (Style)Application.Current.FindResource("SectionLabel") };
    private static DockPanel Buttons(Window window, string confirm, Func<bool> accept)
    {
        var bar = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "إلغاء", IsCancel = true, Padding = new Thickness(18, 6, 18, 6) };
        DockPanel.SetDock(cancel, Dock.Left); bar.Children.Add(cancel);
        var ok = new Button { Content = confirm, IsDefault = true, Style = (Style)Application.Current.FindResource("ActionButton"), HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(18, 6, 18, 6) };
        ok.Click += (_, _) => { if (accept()) window.DialogResult = true; };
        bar.Children.Add(ok);
        return bar;
    }

    public static string? PickMonth(Window owner, IReadOnlyList<string> months)
    {
        var window = Dialog(owner, "مراجعة كاش شهر", 440, 250);
        var panel = new StackPanel { Margin = new Thickness(20) }; window.Content = panel;
        panel.Children.Add(new TextBlock { Text = "تُضاف رواتب الكاش غير المسجلة للشهر وتُعدّ مودعة. أي تعارض أو تكرار يوقف الإضافة للمراجعة دون حذف أي قيد.", TextWrapping = TextWrapping.Wrap });
        var list = new ComboBox { ItemsSource = months, SelectedIndex = 0, Height = 36, Margin = new Thickness(0, 12, 0, 0) }; panel.Children.Add(list);
        panel.Children.Add(Buttons(window, "إضافة", () => list.SelectedItem is string));
        return window.ShowDialog() == true ? list.SelectedItem as string : null;
    }

    private sealed record BookChoice(ArchiveBook Book, string Title, string Detail);
    public static ArchiveBook? PickBook(Window owner, BackupSnapshot source, int count, Action<Window, ArchiveBook> open)
    {
        var window = Dialog(owner, "ربط كتاب من الأرشيف", 620, 560);
        var panel = new DockPanel { Margin = new Thickness(20) }; window.Content = panel;
        var heading = new TextBlock { Text = $"اختر كتاباً لربطه بـ {count:N0} قيد. الكتاب يبقى نسخة واحدة في الأرشيف، والربط لا يسجّل صرفاً ولا يغيّر الرصيد.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var search = new TextBox { Height = 36, Tag = "ابحث بعنوان الكتاب أو الشهر أو الجهة…", Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(search, Dock.Top); panel.Children.Add(search);
        var list = new ListBox { BorderThickness = new Thickness(1) };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        list.SetResourceReference(Control.BorderBrushProperty, "Line");
        var template = new DataTemplate();
        var stack = new FrameworkElementFactory(typeof(StackPanel)); stack.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 3, 2, 3));
        var title = new FrameworkElementFactory(typeof(TextBlock)); title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Title")); title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        var detail = new FrameworkElementFactory(typeof(TextBlock)); detail.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Detail")); detail.SetValue(TextBlock.FontSizeProperty, 12.0); detail.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        stack.AppendChild(title); stack.AppendChild(detail); template.VisualTree = stack; list.ItemTemplate = template;
        var all = source.Books.OrderByDescending(b => MonthNames.SortKey(b.Month)).ThenBy(b => b.Title, StringComparer.CurrentCulture)
            .Select(b => new BookChoice(b, b.Title, string.Join(" · ", new[] { b.Month, b.Entity ?? "", b.Pages.ToString(CultureInfo.InvariantCulture) + " صفحة" }.Where(v => v.Length > 0)))).ToArray();
        void Filter()
        {
            var key = PersonProfiles.Key(search.Text);
            list.ItemsSource = all.Where(c => key.Length == 0 || PersonProfiles.Key(c.Title + c.Detail).Contains(key, StringComparison.Ordinal)).ToArray();
        }
        search.TextChanged += (_, _) => Filter(); Filter();
        var bar = Buttons(window, "ربط الكتاب", () => list.SelectedItem is BookChoice);
        var preview = new Button { Content = "معاينة الكتاب", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(14, 6, 14, 6) };
        preview.Click += (_, _) => { if (list.SelectedItem is BookChoice c) open(window, c.Book); };
        DockPanel.SetDock(preview, Dock.Left); bar.Children.Insert(1, preview);
        DockPanel.SetDock(bar, Dock.Bottom); panel.Children.Add(bar);
        panel.Children.Add(list);
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is BookChoice) window.DialogResult = true; };
        return window.ShowDialog() == true && list.SelectedItem is BookChoice choice ? choice.Book : null;
    }

    public static SalaryTrustPayment? EditPayment(Window owner, SalaryTrustPayment payment)
    {
        var window = Dialog(owner, "بيانات كتاب الصرف", 460, 470);
        var panel = new StackPanel { Margin = new Thickness(20) }; window.Content = panel;
        panel.Children.Add(new TextBlock { Text = "مبلغ الصرف: " + SalaryTrustWindow.Number(payment.Amount) + " د.ع (لا يتغير)", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(Label("رقم الكتاب (اختياري)"));
        var number = new TextBox { Text = payment.BookNumber, Height = 36 }; panel.Children.Add(number);
        var hasDate = new CheckBox { Content = "إضافة تاريخ الكتاب (اختياري)", IsChecked = payment.BookDate.Length > 0, Margin = new Thickness(0, 12, 0, 6) }; panel.Children.Add(hasDate);
        var date = new DatePicker { Width = 200, HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = payment.BookDate.Length > 0,
            SelectedDate = DateTime.TryParseExact(payment.BookDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : DateTime.Today };
        hasDate.Click += (_, _) => date.IsEnabled = hasDate.IsChecked == true; panel.Children.Add(date);
        panel.Children.Add(Label("ملاحظات الصرف (اختياري)"));
        var note = new TextBox { Text = payment.Note, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60, VerticalContentAlignment = VerticalAlignment.Top }; panel.Children.Add(note);
        SalaryTrustPayment? result = null;
        panel.Children.Add(Buttons(window, "حفظ", () =>
        {
            var stamp = "";
            if (hasDate.IsChecked == true)
            {
                if (date.SelectedDate is not { } when) { Ui.Message(window, "اختر التاريخ أو ألغِ خيار إضافة التاريخ.", "بيانات كتاب الصرف"); return false; }
                stamp = when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            result = payment with { BookNumber = number.Text.Trim(), BookDate = stamp, Note = note.Text.Trim() };
            return true;
        }));
        return window.ShowDialog() == true ? result : null;
    }
}
