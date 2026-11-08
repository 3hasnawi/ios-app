using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Payroll.Core;

namespace Payroll.Desktop;

public partial class MainWindow
{
    private IReadOnlyList<ArchivedBook> _archiveBooks = [];
    private readonly List<string> _previewFiles = [];

    private void ResetArchive()
    {
        _archiveBooks = [];
        ArchiveGrid.ItemsSource = null;
        if (ArchivePanel.Visibility == Visibility.Visible) _ = LoadArchiveAsync();
    }

    private async void Archive_Click(object sender, RoutedEventArgs e)
    {
        PageTitle.Text = "الأرشيف";
        HomePanel.Visibility = TablePanel.Visibility = ComparisonPanel.Visibility = EntityReportPanel.Visibility = Visibility.Collapsed;
        ArchivePanel.Visibility = Visibility.Visible;
        await LoadArchiveAsync();
    }

    private async Task LoadArchiveAsync()
    {
        if (_dataset is null) { ArchiveHint.Text = "افتح نسخة الآيفون أولاً."; return; }
        var dataset = _dataset;
        try
        {
            var books = await Task.Run(() => ArchivedBooks.List(dataset));
            if (dataset != _dataset) return;
            _archiveBooks = books;
            FilterArchivedBooks();
        }
        catch (Exception ex) { if (dataset == _dataset) ArchiveHint.Text = "تعذّرت قراءة الأرشيف: " + ex.Message; }
    }

    private void ArchiveSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded) FilterArchivedBooks();
    }

    private void FilterArchivedBooks()
    {
        var query = ArabicSearch.Normalize(ArchiveSearchBox.Text);
        var filtered = _archiveBooks.Where(b => ArabicSearch.Normalize(b.Title).Contains(query, StringComparison.Ordinal)
            || ArabicSearch.Normalize(b.Month).Contains(query, StringComparison.Ordinal)
            || ArabicSearch.Normalize(b.Entity).Contains(query, StringComparison.Ordinal)).ToArray();
        ArchiveGrid.ItemsSource = filtered;
        ArchiveHint.Text = $"{filtered.Length:N0} كتاب من أصل {_archiveBooks.Count:N0}. المعاينة تُفتح في عارض PDF على الجهاز.";
    }

    private async void PreviewBook_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null || ArchiveGrid.SelectedItem is not ArchivedBook book) { StatusText.Text = "اختر كتاباً للمعاينة."; return; }
        var path = Path.Combine(Path.GetTempPath(), "PayrollBook-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await ArchivedBooks.CopyPdfAsync(_dataset, book.Id, output);
            _previewFiles.Add(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            StatusText.Text = "فُتح الكتاب في عارض PDF؛ أُنشئت نسخة مؤقتة للمعاينة.";
        }
        catch (Exception ex) { TryDeletePreview(path); MessageBox.Show(this, "تعذّرت معاينة الكتاب.\n\n" + ex.Message, "الأرشيف", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void SaveBook_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null || ArchiveGrid.SelectedItem is not ArchivedBook book) { StatusText.Text = "اختر كتاباً لحفظه."; return; }
        var picker = new SaveFileDialog { Title = "حفظ نسخة من الكتاب", Filter = "ملف PDF (*.pdf)|*.pdf", DefaultExt = ".pdf", AddExtension = true, OverwritePrompt = true, FileName = "كتاب.pdf" };
        if (picker.ShowDialog(this) != true) return;
        var target = Path.GetFullPath(picker.FileName);
        var relative = Path.GetRelativePath(_dataRoot, target);
        if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        { MessageBox.Show(this, "اختر موقعاً خارج مجلد بيانات التطبيق لحماية النسخة المحلية.", "الأرشيف"); return; }
        if (!string.Equals(Path.GetExtension(target), ".pdf", StringComparison.OrdinalIgnoreCase))
        { MessageBox.Show(this, "اختر اسماً ينتهي بـ .pdf.", "الأرشيف"); return; }
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, ".payroll-book-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await ArchivedBooks.CopyPdfAsync(_dataset, book.Id, output);
            File.Move(temporary, target, true);
            StatusText.Text = "حُفظت نسخة PDF؛ بقي الكتاب الأصلي محفوظاً في نسخة الآيفون.";
        }
        catch (Exception ex) { TryDeletePreview(temporary); MessageBox.Show(this, "تعذّر حفظ الكتاب.\n\n" + ex.Message, "الأرشيف", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private static void TryDeletePreview(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private void ClearPreviewFiles() { foreach (var path in _previewFiles) TryDeletePreview(path); _previewFiles.Clear(); }
}
