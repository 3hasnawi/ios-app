using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using SalaryDesktop.Core;

namespace SalaryDesktop;

/// أمانات الرواتب (مثل SalaryTrustView في الآيفون): الدفتر مرتبط بالنسخة المفتوحة، ومرشحات الشهر وحالة الصرف تنطبق
/// على الجدول والكشف PDF وExcel. لا يُحفظ شيء قبل المراجعة، والربط بكتاب لا يسجّل صرفاً.
public partial class SalaryTrustWindow : Window
{
    public sealed record Row(SalaryTrustEntry Entry, string Name, string Rank, string Month, int Period, string Amount, long AmountValue, string Paid,
        string Remaining, long RemainingValue, string CaseStatus, string Status, string Kind, string Documents)
    {
        public IReadOnlyDictionary<Guid, string> BookNames { get; init; } = new Dictionary<Guid, string>();
        public string Books => string.Join(" · ", (Entry.BookIds ?? []).Select(id => Guid.TryParse(id, out var key) && BookNames.TryGetValue(key, out var title) ? title : "كتاب غير متاح"));
        public string PaymentDetails => string.Join(" · ", Entry.Payments.Select(p => string.Join(" / ", new[] { Number(p.Amount), p.BookNumber, p.BookDate, p.Note }.Where(v => !string.IsNullOrWhiteSpace(v)))));
    }
    public sealed record MonthOption(int Key, string Label);
    public sealed record StatusOption(SalaryTrustFilter Filter, string Title);

    private readonly BackupSnapshot source;
    private readonly LocalSalaryTrustStore store;
    private readonly IReadOnlyDictionary<Guid, string> bookNames;
    private readonly Func<bool> current;
    private SalaryTrustSnapshot state;
    private bool syncing;
    private IReadOnlyList<SalaryTrustEntry> shown = [];
    internal static string Number(long n) => n.ToString("N0", CultureInfo.GetCultureInfo("en-US"));

    public SalaryTrustWindow(BackupSnapshot source, LocalSalaryTrustStore store, Func<bool> current)
    {
        InitializeComponent();
        this.source = source; this.store = store; this.current = current; bookNames = source.Books.ToDictionary(b => b.Id, b => b.Title); state = store.Load(source);
        syncing = true;
        StatusPicker.ItemsSource = SalaryTrust.Filters.Select(f => new StatusOption(f.Filter, f.Title)).ToArray();
        StatusPicker.SelectedIndex = 0;
        syncing = false;
        Render();
    }

    private void CloseWindow(object sender, RoutedEventArgs e) => Close();

    private void Render()
    {
        var entries = state.Ledger.Entries;
        // قائمة الأشهر من القيود نفسها، مع الإبقاء على الاختيار الحالي.
        var selected = (MonthPicker.SelectedItem as MonthOption)?.Key ?? 0;
        var months = new[] { new MonthOption(0, "كل الأشهر") }.Concat(entries.Select(e => e.Year * 100 + e.Month).Distinct().OrderByDescending(k => k)
            .Select(k => new MonthOption(k, SalaryTrust.MonthLabel(k / 100, k % 100)))).ToArray();
        syncing = true;
        MonthPicker.ItemsSource = months;
        MonthPicker.SelectedItem = months.FirstOrDefault(m => m.Key == selected) ?? months[0];
        syncing = false;

        var review = entries.Count(e => e.ReviewPending);
        OutstandingText.Text = Number(SalaryTrust.Outstanding(entries)) + " د.ع";
        CountsText.Text = $"{entries.Count:N0} قيد" + (review > 0 ? $" · {review:N0} للمراجعة دون احتساب" : "");
        ImportButton.Visibility = state.Ledger.OpeningImported || entries.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        ApplyFilters();
    }

    private void FiltersChanged(object sender, EventArgs e) { if (!syncing) ApplyFilters(); }

    private void ApplyFilters()
    {
        var month = (MonthPicker.SelectedItem as MonthOption)?.Key ?? 0;
        var filter = (StatusPicker.SelectedItem as StatusOption)?.Filter ?? SalaryTrustFilter.All;
        // المرشحات متقاطعة (الشهر وحالة الصرف والبحث بالاسم) وتنطبق على الجدول وملخصه والكشف، كما في الآيفون.
        shown = SalaryTrustReports.Filtered(state.Ledger.Entries, month, filter, SearchBox.Text);
        Table.ItemsSource = shown.Select(e => new Row(e, e.Name, e.Rank, SalaryTrust.MonthLabel(e.Year, e.Month), e.Year * 100 + e.Month, Number(e.Amount), e.Amount,
            e.ReviewPending ? "—" : Number(SalaryTrust.Paid(e)), e.ReviewPending ? "—" : Number(SalaryTrust.Remaining(e)), e.ReviewPending ? -1 : SalaryTrust.Remaining(e),
            e.CaseStatus, SalaryTrust.PaymentStatus(e), e.ReviewPending ? "review" : SalaryTrust.Remaining(e) == 0 ? "paid" : SalaryTrust.Paid(e) > 0 ? "partial" : "unpaid",
            (e.BookIds?.Count ?? 0) == 0 ? "" : (e.BookIds!.Count).ToString(CultureInfo.InvariantCulture)) { BookNames = bookNames }).ToArray();
        EmptyText.Text = state.Ledger.Entries.Count == 0
            ? "لا توجد أمانات بعد. استورد الملف الافتتاحي على جهاز واحد (هنا أو في الآيفون)، ثم انقل نسخته الاحتياطية إلى الجهاز الآخر."
            : "لا توجد قيود مطابقة للمرشحات.";
        EmptyText.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var review = shown.Count(e => e.ReviewPending);
        ShownText.Text = $"المعروض: {shown.Count:N0} قيد · الأمانات {Number(shown.Sum(e => e.Amount))} · المتبقي المؤكد {Number(SalaryTrust.Outstanding(shown))}" + (review > 0 ? $" · للمراجعة {review:N0}" : "");
        SelectionChanged(this, EventArgs.Empty);
    }

    private void SelectionChanged(object sender, EventArgs e)
    {
        var count = Table.SelectedItems.Count;
        LinkText.Text = count > 1 ? $"ربط المحدد بكتاب ({count:N0})" : "ربط المحدد بكتاب";
        LinkButton.IsEnabled = count > 0;
    }

    private bool Save(SalaryTrustLedger next)
    {
        if (!current()) { Ui.Message(this, "تغيّرت النسخة المفتوحة أو توجد عملية نسخ جارية؛ أعد فتح دفتر الأمانات.", "أمانات الرواتب"); return false; }
        try { state = store.Save(source, state, next); Render(); return true; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException)
        { Ui.Message(this, ex.Message, "أمانات الرواتب", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
    }

    // ——— الملف الافتتاحي وكاش الأشهر ———
    private async void ImportOpening(object sender, RoutedEventArgs e)
    {
        if (state.Ledger.OpeningImported || state.Ledger.Entries.Count > 0) { Ui.Message(this, "الدفتر غير فارغ؛ لم تُستبدل البيانات.", "أمانات الرواتب"); return; }
        var dialog = new OpenFileDialog { Title = "اختيار ملف الأمانات الافتتاحي", Filter = "Excel (*.xlsx)|*.xlsx", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var ledger = await Task.Run(() => SalaryTrustSpreadsheet.Read(dialog.FileName));
            var answer = Ui.Message(this,
                $"عدد السجلات: {ledger.Entries.Count:N0}\nمصروف بالكامل («صرف»): {ledger.Entries.Count(x => x.LegacyPaid):N0}\nللمراجعة دون احتساب («قطع علاقة تقاعد»): {ledger.Entries.Count(x => x.ReviewPending):N0}\nالمتبقي المؤكد: {Number(SalaryTrust.Outstanding(ledger.Entries))} د.ع\n\nاستورد الملف على جهاز واحد فقط، ثم انقل نسخته الاحتياطية إلى الجهاز الآخر. لن تتغير البيانات قبل التأكيد.\n\nهل تريد الاستيراد؟",
                "مراجعة ملف الأمانات", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes || !Save(ledger)) return;
            // الأشهر المستوردة بعد الافتتاح تُضاف قوائم كاشها، وأي شهر فيه تعارض يبقى للمراجعة.
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
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException)
        { Ui.Message(this, ex.Message, "استيراد الأمانات", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void AddCash(object sender, RoutedEventArgs e)
    {
        if (!current()) return;
        if (!state.Ledger.OpeningImported) { Ui.Message(this, "استورد ملف الأمانات الافتتاحي أولاً (هنا أو في الآيفون ثم افتح نسخته).", "كاش الشهر"); return; }
        var months = source.Months.Where(m => MonthNames.SortKey(m.Name) > SalaryTrust.OpeningCutoff).OrderByDescending(m => MonthNames.SortKey(m.Name)).Select(m => m.Name).ToArray();
        if (months.Length == 0) { Ui.Message(this, "لا توجد أشهر رواتب مستوردة بعد أيلول 2026.", "كاش الشهر"); return; }
        var month = TrustDialogs.PickMonth(this, months);
        if (month is null) return;
        try
        {
            var before = state.Ledger.Entries.Count;
            state = await Task.Run(() => store.AddCash(source, month)); Render();
            var added = state.Ledger.Entries.Count - before;
            Ui.Message(this, added > 0 ? $"أُضيف {added:N0} قيد من رواتب كاش {month} وعُدّت مودعة." : $"قائمة كاش {month} مسجلة كاملة؛ لا جديد.", "كاش الشهر");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException)
        { Ui.Message(this, ex.Message, "كاش الشهر", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    // ——— الربط بكتاب من الأرشيف ———
    private void LinkArchive(object sender, RoutedEventArgs e)
    {
        var selected = Table.SelectedItems.OfType<Row>().Select(r => r.Entry.Id).ToHashSet(StringComparer.Ordinal);
        if (selected.Count == 0) { Ui.Message(this, "حدد قيداً أو مجموعة قيود أولاً.", "ربط كتاب الصرف"); return; }
        LinkBook(selected);
    }
    internal bool LinkBook(IReadOnlySet<string> ids)
    {
        if (source.Books.Count == 0) { Ui.Message(this, "لا توجد كتب في أرشيف النسخة المفتوحة. امسح الكتاب في الآيفون واحفظ نسخة، ثم افتحها هنا.", "ربط كتاب الصرف"); return false; }
        var book = TrustDialogs.PickBook(this, source, ids.Count, OpenBook);
        if (book is null) return false;
        try
        {
            if (Save(SalaryTrust.Link(state.Ledger, book.Id, ids))) { Ui.Message(this, "رُبط الكتاب بالقيود المحددة دون تسجيل صرف أو تغيير الرصيد.", "ربط كتاب الصرف"); return true; }
        }
        catch (InvalidDataException ex) { Ui.Message(this, ex.Message, "ربط كتاب الصرف", MessageBoxButton.OK, MessageBoxImage.Warning); }
        return false;
    }
    internal void OpenBook(Window owner, ArchiveBook book)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "salary-trust-book-" + Guid.NewGuid().ToString("N") + ".pdf");
        try { BackupReader.CopyBook(source, book, temporary); Exports.ShowBytes(owner, book.Title, File.ReadAllBytes(temporary)); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        { Ui.Message(owner, "تعذّر فتح الكتاب.\n" + ex.Message, "الأرشيف", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    // ——— القيد ———
    private void SelectContextRow(object sender, MouseButtonEventArgs e) => TableContextSelection.Select(Table, e);
    private void GuardTableContext(object sender, ContextMenuEventArgs e) => TableContextSelection.Guard(Table, e);
    private void OpenSelectedClick(object sender, RoutedEventArgs e) => OpenSelected();
    private void TableDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();
    private void TableKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; OpenSelected(); } }
    private void OpenSelected()
    {
        if (Table.SelectedItem is not Row row) return;
        new SalaryTrustEntryWindow(this, row.Entry, source.Books) { Owner = this }.ShowDialog();
    }
    /// يستبدل القيد إن لم يتغير منذ فتحه؛ يعيد القيد الجديد أو null.
    internal SalaryTrustEntry? Replace(SalaryTrustEntry original, SalaryTrustEntry changed)
    {
        var entries = state.Ledger.Entries.ToArray();
        var index = Array.FindIndex(entries, e => e.Id == original.Id);
        if (index < 0 || !ReferenceEquals(entries[index], original)) { Ui.Message(this, "تغيّر القيد منذ فتحه؛ أعد فتحه.", "أمانات الرواتب"); return null; }
        entries[index] = changed;
        return Save(state.Ledger with { Entries = entries }) ? state.Ledger.Entries.First(e => e.Id == original.Id) : null;
    }
    public SalaryTrustEntry? Latest(string id) => state.Ledger.Entries.FirstOrDefault(e => e.Id == id);

    // ——— الكشف ———
    private (ReportSheet Sheet, IReadOnlyList<SalaryTrustEntry> Entries)? Report()
    {
        if (shown.Count == 0) { Ui.Message(this, "لا توجد قيود معروضة للتصدير.", "كشف الأمانات"); return null; }
        try
        {
            var month = (MonthPicker.SelectedItem as MonthOption)?.Label ?? "كل الأشهر";
            var status = (StatusPicker.SelectedItem as StatusOption)?.Title ?? "كل الحالات";
            var entries = shown;
            return (SalaryTrustReports.Sheet(state.Ledger, entries, ReportSheet.HeaderFor(source), month, status, SearchBox.Text.Trim()), entries);
        }
        catch (InvalidDataException ex) { Ui.Message(this, ex.Message, "كشف الأمانات", MessageBoxButton.OK, MessageBoxImage.Warning); return null; }
    }
    private async void PreviewReport(object sender, RoutedEventArgs e) { if (Report() is { } r) await Exports.Preview(this, SalaryTrustReports.FileName, assets => SalaryTrustReports.Pdf(r.Sheet, assets, DateTime.Now)); }
    private async void PrintReport(object sender, RoutedEventArgs e) { if (Report() is { } r) await Exports.Print(this, SalaryTrustReports.FileName, assets => SalaryTrustReports.Pdf(r.Sheet, assets, DateTime.Now)); }
    private async void ExportPdf(object sender, RoutedEventArgs e) { if (Report() is { } r) await Exports.SavePdf(this, SalaryTrustReports.FileName, assets => SalaryTrustReports.Pdf(r.Sheet, assets, DateTime.Now)); }
    private async void ExportExcel(object sender, RoutedEventArgs e) { if (Report() is { } r) await Exports.SaveExcel(this, SalaryTrustReports.FileName, () => SalaryTrustReports.Excel(r.Sheet, r.Entries)); }
}
