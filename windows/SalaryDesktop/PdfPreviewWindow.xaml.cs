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
using Windows.Storage.Streams;

namespace SalaryDesktop;

/// معاينة PDF داخل البرنامج (بدل فتح عارض خارجي): تُرسم الصفحات بمكتبة ويندوز (Windows.Data.Pdf)، مع الحفظ والطباعة من النافذة نفسها.
public partial class PdfPreviewWindow : Window
{
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

    /// يحوّل كل صفحة إلى صورة بدقة مضاعفة لتبقى واضحة عند التكبير.
    private static async Task<List<PageView>> RenderPages(byte[] bytes)
    {
        using var input = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(input.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes); await writer.StoreAsync(); await writer.FlushAsync(); writer.DetachStream();
        }
        var document = await Windows.Data.Pdf.PdfDocument.LoadFromStreamAsync(input);
        var result = new List<PageView>();
        for (uint i = 0; i < document.PageCount; i++)
        {
            using var page = document.GetPage(i);
            double width = page.Size.Width, height = page.Size.Height;
            using var output = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(output, new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = (uint)Math.Max(1, Math.Round(width * 2.2)) });
            var buffer = new byte[output.Size];
            using (var reader = new DataReader(output.GetInputStreamAt(0)))
            {
                await reader.LoadAsync((uint)output.Size); reader.ReadBytes(buffer); reader.DetachStream();
            }
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = new MemoryStream(buffer); image.EndInit(); image.Freeze();
            result.Add(new PageView(image, width, height, $"صفحة {i + 1:N0} من {document.PageCount:N0}"));
        }
        return result;
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
        try
        {
            var document = new FixedDocument();
            foreach (var page in pages)
            {
                var fixedPage = new FixedPage { Width = page.PageWidth, Height = page.PageHeight };
                fixedPage.Children.Add(new Image { Source = page.Image, Width = page.PageWidth, Height = page.PageHeight, Stretch = System.Windows.Media.Stretch.Fill });
                var content = new PageContent(); ((System.Windows.Markup.IAddChild)content).AddChild(fixedPage);
                document.Pages.Add(content);
            }
            dialog.PrintDocument(document.DocumentPaginator, TitleText.Text);
            InfoText.Text = "أُرسل الكشف إلى الطابعة.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Printing.PrintQueueException or System.Printing.PrintSystemException)
        { Ui.Message(this, "تعذّرت الطباعة.\n" + ex.Message, "طباعة", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
