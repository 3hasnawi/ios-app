using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SalaryDesktop.Core;
using SalaryLocal.Core;

namespace SalaryLocal.App;

public partial class MainWindow
{
    private void AddArchiveEditingActions(StackPanel panel, BackupSnapshot source, DataGrid grid)
    {
        panel.Children.Add(Ui.Text("إدارة محلية: الإصدارات القديمة وملفات المصدر تبقى محفوظة. لا يمكن حذف كتاب مرتبط بالأمانات."));
        panel.Children.Add(Ui.Button("إضافة كتاب PDF", async () => await Run(() => EditArchive(source, null))));
        AddArchiveImageActions(panel, source, grid);
        panel.Children.Add(Ui.Button("تعديل الاسم والشهر والجهة", async () => await Run(async () => {
            if (grid.SelectedItem is not ArchiveView row) { Ui.Message(this, "اختر كتاباً أولاً."); return; }
            await EditArchive(source, row.Source);
        })));
        panel.Children.Add(Ui.Button("استبدال PDF مع إبقاء هوية الكتاب", async () => await Run(async () => {
            if (grid.SelectedItem is not ArchiveView row) { Ui.Message(this, "اختر كتاباً أولاً."); return; }
            var pdf = await PickArchivePdf(); if (pdf is null) return;
            int pages = ArchivePdf.Pages(pdf);
            if (Ui.Message(this, $"استبدال ملف «{row.Title}»؟\nالصفحات: {row.Pages} ← {pages}\nتبقى هوية الكتاب وروابط الأمانات وتاريخ إنشائه. يُحفظ الملف السابق في سجل المراجعات.", true) != MessageBoxResult.Yes) return;
            await Task.Run(() => workspace.Store.ReplaceArchivePdf(row.Source.Id, pdf, source.SourceDigest)); await LoadMonth();
        })));
        panel.Children.Add(Ui.Button("حذف الكتاب المحدد من الأرشيف الحالي", async () => await Run(async () => {
            if (grid.SelectedItem is not ArchiveView row) { Ui.Message(this, "اختر كتاباً أولاً."); return; }
            if (Ui.Message(this, $"حذف «{row.Title}» من الأرشيف الحالي؟\nتبقى النسخ التاريخية. يُرفض الحذف إذا كان مرتبطاً بأي قيد أمانات.", true) != MessageBoxResult.Yes) return;
            await Task.Run(() => workspace.Store.DeleteArchiveBook(row.Source.Id, source.SourceDigest)); await LoadMonth();
        })));
    }
    private async Task<byte[]?> PickArchivePdf()
    {
        var picker = new OpenFileDialog { Filter = "PDF (*.pdf)|*.pdf", CheckFileExists = true, Multiselect = false };
        if (Ui.ShowNativeDialog(() => picker.ShowDialog(this)) != true) return null;
        var bytes = await Task.Run(() => {
            using var file = new FileStream(picker.FileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is < 5 or > ArchivePdf.MaxBytes) throw new InvalidDataException("حجم PDF غير صالح؛ الحد الأقصى ٦٠ ميغابايت.");
            var data = new byte[checked((int)file.Length)]; file.ReadExactly(data); _ = ArchivePdf.Pages(data); return data;
        });
        return bytes;
    }
    private async Task EditArchive(BackupSnapshot source, ArchiveBook? book)
    {
        byte[]? pdf = book is null ? await PickArchivePdf() : null;
        if (book is null && pdf is null) return;
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(Ui.Text("راجع بيانات الكتاب قبل الحفظ المحلي. ملف المصدر لا يتغير."));
        var title = Field(panel, "اسم واضح للكتاب", book?.Title ?? "");
        var month = Field(panel, "الشهر", book?.Month ?? "");
        var entity = Field(panel, "الجهة (اختياري)", book?.Entity ?? "");
        panel.Children.Add(Ui.Text($"عدد الصفحات: {book?.Pages ?? ArchivePdf.Pages(pdf!)}"));
        var dialog = Ui.Dialog(this, book is null ? "إضافة كتاب" : "تعديل الكتاب", new ScrollViewer { Content = panel }, 650, 590);
        bool accepted = false;
        panel.Children.Add(Ui.Button("مراجعة وحفظ", () => {
            if (string.IsNullOrWhiteSpace(title.Text) || string.IsNullOrWhiteSpace(month.Text)) { Ui.Message(dialog, "أدخل اسم الكتاب والشهر."); return; }
            if (Ui.Message(dialog, $"حفظ البيانات التالية؟\nالكتاب: {title.Text.Trim()}\nالشهر: {month.Text.Trim()}\nالجهة: {entity.Text.Trim()}", true) != MessageBoxResult.Yes) return;
            accepted = true; dialog.Close();
        }));
        panel.Children.Add(Ui.Button("إلغاء", () => dialog.Close())); dialog.ShowDialog(); if (!accepted) return;
        string name = title.Text, period = month.Text, party = entity.Text;
        await Task.Run(() => book is null ? workspace.Store.AddArchiveBook(name, period, party, pdf!, source.SourceDigest) : workspace.Store.EditArchiveBook(book.Id, name, period, party, source.SourceDigest));
        await LoadMonth();
    }
}
