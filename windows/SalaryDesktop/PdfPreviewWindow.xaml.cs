using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SalaryDesktop.Core;

namespace SalaryDesktop;

/// معاينة PDF داخل البرنامج (بدل فتح عارض خارجي): تُرسم الصفحات بمكتبة ويندوز (Windows.Data.Pdf)، مع الحفظ والطباعة من النافذة نفسها.
public partial class PdfPreviewWindow : Window
{
    /// دقة رسم المعاينة نسبة إلى 72 نقطة في البوصة (ثلاثة أضعاف = وضوح عند التكبير حتى 300٪).
    private const double Sharpness = 3.0;
    public sealed class PageView(BitmapSource image, double width, double height, string label) : INotifyPropertyChanged
    {
        private double zoom = 1;
        public BitmapSource Image { get; } = image;
        public double PageWidth { get; } = width;
        public double PageHeight { get; } = height;
        public string Label { get; } = label;
        public double Width => PageWidth * zoom;
        public double Height => PageHeight * zoom;
        public void Zoom(double value) { zoom = value; Changed(nameof(Width)); Changed(nameof(Height)); }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private readonly byte[] pdf;
    private readonly string fileName;
    private readonly bool printOnOpen;
    private List<PageView> pages = [];
    private double zoom = 1;
    private static readonly double[] Steps = [0.5, 0.67, 0.75, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3];

    /// fileName: الاسم المقترح عند الحفظ (بلا امتداد). print: يفتح نافذة الطباعة بعد تجهيز الصفحات.
    public PdfPreviewWindow(string title, byte[] pdf, string fileName, bool print = false)
    {
        InitializeComponent();
        this.pdf = pdf; this.fileName = ReportSheet.FileName(fileName); printOnOpen = print;
        Title = "معاينة · " + title; TitleText.Text = title;
        Loaded += async (_, _) => await Render();
    }

    /// يحوّل كل صفحة إلى صورة بدقة مضاعفة لتبقى واضحة عند التكبير، بمكتبة PDFium (Docnet) دون كائنات ويندوز WinRT
    /// (كانت تُبقي العملية معلّقة ثم تنهيها بخطأ بعد الإغلاق).
    private static List<PageView> RenderPages(byte[] bytes)
    {
        const double scale = Sharpness * 96 / 72;   // نقاط PDF ← بكسل بدقة ثلاثية لوضوح المعاينة عند التكبير
        var result = new List<PageView>();
        lock (Docnet.Core.DocLib.Instance)
        {
            using var document = Docnet.Core.DocLib.Instance.GetDocReader(bytes, new Docnet.Core.Models.PageDimensions(scale));
            var count = document.GetPageCount();
            for (int i = 0; i < count; i++)
            {
                using var page = document.GetPageReader(i);
                int width = page.GetPageWidth(), height = page.GetPageHeight();
                var raw = page.GetImage();
                // خلفية بيضاء: المكتبة تترك المساحات الفارغة شفافة.
                for (int p = 0; p + 3 < raw.Length; p += 4)
                {
                    var alpha = raw[p + 3];
                    if (alpha == 255) continue;
                    for (int c = 0; c < 3; c++) raw[p + c] = (byte)((raw[p + c] * alpha + 255 * (255 - alpha)) / 255);
                    raw[p + 3] = 255;
                }
                var image = BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, raw, width * 4);
                image.Freeze();
                result.Add(new PageView(image, width / Sharpness, height / Sharpness, $"صفحة {i + 1:N0} من {count:N0}"));
            }
        }
        return result;
    }

    /// صفحة واحدة بدقة محددة (نقطة PDF ← بكسل) على خلفية بيضاء؛ للطباعة بدقة الطابعة.
    internal static BitmapSource RenderPage(byte[] bytes, int index, double pixelsPerPoint, double dpi)
    {
        lock (Docnet.Core.DocLib.Instance)
        {
            using var document = Docnet.Core.DocLib.Instance.GetDocReader(bytes, new Docnet.Core.Models.PageDimensions(pixelsPerPoint));
            using var page = document.GetPageReader(index);
            int width = page.GetPageWidth(), height = page.GetPageHeight();
            var raw = page.GetImage();
            for (int p = 0; p + 3 < raw.Length; p += 4)
            {
                var alpha = raw[p + 3];
                if (alpha == 255) continue;
                for (int c = 0; c < 3; c++) raw[p + c] = (byte)((raw[p + c] * alpha + 255 * (255 - alpha)) / 255);
                raw[p + 3] = 255;
            }
            var image = BitmapSource.Create(width, height, dpi, dpi, System.Windows.Media.PixelFormats.Bgra32, null, raw, width * 4);
            image.Freeze();
            return image;
        }
    }

    /// يرسل الصفحات إلى الطابعة واحدة تلو الأخرى بدقة 600 نقطة في البوصة، فتخرج الحروف والخطوط حادة
    /// كالنسخة الأصلية بدل صورة المعاينة منخفضة الدقة التي تبدو مستنسخة. الرسم عند الطلب يحدّ من استهلاك الذاكرة.
    private sealed class PrintPaginator(byte[] pdf, IReadOnlyList<PageView> pages, double dpi) : DocumentPaginator
    {
        public override DocumentPage GetPage(int pageNumber)
        {
            var view = pages[pageNumber];
            var size = new Size(view.PageWidth, view.PageHeight);
            var image = RenderPage(pdf, pageNumber, dpi / 72.0, dpi);
            var visual = new System.Windows.Media.DrawingVisual();
            using (var context = visual.RenderOpen()) context.DrawImage(image, new Rect(size));
            return new DocumentPage(visual, size, new Rect(size), new Rect(size));
        }
        public override bool IsPageCountValid => true;
        public override int PageCount => pages.Count;
        public override Size PageSize { get => new(pages[0].PageWidth, pages[0].PageHeight); set { } }
        public override IDocumentPaginatorSource Source => null!;
    }

    private async Task Render()
    {
        try
        {
            // الرسم بكائنات ويندوز في خيط خلفي، فلا ترتبط بخيط الواجهة ولا تؤخره؛ الصور مجمّدة فتُعرض من أي خيط.
            var bytes = pdf;
            pages = await Task.Run(() => RenderPages(bytes));
            Rendering.Visibility = Visibility.Collapsed;
            Pages.ItemsSource = pages;
            InfoText.Text = $"{pages.Count:N0} صفحة · {pdf.Length / 1024.0:N0} كيلوبايت · معاينة داخل البرنامج";
            SaveButton.IsEnabled = PrintButton.IsEnabled = pages.Count > 0;
            FitWidth(this, new RoutedEventArgs());
            if (printOnOpen && pages.Count > 0) Print();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            InfoText.Text = "تعذّرت المعاينة: " + ex.Message; Rendering.Visibility = Visibility.Collapsed;
            SaveButton.IsEnabled = true;
        }
    }

    private void ApplyZoom(double value)
    {
        zoom = Math.Clamp(value, 0.4, 3);
        foreach (var page in pages) page.Zoom(zoom);
        ZoomText.Text = $"{Math.Round(zoom * 100):N0}٪";
    }
    private void ZoomIn(object sender, RoutedEventArgs e) => ApplyZoom(Steps.FirstOrDefault(s => s > zoom + 0.01, 3));
    private void ZoomOut(object sender, RoutedEventArgs e) => ApplyZoom(Steps.LastOrDefault(s => s < zoom - 0.01, 0.5));
    private void FitWidth(object sender, RoutedEventArgs e)
    {
        var widest = pages.Count == 0 ? 0 : pages.Max(p => p.PageWidth);
        var room = Scroller.ActualWidth - 60;
        if (widest > 0 && room > 100) ApplyZoom(Math.Min(1.6, room / widest));
    }
    private void ScrollerWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true;
        if (e.Delta > 0) ZoomIn(this, e); else ZoomOut(this, e);
    }
    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.S && SaveButton.IsEnabled) { e.Handled = true; SaveClicked(this, e); }
        else if (e.Key == Key.P && PrintButton.IsEnabled) { e.Handled = true; Print(); }
        else if (e.Key is Key.OemPlus or Key.Add) { e.Handled = true; ZoomIn(this, e); }
        else if (e.Key is Key.OemMinus or Key.Subtract) { e.Handled = true; ZoomOut(this, e); }
    }
    private void CloseClicked(object sender, RoutedEventArgs e) => Close();

    private async void SaveClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "حفظ PDF", Filter = "ملف PDF (*.pdf)|*.pdf", FileName = fileName + ".pdf", AddExtension = true, DefaultExt = ".pdf", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        var destination = dialog.FileName;
        try
        {
            await Task.Run(() => ReportFiles.Save(pdf, destination, ".pdf"));
            InfoText.Text = "حُفظ الملف: " + destination;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        { Ui.Message(this, "تعذّر حفظ الملف.\n" + ex.Message, "حفظ PDF", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void PrintClicked(object sender, RoutedEventArgs e) => Print();
    /// الطباعة من البرنامج مباشرة: صفحة لكل صفحة PDF بحجمها، دون الحاجة إلى عارض PDF.
    private void Print()
    {
        if (pages.Count == 0) return;
        var dialog = new PrintDialog { UserPageRangeEnabled = false };
        try
        {
            if (dialog.PrintTicket is not null)
                dialog.PrintTicket.PageOrientation = pages[0].PageWidth > pages[0].PageHeight ? System.Printing.PageOrientation.Landscape : System.Printing.PageOrientation.Portrait;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Printing.PrintQueueException) { }
        if (dialog.ShowDialog() != true) return;
        var cursor = Cursor; Cursor = Cursors.Wait;
        try
        {
            // أعلى جودة عملية (طلب المالك): 600 نقطة في البوصة دائماً، وهي دقة الطباعة الحقيقية لمعظم طابعات المكاتب؛
            // الأعلى منها يضاعف الذاكرة أربع مرات لكل صفحة دون فرق ظاهر في الكشوف.
            const double resolution = 600;
            dialog.PrintDocument(new PrintPaginator(pdf, pages, resolution), TitleText.Text);
            InfoText.Text = $"أُرسل الكشف إلى الطابعة بدقة {resolution:N0} نقطة في البوصة.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Printing.PrintQueueException or System.Printing.PrintSystemException)
        { Ui.Message(this, "تعذّرت الطباعة.\n" + ex.Message, "طباعة", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { Cursor = cursor; }
    }
}
