using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using SalaryDesktop.Core;

namespace SalaryDesktop;

/// دفتر الأمانات مرتبط بالنسخة المفتوحة، ولا يحفظ أية بيانات قبل المراجعة.
public sealed class SalaryTrustWindow : Window
{
    private sealed record Row(string Id, string Name, string Month, string Amount, string Remaining, string CaseStatus, string Status, string Documents);
    private readonly BackupSnapshot source;
    private readonly LocalSalaryTrustStore store;
    private readonly Func<bool> current;
    private readonly DataGrid table = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Extended, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), FontSize = 15 };
    private readonly TextBox search = new() { Margin = new Thickness(0, 6, 0, 6), MinWidth = 260 };
    private SalaryTrustSnapshot state;
    private static string Number(long n) => n.ToString("N0", CultureInfo.GetCultureInfo("en-US"));
    private static string Trim(string? value) => (value ?? "").Trim();

    public SalaryTrustWindow(BackupSnapshot source, LocalSalaryTrustStore store, Func<bool> current)
    {
        this.source = source; this.store = store; this.current = current; state = store.Load(source);
        Title = "أمانات الرواتب"; Width = 1050; Height = 720; MinWidth = 820; MinHeight = 580;
        FlowDirection = FlowDirection.RightToLeft; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(22) }; Content = panel;
        var title = new TextBlock { Text = "أمانات الرواتب · خزينة الدولة", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(title, Dock.Top); panel.Children.Add(title);
        DockPanel.SetDock(summary, Dock.Top); panel.Children.Add(summary);
        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 5) };
        Button Action(string text, RoutedEventHandler click)
        { var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 4), Padding = new Thickness(12, 7, 12, 7) }; button.Click += click; return button; }
        actions.Children.Add(Action("استيراد ملف الأمانات", ImportOpening));
        actions.Children.Add(Action("مراجعة وإضافة كاش الشهر", AddCash));
        actions.Children.Add(Action("فتح القيد وتسجيل الصرف", OpenEntry));
        actions.Children.Add(Action("ربط المحدد بكتاب من الأرشيف", LinkArchive));
        DockPanel.SetDock(actions, Dock.Top); panel.Children.Add(actions);
        search.ToolTip = "البحث بالاسم"; search.TextChanged += (_, _) => Render();
        DockPanel.SetDock(search, Dock.Top); panel.Children.Add(search);
        foreach (var (header, key, width) in new[] { ("الاسم", "Name", 3.0), ("الشهر", "Month", 1.0), ("المبلغ", "Amount", 1.0), ("المتبقي", "Remaining", 1.0), ("الحالة", "CaseStatus", 1.5), ("الصرف", "Status", 1.0), ("كتب", "Documents", 0.6) })
            table.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(key), Width = new DataGridLength(width, DataGridLengthUnitType.Star) });
        table.MouseDoubleClick += (_, _) => EditSelected(); panel.Children.Add(table);
        Render();
    }
    private void Render()
    {
        var entries = state.Ledger.Entries;
        var counted = entries.Where(e => !e.ReviewPending).ToArray();
        summary.Text = $"السجلات: {entries.Count:N0} · المتبقي: {Number(counted.Sum(SalaryTrust.Remaining))} دينار · للمراجعة دون احتساب: {entries.Count(e => e.ReviewPending):N0}";
        var key = PersonProfiles.Key(search.Text);
        table.ItemsSource = entries.Where(e => key.Length == 0 || PersonProfiles.Key(e.Name).Contains(key, StringComparison.Ordinal))
            .OrderByDescending(e => e.Year).ThenByDescending(e => e.Month).ThenBy(e => e.Name, StringComparer.CurrentCulture)
            .Select(e => new Row(e.Id, e.Name, $"{e.Month}/{e.Year}", Number(e.Amount), e.ReviewPending ? "—" : Number(SalaryTrust.Remaining(e)), e.CaseStatus,
                e.ReviewPending ? "للمراجعة" : SalaryTrust.Remaining(e) == 0 ? "مصروف بالكامل" : SalaryTrust.Paid(e) > 0 ? "مصروف جزئياً" : "غير مصروف", (e.BookIds?.Count ?? 0).ToString(CultureInfo.InvariantCulture))).ToArray();
    }
    private bool Save(SalaryTrustLedger next)
    {
        if (!current()) { Ui.Message(this, "تغيّرت النسخة المفتوحة؛ أعد فتح دفتر الأمانات.", "أمانات الرواتب"); return false; }
        try { state = store.Save(source, state, next); Render(); return true; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException)
        { Ui.Message(this, ex.Message, "أمانات الرواتب", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
    }
    private async void ImportOpening(object sender, RoutedEventArgs e)
    {
        if (state.Ledger.OpeningImported || state.Ledger.Entries.Count > 0) { Ui.Message(this, "الدفتر غير فارغ؛ لم تُستبدل البيانات.", "أمانات الرواتب"); return; }
        var dialog = new OpenFileDialog { Title = "اختيار ملف الأمانات", Filter = "Excel (*.xlsx)|*.xlsx", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var ledger = await Task.Run(() => SalaryTrustSpreadsheet.Read(dialog.FileName));
            var answer = Ui.Message(this, $"عدد السجلات: {ledger.Entries.Count:N0}\nمصروف بالكامل: {ledger.Entries.Count(x => x.LegacyPaid):N0}\nللمراجعة دون احتساب: {ledger.Entries.Count(x => x.ReviewPending):N0}\n\nاستورد الملف على جهاز واحد، ثم انقل نسخته الاحتياطية إلى الجهاز الآخر. هل تريد الاستيراد؟", "مراجعة ملف الأمانات", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes && Save(ledger))
            {
                var problems = new List<string>();
                foreach (var month in source.Months.Where(m => MonthNames.SortKey(m.Name) > SalaryTrust.OpeningCutoff).OrderBy(m => MonthNames.SortKey(m.Name)))
                {
                    if (!current()) { problems.Add("تغيّرت النسخة المفتوحة؛ أعد فتح الدفتر."); break; }
                    try { state = await Task.Run(() => store.AddCash(source, month.Name)); }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException) { problems.Add(month.Name + ": " + ex.Message); }
                }
                Render();
                if (problems.Count > 0) Ui.Message(this, "استُورد الرصيد الافتتاحي، لكن بعض الأشهر تحتاج مراجعة:\n" + string.Join("\n", problems), "أمانات الرواتب", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException)
        { Ui.Message(this, ex.Message, "استيراد الأمانات", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void AddCash(object sender, RoutedEventArgs e)
    {
        if (!current()) return;
        var months = source.Months.Where(m => MonthNames.SortKey(m.Name) > SalaryTrust.OpeningCutoff).OrderByDescending(m => MonthNames.SortKey(m.Name)).Select(m => m.Name).ToArray();
        var picker = new Window { Title = "إضافة كاش الشهر", Owner = this, Width = 420, Height = 190, WindowStartupLocation = WindowStartupLocation.CenterOwner, FlowDirection = FlowDirection.RightToLeft };
        var stack = new StackPanel { Margin = new Thickness(18) }; picker.Content = stack;
        stack.Children.Add(new TextBlock { Text = "اختر الشهر لإضافة رواتب الكاش غير المكررة إلى الأمانات." });
        var list = new ComboBox { ItemsSource = months, SelectedIndex = months.Length > 0 ? 0 : -1, Margin = new Thickness(0, 10, 0, 10) }; stack.Children.Add(list);
        var ok = new Button { Content = "إضافة", Padding = new Thickness(16, 6, 16, 6), HorizontalAlignment = HorizontalAlignment.Left };
        ok.Click += (_, _) => { if (list.SelectedItem is string) picker.DialogResult = true; }; stack.Children.Add(ok);
        if (picker.ShowDialog() != true || list.SelectedItem is not string month) return;
        try
        {
            if (!state.Ledger.OpeningImported) throw new InvalidDataException("استورد ملف الأمانات الافتتاحي أولاً.");
            state = store.AddCash(source, month); Render();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException)
        { Ui.Message(this, ex.Message, "إضافة كاش الشهر", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void LinkArchive(object sender, RoutedEventArgs e)
    {
        var selected = table.SelectedItems.OfType<Row>().Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        if (selected.Count == 0) { Ui.Message(this, "حدد قيداً أو مجموعة قيود أولاً.", "ربط كتاب الصرف"); return; }
        if (source.Books.Count == 0) { Ui.Message(this, "لا توجد كتب في الأرشيف داخل النسخة المفتوحة. امسح الكتاب في الآيفون واحفظ نسخة ثم افتحها هنا.", "ربط كتاب الصرف"); return; }
        var picker = new Window { Title = "ربط كتاب من الأرشيف", Owner = this, Width = 560, Height = 430, WindowStartupLocation = WindowStartupLocation.CenterOwner, FlowDirection = FlowDirection.RightToLeft };
        var panel = new DockPanel { Margin = new Thickness(18) }; picker.Content = panel;
        var heading = new TextBlock { Text = $"اختر كتاباً لربطه بـ {selected.Count:N0} قيد. الربط لا يسجّل صرفاً ولا يغيّر الأرصدة.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var button = new Button { Content = "ربط الكتاب", Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(14, 7, 14, 7) };
        DockPanel.SetDock(button, Dock.Bottom); panel.Children.Add(button);
        var books = new ListBox { ItemsSource = source.Books.Select(b => new BookChoice(b, b.Title + " · " + b.Month)).ToArray(), DisplayMemberPath = "Label" };
        panel.Children.Add(books); button.Click += (_, _) => { if (books.SelectedItem is BookChoice) picker.DialogResult = true; };
        if (picker.ShowDialog() != true || books.SelectedItem is not BookChoice choice) return;
        try
        {
            var linked = SalaryTrust.Link(state.Ledger, choice.Book.Id, selected);
            if (Save(linked)) Ui.Message(this, "رُبط الكتاب بالقيود المحددة دون تسجيل صرف.", "ربط كتاب الصرف");
        }
        catch (InvalidDataException ex) { Ui.Message(this, ex.Message, "ربط كتاب الصرف", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private sealed record BookChoice(ArchiveBook Book, string Label);
    private void OpenArchive(ArchiveBook book)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "salary-trust-book-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            BackupReader.CopyBook(source, book, temporary);
            var bytes = File.ReadAllBytes(temporary);
            var owner = Application.Current.Windows.OfType<SalaryTrustEntryWindow>().FirstOrDefault(w => w.IsActive) as Window ?? this;
            Exports.ShowBytes(owner, book.Title, bytes);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        { Ui.Message(this, "تعذّر فتح الكتاب.\n" + ex.Message, "الأرشيف", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    private void OpenEntry(object sender, RoutedEventArgs e) => EditSelected();
    private void EditSelected()
    {
        if (table.SelectedItem is not Row row) return;
        var entry = state.Ledger.Entries.First(e => e.Id == row.Id);
        new SalaryTrustEntryWindow(entry, source.Books, OpenArchive, changed => {
            var index = state.Ledger.Entries.ToList().FindIndex(e => e.Id == entry.Id);
            if (index < 0 || state.Ledger.Entries[index] != entry) { Ui.Message(this, "تغيّر القيد؛ أعد فتحه.", "أمانات الرواتب"); return false; }
            var entries = state.Ledger.Entries.ToArray(); entries[index] = changed;
            return Save(state.Ledger with { Entries = entries });
        }) { Owner = this }.ShowDialog();
    }
}

internal sealed class SalaryTrustEntryWindow : Window
{
    private readonly SalaryTrustEntry entry;
    private readonly Func<SalaryTrustEntry, bool> save;
    private readonly TextBox caseStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox note = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70 };
    private readonly TextBox amount = new(), book = new(), paymentNote = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 50 };
    private readonly DatePicker date = new() { SelectedDate = DateTime.Today };
    public SalaryTrustEntryWindow(SalaryTrustEntry entry, IReadOnlyList<ArchiveBook> books, Action<ArchiveBook> openBook, Func<SalaryTrustEntry, bool> save)
    {
        this.entry = entry; this.save = save; Title = "تفاصيل الأمانة"; Width = 560; Height = 680; MinWidth = 450; MinHeight = 540;
        FlowDirection = FlowDirection.RightToLeft; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Content = scroll;
        var panel = new StackPanel { Margin = new Thickness(22) }; scroll.Content = panel;
        panel.Children.Add(new TextBlock { Text = entry.Name, FontSize = 20, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = $"{entry.Month}/{entry.Year} · مبلغ الأمانة {entry.Amount:N0} · " + (entry.ReviewPending ? "للمراجعة دون احتساب" : $"المتبقي {SalaryTrust.Remaining(entry):N0}"), Margin = new Thickness(0, 7, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "الحالة (من ملاحظات الراتب)" });
        caseStatus.Text = entry.CaseStatus; panel.Children.Add(caseStatus);
        if (entry.ImportedStatus.Length > 0) panel.Children.Add(new TextBlock { Text = "حالة الملف الأصلي: " + entry.ImportedStatus, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(new TextBlock { Text = "ملاحظات القيد" }); note.Text = entry.Note; panel.Children.Add(note);
        var saveNote = new Button { Content = "حفظ الحالة والملاحظات", Margin = new Thickness(0, 8, 0, 14), HorizontalAlignment = HorizontalAlignment.Left };
        saveNote.Click += (_, _) => Save(entry with { CaseStatus = caseStatus.Text.Trim(), Note = note.Text.Trim() }); panel.Children.Add(saveNote);
        panel.Children.Add(new TextBlock { Text = "كتب الصرف المرتبطة", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 6) });
        foreach (var rawId in entry.BookIds ?? [])
        {
            var found = Guid.TryParse(rawId, out var id) ? books.FirstOrDefault(b => b.Id == id) : null;
            if (found is null) { panel.Children.Add(new TextBlock { Text = "كتاب مرتبط غير موجود في الأرشيف." }); continue; }
            var row = new WrapPanel();
            var open = new Button { Content = found.Title, Margin = new Thickness(0, 0, 8, 4) }; open.Click += (_, _) => openBook(found); row.Children.Add(open);
            var unlink = new Button { Content = "إزالة الارتباط", Margin = new Thickness(0, 0, 0, 4) };
            unlink.Click += (_, _) => { if (Ui.Message(this, "إزالة ارتباط هذا الكتاب بالقيد؟ سيبقى الكتاب في الأرشيف.", "إزالة الارتباط", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) Save(entry with { BookIds = (entry.BookIds ?? []).Where(x => !Guid.TryParse(x, out var parsed) || parsed != found.Id).ToArray(), CaseStatus = caseStatus.Text.Trim(), Note = note.Text.Trim() }); }; row.Children.Add(unlink);
            panel.Children.Add(row);
        }
        panel.Children.Add(new TextBlock { Text = "ربط الكتاب أو إزالته لا يسجّل صرفاً ولا يغيّر الرصيد.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 10) });
        if (entry.ReviewPending)
        {
            panel.Children.Add(new TextBlock { Text = "هذا القيد مستبعد من الرصيد حتى تحدد حالته.", TextWrapping = TextWrapping.Wrap });
            var outstanding = new Button { Content = "اعتمادها غير مصروفة", Margin = new Thickness(0, 8, 0, 4) };
            outstanding.Click += (_, _) => Save(entry with { ReviewPending = false, LegacyPaid = false, CaseStatus = caseStatus.Text.Trim(), Note = note.Text.Trim() }); panel.Children.Add(outstanding);
            var paid = new Button { Content = "اعتمادها مصروفة بالكامل", Margin = new Thickness(0, 4, 0, 4) };
            paid.Click += (_, _) => { if (Ui.Message(this, "هل تؤكد أن مبلغ الأمانة صُرف بالكامل؟", "حالة الأمانة", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) Save(entry with { ReviewPending = false, LegacyPaid = true, CaseStatus = caseStatus.Text.Trim(), Note = note.Text.Trim() }); }; panel.Children.Add(paid);
        }
        else
        {
            foreach (var p in entry.Payments) panel.Children.Add(new TextBlock { Text = $"{p.Amount:N0} · كتاب {p.BookNumber} · {p.BookDate}" + (p.Note.Length > 0 ? "\n" + p.Note : ""), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
            if (SalaryTrust.Remaining(entry) > 0)
            {
                panel.Children.Add(new TextBlock { Text = "مبلغ الصرف" }); panel.Children.Add(amount);
                panel.Children.Add(new TextBlock { Text = "رقم الكتاب", Margin = new Thickness(0, 8, 0, 0) }); panel.Children.Add(book);
                panel.Children.Add(new TextBlock { Text = "تاريخ الكتاب", Margin = new Thickness(0, 8, 0, 0) }); panel.Children.Add(date);
                panel.Children.Add(new TextBlock { Text = "ملاحظات الصرف (اختياري)", Margin = new Thickness(0, 8, 0, 0) }); panel.Children.Add(paymentNote);
                var button = new Button { Content = "حفظ الصرف", Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left }; button.Click += (_, _) => SavePayment(); panel.Children.Add(button);
            }
        }
    }
    private void Save(SalaryTrustEntry changed) { if (save(changed)) Close(); }
    private void SavePayment()
    {
        if (!long.TryParse(amount.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0 || value > SalaryTrust.Remaining(entry) || string.IsNullOrWhiteSpace(book.Text) || date.SelectedDate is not { } when)
        { Ui.Message(this, "أدخل مبلغاً لا يتجاوز المتبقي ورقم الكتاب وتاريخه.", "تسجيل الصرف"); return; }
        var payment = new SalaryTrustPayment(Guid.NewGuid().ToString(), value, book.Text.Trim(), when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), paymentNote.Text.Trim());
        Save(entry with { CaseStatus = caseStatus.Text.Trim(), Note = note.Text.Trim(), Payments = entry.Payments.Append(payment).ToArray() });
    }
}
