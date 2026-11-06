using System.Windows;
using System.Windows.Controls;
using SalaryDesktop.Core;
using SalaryLocal.Core;
namespace SalaryLocal.App;
public partial class MainWindow
{
    // يُستدعى من مراجعة الكتاب داخل حارس العمل، ولا يشغّل حارساً متداخلاً.
    private async Task<bool> OpenSingleIssuance(BackupSnapshot source, LocalLetterPreview preview, Window owner)
    {
        LetterCurrent(source);
        var review = await Task.Run(() => LocalTransferIssuance.Review(source, preview));
        var panel = new StackPanel { Margin = new Thickness(24) }; panel.Children.Add(Ui.Text("إقرار إصدار يدوي • " + preview.Title, 23));
        panel.Children.Add(Ui.Text(preview.Month + " • " + ReportSheet.Money(preview.Total) + " د.ع"));
        panel.Children.Add(Ui.Text("هذا سجل لكتاب أُصدر فعلاً خارج التطبيق. لا يحوّل الأموال ولا يثبت استلام المصرف أو مطابقة الملف الصادر."));
        foreach (var change in review.Changes) panel.Children.Add(Ui.Text("⚠ " + change));
        var note = Field(panel, "ملاحظة الإقرار (اختيارية)", "");
        var consent = new CheckBox { Content = Ui.Text("أصدرت الكتاب فعلاً خارج التطبيق وراجعت الأسماء والمبالغ والحسابات والبنود وتنبيهات التكرار والتغيّر.") }; panel.Children.Add(consent);
        var dialog = Ui.Dialog(owner, "مراجعة إقرار الإصدار", new ScrollViewer { Content = panel }, 850, 650); bool accepted = false;
        panel.Children.Add(Ui.Button("تسجيل الإقرار اليدوي", () => {
            if (consent.IsChecked != true) { Ui.Message(dialog, "يلزم إقرار الإصدار الفعلي والمراجعة أولاً."); return; }
            if (Ui.Message(dialog, "تسجيل هذا الإقرار المحلي؟ لا تُرسل أموال أو ملفات إلى المصرف.", true) != MessageBoxResult.Yes) return;
            accepted = true; dialog.Close();
        }));
        panel.Children.Add(Ui.Button("إلغاء", dialog.Close)); dialog.ShowDialog(); if (!accepted) return false;
        var text = note.Text; await Task.Run(() => workspace.Store.ConfirmTransferLetterSingle(preview, text)); return true;
    }
    private async void ReviewTransferIssuance(BackupSnapshot source)
    {
        if (busy) return;
        await Run(async () => {
            LetterCurrent(source); var records = await Task.Run(() => LocalTransferIssuance.Records(source));
            var panel = new StackPanel { Margin = new Thickness(24) }; panel.Children.Add(Ui.Text("سجل إقرارات الإصدار", 24));
            panel.Children.Add(Ui.Text("إقرارات يدوية فقط. راجع التغيّرات من معاينة الكتاب الحالية. إلغاء الإقرار لا يحذف الكتاب أو قيود الشهر، وتبقى النسخة السابقة في التاريخ المحلي."));
            var grid = GridFor(("الشهر", "Month"), ("الكتاب", "Book"), ("المبلغ", "Total"), ("الملاحظة", "Note")); grid.ItemsSource = records; grid.Height = 370; panel.Children.Add(grid);
            var dialog = Ui.Dialog(this, "سجل الإقرار اليدوي", panel, 1000, 630); string? id = null;
            panel.Children.Add(Ui.Button("إلغاء الإقرار المحدد", () => {
                if (grid.SelectedItem is not LocalIssuanceRecord selected) { Ui.Message(dialog, "اختر إقراراً أولاً."); return; }
                if (Ui.Message(dialog, "إلغاء إقرار " + selected.Book + " عن " + selected.Month + " فقط؟ لا يُحذف الكتاب أو أي مبلغ، وقد يعود إقرار المجموعة السابق هو الأحدث.", true) != MessageBoxResult.Yes) return;
                id = selected.Id; dialog.Close();
            }));
            panel.Children.Add(Ui.Button("إغلاق", dialog.Close)); dialog.ShowDialog(); if (id is null) return;
            await Task.Run(() => workspace.Store.RevokeTransferIssuance(id, source.SourceDigest)); await LoadMonth();
        });
    }
}
