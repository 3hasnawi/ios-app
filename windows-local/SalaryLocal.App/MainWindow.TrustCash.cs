using System.IO;
using System.Windows;
using System.Windows.Controls;
using SalaryDesktop.Core;
using SalaryLocal.Core;

namespace SalaryLocal.App;

public partial class MainWindow
{
    private void AddMonthlyCashAction(StackPanel panel, BackupSnapshot source, SalaryTrustLedger ledger)
    {
        panel.Children.Add(Ui.Text("إضافة رواتب النقد الشهرية إلى الأمانات", 18));
        var names = source.Months.Where(m => m.EmployeeCount > 0 && MonthNames.SortKey(m.Name) > SalaryTrust.OpeningCutoff)
            .OrderBy(m => MonthNames.SortKey(m.Name)).Select(m => m.Name).ToArray();
        var month = new ComboBox { ItemsSource = names, SelectedIndex = names.Length > 0 ? 0 : -1 };
        if (names.Contains(Month, StringComparer.Ordinal)) month.SelectedItem = Month;
        panel.Children.Add(month);
        var button = Ui.Button("معاينة كاش الشهر وإضافته للأمانات", () => {
            if (busy) return;
            if (month.SelectedItem is not string selected) { Ui.Message(this, "اختر شهر رواتب محفوظاً بعد أيلول 2026."); return; }
            ReviewMonthlyCash(source, selected);
        });
        button.IsEnabled = ledger.OpeningImported && names.Length > 0;
        panel.Children.Add(button);
        panel.Children.Add(Ui.Text(!ledger.OpeningImported
            ? "استورد الافتتاحي أولاً. لا تُضاف رواتب الكاش إلى الدفتر قبل مراجعتها هنا."
            : "استيراد الرواتب وحده لا يضيف الأمانات. راجع كاش كل شهر هنا؛ لا يتكرر القيد المطابق ولا يُستبدل قيد تغير مبلغه أو حالته."));
    }

    private async void ReviewMonthlyCash(BackupSnapshot source, string month)
    {
        if (busy) return;
        await Run(async () => {
            var preview = await Task.Run(() => LocalMonthlyCashTrust.Prepare(source, month));
            if (preview.Additions.Count == 0) { Ui.Message(this, "جميع رواتب الكاش لهذا الشهر مسجلة بصورة مطابقة؛ لم تُضف قيود أو مراجعات مكررة."); return; }
            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(Ui.Text("مراجعة أمانات كاش • " + month, 23));
            panel.Children.Add(Ui.Text($"{preview.Additions.Count:N0} قيد جديد • المجموع: {ReportSheet.Money(preview.Total)} د.ع\n{preview.ExistingCount:N0} قيد سابق مطابق سيبقى دون تغيير • كاش الشهر كاملاً: {ReportSheet.Money(preview.CashTotal)} د.ع"));
            panel.Children.Add(Ui.Text("ستُضاف القيود الجديدة كأمانات غير مصروفة. تبقى القيود والدفعات السابقة دون تغيير. هذا تسجيل محلي، ولا ينفّذ دفعاً أو تحويلاً."));
            var grid = RecordsGrid(("الاسم", "Name"), ("الرتبة", "Rank"), ("محل العمل", "Workplace"), ("الحالة", "CaseStatus"), ("الأمانة", "Amount"));
            grid.Height = 330;
            grid.ItemsSource = preview.Additions.Take(200).Select(e => new { e.Name, e.Rank, e.Workplace, e.CaseStatus, Amount = ReportSheet.Money(e.Amount) }).ToArray();
            panel.Children.Add(grid);
            if (preview.Additions.Count > 200) panel.Children.Add(Ui.Text("المعاينة تعرض أول 200 قيد؛ الحفظ يشمل جميع القيود الجديدة المفحوصة."));
            var dialog = Ui.Dialog(this, "مراجعة الأمانات الشهرية", new ScrollViewer { Content = panel }, 1000, 760);
            bool accepted = false;
            panel.Children.Add(Ui.Button("تأكيد إضافة الأمانات المعروضة", () => {
                if (Ui.Message(dialog, $"إضافة {preview.Additions.Count:N0} قيد عن {month} بمجموع {ReportSheet.Money(preview.Total)} د.ع؟\nلا تُحذف أو تُعدّل أي قيود أو دفعات سابقة.", true) != MessageBoxResult.Yes) return;
                accepted = true; dialog.Close();
            }));
            panel.Children.Add(Ui.Button("إلغاء دون تغيير", dialog.Close)); dialog.ShowDialog();
            if (!accepted) return;
            var result = await Task.Run(() => workspace.Store.SaveMonthlyCashTrust(preview));
            await LoadMonth();
            Ui.Message(this, result.Changed ? "حُفظت أمانات كاش الشهر مع الاحتفاظ بالدفتر السابق. أنشئ نسخة محلية قبل النقل." : "القيود مطابقة للمحفوظ؛ لم تُضف مراجعة مكررة.");
        });
    }
}
