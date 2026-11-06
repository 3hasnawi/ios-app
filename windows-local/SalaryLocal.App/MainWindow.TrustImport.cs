using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SalaryDesktop.Core;
using SalaryLocal.Core;

namespace SalaryLocal.App;

public partial class MainWindow
{
    private void AddTrustImportAction(StackPanel panel, BackupSnapshot source, SalaryTrustLedger current)
    {
        var button = Ui.Button("استيراد ملف الأمانات الافتتاحي • Excel", () => ImportTrustOpening(source, current));
        button.IsEnabled = !current.OpeningImported;
        panel.Children.Add(button);
        panel.Children.Add(Ui.Text(current.OpeningImported
            ? "الملف الافتتاحي محفوظ؛ لا يعاد استيراده فوق الدفتر. تبقى التعديلات في سجل النسخ المحلية."
            : "الافتتاحي يشمل الأشهر حتى أيلول 2026. يُعرض للمراجعة قبل الحفظ، ولا تُحذف القيود الحالية."));
    }

    private async void ImportTrustOpening(BackupSnapshot source, SalaryTrustLedger current)
    {
        if (busy) return;
        if (current.OpeningImported) { Ui.Message(this, "الملف الافتتاحي محفوظ بالفعل؛ لن يُستبدل الدفتر."); return; }
        var picker = new OpenFileDialog { Filter = "ملف الأمانات الافتتاحي (*.xlsx)|*.xlsx", CheckFileExists = true };
        if (Ui.ShowNativeDialog(() => picker.ShowDialog(this)) != true) return;
        await Run(async () => {
            var loaded = await Task.Run(() => ReadVerified(picker.FileName, () => LocalSalaryTrustSpreadsheet.Read(picker.FileName)));
            var imported = loaded.Preview;
            var merged = new SalaryTrustLedger(current.Version, current.Entries.Concat(imported.Entries).ToArray(), true);
            await Task.Run(() => { SalaryTrust.Validate(merged); SalaryTrust.ValidateBooks(merged, source.Books); });
            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(Ui.Text("مراجعة الملف الافتتاحي للأمانات", 23));
            panel.Children.Add(Ui.Text(Path.GetFileName(picker.FileName)));
            panel.Children.Add(Ui.Text($"{imported.Entries.Count:N0} قيد جديد • أصل الأمانات: {ReportSheet.Money(imported.Entries.Sum(e => e.Amount))} د.ع\nالمتبقي المؤكد: {ReportSheet.Money(SalaryTrust.Outstanding(imported.Entries))} د.ع • {imported.Entries.Count(e => e.ReviewPending):N0} قيد للمراجعة\nستبقى {current.Entries.Count:N0} قيود سابقة دون تغيير."));
            panel.Children.Add(Ui.Text("حالة «صرف» تعني مصروفاً بالكامل دون تفاصيل دفعات. حالة «قطع علاقة تقاعد» تبقى للمراجعة ومستبعدة من الرصيد المؤكد. راجع الملف والمجاميع قبل الحفظ؛ هذه ليست عملية دفع."));
            var grid = RecordsGrid(("الاسم", "Name"), ("الشهر", "Month"), ("الحالة", "State"), ("الأمانة", "Amount"), ("المتبقي", "Remaining"));
            grid.Height = 280;
            grid.ItemsSource = imported.Entries.Take(200).Select(e => new {
                e.Name, Month = SalaryTrust.MonthLabel(e.Year, e.Month), State = SalaryTrust.PaymentStatus(e),
                Amount = ReportSheet.Money(e.Amount), Remaining = e.ReviewPending ? "للمراجعة" : ReportSheet.Money(SalaryTrust.Remaining(e))
            }).ToArray();
            panel.Children.Add(grid);
            if (imported.Entries.Count > 200) panel.Children.Add(Ui.Text("المعاينة تعرض أول 200 قيد فقط؛ الحفظ يشمل جميع القيود المفحوصة."));
            var dialog = Ui.Dialog(this, "مراجعة استيراد الأمانات", new ScrollViewer { Content = panel }, 1000, 800);
            bool approved = false;
            panel.Children.Add(Ui.Button("تأكيد وحفظ الملف الافتتاحي", () => {
                if (Ui.Message(dialog, $"إضافة {imported.Entries.Count:N0} قيد أمانات من الملف المعروض؟\nلا يمكن إعادة استيراد افتتاحي آخر فوقه؛ تبقى نسخة التاريخ محفوظة.", true) != MessageBoxResult.Yes) return;
                approved = true; dialog.Close();
            }));
            panel.Children.Add(Ui.Button("إلغاء", dialog.Close)); dialog.ShowDialog();
            if (!approved) return;
            await Task.Run(() => workspace.Store.SaveTrustLedger(source, merged, "استيراد افتتاحي الأمانات: " + Path.GetFileName(picker.FileName)));
            await LoadMonth();
            Ui.Message(this, "حُفظ الملف الافتتاحي دون تعديل ملف Excel أو القيود السابقة. راجع الآن كاش الأشهر اللاحقة من هذه الصفحة لإضافته للأمانات، ثم أنشئ نسخة محلية للنقل والحماية.");
        });
    }
}
