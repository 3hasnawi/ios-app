using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SalaryDesktop.Core;

namespace SalaryDesktop;

public enum ArchiveBookMode { Add, Edit, Replace }

/// إضافة كتاب إلى الأرشيف (ملف PDF أو صور أو مسح من الطابعة)، أو تعديل بياناته، أو استبدال ملفه.
/// الصفحات تُراجع قبل الحفظ (تدوير وإزالة)، وحد الصفحات ثلاث مثل الآيفون.
public partial class ArchiveBookWindow : Window
{
    public sealed class PageItem
    {
        public required ImageSource? Image { get; init; }
        public required string Label { get; init; }
        public required int Index { get; init; }
        public Visibility RotateVisibility { get; init; }
    }
    private static readonly string[] ImageTypes = [".jpg", ".jpeg", ".png", ".bmp", ".webp"];
    public const string FileFilter = "ملف PDF أو صور الكتاب|*.pdf;*.jpg;*.jpeg;*.png;*.bmp;*.webp|ملف PDF|*.pdf|صور|*.jpg;*.jpeg;*.png;*.bmp;*.webp";
    public static bool Supported(string path) => Path.GetExtension(path).ToLowerInvariant() is var ext && (ext == ".pdf" || ImageTypes.Contains(ext));

    private readonly ArchiveBookMode mode;
    private readonly List<ArchiveImage> images = [];
    private byte[]? pdf;
    private int renderTicket;

    public string ResultTitle { get; private set; } = "";
    public string ResultMonth { get; private set; } = "";
    public string? ResultEntity { get; private set; }
    public byte[]? ResultPdf { get; private set; }
    /// يبدأ المسح من الماسح فور فتح النافذة (زر «مسح من الماسح الضوئي» في الأرشيف).
    public bool ScanOnOpen { get; init; }

    public ArchiveBookWindow(ArchiveBookMode mode, IReadOnlyList<string> months, IReadOnlyList<string> entities, string? month, ArchiveBook? book = null, IReadOnlyList<string>? files = null)
    {
        InitializeComponent();
        this.mode = mode;
        MonthBox.ItemsSource = months;
        MonthBox.SelectedItem = book?.Month is { } m && months.Contains(m) ? m : month is not null && months.Contains(month) ? month : months.FirstOrDefault();
        EntityBox.ItemsSource = entities;
        if (book is not null) { TitleBox.Text = book.Title; EntityBox.Text = book.Entity ?? ""; }
        switch (mode)
        {
            case ArchiveBookMode.Edit:
                Heading.Text = "تعديل بيانات الكتاب"; SubHeading.Text = "الاسم والشهر والجهة؛ ملف الكتاب لا يتغير.";
                HeadIcon.Text = ""; SaveText.Text = "حفظ التعديل";
                PagesCard.Visibility = Visibility.Collapsed; InfoColumn.Width = new GridLength(1, GridUnitType.Star); InfoCard.Margin = new Thickness(0);
                Width = 520; MinWidth = 460; Height = 600; MinHeight = 520; AllowDrop = false;
                SaveNote.Text = "يُحفظ التعديل على هذا الجهاز ويدخل في النسخة الاحتياطية التالية.";
                break;
            case ArchiveBookMode.Replace:
                Heading.Text = "استبدال ملف الكتاب"; SubHeading.Text = "تبقى بيانات الكتاب وارتباطاته (مثل الأمانات)، ويُستبدل الملف فقط.";
                HeadIcon.Text = ""; SaveText.Text = "استبدال الملف";
                InfoCard.IsEnabled = false; SuggestButton.Visibility = Visibility.Collapsed;
                break;
        }
        RefreshPages();
        if (files is { Count: > 0 }) Loaded += (_, _) => LoadFiles(files);
        Loaded += (_, _) => { if (mode != ArchiveBookMode.Replace) { TitleBox.Focus(); TitleBox.SelectAll(); } };
        ContentRendered += (_, _) => { if (ScanOnOpen && mode != ArchiveBookMode.Edit) ScanPage(this, new RoutedEventArgs()); };
    }

    /// أسماء ملفات الماسح والكاميرا (Scan_0001، IMG_2034، 20261102…) ليست اسماً للكتاب.
    private static bool Meaningful(string name)
    {
        var value = name.Trim();
        if (Regex.IsMatch(value, @"^(scan(ned)?|img|image|doc(ument)?|page|pdf|new|untitled|cam|photo|dsc|wa|whatsapp|screenshot|مستند|صورة|مسح)[\s\W_\d-]*$", RegexOptions.IgnoreCase)) return false;
        return value.Count(char.IsLetter) >= 3;
    }

    private void LoadFiles(IReadOnlyList<string> paths)
    {
        if (mode == ArchiveBookMode.Edit) return;
        Status.Text = "";
        try
        {
            var pdfs = paths.Where(p => Path.GetExtension(p).Equals(".pdf", StringComparison.OrdinalIgnoreCase)).ToArray();
            var pictures = paths.Where(p => ImageTypes.Contains(Path.GetExtension(p).ToLowerInvariant())).ToArray();
            if (pdfs.Length + pictures.Length != paths.Count) throw new InvalidDataException("نوع الملف غير مدعوم؛ اختر ملف PDF أو صور JPG أو PNG.");
            if (pdfs.Length > 1 || (pdfs.Length == 1 && (pictures.Length > 0 || images.Count > 0)) || (pictures.Length > 0 && pdf is not null))
                throw new InvalidDataException("اختر ملف PDF واحداً، أو صوراً فقط (حتى ثلاث صفحات).");
            if (pdfs.Length == 1)
            {
                var info = new FileInfo(pdfs[0]);
                if (info.Length > ArchivePdf.MaxBytes) throw new InvalidDataException("ملف الكتاب كبير جداً (أكثر من ٦٠ ميغابايت).");
                var bytes = File.ReadAllBytes(pdfs[0]);
                _ = ArchivePdf.Pages(bytes);
                pdf = bytes;
                var name = Path.GetFileNameWithoutExtension(pdfs[0]);
                if (mode == ArchiveBookMode.Add && TitleBox.Text.Trim().Length == 0 && Meaningful(name)) TitleBox.Text = name.Replace('_', ' ').Trim();
            }
            else
            {
                if (images.Count + pictures.Length > ArchivePdf.MaxPages) throw new InvalidDataException(ArchivePdf.PagesRule);
                foreach (var picture in pictures)
                {
                    if (new FileInfo(picture).Length > ArchivePdf.MaxBytes) throw new InvalidDataException("إحدى الصور كبيرة جداً.");
                    var bytes = File.ReadAllBytes(picture); ArchivePdf.ValidateImage(bytes); images.Add(new ArchiveImage(bytes));
                }
                if (mode == ArchiveBookMode.Add && pictures.Length == 1 && TitleBox.Text.Trim().Length == 0 && Meaningful(Path.GetFileNameWithoutExtension(pictures[0])))
                    TitleBox.Text = Path.GetFileNameWithoutExtension(pictures[0]).Replace('_', ' ').Trim();
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { Status.Text = ex.Message; }
        RefreshPages();
    }

    private async void RefreshPages()
    {
        var ticket = ++renderTicket;
        var count = pdf is not null ? ArchivePdf.Pages(pdf) : images.Count;
        PagesTitle.Text = count == 0 ? "الصفحات" : $"الصفحات ({count:N0} من {ArchivePdf.MaxPages:N0})";
        PagesHint.Text = pdf is not null ? "ملف PDF كما هو؛ لإعادة ترتيبه أو تدويره امسحه من جديد أو أضفه كصور." :
            images.Count > 0 ? "راجع الصفحات: دوّر الصفحة المقلوبة أو أزل الصفحة الخاطئة، ثم احفظ." : ArchivePdf.PagesRule;
        EmptyPages.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ScanButton.IsEnabled = pdf is null && images.Count < ArchivePdf.MaxPages;
        FileButton.IsEnabled = pdf is null && images.Count < ArchivePdf.MaxPages;
        ScanText.Text = images.Count == 0 ? "مسح صفحة من الماسح" : "مسح الصفحة التالية";
        var source = pdf; var pages = images.ToArray();
        try
        {
            var rendered = await Task.Run(() => source is not null
                ? Enumerable.Range(0, count).Select(i => (ImageSource)PdfPreviewWindow.RenderPage(source, i, 0.5, 96)).ToArray()
                : pages.Select(p => (ImageSource)Frozen(ArchivePdf.Thumbnail(p, 480))).ToArray());
            if (ticket != renderTicket) return;
            PageList.ItemsSource = rendered.Select((image, i) => new PageItem { Image = image, Index = i, Label = $"صفحة {i + 1:N0}", RotateVisibility = source is null ? Visibility.Visible : Visibility.Collapsed }).ToArray();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ticket != renderTicket) return;
            PageList.ItemsSource = Enumerable.Range(0, count).Select(i => new PageItem { Image = null, Index = i, Label = $"صفحة {i + 1:N0}", RotateVisibility = source is null ? Visibility.Visible : Visibility.Collapsed }).ToArray();
            Status.Text = "تعذّر عرض معاينة الصفحات: " + ex.Message;
        }
    }
    private static BitmapImage Frozen(byte[] png)
    {
        var image = new BitmapImage();
        using var stream = new MemoryStream(png);
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
        return image;
    }

    private void AddFromFile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "اختر ملف الكتاب", Filter = FileFilter, Multiselect = true };
        if (dialog.ShowDialog(this) == true) LoadFiles(dialog.FileNames);
    }
    private void ScanPage(object sender, RoutedEventArgs e)
    {
        Status.Text = "";
        var cursor = Cursor; Cursor = Cursors.Wait;
        try
        {
            if (pdf is not null || images.Count >= ArchivePdf.MaxPages) throw new InvalidDataException(ArchivePdf.PagesRule);
            var scanned = Scanner.Acquire();
            if (scanned is null) return;
            ArchivePdf.ValidateImage(scanned);
            images.Add(new ArchiveImage(scanned));
            RefreshPages();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { Status.Text = ex.Message; }
        finally { Cursor = cursor; }
    }
    private void RotatePage(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PageItem item || pdf is not null || item.Index >= images.Count) return;
        images[item.Index] = images[item.Index] with { Turns = (images[item.Index].Turns + 1) % 4 };
        RefreshPages();
    }
    private void RemovePage(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PageItem item) return;
        if (pdf is not null) pdf = null;
        else if (item.Index < images.Count) images.RemoveAt(item.Index);
        RefreshPages();
    }
    private void FieldChanged(object sender, RoutedEventArgs e) { if (IsLoaded) Status.Text = ""; }
    private void SuggestTitle(object sender, RoutedEventArgs e)
    {
        var month = MonthBox.SelectedItem as string ?? ""; var entity = EntityBox.Text.Trim();
        TitleBox.Text = entity.Length > 0 ? $"كتاب {entity} - {month}" : $"كتاب {month}";
        TitleBox.Focus(); TitleBox.SelectAll();
    }

    private static string[] Dropped(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files ? files : [];
    private void FileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = mode != ArchiveBookMode.Edit && Dropped(e) is { Length: > 0 } files && files.All(Supported) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private void FileDrop(object sender, DragEventArgs e) { var files = Dropped(e); if (files.Length > 0) { e.Handled = true; LoadFiles(files); } }

    private async void Save(object sender, RoutedEventArgs e)
    {
        Status.Text = "";
        try
        {
            ResultTitle = LocalArchiveStore.CleanTitle(TitleBox.Text);
            ResultMonth = MonthBox.SelectedItem as string is { Length: > 0 } month ? month : throw new InvalidDataException("اختر شهر الكتاب.");
            ResultEntity = LocalArchiveStore.CleanEntity(EntityBox.Text);
            if (mode != ArchiveBookMode.Edit)
            {
                if (pdf is null && images.Count == 0) throw new InvalidDataException("أضف صفحات الكتاب أولاً: من ملف أو بالمسح من الماسح.");
                SaveButton.IsEnabled = false; Cursor = Cursors.Wait;
                var source = pdf; var pages = images.ToArray();
                ResultPdf = await Task.Run(() => source ?? ArchivePdf.FromImages(pages));
            }
            DialogResult = true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException) { Status.Text = ex.Message; }
        finally { SaveButton.IsEnabled = true; Cursor = null; }
    }
}
