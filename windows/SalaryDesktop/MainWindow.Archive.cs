using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using SalaryDesktop.Core;

namespace SalaryDesktop;

/// قسم الأرشيف: الكتب حسب الشهر مع معاينة الكتاب المختار، وإضافة كتاب من ملف PDF أو صور أو بالمسح من الطابعة،
/// وتعديل بياناته واستبدال ملفه وحذفه من القائمة (الزر الأيمن). التعديلات محلية (LocalArchiveStore) وتدخل النسخة الاحتياطية التالية.
public partial class MainWindow
{
    private readonly LocalArchiveStore archiveStore = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SalaryDesktop", "Archive"));
    /// النسخة كما فُتحت قبل تعديلات الأرشيف والأشهر والروابط؛ أساس تعديلات الأرشيف.
    private BackupSnapshot? openedSnapshot;
    private string? archiveError;
    private int archiveThumbTicket;
    private const string AllMonths = "كل الأشهر";

    private sealed class ArchiveRow
    {
        public required ArchiveBook Book { get; init; }
        public string Title => Book.Title;
        public string Month => Book.Month;
        public required string Detail { get; init; }
        public required bool IsLocal { get; init; }
        public Visibility LocalVisibility => IsLocal ? Visibility.Visible : Visibility.Collapsed;
    }

    /// تطبيق تعديلات الأرشيف على النسخة المفتوحة؛ إن تعذرت قراءتها تُعرض النسخة دونها مع تنبيه في الأرشيف.
    private BackupSnapshot WithArchive(BackupSnapshot loaded)
    {
        openedSnapshot = loaded; archiveError = null;
        try { return archiveStore.Apply(loaded); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        { archiveError = "تعذّر قراءة تعديلات الأرشيف المحلية؛ تُعرض كتب النسخة فقط. " + ex.Message; return loaded; }
    }
    private string? ArchiveRevision(BackupSnapshot? source)
    {
        if (openedSnapshot is null || source is null || openedSnapshot.SourceDigest != source.SourceDigest) return null;
        try { return archiveStore.Load(openedSnapshot).Revision; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { return null; }
    }
    /// يعيد بناء طبقات النسخة (الأرشيف ← الأشهر ← الروابط) بعد تعديل الأرشيف، مع إبقاء الشهر المختار.
    private void RebuildAfterArchive()
    {
        if (openedSnapshot is not { } raw) return;
        var month = SelectedMonth;
        var archived = WithArchive(raw);
        var payroll = payrollStore.Load(archived);
        var links = linkStore.Load(payroll.Snapshot);
        ApplySources(archived, payroll, links, month.Length > 0 ? month : null);
    }

    private ArchiveRow? SelectedArchive => ArchiveList.SelectedItem as ArchiveRow;

    private void RenderArchive(Guid? select = null)
    {
        var source = snapshot;
        var keep = select ?? SelectedArchive?.Book.Id;
        ArchiveAddButton.IsEnabled = ArchiveScanButton.IsEnabled = source is not null && source.Version >= 3;
        if (source is null)
        {
            ArchiveList.ItemsSource = null; ArchiveStatus.Text = "افتح نسخة الآيفون أولاً لعرض الأرشيف.";
            ArchiveEmpty.Visibility = Visibility.Visible; ArchiveEmptyTitle.Text = "لا توجد نسخة مفتوحة"; ArchiveEmptyText.Text = "افتح نسخة احتياطية من الآيفون أو ابدأ البيانات لعرض الكتب.";
            ShowArchiveDetails(); return;
        }
        var months = source.Books.Select(b => b.Month).Distinct(StringComparer.Ordinal).OrderByDescending(MonthNames.SortKey).ToList();
        var chosen = ArchiveMonthFilter.SelectedItem as string;
        var options = new List<string> { AllMonths }; options.AddRange(months);
        if (ArchiveMonthFilter.ItemsSource is not IEnumerable<string> old || !old.SequenceEqual(options))
        {
            ArchiveMonthFilter.SelectionChanged -= ArchiveFilterChanged;
            ArchiveMonthFilter.ItemsSource = options;
            ArchiveMonthFilter.SelectedItem = chosen is not null && options.Contains(chosen) ? chosen : AllMonths;
            ArchiveMonthFilter.SelectionChanged += ArchiveFilterChanged;
        }
        var month = ArchiveMonthFilter.SelectedItem as string is { } m && m != AllMonths ? m : null;
        var tokens = PayrollAnalysis.Normalize(ArchiveSearch.Text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var local = source.LocalBookFiles ?? new Dictionary<Guid, string>();
        var rows = source.Books
            .Where(b => month is null || b.Month == month)
            .Where(b => tokens.Length == 0 || tokens.All(t => PayrollAnalysis.Normalize($"{b.Title} {b.Entity} {b.Month}").Contains(t, StringComparison.Ordinal)))
            .OrderByDescending(b => MonthNames.SortKey(b.Month)).ThenByDescending(b => b.CreatedAt ?? 0)
            .Select(b => new ArchiveRow
            {
                Book = b, IsLocal = local.ContainsKey(b.Id),
                Detail = string.Join(" · ", new[] { b.Entity ?? "بلا جهة", b.Pages == 1 ? "صفحة واحدة" : $"{b.Pages:N0} صفحات" }),
            }).ToList();
        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ArchiveRow.Month)));
        ArchiveList.ItemsSource = view;
        var localCount = source.Books.Count(b => local.ContainsKey(b.Id));
        ArchiveStatus.Text = archiveError ?? (source.Version < 3 ? "إصدار هذه النسخة لا يدعم الأرشيف؛ افتح نسخة أحدث من الآيفون." :
            $"{source.Books.Count:N0} كتاب في {months.Count:N0} شهر" + (rows.Count != source.Books.Count ? $" · المعروض {rows.Count:N0}" : "") +
            (localCount > 0 ? $" · {localCount:N0} من ويندوز تدخل في النسخة الاحتياطية التالية" : ""));
        ArchiveEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ArchiveEmptyTitle.Text = source.Books.Count == 0 ? "لا توجد كتب في الأرشيف بعد" : "لا توجد كتب مطابقة";
        ArchiveEmptyText.Text = source.Books.Count == 0 ? "أضف كتاباً من ملف PDF، أو ضعه في الطابعة واختر «مسح من الماسح الضوئي»." : "غيّر البحث أو اختر «كل الأشهر».";
        ArchiveList.SelectedItem = rows.FirstOrDefault(r => r.Book.Id == keep) ?? rows.FirstOrDefault();
        if (ArchiveList.SelectedItem is { } selected) ArchiveList.ScrollIntoView(selected);
        ShowArchiveDetails();
    }
    private void ArchiveFilterChanged(object sender, RoutedEventArgs e) { if (IsLoaded && page == "archive") RenderArchive(); }
    private void ArchiveSelectionChanged(object sender, SelectionChangedEventArgs e) => ShowArchiveDetails();

    private async void ShowArchiveDetails()
    {
        var row = SelectedArchive; var source = snapshot; var ticket = ++archiveThumbTicket;
        ArchiveDetails.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        ArchiveNoSelection.Visibility = row is null ? Visibility.Visible : Visibility.Collapsed;
        if (row is null || source is null) { ArchiveThumb.Source = null; return; }
        ArchiveTitle.Text = row.Title; ArchiveMonth.Text = row.Month; ArchiveEntity.Text = row.Book.Entity ?? "—";
        ArchivePages.Text = row.Book.Pages == 1 ? "صفحة واحدة" : $"{row.Book.Pages:N0} صفحات";
        ArchiveOrigin.Text = row.IsLocal ? "أُضيف في ويندوز؛ يدخل في النسخة الاحتياطية التالية" : "من النسخة الاحتياطية";
        ArchiveThumb.Source = null; ArchiveThumbText.Text = "جارٍ تجهيز المعاينة…";
        try
        {
            var image = await Task.Run(() => PdfPreviewWindow.RenderPage(BookBytes(source, row.Book), 0, 0.75, 96));
            if (ticket != archiveThumbTicket) return;
            ArchiveThumb.Source = image; ArchiveThumbText.Text = "";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (ticket == archiveThumbTicket) ArchiveThumbText.Text = "تعذّر عرض المعاينة؛ افتح الكتاب لعرضه."; }
    }

    private static byte[] BookBytes(BackupSnapshot source, ArchiveBook book)
    {
        var folder = Path.Combine(Path.GetTempPath(), "salary-desktop-books");
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".pdf");
        BackupReader.CopyBook(source, book, destination);
        try { return File.ReadAllBytes(destination); }
        finally { try { File.Delete(destination); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    private async Task<byte[]?> SelectedBookBytes(string title)
    {
        if (SelectedArchive is not { } row || snapshot is not { } source) return null;
        try { return await Task.Run(() => BookBytes(source, row.Book)); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        { Ui.Message(this, "تعذّر فتح الكتاب.\n" + ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning); return null; }
    }

    private async void ArchiveOpen(object sender, RoutedEventArgs e)
    {
        if (SelectedArchive is { } row && await SelectedBookBytes("الأرشيف") is { } bytes) Exports.ShowBytes(this, row.Title, bytes);
    }
    private async void ArchivePrint(object sender, RoutedEventArgs e)
    {
        if (SelectedArchive is { } row && await SelectedBookBytes("طباعة الكتاب") is { } bytes) Exports.ShowBytes(this, row.Title, bytes, print: true);
    }
    private async void ArchiveSaveCopy(object sender, RoutedEventArgs e)
    {
        if (SelectedArchive is not { } row) return;
        var dialog = new SaveFileDialog { Title = "حفظ نسخة من الكتاب", Filter = "ملف PDF (*.pdf)|*.pdf", FileName = ReportSheet.FileName(row.Title) + ".pdf", AddExtension = true, DefaultExt = ".pdf", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true || await SelectedBookBytes("حفظ نسخة") is not { } bytes) return;
        try { await File.WriteAllBytesAsync(dialog.FileName, bytes); StatusBar.Text = "حُفظت نسخة من الكتاب: " + dialog.FileName; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Ui.Message(this, "تعذّر حفظ النسخة.\n" + ex.Message, "حفظ نسخة", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void ArchiveCopyTitle(object sender, RoutedEventArgs e)
    {
        if (SelectedArchive is not { } row) return;
        try { Clipboard.SetText(row.Title); StatusBar.Text = "نُسخ اسم الكتاب."; }
        catch (System.Runtime.InteropServices.COMException) { StatusBar.Text = "تعذّر النسخ؛ الحافظة مشغولة."; }
    }
    private void ArchiveThumbClicked(object sender, MouseButtonEventArgs e) => ArchiveOpen(sender, e);
    private void ArchiveDoubleClick(object sender, MouseButtonEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = node is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is ListBoxItem) { ArchiveOpen(sender, e); return; }
            if (node is ListBox or System.Windows.Controls.Primitives.ScrollBar) return;
        }
    }
    private void ArchiveKeyDown(object sender, KeyEventArgs e)
    {
        if (SelectedArchive is null) return;
        if (e.Key == Key.Enter) { e.Handled = true; ArchiveOpen(sender, e); }
        else if (e.Key == Key.F2) { e.Handled = true; ArchiveEdit(sender, e); }
        else if (e.Key == Key.Delete) { e.Handled = true; ArchiveDelete(sender, e); }
    }
    private void ArchiveMenuOpened(object sender, RoutedEventArgs e)
    {
        var any = SelectedArchive is not null; var editable = any && snapshot?.Version >= 3;
        ArchiveOpenItem.IsEnabled = ArchivePrintItem.IsEnabled = ArchiveSaveItem.IsEnabled = ArchiveCopyItem.IsEnabled = any;
        ArchiveEditItem.IsEnabled = ArchiveReplaceItem.IsEnabled = ArchiveDeleteItem.IsEnabled = editable;
    }

    // ——— الإضافة والتعديل ———

    private (IReadOnlyList<string> Months, IReadOnlyList<string> Entities)? ArchiveChoices()
    {
        if (snapshot is not { } source) { Ui.Message(this, "افتح البيانات أولاً.", "الأرشيف"); return null; }
        if (source.Version < 3) { Ui.Message(this, "إصدار هذه النسخة لا يدعم الأرشيف؛ افتح نسخة أحدث من الآيفون.", "الأرشيف"); return null; }
        var months = source.Months.Select(m => m.Name).OrderByDescending(MonthNames.SortKey).ToArray();
        if (months.Length == 0) { Ui.Message(this, "لا توجد أشهر في النسخة؛ استورد رواتب شهر أولاً ليُربط الكتاب به.", "الأرشيف"); return null; }
        var entities = source.Deductions.Select(d => d.Title).Concat(source.Accounts.Select(a => a.Entity)).Concat(source.Books.Select(b => b.Entity ?? ""))
            .Select(v => v.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.CurrentCulture).ToArray();
        return (months, entities);
    }
    private string? DefaultArchiveMonth() =>
        ArchiveMonthFilter.SelectedItem as string is { } m && m != AllMonths ? m : SelectedMonth.Length > 0 ? SelectedMonth : null;

    private async Task<bool> ArchiveChange(string title, Func<BackupSnapshot, ArchiveState, ArchiveState> change, Func<ArchiveState, Guid?> select)
    {
        var raw = openedSnapshot; var root = rootSnapshot;
        if (raw is null || root is null || loading) { Ui.Message(this, "انتظر انتهاء تحميل البيانات ثم أعد المحاولة.", title); return false; }
        try
        {
            var state = await Task.Run(() => change(raw, archiveStore.Load(raw)));
            if (!ReferenceEquals(root, rootSnapshot)) { Ui.Message(this, "حُفظ التعديل للنسخة السابقة؛ لم تتغير النسخة المعروضة.", title); return false; }
            RebuildAfterArchive();
            RenderArchive(select(state));
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { Ui.Message(this, "تعذّر حفظ التعديل في الأرشيف؛ لم تتغير البيانات.\n" + ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
    }

    private async Task AddArchiveBook(IReadOnlyList<string>? files, bool scan)
    {
        if (ArchiveChoices() is not { } choices) return;
        var window = new ArchiveBookWindow(ArchiveBookMode.Add, choices.Months, choices.Entities, DefaultArchiveMonth(), files: files) { Owner = this, ScanOnOpen = scan };
        if (window.ShowDialog() != true || window.ResultPdf is not { } pdf) return;
        if (await ArchiveChange("إضافة كتاب", (raw, state) => archiveStore.Add(raw, state, window.ResultTitle, window.ResultMonth, window.ResultEntity, pdf), state => state.Books.FirstOrDefault()?.Id))
            StatusBar.Text = $"أُضيف «{window.ResultTitle}» إلى الأرشيف؛ يدخل في النسخة الاحتياطية التالية.";
    }
    /// ملفات PDF كل واحد كتاب مستقل، والصور معاً كتاب واحد (حتى ثلاث صفحات).
    private async Task AddArchiveFiles(IReadOnlyList<string> files)
    {
        var pictures = files.Where(f => !Path.GetExtension(f).Equals(".pdf", StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var pdf in files.Where(f => Path.GetExtension(f).Equals(".pdf", StringComparison.OrdinalIgnoreCase)).Take(20)) await AddArchiveBook([pdf], false);
        if (pictures.Length > 0) await AddArchiveBook(pictures, false);
    }
    private async void ArchiveAddFromFile(object sender, RoutedEventArgs e)
    {
        if (ArchiveChoices() is null) return;
        var dialog = new OpenFileDialog { Title = "اختر ملف الكتاب (PDF) أو صوره", Filter = ArchiveBookWindow.FileFilter, Multiselect = true };
        if (dialog.ShowDialog(this) == true) await AddArchiveFiles(dialog.FileNames);
    }
    private async void ArchiveScan(object sender, RoutedEventArgs e) => await AddArchiveBook(null, true);

    private async void ArchiveEdit(object sender, RoutedEventArgs e)
    {
        if (SelectedArchive is not { } row || ArchiveChoices() is not { } choices) return;
        var months = choices.Months.Contains(row.Month) ? choices.Months : [row.Month, .. choices.Months];
        var window = new ArchiveBookWindow(ArchiveBookMode.Edit, months, choices.Entities, row.Month, row.Book) { Owner = this };
        if (window.ShowDialog() != true) return;
        var id = row.Book.Id;
        if (await ArchiveChange("تعديل الكتاب", (raw, state) => archiveStore.Edit(raw, state, id, window.ResultTitle, window.ResultMonth, window.ResultEntity), _ => id))
            StatusBar.Text = "حُفظ تعديل الكتاب؛ يدخل في النسخة الاحتياطية التالية.";
    }
    private async Task ReplaceArchive(IReadOnlyList<string>? files, bool scan)
    {
        if (SelectedArchive is not { } row || ArchiveChoices() is not { } choices) return;
        var months = choices.Months.Contains(row.Month) ? choices.Months : [row.Month, .. choices.Months];
        var window = new ArchiveBookWindow(ArchiveBookMode.Replace, months, choices.Entities, row.Month, row.Book, files) { Owner = this, ScanOnOpen = scan };
        if (window.ShowDialog() != true || window.ResultPdf is not { } pdf) return;
        var id = row.Book.Id;
        if (await ArchiveChange("استبدال ملف الكتاب", (raw, state) => archiveStore.Replace(raw, state, id, pdf), _ => id))
            StatusBar.Text = "استُبدل ملف الكتاب؛ يدخل في النسخة الاحتياطية التالية.";
    }
    private async void ArchiveReplaceFromFile(object sender, RoutedEventArgs e)
    {
        if (SelectedArchive is null) return;
        var dialog = new OpenFileDialog { Title = "اختر الملف الجديد للكتاب", Filter = ArchiveBookWindow.FileFilter, Multiselect = true };
        if (dialog.ShowDialog(this) == true) await ReplaceArchive(dialog.FileNames, false);
    }
    private async void ArchiveReplaceScan(object sender, RoutedEventArgs e) => await ReplaceArchive(null, true);

    private async void ArchiveDelete(object sender, RoutedEventArgs e)
    {
        if (SelectedArchive is not { } row || snapshot is not { } source) return;
        var id = row.Book.Id;
        try
        {
            var ledger = await Task.Run(() => trustStore.Load(source).Ledger);
            if (ledger.Entries.Any(entry => entry.BookIds?.Any(x => Guid.TryParse(x, out var linked) && linked == id) == true))
            { Ui.Message(this, "الكتاب مرتبط بقيود في أمانات الرواتب؛ أزل ارتباطاته من «أمانات الرواتب» قبل حذفه.", "حذف الكتاب", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { Ui.Message(this, "تعذّر التحقق من ارتباطات الكتاب بالأمانات؛ لم يُحذف.\n" + ex.Message, "حذف الكتاب", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (Ui.Message(this, $"سيُحذف الكتاب «{row.Title}» ({row.Month}) من الأرشيف ومن النسخة الاحتياطية التالية.\nملف النسخة الأصلي لا يتغير حتى تحفظ نسخة جديدة.\n\nهل تريد الحذف؟",
            "حذف الكتاب", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var index = ArchiveList.SelectedIndex;
        if (await ArchiveChange("حذف الكتاب", (raw, state) => archiveStore.Delete(raw, state, id), _ => null))
        {
            if (ArchiveList.Items.Count > 0) ArchiveList.SelectedIndex = Math.Clamp(index, 0, ArchiveList.Items.Count - 1);
            StatusBar.Text = $"حُذف «{row.Title}» من الأرشيف.";
        }
    }

    // ——— السحب إلى الأرشيف ———

    private void ArchiveDragOver(object sender, DragEventArgs e)
    {
        var files = DroppedFiles(e);
        if (files.Length == 0 || !files.All(ArchiveBookWindow.Supported)) return;   // ملفات النسخة وExcel تمر إلى الاستيراد الموحّد.
        e.Effects = snapshot is { Version: >= 3 } && !loading && !Locker.IsLocked ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private async void ArchiveDrop(object sender, DragEventArgs e)
    {
        var files = DroppedFiles(e);
        if (files.Length == 0 || !files.All(ArchiveBookWindow.Supported) || Locker.IsLocked) return;
        e.Handled = true;
        await AddArchiveFiles(files);
    }
}
