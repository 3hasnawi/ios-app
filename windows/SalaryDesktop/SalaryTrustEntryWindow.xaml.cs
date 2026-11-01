using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SalaryDesktop.Core;

namespace SalaryDesktop;

/// تفاصيل قيد الأمانة (مثل صفحة القيد في الآيفون): الحالة والملاحظات، كتب الصرف المرتبطة، حسم قيد المراجعة،
/// ودفعات الصرف الجزئية مع تعديل بيانات كتابها. رقم الكتاب وتاريخه اختياريان ومستقلان.
public partial class SalaryTrustEntryWindow : Window
{
    private readonly SalaryTrustWindow owner;
    private readonly IReadOnlyList<ArchiveBook> books;
    private SalaryTrustEntry entry;
    private static string Number(long n) => SalaryTrustWindow.Number(n);

    public SalaryTrustEntryWindow(SalaryTrustWindow owner, SalaryTrustEntry entry, IReadOnlyList<ArchiveBook> books)
    {
        InitializeComponent();
        this.owner = owner; this.entry = entry; this.books = books;
        DateBox.SelectedDate = DateTime.Today;
        CaseStatusBox.Text = entry.CaseStatus; NoteBox.Text = entry.Note;
        Render();
    }

    private void CloseWindow(object sender, RoutedEventArgs e) => Close();

    private void Render()
    {
        Title = "تفاصيل الأمانة · " + entry.Name;
        NameText.Text = entry.Name;
        IdentityText.Text = string.Join(" · ", new[] { SalaryTrust.MonthLabel(entry.Year, entry.Month), entry.Rank, entry.Workplace }.Where(v => !string.IsNullOrWhiteSpace(v)));
        AmountText.Text = Number(entry.Amount);
        PaidText.Text = entry.ReviewPending ? "—" : Number(SalaryTrust.Paid(entry));
        RemainingLabel.Text = entry.ReviewPending ? "للمراجعة" : "المتبقي";
        RemainingText.Text = entry.ReviewPending ? "غير محتسب" : Number(SalaryTrust.Remaining(entry));
        ImportedText.Text = "حالة الملف الافتتاحي: " + entry.ImportedStatus + (entry.LegacyPaid ? " (مصروف بالكامل)" : "");
        ImportedText.Visibility = entry.ImportedStatus.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        BooksPanel.Children.Clear();
        foreach (var raw in entry.BookIds ?? [])
        {
            var book = Guid.TryParse(raw, out var id) ? books.FirstOrDefault(b => b.Id == id) : null;
            if (book is null) { BooksPanel.Children.Add(new TextBlock { Text = "كتاب مرتبط غير موجود في الأرشيف؛ راجع الأرشيف.", Foreground = (System.Windows.Media.Brush)FindResource("Warn") }); continue; }
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var unlink = new Button { Content = "إزالة الارتباط", Style = (Style)FindResource("QuietButton") };
            unlink.Click += (_, _) => Unlink(book);
            DockPanel.SetDock(unlink, Dock.Right); row.Children.Add(unlink);
            var open = new Button { HorizontalContentAlignment = HorizontalAlignment.Left, Content = new TextBlock { Text = $"{book.Title} · {book.Month}" + (book.Entity is { Length: > 0 } entity ? " · " + entity : ""), TextTrimming = TextTrimming.CharacterEllipsis } };
            open.Click += (_, _) => owner.OpenBook(this, book);
            row.Children.Add(open);
            BooksPanel.Children.Add(row);
        }
        if (BooksPanel.Children.Count == 0) BooksPanel.Children.Add(new TextBlock { Text = "لا توجد كتب مرتبطة.", Style = (Style)FindResource("Hint") });

        ReviewCard.Visibility = entry.ReviewPending ? Visibility.Visible : Visibility.Collapsed;
        PaymentsCard.Visibility = !entry.ReviewPending && (entry.Payments.Count > 0 || entry.LegacyPaid) ? Visibility.Visible : Visibility.Collapsed;
        PaymentCard.Visibility = !entry.ReviewPending && SalaryTrust.Remaining(entry) > 0 ? Visibility.Visible : Visibility.Collapsed;
        PaymentsPanel.Children.Clear();
        if (entry.LegacyPaid) PaymentsPanel.Children.Add(new TextBlock { Text = "مصروف بالكامل حسب الملف الافتتاحي («صرف»).", Style = (Style)FindResource("Hint") });
        foreach (var payment in entry.Payments)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var edit = new Button { Content = "تعديل بيانات الكتاب", Style = (Style)FindResource("QuietButton"), VerticalAlignment = VerticalAlignment.Top };
            edit.Click += (_, _) => EditPayment(payment);
            DockPanel.SetDock(edit, Dock.Right); row.Children.Add(edit);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = Number(payment.Amount) + " د.ع", FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = string.Join(" · ", new[] { payment.BookNumber.Length > 0 ? "كتاب " + payment.BookNumber : "بلا رقم كتاب", payment.BookDate.Length > 0 ? payment.BookDate : "بلا تاريخ" }), Style = (Style)FindResource("Hint") });
            if (payment.Note.Length > 0) text.Children.Add(new TextBlock { Text = payment.Note, TextWrapping = TextWrapping.Wrap, Foreground = (System.Windows.Media.Brush)FindResource("Muted"), FontSize = 12.5 });
            row.Children.Add(text);
            PaymentsPanel.Children.Add(row);
        }
    }

    private bool Apply(SalaryTrustEntry changed)
    {
        if (owner.Replace(entry, changed) is not { } saved) return false;
        entry = saved; Render(); return true;
    }
    private SalaryTrustEntry WithNotes(SalaryTrustEntry e) => e with { CaseStatus = CaseStatusBox.Text.Trim(), Note = NoteBox.Text.Trim() };

    private void SaveNotes(object sender, RoutedEventArgs e) { if (Apply(WithNotes(entry))) Ui.Message(this, "حُفظت الحالة والملاحظات.", "تفاصيل الأمانة"); }

    private void Unlink(ArchiveBook book)
    {
        if (Ui.Message(this, "إزالة ارتباط هذا الكتاب بالقيد؟ يبقى الكتاب في الأرشيف ولا يتغير الرصيد.", "إزالة الارتباط", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var links = (entry.BookIds ?? []).Where(x => !Guid.TryParse(x, out var id) || id != book.Id).ToArray();
        Apply(entry with { BookIds = links.Length == 0 ? null : links });
    }
    private void LinkBook(object sender, RoutedEventArgs e)
    {
        if (owner.LinkBook(new HashSet<string>([entry.Id], StringComparer.Ordinal)) && owner.Latest(entry.Id) is { } updated) { entry = updated; Render(); }
    }

    private void ResolveOutstanding(object sender, RoutedEventArgs e) => Apply(WithNotes(entry) with { ReviewPending = false, LegacyPaid = false });
    private void ResolvePaid(object sender, RoutedEventArgs e)
    {
        if (Ui.Message(this, "هل تؤكد أن مبلغ الأمانة صُرف بالكامل؟", "تأكيد الصرف الكامل", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            Apply(WithNotes(entry) with { ReviewPending = false, LegacyPaid = true });
    }

    private void DateToggled(object sender, RoutedEventArgs e) => DateBox.IsEnabled = HasDateBox.IsChecked == true;

    private void SavePayment(object sender, RoutedEventArgs e)
    {
        if (PersonProfiles.Amount(AmountBox.Text) is not { } value || value <= 0 || value != decimal.Truncate(value) || value > SalaryTrust.Remaining(entry))
        { Ui.Message(this, $"أدخل مبلغاً صحيحاً بالدينار لا يتجاوز المتبقي ({Number(SalaryTrust.Remaining(entry))}).", "تسجيل الصرف"); return; }
        string date = "";
        if (HasDateBox.IsChecked == true)
        {
            if (DateBox.SelectedDate is not { } when) { Ui.Message(this, "اختر تاريخ الكتاب أو ألغِ خيار إضافة التاريخ.", "تسجيل الصرف"); return; }
            date = when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        var payment = new SalaryTrustPayment(Guid.NewGuid().ToString(), (long)value, BookNumberBox.Text.Trim(), date, PaymentNoteBox.Text.Trim());
        if (Apply(WithNotes(entry) with { Payments = entry.Payments.Append(payment).ToArray() }))
        { AmountBox.Text = ""; BookNumberBox.Text = ""; PaymentNoteBox.Text = ""; HasDateBox.IsChecked = false; DateBox.IsEnabled = false; }
    }

    /// تعديل رقم الكتاب وتاريخه وملاحظته دون تغيير مبلغ الدفعة (مثل SalaryTrustPaymentEditor).
    private void EditPayment(SalaryTrustPayment payment)
    {
        if (TrustDialogs.EditPayment(this, payment) is not { } updated) return;
        var payments = entry.Payments.ToArray();
        var index = Array.FindIndex(payments, p => p.Id == payment.Id);
        if (index < 0 || !Equals(payments[index], payment)) { Ui.Message(this, "تغيّرت الدفعة؛ أعد فتح القيد.", "بيانات كتاب الصرف"); return; }
        payments[index] = updated;
        Apply(entry with { Payments = payments });
    }
}
