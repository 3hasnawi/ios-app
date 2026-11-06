using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SalaryDesktop.Core;
using SalaryLocal.Core;

namespace SalaryLocal.App;

public partial class MainWindow
{
    private void AddArchiveImageActions(StackPanel panel, BackupSnapshot source, DataGrid grid)
    {
        panel.Children.Add(Ui.Button("إضافة كتاب من صور / ماسح", async () => await Run(() => ReviewArchiveImages(source, null))));
        panel.Children.Add(Ui.Button("استبدال صفحات الكتاب بصور / ماسح", async () => await Run(async () => {
            if (grid.SelectedItem is not ArchiveView row) { Ui.Message(this, "اختر كتاباً أولاً."); return; }
            await ReviewArchiveImages(source, row.Source);
        })));
    }

    private async Task ReviewArchiveImages(BackupSnapshot source, ArchiveBook? replacing)
    {
        var pages = new List<LocalArchiveImage>();
        var panel = new StackPanel { Margin = new Thickness(20) };
        var dialog = Ui.Dialog(this, replacing is null ? "مراجعة صور الكتاب" : "مراجعة استبدال صفحات الكتاب", new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, 1040, 900);
        panel.Children.Add(Ui.Text(replacing is null ? "أضف الصفحات ثم راجع اتجاهها وترتيبها واسم الكتاب." : $"استبدال صفحات «{replacing.Title}» مع إبقاء هويته وروابطه وتاريخ إنشائه.", 21));
        panel.Children.Add(Ui.Text(LocalArchiveImages.Limits, 12));
        panel.Children.Add(Ui.Text("كل المعالجة محلية، دون تعرّف نصي أو رفع إلى الإنترنت. الصور الأصلية لا تتغير. المسح المباشر يحتاج جهازاً وتعريف WIA متوافقين؛ لم يُختبر على ماسح فعلي.", 12));
        var title = Field(panel, "اسم واضح للكتاب", replacing?.Title ?? "");
        var month = Field(panel, "الشهر", replacing?.Month ?? Month);
        var entity = Field(panel, "الجهة (اختياري)", replacing?.Entity ?? "");
        if (replacing is not null) title.IsReadOnly = month.IsReadOnly = entity.IsReadOnly = true;
        var addActions = new WrapPanel(); panel.Children.Add(addActions);
        var status = Ui.Text("لم تُضف صفحات بعد."); panel.Children.Add(status);
        var layout = new Grid { Height = 410, Margin = new Thickness(0, 8, 0, 8) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var list = new ListBox { Margin = new Thickness(0, 0, 12, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var preview = new Image { Stretch = Stretch.Uniform, FlowDirection = FlowDirection.LeftToRight };
        var frame = new Border { Child = preview, BorderThickness = new Thickness(1), Padding = new Thickness(8) };
        frame.SetResourceReference(Border.BorderBrushProperty, "Line");
        frame.SetResourceReference(Border.BackgroundProperty, "Page");
        Grid.SetColumn(frame, 1); layout.Children.Add(list); layout.Children.Add(frame); panel.Children.Add(layout);
        var pageActions = new WrapPanel(); panel.Children.Add(pageActions);
        var finalActions = new WrapPanel(); panel.Children.Add(finalActions);
        bool working = false, updatingList = false, accepted = false;
        byte[]? result = null;
        dialog.Closing += (_, e) => { if (working) { e.Cancel = true; Ui.Message(dialog, "انتظر انتهاء تجهيز الصفحة قبل إغلاق النافذة."); } };

        async Task Work(Func<Task> action)
        {
            if (working) return;
            working = true; panel.IsEnabled = false;
            string previousStatus = status.Text; const string progressText = "جارٍ تجهيز الصفحات محلياً…"; status.Text = progressText;
            try { await action(); }
            catch (Exception ex) { Ui.Message(dialog, "لم يُحفظ أي تغيير في الأرشيف.\n" + ex.Message); }
            finally { working = false; panel.IsEnabled = true; if (status.Text == progressText) status.Text = previousStatus; }
        }
        async Task ShowPage()
        {
            preview.Source = null;
            int index = list.SelectedIndex;
            status.Text = pages.Count == 0 ? "لم تُضف صفحات بعد." : $"{pages.Count} من ٣ صفحات • ترتيب PDF هو ترتيب القائمة من الأعلى إلى الأسفل.";
            if (index < 0 || index >= pages.Count) return;
            var page = pages[index];
            byte[] png = await Task.Run(() => LocalArchiveImages.PagePreview(page));
            using var stream = new MemoryStream(png);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            preview.Source = bitmap;
            status.Text += $"\nالصفحة {index + 1} • {page.Width} × {page.Height} بكسل • تدوير إضافي: {page.ClockwiseTurns * 90}°";
        }
        async Task Refresh(int selected)
        {
            updatingList = true;
            try { list.ItemsSource = pages.Select((p, i) => $"الصفحة {i + 1}\n{p.SourceName}").ToArray(); list.SelectedIndex = pages.Count == 0 ? -1 : Math.Clamp(selected, 0, pages.Count - 1); }
            finally { updatingList = false; }
            await ShowPage();
        }
        bool CanAdd(int count)
        {
            if (pages.Count + count <= LocalArchiveImages.MaxPages) return true;
            Ui.Message(dialog, "الحد ثلاث صفحات للكتاب. أزل صفحة أولاً أو اختر عدداً أقل؛ لم تُضف أي صورة من الاختيار الزائد."); return false;
        }
        list.SelectionChanged += async (_, _) => { if (!updatingList && !working) await Work(ShowPage); };
        addActions.Children.Add(Ui.Button("إضافة صور JPEG / PNG", async () => await Work(async () => {
            if (!CanAdd(1)) return;
            var picker = new OpenFileDialog { Filter = "JPEG / PNG (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png", Multiselect = true, CheckFileExists = true };
            if (Ui.ShowNativeDialog(() => picker.ShowDialog(dialog)) != true || !CanAdd(picker.FileNames.Length)) return;
            var loaded = await Task.Run(() => LocalArchiveImages.ReadFiles(picker.FileNames));
            int first = pages.Count; pages.AddRange(loaded); await Refresh(first);
        })));
        addActions.Children.Add(Ui.Button("مسح صفحة عبر WIA", async () => await Work(async () => {
            if (!CanAdd(1)) return;
            var page = LocalArchiveScanner.AcquirePage();
            if (page is null) return;
            pages.Add(page); await Refresh(pages.Count - 1);
        })));
        pageActions.Children.Add(Ui.Button("تدوير ٩٠°", async () => await Work(async () => {
            int i = list.SelectedIndex; if (i < 0) return; pages[i] = pages[i].RotateClockwise(); await Refresh(i);
        })));
        async Task Move(int difference)
        {
            int i = list.SelectedIndex, to = i + difference; if (i < 0 || to < 0 || to >= pages.Count) return;
            (pages[i], pages[to]) = (pages[to], pages[i]); await Refresh(to);
        }
        pageActions.Children.Add(Ui.Button("نقل إلى الأعلى", async () => await Work(() => Move(-1))));
        pageActions.Children.Add(Ui.Button("نقل إلى الأسفل", async () => await Work(() => Move(1))));
        pageActions.Children.Add(Ui.Button("إزالة من المسودة", async () => await Work(async () => {
            int i = list.SelectedIndex; if (i < 0) return; pages.RemoveAt(i); await Refresh(i);
        })));
        var save = Ui.Button("مراجعة وحفظ PDF محلياً", async () => {
            await Work(async () => {
                if (pages.Count == 0) { Ui.Message(dialog, "أضف صفحة واحدة على الأقل."); return; }
                string name = title.Text.Trim(), period = month.Text.Trim(), party = entity.Text.Trim();
                if (name.Length is < 1 or > 200 || period.Length is < 1 or > 200 || party.Length > 200 || (name + period + party).Any(char.IsControl))
                { Ui.Message(dialog, "أدخل اسماً وشهراً واضحين، بحد أقصى ٢٠٠ حرف لكل حقل، دون أسطر جديدة."); return; }
                if (replacing is null && System.Text.RegularExpressions.Regex.IsMatch(name, @"^(?:scan|img|image|document)[ _-]*\d*(?:\.(?:pdf|jpg|jpeg|png))?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                { Ui.Message(dialog, "اكتب اسم الكتاب الحقيقي بدلاً من الاسم التلقائي للماسح أو الكاميرا."); return; }
                var ordered = pages.ToArray();
                var pdf = await Task.Run(() => LocalArchiveImages.CreatePdf(ordered));
                string operation = replacing is null ? "إضافة الكتاب" : "استبدال ملف الكتاب مع حفظ الملف السابق في سجل المراجعات";
                if (Ui.Message(dialog, $"{operation}؟\nالكتاب: {name}\nالشهر: {period}\nالجهة: {party}\nالصفحات بالترتيب الذي راجعته: {ordered.Length}\nلا يتغير أي ملف أصلي، ولا تُضاف دفعة أمانات.", true) != MessageBoxResult.Yes) return;
                result = pdf; accepted = true;
            });
            if (accepted) dialog.Close();
        });
        save.SetResourceReference(StyleProperty, "PrimaryButton"); finalActions.Children.Add(save);
        finalActions.Children.Add(Ui.Button("إلغاء دون حفظ", dialog.Close));
        dialog.ShowDialog();
        if (!accepted || result is null) return;
        string savedTitle = title.Text.Trim(), savedMonth = month.Text.Trim(), savedEntity = entity.Text.Trim();
        if (replacing is null)
            await Task.Run(() => workspace.Store.AddArchiveBook(savedTitle, savedMonth, savedEntity, result, source.SourceDigest));
        else
            await Task.Run(() => workspace.Store.ReplaceArchivePdf(replacing.Id, result, source.SourceDigest));
        await LoadMonth();
        Ui.Message(this, "حُفظ PDF في الأرشيف المحلي مع الاحتفاظ بالمراجعات السابقة. الصور الأصلية لم تتغير.");
    }
}
