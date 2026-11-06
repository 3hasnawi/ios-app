using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using SalaryDesktop.Core;
namespace SalaryLocal.App;
public partial class MainWindow
{
    private static ImportCandidate DetectExcel(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > 100 * 1024 * 1024) throw new InvalidDataException("حجم ملف Excel غير مدعوم.");
        using (var archive = new ZipArchive(input, ZipArchiveMode.Read, true))
        {
            long size = 0; var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in archive.Entries) if (!names.Add(entry.FullName) || entry.Length > 512 * 1024 * 1024 || (size += entry.Length) > 1_000_000_000) throw new InvalidDataException("ملف Excel مكرر المحتوى أو يتجاوز الحجم المدعوم.");
        }
        return ImportDetector.Detect(path, null);
    }
    private static (T Preview, string Digest) ReadVerified<T>(string path, Func<T> read)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > 100 * 1024 * 1024) throw new InvalidDataException("حجم الملف غير مدعوم.");
        var digest = Convert.ToHexString(SHA256.HashData(input)); var preview = read(); input.Position = 0;
        if (digest != Convert.ToHexString(SHA256.HashData(input))) throw new InvalidDataException("تغيّر ملف Excel أثناء المراجعة؛ أعد اختياره.");
        return (preview, digest);
    }
    private async Task ImportAnnual(string path)
    {
        var source = await Task.Run(() => workspace.Store.GetSnapshot());
        var read = await Task.Run(() => ReadVerified(path, () => AnnualDeductionSpreadsheet.Read(path, source)));
        var preview = read.Preview; var chosen = new Dictionary<string, bool>(StringComparer.Ordinal);
        var dock = new DockPanel { Margin = new Thickness(22) }; var header = new StackPanel();
        header.Children.Add(Ui.Text("مراجعة الاستقطاعات السنوية", 24));
        header.Children.Add(Ui.Text($"{preview.Months.Count:N0} شهر • {preview.Months.Sum(m => m.Deductions.Count):N0} قيد • {preview.Problems.Count:N0} أخطاء"));
        header.Children.Add(Ui.Text("الأشهر الموجودة متروكة دون تغيير افتراضياً. تأشير شهر موجود يعني طلب استبدال استقطاعاته فقط؛ رواتبه تبقى محفوظة. كل الأشهر المختارة تُحفظ في معاملة واحدة."));
        DockPanel.SetDock(header, Dock.Top); dock.Children.Add(header);
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); dock.Children.Add(footer);
        var body = new TabControl(); dock.Children.Add(body); var choices = new StackPanel();
        var switches = new List<(PayrollImportPreview Month, bool Existing, CheckBox Check)>();
        foreach (var month in preview.Months)
        {
            var existing = source.Deductions.Count(d => d.Month == month.Month); var check = new CheckBox { Content = $"{month.Month} • {month.Deductions.Count:N0} قيد جديد • {existing:N0} محفوظ" + (existing > 0 ? " • استبدال الموجود (اختياري)" : " • إضافة شهر"), IsChecked = existing == 0, Margin = new Thickness(8, 12, 8, 12) };
            choices.Children.Add(check); switches.Add((month, existing > 0, check));
            var grid = GridFor(("الاسم", "Name"), ("الجهة", "Title"), ("المبلغ", "Amount"), ("ملاحظات / الحالة", "Note")); grid.ItemsSource = month.Deductions.Take(200);
            body.Items.Add(new TabItem { Header = month.Month, Content = grid });
        }
        body.Items.Insert(0, new TabItem { Header = "اختيار الأشهر", Content = new ScrollViewer { Content = choices, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        var problems = GridFor(("الورقة", "Sheet"), ("الصف", "Row"), ("الخطأ", "Message")); problems.ItemsSource = preview.Problems;
        body.Items.Add(new TabItem { Header = "الأخطاء", Content = problems }); body.SelectedIndex = preview.Problems.Count > 0 ? body.Items.Count - 1 : 0;
        footer.Children.Add(Ui.Text("معاينة الصفوف: أول 200 قيد لكل شهر. الحالات موقوف/هارب/محكوم تُحفظ دون إدخالها في المجموع."));
        var dialog = Ui.Dialog(this, "استيراد سنوي", dock, 1100, 740); bool accepted = false;
        var save = Ui.Button("حفظ الأشهر المختارة", () => {
            var selected = switches.Where(s => s.Check.IsChecked == true).ToArray(); if (selected.Length == 0) { Ui.Message(dialog, "اختر شهراً واحداً على الأقل."); return; }
            var replacing = selected.Where(s => s.Existing).ToArray();
            if (replacing.Length > 0 && Ui.Message(dialog, "سيُستبدل قسم الاستقطاعات لهذه الأشهر مع حفظ التاريخ السابق:\n" + string.Join("\n", replacing.Select(s => s.Month.Month)) + "\nهل تؤكد؟", true) != MessageBoxResult.Yes) return;
            foreach (var s in selected) chosen.Add(s.Month.Month, s.Existing); accepted = true; dialog.Close();
        }); save.IsEnabled = preview.Problems.Count == 0 && preview.Months.Count > 0; footer.Children.Add(save); footer.Children.Add(Ui.Button("إلغاء دون تغيير", dialog.Close)); dialog.ShowDialog();
        if (!accepted) return;
        var result = await Task.Run(() => workspace.Store.ImportAnnualDeductions(preview, chosen, Path.GetFileName(path), read.Digest, source.SourceDigest));
        await Reload(); Ui.Message(this, result.Changed ? "حُفظت الأشهر المختارة معاً. الأشهر غير المختارة لم تتغير." : "البيانات المختارة مطابقة للمحفوظ؛ لم تُضف نسخة مكررة.");
    }
    private async Task ImportAccounts(string path)
    {
        var source = await Task.Run(() => workspace.Store.GetSnapshot()); var read = await Task.Run(() => ReadVerified(path, () => AccountSpreadsheet.Read(path)));
        var preview = read.Preview; var dock = new DockPanel { Margin = new Thickness(22) }; var header = new StackPanel();
        header.Children.Add(Ui.Text("مراجعة حسابات جهات التنفيذ", 24)); header.Children.Add(Ui.Text($"{preview.Accounts.Count:N0} حساب • {preview.Problems.Count:N0} أخطاء"));
        header.Children.Add(Ui.Text("إضافة الجديد وتحديث الجهة المطابقة بالاسم فقط. لا تُحذف الجهات الغائبة. الفرع وIBAN وSWIFT الفارغة لا تمسح المحفوظ. صلاحية IBAN حسابياً لا تثبت ملكية الحساب.")); DockPanel.SetDock(header, Dock.Top); dock.Children.Add(header);
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); dock.Children.Add(footer); var tabs = new TabControl(); dock.Children.Add(tabs);
        var rows = await Task.Run(() => {
            var existing = new Dictionary<string, ExecutionAccount>(StringComparer.Ordinal);
            foreach (var account in source.Accounts) if (!existing.TryAdd(PayrollAnalysis.Normalize(account.Entity), account)) throw new InvalidDataException("توجد حسابات محفوظة متعددة بالاسم نفسه؛ راجعها قبل الدمج.");
            return preview.Accounts.Select(a => { var old = existing.GetValueOrDefault(PayrollAnalysis.Normalize(a.Entity)); return new { a.Entity, Branch = a.Branch.Length == 0 ? old?.Branch ?? "" : a.Branch, Iban = a.Iban.Length > 0 ? a.Iban : old?.Iban ?? "", Swift = a.Swift.Length > 0 ? a.Swift : old?.Swift ?? "", LocalAccount = string.IsNullOrEmpty(a.LocalAccount) ? old?.LocalAccount : a.LocalAccount, Previous = old?.Iban ?? "", Change = old is null ? "إضافة" : "تحديث الموجود" }; }).ToArray();
        });
        var grid = GridFor(("الجهة", "Entity"), ("الإجراء", "Change"), ("IBAN السابق", "Previous"), ("IBAN بعد الحفظ", "Iban"), ("الحساب المحلي", "LocalAccount"), ("الفرع", "Branch"), ("SWIFT", "Swift")); grid.ItemsSource = rows; tabs.Items.Add(new TabItem { Header = "الحسابات", Content = grid });
        var errors = GridFor(("الورقة", "Sheet"), ("الصف", "Row"), ("الخطأ", "Message")); errors.ItemsSource = preview.Problems; tabs.Items.Add(new TabItem { Header = "الأخطاء", Content = errors }); tabs.SelectedIndex = preview.Problems.Count > 0 ? 1 : 0;
        var dialog = Ui.Dialog(this, "استيراد الحسابات", dock, 1150, 700); bool accepted = false;
        var save = Ui.Button("تأكيد إضافة وتحديث الحسابات", () => { if (Ui.Message(dialog, "حفظ الحسابات المبينة وإجراء التحديثات المعروضة؟ راجع أرقام المستفيدين قبل الموافقة.", true) == MessageBoxResult.Yes) { accepted = true; dialog.Close(); } }); save.IsEnabled = preview.Problems.Count == 0 && preview.Accounts.Count > 0; footer.Children.Add(save); footer.Children.Add(Ui.Button("إلغاء", dialog.Close)); dialog.ShowDialog();
        if (!accepted) return; var result = await Task.Run(() => workspace.Store.ImportAccounts(preview, Path.GetFileName(path), read.Digest, source.SourceDigest)); await Reload(); Ui.Message(this, result.Changed ? "حُفظت الحسابات؛ الجهات الغائبة بقيت كما هي." : "الحسابات مطابقة للمحفوظ.");
    }
}
