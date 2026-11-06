using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps;
using Microsoft.Win32;
using SalaryDesktop.Core;
using SalaryLocal.Core;

namespace SalaryLocal.App;

/// لقطة مستقلة: لا ملفات مؤقتة ولا عارض خارجي ولا تخزين لصور جميع الصفحات.
public sealed class LocalPdfPreviewWindow : Window
{
    private readonly byte[] pdf;
    private readonly string documentTitle;
    private readonly Action? validateBeforeOutput;
    private readonly Image pageImage = new() { Stretch = Stretch.Fill, FlowDirection = FlowDirection.LeftToRight };
    private readonly TextBlock status = new() { Margin = new Thickness(12) };
    private readonly TextBlock counter = new() { Margin = new Thickness(12), VerticalAlignment = VerticalAlignment.Center };
    private readonly ScrollViewer scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FlowDirection = FlowDirection.LeftToRight };
    private readonly Button previous, next, save, print, cancel;
    private readonly CancellationTokenSource lifetime = new();
    private LocalPdfPageSize[] pages = [];
    private int index;
    private double zoom = 1;
    private bool busy, closed;
    private XpsDocumentWriter? writer;

    public LocalPdfPreviewWindow(Window owner, string title, byte[] bytes, Action? validateBeforeOutput = null)
    {
        if (bytes.Length is 0 or > 134217728) throw new InvalidDataException("حجم ملف PDF غير مدعوم للمعاينة الآمنة.");
        pdf = (byte[])bytes.Clone(); documentTitle = title; this.validateBeforeOutput = validateBeforeOutput;
        Owner = owner; Title = "معاينة • " + title; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = Math.Min(1100, SystemParameters.WorkArea.Width); Height = Math.Min(850, SystemParameters.WorkArea.Height);
        var root = new DockPanel { Margin = new Thickness(12) };
        var heading = new TextBlock { Text = title, FontSize = 22, Margin = new Thickness(12) }; DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var toolbar = new WrapPanel(); DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        Button Add(string label, RoutedEventHandler action) { var button = new Button { Content = label }; button.Click += action; toolbar.Children.Add(button); return button; }
        previous = Add("السابقة", async (_, _) => await Navigate(-1)); next = Add("التالية", async (_, _) => await Navigate(1)); toolbar.Children.Add(counter);
        Add("−", (_, _) => ApplyZoom(zoom / 1.25)); Add("+", (_, _) => ApplyZoom(zoom * 1.25)); Add("ملاءمة العرض", (_, _) => { if (pages.Length > 0) ApplyZoom((scroll.ViewportWidth - 40) / pages[index].Width); });
        save = Add("حفظ PDF", async (_, _) => await Save()); print = Add("طباعة…", (_, _) => Print());
        cancel = Add("إلغاء الإرسال", (sender, _) => { if (writer is not null) { ((Button)sender).IsEnabled = false; status.Text = "طُلب الإلغاء؛ قد تكون بعض الصفحات أُرسلت بالفعل. راجع قائمة الطابعة."; try { writer.CancelAsync(); } catch (Exception ex) when (ex is not OutOfMemoryException) { status.Text = "تعذّر تأكيد الإلغاء. راجع قائمة الطابعة."; ((Button)sender).IsEnabled = true; } } });
        DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
        var paper = new Border { Background = Brushes.White, Padding = new Thickness(0), Margin = new Thickness(20), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Child = pageImage };
        scroll.Content = paper; root.Children.Add(scroll); Content = root;
        Loaded += async (_, _) => await Initialize();
        Closing += (_, e) => { if (writer is not null || outputInProgress) { e.Cancel = true; status.Text = "انتظر اكتمال الإرسال أو الحفظ؛ يمكن طلب إلغاء الطباعة."; } };
        Closed += (_, _) => { closed = true; lifetime.Cancel(); pageImage.Source = null; };
        KeyDown += async (_, e) => { if (e.Key == Key.Escape && writer is null && !outputInProgress) Close(); else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S) { e.Handled = true; await Save(); } else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.P) { e.Handled = true; Print(); } };
        UpdateButtons();
    }
    private bool outputInProgress;
    private void UpdateButtons()
    {
        previous.IsEnabled = !busy && index > 0; next.IsEnabled = !busy && index + 1 < pages.Length;
        save.IsEnabled = !busy; print.IsEnabled = !busy && pages.Length > 0; cancel.IsEnabled = writer is not null;
    }
    private async Task Initialize()
    {
        busy = true; UpdateButtons(); status.Text = "جارٍ تجهيز معاينة محلية…";
        try
        {
            pages = await Task.Run(() =>
            {
                lock (Docnet.Core.DocLib.Instance)
                {
                    using var doc = Docnet.Core.DocLib.Instance.GetDocReader(pdf, new Docnet.Core.Models.PageDimensions(96d / 72));
                    int count = doc.GetPageCount(); if (count is <= 0 or > 10000) throw new InvalidDataException("عدد صفحات PDF خارج الحد المدعوم.");
                    var result = new LocalPdfPageSize[count];
                    for (int i = 0; i < count; i++) { lifetime.Token.ThrowIfCancellationRequested(); using var page = doc.GetPageReader(i); result[i] = new(page.GetPageWidth(), page.GetPageHeight()); LocalPdfPrintLayout.ValidateRaster((int)Math.Ceiling(result[i].Width * 600 / 96), (int)Math.Ceiling(result[i].Height * 600 / 96)); }
                    return result;
                }
            });
            if (!closed) await RenderCurrent();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (!closed) status.Text = "تعذّرت المعاينة. يمكن حفظ الملف الأصلي. " + ex.Message; pages = []; }
        finally { busy = false; if (!closed) UpdateButtons(); }
    }
    private async Task Navigate(int delta)
    {
        if (busy || pages.Length == 0) return; busy = true; UpdateButtons();
        index = Math.Clamp(index + delta, 0, pages.Length - 1); pageImage.Source = null;
        try { await RenderCurrent(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (!closed) status.Text = "تعذّر رسم الصفحة: " + ex.Message; }
        finally { busy = false; if (!closed) UpdateButtons(); }
    }
    private async Task RenderCurrent()
    {
        var bitmap = await Task.Run(() => Render(pdf, index, 288, lifetime.Token));
        if (closed) return; pageImage.Source = bitmap; ApplyZoom(zoom); scroll.ScrollToTop();
        status.Text = "معاينة ×3 • صفحة واحدة في الذاكرة • الطباعة تُرسم بدقة 600 نقطة/بوصة";
    }
    private void ApplyZoom(double value)
    {
        if (pages.Length == 0 || !double.IsFinite(value)) return;
        zoom = Math.Clamp(value, .25, 3); pageImage.Width = pages[index].Width * zoom; pageImage.Height = pages[index].Height * zoom;
        counter.Text = $"صفحة {index + 1:N0} / {pages.Length:N0} • {zoom:P0}";
    }
    private static BitmapSource Render(byte[] bytes, int pageIndex, double dpi, CancellationToken token)
    {
        lock (Docnet.Core.DocLib.Instance)
        {
            token.ThrowIfCancellationRequested();
            using var doc = Docnet.Core.DocLib.Instance.GetDocReader(bytes, new Docnet.Core.Models.PageDimensions(dpi / 72));
            using var page = doc.GetPageReader(pageIndex); int w = page.GetPageWidth(), h = page.GetPageHeight();
            LocalPdfPrintLayout.ValidateRaster(w, h); var raw = page.GetImage(); token.ThrowIfCancellationRequested();
            for (int p = 0; p < raw.Length; p += 4) { int a = raw[p + 3]; if (a != 255) { for (int c = 0; c < 3; c++) raw[p + c] = (byte)((raw[p + c] * a + 255 * (255 - a)) / 255); raw[p + 3] = 255; } }
            var image = BitmapSource.Create(w, h, dpi, dpi, PixelFormats.Bgra32, null, raw, checked(w * 4)); image.Freeze(); return image;
        }
    }
    private async Task Save()
    {
        if (busy) return; busy = outputInProgress = true; UpdateButtons();
        try
        {
            var validateSession = Ui.CaptureUnlockedSession();
            validateBeforeOutput?.Invoke();
            var dialog = new SaveFileDialog { Filter = "PDF (*.pdf)|*.pdf", FileName = ReportSheet.FileName(documentTitle) + ".pdf", DefaultExt = ".pdf", AddExtension = true, OverwritePrompt = true };
            if (Ui.ShowNativeDialog(() => dialog.ShowDialog(this)) != true) return;
            validateSession(); validateBeforeOutput?.Invoke(); await Task.Run(() => { validateSession(); ReportFiles.Save(pdf, dialog.FileName, ".pdf"); }); status.Text = "حُفظت نسخة PDF المحلية.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Ui.Message(this, "تعذّر الحفظ. " + ex.Message); }
        finally { busy = outputInProgress = false; UpdateButtons(); }
    }
    private void Print()
    {
        if (busy || pages.Length == 0) return; busy = outputInProgress = true; UpdateButtons();
        try
        {
            var validateSession = Ui.CaptureUnlockedSession();
            validateBeforeOutput?.Invoke();
            var dialog = new PrintDialog { UserPageRangeEnabled = false };
            dialog.PrintTicket = new PrintTicket { PageMediaSize = new PageMediaSize(PageMediaSizeName.ISOA4), PageOrientation = pages[0].Width > pages[0].Height ? PageOrientation.Landscape : PageOrientation.Portrait };
            if (Ui.ShowNativeDialog(() => dialog.ShowDialog()) != true) return;
            validateBeforeOutput?.Invoke();
            var queue = dialog.PrintQueue ?? throw new InvalidDataException("لم تُحدّد طابعة.");
            var ticket = dialog.PrintTicket ?? throw new InvalidDataException("إعداد الطباعة غير متاح.");
            var caps = queue.GetPrintCapabilities(ticket); var area = caps.PageImageableArea ?? throw new InvalidDataException("الطابعة لا تُبلغ عن هوامشها؛ أُلغي الإرسال لمنع القص.");
            var paper = new LocalPdfPageSize(caps.OrientedPageMediaWidth ?? 0, caps.OrientedPageMediaHeight ?? 0);
            var placements = pages.Select(p => LocalPdfPrintLayout.Fit(p, paper, area.OriginWidth, area.OriginHeight, area.ExtentWidth, area.ExtentHeight)).ToArray();
            var scale = placements.Min(p => p.Scale);
            if (scale < .999 && Ui.Message(this, $"سيُحتوى كامل محتوى الصفحات داخل هوامش الورق المختار دون قص. أصغر مقياس {scale:P1}. هل توافق على الإرسال بهذا المقياس؟", confirm: true) != MessageBoxResult.Yes) return;
            validateBeforeOutput?.Invoke();
            validateSession();
            // لا نحفظ الصور في قائمة؛ يطلب نظام الطباعة الصفحة المطلوبة فقط.
            writer = PrintQueue.CreateXpsDocumentWriter(queue);
            writer.WritingCompleted += (_, e) => Dispatcher.Invoke(() =>
            {
                status.Text = e.Cancelled ? "أُلغي الإرسال؛ راجع قائمة الطابعة فقد تكون بعض الصفحات أُرسلت." : e.Error is not null ? "تعذّر إكمال الإرسال؛ راجع قائمة الطابعة قبل إعادة المحاولة." : "أُرسلت المهمة إلى قائمة الطابعة. لم يُتحقق من خروج الورق فعلياً.";
                writer = null; busy = outputInProgress = false; UpdateButtons();
            });
            status.Text = "جارٍ إرسال الصفحات بدقة رسم 600 نقطة/بوصة…";
            writer.WriteAsync(new Paginator(pdf, pages.Length, paper, placements), ticket); UpdateButtons();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { writer = null; Ui.Message(this, "تعذّر إرسال الطباعة. راجع قائمة الطابعة قبل إعادة المحاولة. " + ex.Message); }
        finally { if (writer is null) { busy = outputInProgress = false; UpdateButtons(); } }
    }
    private sealed class Paginator(byte[] pdf, int count, LocalPdfPageSize paper, LocalPdfPrintPlacement[] placements) : DocumentPaginator
    {
        public override DocumentPage GetPage(int pageNumber)
        {
            if (pageNumber < 0 || pageNumber >= count) return DocumentPage.Missing;
            var p = placements[pageNumber]; var bitmap = Render(pdf, pageNumber, 600, CancellationToken.None);
            var visual = new DrawingVisual(); using (var drawing = visual.RenderOpen()) drawing.DrawImage(bitmap, new Rect(p.X, p.Y, p.Width, p.Height));
            return new DocumentPage(visual, PageSize, new Rect(PageSize), new Rect(p.X, p.Y, p.Width, p.Height));
        }
        public override bool IsPageCountValid => true;
        public override int PageCount => count;
        public override Size PageSize { get => new(paper.Width, paper.Height); set { } }
        public override IDocumentPaginatorSource Source => null!;
    }
}
