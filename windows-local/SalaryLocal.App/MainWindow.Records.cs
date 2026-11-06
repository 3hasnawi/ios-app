using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SalaryDesktop.Core;

namespace SalaryLocal.App;

public partial class MainWindow
{
    private sealed record StaffView(StaffMember Source, string SearchKey)
    {
        public string Name => Source.Name;
        public string Mother => Source.Mother;
        public string Rank => Source.Rank;
        public string Workplace => Source.Workplace;
        public string Status => Source.Status;
        public string Stat => Source.Stat;
        public string Unified => Source.Unified;
    }
    private sealed record RecordField(string Section, string Label, string Value);
    private sealed record ArchiveView(ArchiveBook Source, string? Key, string SearchKey)
    {
        public string Title => Source.Title;
        public string Month => Source.Month;
        public string Entity => Source.Entity ?? "";
        public int Pages => Source.Pages;
        public string Availability => Key is null ? "ملف PDF مفقود" : "ملف محفوظ";
    }
    private sealed record TrustView(SalaryTrustEntry Source, string Month, string Status, string Amount, string Paid, string Remaining)
    {
        public string Name => Source.Name;
        public string CaseStatus => Source.CaseStatus;
    }
    private static DataGrid RecordsGrid(params (string Title, string Path)[] columns)
    {
        var grid = GridFor(columns);
        grid.Height = 410; grid.IsReadOnly = true; grid.CanUserAddRows = false; grid.CanUserDeleteRows = false;
        grid.EnableRowVirtualization = true; grid.EnableColumnVirtualization = true;
        VirtualizingPanel.SetIsVirtualizing(grid, true);
        VirtualizingPanel.SetVirtualizationMode(grid, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(grid, true);
        return grid;
    }
    private void RecordDetails(string title, IReadOnlyList<RecordField> fields)
    {
        var grid = RecordsGrid(("القسم", "Section"), ("الحقل", "Label"), ("القيمة المحفوظة", "Value"));
        grid.Height = double.NaN; grid.ItemsSource = fields;
        Ui.Dialog(this, title + " • قراءة فقط", grid, 1000, 660).ShowDialog();
    }
    private async Task StaffPage() => await LocalStaffPage();
    private async Task ArchivePage()
    {
        var loaded = await Task.Run(() => {
            var source = workspace.Store.GetSnapshot(); var keys = workspace.Store.GetAttachments().ToHashSet(StringComparer.Ordinal);
            var books = source.Books.Select(b => { var key = $"books/{b.Id.ToString().ToUpperInvariant()}.pdf"; return new ArchiveView(b, keys.Contains(key) ? key : null, PayrollAnalysis.Normalize($"{b.Title} {b.Month} {b.Entity}")); }).ToArray();
            return (Source: source, Books: books, Revision: source.SourceDigest);
        });
        Page.Children.Add(Ui.Text("أرشيف الكتب المحفوظ", 25));
        Page.Children.Add(Ui.Text("إضافة وتعديل الكتب محلياً، مع حفظ نسخة من PDF الأصلي. لا تشغيل تلقائي للملفات؛ وجود الكتاب لا يثبت إصدار تحويل أو صحة مبلغه."));
        var count = Ui.Text($"{loaded.Books.Length:N0} كتاب"); Page.Children.Add(count);
        var search = new TextBox { ToolTip = "البحث باسم الكتاب أو الشهر أو الجهة" }; Page.Children.Add(search);
        var grid = RecordsGrid(("الكتاب", "Title"), ("الشهر", "Month"), ("الجهة", "Entity"), ("الصفحات", "Pages"), ("الملف", "Availability")); grid.ItemsSource = loaded.Books; Page.Children.Add(grid);
        AddArchiveEditingActions(Page, loaded.Source, grid);
        if (loaded.Books.Length == 0) Page.Children.Add(Ui.Text("لا توجد كتب محفوظة في مساحة العمل الحالية."));
        int generation = 0; int pageGeneration = searchGeneration;
        search.TextChanged += async (_, _) => {
            var id = ++generation; var query = PayrollAnalysis.Normalize(search.Text);
            try { await Task.Delay(180); if (id != generation || pageGeneration != searchGeneration) return;
                var found = await Task.Run(() => loaded.Books.Where(x => x.SearchKey.Contains(query, StringComparison.Ordinal)).ToArray());
                if (id != generation || pageGeneration != searchGeneration) return; grid.ItemsSource = found; count.Text = $"{found.Length:N0} من {loaded.Books.Length:N0} كتاب";
            } catch (Exception ex) { if (pageGeneration == searchGeneration) Ui.Message(this, "تعذّر البحث في الأرشيف.\n" + ex.Message); }
        };
        Page.Children.Add(Ui.Button("معاينة وطباعة الكتاب المحدد", async () => {
            if (busy) return;
            if (grid.SelectedItem is not ArchiveView row || row.Key is null) { Ui.Message(this, "اختر كتاباً له ملف PDF محفوظ."); return; }
            await Run(async () => {
                void Current() { if (workspace.Store.GetStatus().RevisionId != loaded.Revision) throw new InvalidDataException("تغيّر الأرشيف؛ أعد فتحه قبل إخراج الكتاب."); }
                var bytes = await Task.Run(() => { Current(); using var output = new MemoryStream(); workspace.Store.CopyAttachment(row.Key, output); return output.ToArray(); });
                using (var pdf = UglyToad.PdfPig.PdfDocument.Open(bytes)) if (pdf.NumberOfPages != row.Source.Pages) throw new InvalidDataException("عدد صفحات الكتاب لا يطابق فهرسه.");
                new LocalPdfPreviewWindow(this, row.Source.Title, bytes, Current).ShowDialog();
            });
        }));
        Page.Children.Add(Ui.Button("حفظ نسخة PDF للكتاب المحدد", async () => {
            if (busy) return;
            if (grid.SelectedItem is not ArchiveView row) { Ui.Message(this, "اختر كتاباً أولاً."); return; }
            if (row.Key is null) { Ui.Message(this, "ملف PDF لهذا الكتاب مفقود؛ لم يُنشأ ملف بديل."); return; }
            var picker = new SaveFileDialog { Filter = "PDF (*.pdf)|*.pdf", DefaultExt = ".pdf", AddExtension = true, FileName = $"كتاب-{row.Source.Id:N}.pdf", OverwritePrompt = true };
            if (Ui.ShowNativeDialog(() => picker.ShowDialog(this)) != true) return;
            await Run(async () => {
                await Task.Run(() => {
                    if (File.Exists(picker.FileName)) throw new InvalidDataException("اختر اسماً جديداً؛ لا تُستبدل الملفات الموجودة في هذا المسار.");
                    if (workspace.Store.GetStatus().RevisionId != loaded.Revision) throw new InvalidDataException("تغيّرت البيانات؛ أعد فتح الأرشيف قبل التصدير.");
                    using var data = new MemoryStream(); workspace.Store.CopyAttachment(row.Key, data);
                    if (data.Length < 5 || !data.GetBuffer().AsSpan(0, 5).SequenceEqual("%PDF-"u8)) throw new InvalidDataException("المرفق المحفوظ ليس ملف PDF صالحاً.");
                    data.Position = 0;
                    using (var pdfStream = new MemoryStream(data.GetBuffer(), 0, checked((int)data.Length), false))
                    using (var pdf = UglyToad.PdfPig.PdfDocument.Open(pdfStream))
                        if (pdf.NumberOfPages != row.Source.Pages) throw new InvalidDataException("عدد صفحات PDF لا يطابق فهرس الكتاب؛ راجع المصدر.");
                    if (workspace.Store.GetStatus().RevisionId != loaded.Revision) throw new InvalidDataException("تغيّرت البيانات أثناء القراءة؛ أعد فتح الأرشيف.");
                    var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(picker.FileName))!, ".book-" + Guid.NewGuid().ToString("N") + ".tmp");
                    try {
                        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(data.GetBuffer(), 0, checked((int)data.Length)); file.Flush(true); }
                        File.Move(temporary, picker.FileName, false);
                    } finally { if (File.Exists(temporary)) File.Delete(temporary); }
                });
                Ui.Message(this, "حُفظت نسخة PDF دون تغيير الأصل:\n" + picker.FileName);
            });
        }));
    }
    private async Task TrustPage()
    {
        var loaded = await Task.Run(() => { var source = workspace.Store.GetSnapshot(); var ledger = SalaryTrust.ReadFromBackup(source); var months = ledger.Entries.Select(e => e.Year * 100 + e.Month).Distinct().OrderDescending().Select(m => new KeyValuePair<int, string>(m, SalaryTrust.MonthLabel(m / 100, m % 100))).ToArray(); return (Source: source, Ledger: ledger, Months: months, Revision: source.SourceDigest); });
        Page.Children.Add(Ui.Text("أمانات الرواتب", 25));
        Page.Children.Add(Ui.Text("دفتر محلي بمحرك الأمانات الأصلي. استيراد افتتاحي ومراجعة الملاحظات والدفعات وربط الكتب بتأكيد صريح. قيود المراجعة مستبعدة من المتبقي المؤكد؛ التسجيل هنا لا ينفّذ دفعاً خارج البرنامج."));
        if (!loaded.Source.SettingsPlists.ContainsKey(SalaryTrust.SettingsKey)) Page.Children.Add(Ui.Text("لا يوجد دفتر أمانات في هذه المساحة. استورد الملف الافتتاحي بعد مراجعته؛ لم يُنشأ دفتر تلقائياً."));
        var month = new ComboBox { ItemsSource = new[] { new KeyValuePair<int, string>(0, "كل الأشهر") }.Concat(loaded.Months).ToArray(), DisplayMemberPath = "Value", SelectedValuePath = "Key", SelectedIndex = 0 };
        var status = new ComboBox { ItemsSource = SalaryTrust.Filters.Select(f => new KeyValuePair<SalaryTrustFilter, string>(f.Filter, f.Title)).ToArray(), DisplayMemberPath = "Value", SelectedValuePath = "Key", SelectedIndex = 0 };
        var search = new TextBox { ToolTip = "بحث باسم صاحب الأمانة" };
        Page.Children.Add(month); Page.Children.Add(status); Page.Children.Add(search);
        var summary = Ui.Text(""); Page.Children.Add(summary);
        var grid = RecordsGrid(("الاسم", "Name"), ("الشهر", "Month"), ("الصرف", "Status"), ("الحالة", "CaseStatus"), ("الأمانة", "Amount"), ("المصروف", "Paid"), ("المتبقي", "Remaining")); Page.Children.Add(grid);
        AddTrustImportAction(Page, loaded.Source, loaded.Ledger);
        AddMonthlyCashAction(Page, loaded.Source, loaded.Ledger);
        AddTrustEditingActions(Page, loaded.Source, loaded.Ledger, grid);
        int generation = 0, pageGeneration = searchGeneration; bool filtering = false;
        IReadOnlyList<SalaryTrustEntry> selected = []; string selectedMonth = "كل الأشهر", selectedStatus = "كل الحالات", selectedQuery = "";
        var export = Ui.Button("تصدير القيود المعروضة • Excel / PDF", () => {
            if (filtering || selected.Count == 0) { Ui.Message(this, "انتظر اكتمال التصفية واختر قيوداً غير فارغة."); return; }
            var entries = selected; var m = selectedMonth; var s = selectedStatus; var q = selectedQuery;
            ReportSheet Sheet() {
                if (workspace.Store.GetStatus().RevisionId != loaded.Revision) throw new InvalidDataException("تغيّر الدفتر؛ أعد فتح صفحة الأمانات قبل التصدير.");
                return SalaryTrustReports.Sheet(loaded.Ledger, entries, ReportSheet.HeaderFor(loaded.Source), m, s, q);
            }
            SaveExcel(() => SalaryTrustReports.Excel(Sheet(), entries), SalaryTrustReports.FileName,
                () => SalaryTrustReports.Pdf(Sheet(), ReportAssets.Load(Path.Combine(AppContext.BaseDirectory, "WatermarkLogo.png")), DateTime.Now));
        }); Page.Children.Add(export);
        async Task Filter(bool delay) {
            int id = ++generation; filtering = true; export.IsEnabled = false;
            int m = (int)month.SelectedValue; var s = (SalaryTrustFilter)status.SelectedValue; string q = search.Text;
            try {
                if (delay) await Task.Delay(180); if (id != generation || pageGeneration != searchGeneration) return;
                var result = await Task.Run(() => {
                    var entries = SalaryTrustReports.Filtered(loaded.Ledger.Entries, m, s, q);
                    var rows = entries.Select(e => new TrustView(e, SalaryTrust.MonthLabel(e.Year, e.Month), SalaryTrust.PaymentStatus(e), ReportSheet.Money(e.Amount), e.ReviewPending ? "—" : ReportSheet.Money(SalaryTrust.Paid(e)), e.ReviewPending ? "—" : ReportSheet.Money(SalaryTrust.Remaining(e)))).ToArray();
                    return (Entries: entries, Rows: rows, Outstanding: SalaryTrust.Outstanding(entries));
                });
                if (id != generation || pageGeneration != searchGeneration) return;
                selected = result.Entries; selectedMonth = m == 0 ? "كل الأشهر" : SalaryTrust.MonthLabel(m / 100, m % 100); selectedStatus = SalaryTrust.FilterTitle(s); selectedQuery = q;
                grid.ItemsSource = result.Rows; summary.Text = $"{selected.Count:N0} قيد • المتبقي المؤكد: {ReportSheet.Money(result.Outstanding)} د.ع"; filtering = false; export.IsEnabled = selected.Count > 0;
            } catch (Exception ex) { if (id == generation && pageGeneration == searchGeneration) { selected = []; grid.ItemsSource = null; summary.Text = "تعذّرت قراءة القيود؛ لا تعتمد النتائج السابقة."; filtering = false; Ui.Message(this, "تعذّرت تصفية الأمانات.\n" + ex.Message); } }
        }
        month.SelectionChanged += async (_, _) => await Filter(false); status.SelectionChanged += async (_, _) => await Filter(false); search.TextChanged += async (_, _) => await Filter(true);
        grid.MouseDoubleClick += async (_, _) => {
            if (grid.SelectedItem is not TrustView row) return;
            await Run(async () => {
                var fields = await Task.Run(() => {
                    var e = row.Source;
                    var result = new List<RecordField> { new("القيد", "الاسم", e.Name), new("القيد", "الرتبة", e.Rank), new("القيد", "محل العمل", e.Workplace), new("القيد", "الشهر", row.Month), new("القيد", "الحالة", e.CaseStatus), new("القيد", "حالة المصدر", e.ImportedStatus), new("القيد", "الملاحظات", e.Note), new("الرصيد", "الأمانة", row.Amount), new("الرصيد", "المصروف", row.Paid), new("الرصيد", "المتبقي", row.Remaining), new("الرصيد", "حالة الصرف", row.Status) };
                    if (e.LegacyPaid) result.Add(new("الدفعات", "صرف قديم", "محفوظ كمصروف بالكامل دون تفاصيل دفعات."));
                    for (int i = 0; i < e.Payments.Count; i++) { var p = e.Payments[i]; var label = $"الدفعة {i + 1}"; result.Add(new(label, "المبلغ", ReportSheet.Money(p.Amount))); result.Add(new(label, "رقم الكتاب", p.BookNumber)); result.Add(new(label, "تاريخ الكتاب", p.BookDate)); result.Add(new(label, "الملاحظة", p.Note)); }
                    var books = loaded.Source.Books.ToDictionary(b => b.Id);
                    foreach (var id in e.BookIds ?? []) result.Add(new("الكتب المرتبطة", id, Guid.TryParse(id, out var guid) && books.TryGetValue(guid, out var b) ? $"{b.Title} • {b.Month} • {b.Entity}" : "الكتاب مفقود"));
                    return result;
                }); RecordDetails(row.Name, fields);
            });
        };
        await Filter(false);
    }
}
