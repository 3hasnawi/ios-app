using System.Windows;
using System.Windows.Controls;
using SalaryDesktop.Core;
namespace SalaryLocal.App;
public partial class MainWindow
{
    private async Task Accounts()
    {
        var source = await Task.Run(() => workspace.Store.GetSnapshot()); Page.Children.Add(Ui.Text("حسابات جهات التنفيذ", 25));
        Page.Children.Add(Ui.Text($"{source.Accounts.Count:N0} حساب محفوظ في مساحة العمل الحالية. الاستيراد يُضيف ويُحدّث دون حذف الجهات الغائبة. فحص IBAN لا يثبت ملكية الحساب."));
        Page.Children.Add(Ui.Button("استيراد ملف Excel للحسابات", () => ImportClick(this, new RoutedEventArgs())));
        var search = new TextBox { Margin = new Thickness(0, 12, 0, 12), ToolTip = "ابحث باسم الجهة أو الفرع أو الحساب" }; Page.Children.Add(search);
        var grid = GridFor(("الجهة", "Entity"), ("IBAN", "Iban"), ("الحساب المحلي", "LocalAccount"), ("الفرع", "Branch"), ("SWIFT", "Swift")); grid.ItemsSource = source.Accounts; grid.Height = 460; Page.Children.Add(grid);
        var generation = 0; var renderGeneration = searchGeneration;
        search.TextChanged += async (_, _) => { var id = ++generation; var query = PayrollAnalysis.Normalize(search.Text); await Task.Delay(180); if (id != generation || renderGeneration != searchGeneration) return;
            var rows = await Task.Run(() => source.Accounts.Where(a => PayrollAnalysis.Normalize(a.Entity + " " + a.Branch + " " + a.Iban + " " + a.LocalAccount).Contains(query, StringComparison.Ordinal)).ToArray()); if (id == generation && renderGeneration == searchGeneration) grid.ItemsSource = rows;
        };
        Page.Children.Add(Ui.Button("تصدير الحسابات المعروضة • Excel / PDF", () => {
            var shown = grid.Items.Cast<ExecutionAccount>().ToArray(); var sheet = new ReportSheet(ReportSheet.HeaderFor(source), "حسابات جهات التنفيذ",
                new[] { new ReportColumn("الجهة", 34, ReportColumnKind.Text), new ReportColumn("IBAN", 38, ReportColumnKind.Text), new ReportColumn("الحساب المحلي", 24, ReportColumnKind.Text), new ReportColumn("الفرع", 24, ReportColumnKind.Text), new ReportColumn("SWIFT", 18, ReportColumnKind.Text) },
                shown.Select(a => new[] { a.Entity, a.Iban, a.LocalAccount ?? "", a.Branch, a.Swift }).ToArray(), [], ShowTotal: false);
            SaveExcel(() => XlsxReport.Generate(sheet), "حسابات جهات التنفيذ", () => Pdf(sheet));
        }));
        Page.Children.Add(Ui.Text("الملف المصدّر يحوي حسابات مالية غير مشفرة. احفظه على وسيط محمي ولا تشاركه إلا مع المخوّل."));
    }
}
