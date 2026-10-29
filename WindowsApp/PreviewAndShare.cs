using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace SalaryWindows;

// نافذة المشاركة الرسمية في تطبيق سطح مكتب غير محزّم.
[ComImport]
[Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDataTransferManagerInterop
{
    IntPtr GetForWindow(IntPtr appWindow, ref Guid riid);
    void ShowShareUIForWindow(IntPtr appWindow);
}

public sealed partial class MainWindow
{
    private DataTransferManager? shareManager;
    private StorageFile? sharingFile;
    private string sharingTitle = "مستند الرواتب";

    private void SharePreparedFile(StorageFile file, string title)
    {
        var handle = WindowNative.GetWindowHandle(this);
        var interop = DataTransferManager.As<IDataTransferManagerInterop>();
        if (shareManager == null)
        {
            var id = new Guid("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C");
            var pointer = interop.GetForWindow(handle, ref id);
            try { shareManager = WinRT.MarshalInterface<DataTransferManager>.FromAbi(pointer); }
            finally { if (pointer != IntPtr.Zero) Marshal.Release(pointer); }
            shareManager.DataRequested += ShareDataRequested;
        }
        sharingFile = file;
        sharingTitle = title;
        interop.ShowShareUIForWindow(handle);
    }

    private void ShareDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        if (sharingFile == null) { args.Request.FailWithDisplayText("تعذّر تجهيز الملف للمشاركة."); return; }
        args.Request.Data.Properties.Title = sharingTitle;
        args.Request.Data.SetStorageItems(new IStorageItem[] { sharingFile });
    }

    private async Task ShowDocumentPreview(string path, string title)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var pdf = Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            ? await PdfDocument.LoadFromFileAsync(file) : null;
        if (pdf != null && pdf.PageCount == 0) throw new InvalidDataException("المستند لا يحتوي صفحات للمعاينة.");
        var picture = new Image { Stretch = Stretch.Uniform, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Center };
        var caption = new TextBlock { TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center };
        var previous = new Button { Content = "السابق" };
        var next = new Button { Content = "التالي" };
        var page = 0;
        var busy = false;
        var total = pdf == null ? 1 : checked((int)pdf.PageCount);
        async Task RenderPage()
        {
            if (busy) return;
            busy = true; previous.IsEnabled = false; next.IsEnabled = false;
            caption.Text = "جارٍ تجهيز المعاينة…";
            try
            {
                using var stream = new InMemoryRandomAccessStream();
                if (pdf != null)
                {
                    using var current = pdf.GetPage((uint)page);
                    var dimensions = PreviewGeometry.Fit(current.Size.Width, current.Size.Height);
                    var options = new PdfPageRenderOptions { DestinationWidth = dimensions.Width, DestinationHeight = dimensions.Height };
                    await current.RenderToStreamAsync(stream, options);
                }
                else
                {
                    using var input = await file.OpenReadAsync();
                    await RandomAccessStream.CopyAsync(input, stream);
                }
                stream.Seek(0);
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                picture.Source = bitmap;
                caption.Text = total == 1 ? "صفحة واحدة" : $"صفحة {page + 1} من {total}";
            }
            catch (Exception)
            {
                picture.Source = null;
                caption.Text = "تعذّر عرض الصفحة. تحقق من سلامة المستند.";
            }
            finally
            {
                busy = false; previous.IsEnabled = page > 0; next.IsEnabled = page + 1 < total;
            }
        }
        previous.Click += async (_, _) => { if (!busy && page > 0) { page--; await RenderPage(); } };
        next.Click += async (_, _) => { if (!busy && page + 1 < total) { page++; await RenderPage(); } };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center };
        controls.Children.Add(previous); controls.Children.Add(caption); controls.Children.Add(next);
        var scroll = new ScrollViewer { Content = picture, MaxHeight = 520, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, ZoomMode = ZoomMode.Enabled, MinZoomFactor = 0.75f, MaxZoomFactor = 3f };
        var zoomOut = new Button { Content = "تصغير" }; var zoomIn = new Button { Content = "تكبير" };
        zoomOut.Click += (_, _) => scroll.ChangeView(null, null, Math.Max(scroll.MinZoomFactor, scroll.ZoomFactor - 0.25f));
        zoomIn.Click += (_, _) => scroll.ChangeView(null, null, Math.Min(scroll.MaxZoomFactor, scroll.ZoomFactor + 0.25f));
        var zoomControls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center };
        zoomControls.Children.Add(zoomOut); zoomControls.Children.Add(zoomIn);
        if (pdf == null)
        {
            var copyImage = new Button { Content = "نسخ الصورة" };
            copyImage.Click += (_, _) => {
                try { var package = new DataPackage(); package.SetBitmap(RandomAccessStreamReference.CreateFromFile(file)); Clipboard.SetContent(package); Clipboard.Flush(); caption.Text = "نُسخت الصورة."; }
                catch { caption.Text = "تعذّر نسخ الصورة."; }
            };
            zoomControls.Children.Add(copyImage);
        }
        var body = new StackPanel { Spacing = 12, FlowDirection = FlowDirection.RightToLeft };
        body.Children.Add(controls); body.Children.Add(zoomControls); body.Children.Add(scroll);
        var dialog = new ContentDialog { Title = title, Content = body, PrimaryButtonText = "مشاركة", SecondaryButtonText = "حفظ باسم", CloseButtonText = "إغلاق", XamlRoot = Content.XamlRoot, MaxWidth = 900, FlowDirection = FlowDirection.RightToLeft };
        var shown = dialog.ShowAsync();
        await RenderPage();
        var choice = await shown;
        if (choice == ContentDialogResult.Primary) SharePreparedFile(file, title);
        else if (choice == ContentDialogResult.Secondary) await SavePreviewAs(path, title);
    }

    private async Task SavePreviewAs(string source, string title)
    {
        var extension = Path.GetExtension(source).ToLowerInvariant();
        var safeTitle = new string(title.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
        if (safeTitle.Length > 80) safeTitle = safeTitle[..80];
        if (string.IsNullOrWhiteSpace(safeTitle)) safeTitle = "مستند الرواتب";
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = safeTitle };
        picker.FileTypeChoices.Add(extension == ".pdf" ? "PDF" : "PNG", new List<string> { extension });
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var destination = await picker.PickSaveFileAsync();
        if (destination == null) return;
        var temporary = destination.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await Task.Run(() => { File.Copy(source, temporary); File.Move(temporary, destination.Path, true); }); }
        finally { try { File.Delete(temporary); } catch { } }
    }
}
