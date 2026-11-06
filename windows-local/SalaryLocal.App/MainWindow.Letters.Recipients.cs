using System.Windows;
using System.Windows.Controls;
using SalaryDesktop.Core;
using SalaryLocal.Core;
namespace SalaryLocal.App;
public partial class MainWindow
{
    private async void EditTransferRecipients(BackupSnapshot source, TransferSnapshot transfer)
    {
        if (busy) return;
        await Run(async () => {
            LetterCurrent(source);
            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(Ui.Text("حساب مستفيد الكتاب", 24));
            panel.Children.Add(Ui.Text("لا يُنشئ البرنامج حساباً ولا يخمّنه. يجب أن يطابق حساب الجهة المحفوظ، وأن يبقى مرتبطاً بالمرجع الذي راجعته. كتب المجموعات وفرع الأنبار لا تقبل هذا التخصيص."));
            var books = new ComboBox { ItemsSource = LocalTransferLetterSettings.Choices.Where(c => !LocalTransferLetterSettings.IsGroup(c.Id) && c.Id != "rafidain_anbar").Select(c => new LetterChoice(c.Id, c.Title)).ToArray(), DisplayMemberPath = "Title", SelectedIndex = 0 };
            panel.Children.Add(books);
            panel.Children.Add(Ui.Text("للرمادي والإسكان والموارد المائية: اختر جهة الاستقطاع صراحة"));
            var entries = new ComboBox { ItemsSource = transfer.Entries.Where(e => e.Group == TransferGroup.Anbar).ToArray(), DisplayMemberPath = "Title" };
            panel.Children.Add(entries);
            var choose = Ui.Dialog(this, "اختيار مستفيد الكتاب", panel, 760, 440); string? id = null, entryId = null;
            panel.Children.Add(Ui.Button("فتح مراجعة الحساب", () => { id = ((LetterChoice)books.SelectedItem).Id; entryId = (entries.SelectedItem as TransferEntry)?.Id; choose.Close(); }));
            panel.Children.Add(Ui.Button("إلغاء", choose.Close)); choose.ShowDialog(); if (id is null) return;
            var review = await Task.Run(() => LocalTransferRecipients.Review(source, transfer.Month, id, entryId));
            var edit = new StackPanel { Margin = new Thickness(24) }; edit.Children.Add(Ui.Text(LocalTransferLetterSettings.Title(id), 24));
            edit.Children.Add(Ui.Text("المصدر: " + review.Source + "\nحساب المصدر: " + review.Original + "\nحساب المرجع: " + review.Reference));
            var account = Field(edit, "الحساب المعتمد لهذا الكتاب", review.Current); account.FlowDirection = FlowDirection.LeftToRight;
            edit.Children.Add(Ui.Text("الحساب المحلي للإسكان والموارد المائية 15 خانة. بقية الكتب تتطلب IBAN صالحاً ومن المصرف نفسه. تغيير مرجع الكتاب أو حساب الجهة يستلزم مراجعة جديدة."));
            var dialog = Ui.Dialog(this, "مراجعة حساب المستفيد", edit, 820, 570); bool save = false; string? chosen = null;
            void Accept(bool reset)
            {
                try {
                    chosen = reset ? null : account.Text;
                    _ = LocalTransferRecipients.Patch(source, review, chosen);
                    if (Ui.Message(dialog, reset ? "إلغاء تخصيص الحساب لهذا الكتاب فقط؟ ستعود قواعد المصدر والمرجع." : "حفظ الحساب الذي أدخلته لهذا الكتاب بعد مراجعته؟ لا يغيّر حساب الجهة ولا ينفذ تحويلاً.", true) != MessageBoxResult.Yes) return;
                    save = true; dialog.Close();
                } catch (Exception ex) { Ui.Message(dialog, ex.Message); }
            }
            edit.Children.Add(Ui.Button("مراجعة وحفظ", () => Accept(false))); edit.Children.Add(Ui.Button("إلغاء التخصيص لهذا الكتاب", () => Accept(true)));
            edit.Children.Add(Ui.Button("إغلاق دون حفظ", dialog.Close)); dialog.ShowDialog(); if (!save) return;
            await Task.Run(() => workspace.Store.SaveTransferRecipient(review, chosen)); await LoadMonth();
            Ui.Message(this, "حُفظت مراجعة الحساب محلياً. افتح الكتاب مجدداً؛ لم تُغيّر بقية الحسابات.");
        });
    }
}
