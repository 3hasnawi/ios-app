using System.IO;
using System.Windows;
using System.Windows.Controls;
using SalaryDesktop.Core;
using SalaryLocal.Core;
namespace SalaryLocal.App;

public partial class MainWindow
{
    private sealed record TrustPaymentRow(SalaryTrustPayment Source, string Amount, string BookNumber, string BookDate, string Note);
    private void AddTrustEditingActions(StackPanel panel, BackupSnapshot source, SalaryTrustLedger ledger, DataGrid grid)
    {
        panel.Children.Add(Ui.Button("إدارة القيد المحدد • الملاحظات والدفعات والكتب", () => {
            if (busy) return;
            if (grid.SelectedItem is not TrustView row) { Ui.Message(this, "اختر قيد أمانة أولاً."); return; }
            OpenTrustEditor(source, ledger, row.Source);
        }));
    }
    private async void OpenTrustEditor(BackupSnapshot source, SalaryTrustLedger ledger, SalaryTrustEntry entry)
    {
        if (busy) return;
        await Run(async () => {
            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(Ui.Text(entry.Name + " • " + SalaryTrust.MonthLabel(entry.Year, entry.Month), 23));
            panel.Children.Add(Ui.Text($"الأمانة: {ReportSheet.Money(entry.Amount)} • المصروف: {ReportSheet.Money(SalaryTrust.Paid(entry))} • المتبقي: {ReportSheet.Money(SalaryTrust.Remaining(entry))} د.ع\nالحالة: {SalaryTrust.PaymentStatus(entry)}\nالتسجيل محلي فقط، ولا ينفذ دفعاً أو تحويلاً. أصل الأمانة والصرف القديم محميان."));
            var note = Field(panel, "ملاحظات القيد", entry.Note);
            var pending = new CheckBox { Content = "قيد بانتظار المراجعة (مستبعد من المتبقي المؤكد)", IsChecked = entry.ReviewPending, IsEnabled = !entry.LegacyPaid && entry.Payments.Count == 0 }; panel.Children.Add(pending);
            var payments = RecordsGrid(("المبلغ", "Amount"), ("رقم الكتاب", "BookNumber"), ("التاريخ", "BookDate"), ("الملاحظة", "Note")); payments.Height = 190;
            payments.ItemsSource = entry.Payments.Select(p => new TrustPaymentRow(p, ReportSheet.Money(p.Amount), p.BookNumber, p.BookDate, p.Note)).ToArray(); panel.Children.Add(payments);
            var dialog = Ui.Dialog(this, "إدارة الأمانة", new ScrollViewer { Content = panel }, 850, 800);
            SalaryTrustEntry? updated = null;
            bool OtherEdits() {
                if (note.Text != entry.Note || (pending.IsChecked == true) != entry.ReviewPending) { Ui.Message(dialog, "احفظ الملاحظات وقرار المراجعة أولاً أو ألغِ تعديلهما قبل تغيير الدفعات أو الكتب."); return true; } return false;
            }
            void Accept(SalaryTrustEntry candidate, string question) {
                try { _ = LocalSalaryTrust.Patch(source, candidate);
                    if (Ui.Message(dialog, question, true) != MessageBoxResult.Yes) return;
                    updated = candidate; dialog.Close();
                } catch (Exception ex) { Ui.Message(dialog, ex.Message); }
            }
            panel.Children.Add(Ui.Button("حفظ الملاحظات وقرار المراجعة", () => Accept(entry with { Note = note.Text, ReviewPending = pending.IsChecked == true }, "حفظ الملاحظات وقرار المراجعة؟ تغيير المراجعة يغيّر إدراج القيد في المتبقي المؤكد، دون تسجيل صرف.")));
            void Payment(bool edit) {
                if (OtherEdits()) return;
                if (entry.ReviewPending || entry.LegacyPaid) { Ui.Message(dialog, "لا يمكن تسجيل دفعة لقيد المراجعة أو الصرف القديم. احفظ قرار المراجعة أولاً إن كان مناسباً."); return; }
                var original = edit ? (payments.SelectedItem as TrustPaymentRow)?.Source : null;
                if (edit && original is null) { Ui.Message(dialog, "اختر دفعة أولاً."); return; }
                var value = EnterTrustPayment(dialog, entry, original); if (value is null) return;
                var candidate = entry with { Payments = edit ? entry.Payments.Select(p => p.Id == value.Id ? value : p).ToArray() : entry.Payments.Concat([value]).ToArray() };
                Accept(candidate, $"حفظ {(edit ? "تصحيح" : "إضافة")} الدفعة؟\nالمصروف: {ReportSheet.Money(SalaryTrust.Paid(entry))} ← {ReportSheet.Money(SalaryTrust.Paid(candidate))}\nالمتبقي: {ReportSheet.Money(SalaryTrust.Remaining(entry))} ← {ReportSheet.Money(SalaryTrust.Remaining(candidate))} د.ع\nهذا تسجيل محلي فقط.");
            }
            panel.Children.Add(Ui.Button("إضافة دفعة جزئية", () => Payment(false)));
            panel.Children.Add(Ui.Button("تصحيح الدفعة المحددة", () => Payment(true)));
            panel.Children.Add(Ui.Button("حذف الدفعة المحددة", () => {
                if (OtherEdits()) return;
                if (payments.SelectedItem is not TrustPaymentRow row) { Ui.Message(dialog, "اختر دفعة أولاً."); return; }
                var candidate = entry with { Payments = entry.Payments.Where(p => p.Id != row.Source.Id).ToArray() };
                Accept(candidate, $"حذف دفعة {row.Amount} د.ع من التسجيل المحلي؟\nسيزيد المتبقي من {ReportSheet.Money(SalaryTrust.Remaining(entry))} إلى {ReportSheet.Money(SalaryTrust.Remaining(candidate))} د.ع. لا يلغي هذا دفعاً خارج البرنامج.");
            }));
            var books = new ComboBox { ItemsSource = source.Books, DisplayMemberPath = "Title", SelectedIndex = -1 }; panel.Children.Add(Ui.Text("اختر كتاباً موجوداً في الأرشيف للربط أو فك الربط")); panel.Children.Add(books);
            var ids = (entry.BookIds ?? []).Select(Guid.Parse).ToHashSet();
            panel.Children.Add(Ui.Text("الكتب المرتبطة: " + string.Join("، ", source.Books.Where(b => ids.Contains(b.Id)).Select(b => b.Title))));
            panel.Children.Add(Ui.Button("ربط / فك ربط الكتاب المحدد", () => {
                if (OtherEdits()) return;
                if (books.SelectedItem is not ArchiveBook book) { Ui.Message(dialog, "اختر كتاباً أولاً."); return; }
                bool linked = ids.Contains(book.Id);
                var links = linked ? (entry.BookIds ?? []).Where(id => Guid.Parse(id) != book.Id).ToArray() : (entry.BookIds ?? []).Concat([book.Id.ToString()]).ToArray();
                Accept(entry with { BookIds = links }, $"{(linked ? "فك ربط" : "ربط")} الكتاب «{book.Title}»؟ لا يتغير الرصيد أو ملف الكتاب.");
            }));
            panel.Children.Add(Ui.Button("إلغاء / إغلاق دون حفظ", dialog.Close)); dialog.ShowDialog();
            if (updated is not null) {
                var result = await Task.Run(() => workspace.Store.SaveSalaryTrustEntry(updated, source.SourceDigest));
                await LoadMonth(); Ui.Message(this, result.Changed ? "حُفظ تعديل الأمانة محلياً مع الاحتفاظ بالمراجعة السابقة." : "لا توجد تغييرات للحفظ.");
            }
        });
    }
    private SalaryTrustPayment? EnterTrustPayment(Window owner, SalaryTrustEntry entry, SalaryTrustPayment? original)
    {
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(Ui.Text(original is null ? "إضافة دفعة محلية" : "تصحيح دفعة محلية", 22));
        var amount = Field(panel, "المبلغ / دينار عراقي", original?.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "");
        var number = Field(panel, "رقم الكتاب (اختياري)", original?.BookNumber ?? "");
        var date = Field(panel, "تاريخ الكتاب yyyy-MM-dd (اختياري)", original?.BookDate ?? "");
        var note = Field(panel, "الملاحظة", original?.Note ?? "");
        var dialog = Ui.Dialog(owner, "بيانات الدفعة", panel, 600, 570); SalaryTrustPayment? result = null;
        panel.Children.Add(Ui.Button("مراجعة الدفعة", () => { try {
            var p = new SalaryTrustPayment(original?.Id ?? Guid.NewGuid().ToString(), checked((long)WholeAmount(amount.Text)), number.Text.Trim(), date.Text.Trim(), note.Text);
            var candidate = entry with { Payments = original is null ? entry.Payments.Concat([p]).ToArray() : entry.Payments.Select(x => x.Id == p.Id ? p : x).ToArray() };
            SalaryTrust.Validate(new(1, [candidate], false)); result = p; dialog.Close();
        } catch (Exception ex) { Ui.Message(dialog, ex.Message); } }));
        panel.Children.Add(Ui.Button("إلغاء", dialog.Close)); dialog.ShowDialog(); return result;
    }
}
