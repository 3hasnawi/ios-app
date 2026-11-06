using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SalaryDesktop.Core;
namespace SalaryLocal.App;
public partial class MainWindow
{
    private async Task Comparison()
    {
        var months = await Task.Run(() => workspace.Store.GetMonths().OrderByDescending(m => MonthNames.SortKey(m.Name)).ToArray());
        Page.Children.Add(Ui.Text("مقارنة الاستقطاعات", 25)); Page.Children.Add(Ui.Text("مقارنة مجموع الشخص أو الشخص والجهة والإضبارة. تُجمع القيود المكررة دون حذف، والربط بالأسماء يحتاج مراجعة الهوية."));
        if (months.Length < 2) { Page.Children.Add(Ui.Text("يلزم شهران محفوظان على الأقل.")); return; }
        var selectors = new WrapPanel(); var from = new ComboBox { ItemsSource = months, DisplayMemberPath = "Name", SelectedIndex = 1, Margin = new Thickness(4) }; var to = new ComboBox { ItemsSource = months, DisplayMemberPath = "Name", SelectedIndex = 0, Margin = new Thickness(4) };
        selectors.Children.Add(Ui.Text("من")); selectors.Children.Add(from); selectors.Children.Add(Ui.Text("إلى")); selectors.Children.Add(to); Page.Children.Add(selectors);
        var level = new ComboBox { ItemsSource = new[] { "حسب الشخص والجهة والإضبارة", "مجموع الشخص" }, SelectedIndex = 0, Margin = new Thickness(4) }; var kind = new ComboBox { ItemsSource = new[] { "كل الحالات", "بدأ", "توقف", "زاد", "نقص", "ثابت" }, SelectedIndex = 0, Margin = new Thickness(4) }; var filters = new WrapPanel(); filters.Children.Add(level); filters.Children.Add(kind); Page.Children.Add(filters);
        var search = new TextBox { ToolTip = "ابحث في الاسم والجهة والإضبارة", Margin = new Thickness(4, 8, 4, 8) }; Page.Children.Add(search);
        var summary = Ui.Text("اختر شهرين ثم اضغط المقارنة."); Page.Children.Add(summary);
        var grid = GridFor(("الاسم", "Name"), ("الجهة", "Entity"), ("الإضبارة", "Note"), ("السابق", "Before"), ("الحالي", "After"), ("الفرق", "Difference"), ("الحالة", "Status")); grid.Height = 400; Page.Children.Add(grid);
        DeductionComparisonResult? result = null; string? revision = null; var generation = 0; var renderGeneration = searchGeneration;
        var export = Ui.Button("تصدير نتائج الفلتر • Excel / PDF", () => {
            if (result is null || revision is null || grid.Items.Count == 0) { Ui.Message(this, "نفذ المقارنة أولاً."); return; }
            var entries = grid.Items.Cast<DeductionChange>().ToArray(); var compared = result; var expected = revision; var grouping = (string)level.SelectedItem; var selectedKind = (string)kind.SelectedItem;
            var columns = new[] { new ReportColumn("الاسم", 26, ReportColumnKind.Text), new ReportColumn("الجهة", 20, ReportColumnKind.Text), new ReportColumn("الإضبارة", 18, ReportColumnKind.Text), new ReportColumn("السابق", 14, ReportColumnKind.Text), new ReportColumn("الحالي", 14, ReportColumnKind.Text), new ReportColumn("الفرق", 14, ReportColumnKind.Text), new ReportColumn("الحالة", 12, ReportColumnKind.Text) };
            string Exact(decimal n) => n.ToString("0.############################", CultureInfo.InvariantCulture);
            var sheet = new ReportSheet(snapshot is null ? ReportSheet.DefaultHeader : ReportSheet.HeaderFor(snapshot), $"مقارنة الاستقطاعات • {compared.From} - {compared.To} • {grouping} • {selectedKind}", columns, entries.Select(x => new[] { x.Name, x.Entity, x.Note, Exact(x.Before), Exact(x.After), Exact(x.Difference), x.Status }).ToArray(), [], ShowTotal: false);
            void Current() { if (workspace.Store.GetStatus().RevisionId != expected) throw new System.IO.InvalidDataException("تغيّرت البيانات؛ أعد المقارنة قبل التصدير."); }
            SaveExcel(() => { Current(); return DeductionComparisonReport.Excel(sheet, entries); }, "مقارنة الاستقطاعات", () => { Current(); return Pdf(sheet); });
        }); export.IsEnabled = false;
        async Task Filter()
        {
            var id = ++generation; var r = result; var byPerson = level.SelectedIndex == 1; var selectedKind = kind.SelectedItem?.ToString(); var query = PayrollAnalysis.Normalize(search.Text); export.IsEnabled = false;
            if (r is null) { grid.ItemsSource = null; return; }
            var displayed = await Task.Run(() => (byPerson ? r.People : r.Entries).Where(x => (selectedKind == "كل الحالات" || x.Status == selectedKind) && PayrollAnalysis.Normalize(x.Name + " " + x.Entity + " " + x.Note).Contains(query, StringComparison.Ordinal)).ToArray());
            if (id != generation || searchGeneration != renderGeneration) return; grid.ItemsSource = displayed; summary.Text = $"{displayed.Length:N0} نتيجة في الفلتر • السابق {ReportSheet.Money(displayed.Sum(x => x.Before))} • الحالي {ReportSheet.Money(displayed.Sum(x => x.After))} د.ع"; export.IsEnabled = displayed.Length > 0;
        }
        void Invalidate() { ++generation; result = null; revision = null; grid.ItemsSource = null; summary.Text = "تغيّر اختيار الأشهر؛ أعد المقارنة."; export.IsEnabled = false; }
        from.SelectionChanged += (_, _) => Invalidate(); to.SelectionChanged += (_, _) => Invalidate(); level.SelectionChanged += async (_, _) => await Filter(); kind.SelectionChanged += async (_, _) => await Filter();
        search.TextChanged += async (_, _) => { var id = ++generation; export.IsEnabled = false; await Task.Delay(180); if (id == generation) await Filter(); };
        Page.Children.Add(Ui.Button("مقارنة الشهرين", async () => {
            if (busy || from.SelectedItem is not SalaryLocal.Core.StoredMonth first || to.SelectedItem is not SalaryLocal.Core.StoredMonth second) return;
            await Run(async () => { var source = await Task.Run(() => workspace.Store.GetSnapshot(first.Name, second.Name));
                var empty = !source.Deductions.Any(d => d.Month == first.Name && !PayrollAnalysis.IsInactive(d)) || !source.Deductions.Any(d => d.Month == second.Name && !PayrollAnalysis.IsInactive(d));
                if (empty && Ui.Message(this, "أحد الشهرين بلا قيود فعالة. هل تؤكد خلوه لأغراض هذه المقارنة؟ لا يعني ذلك اكتمال الاستيراد.", true) != MessageBoxResult.Yes) return;
                result = await Task.Run(() => DeductionComparison.Compare(source, first.Name, second.Name, empty)); revision = source.SourceDigest; await Filter();
            });
        })); Page.Children.Add(export);
        Page.Children.Add(Ui.Text("القيود الناقصة أو السالبة تمنع المقارنة. الحالات غير الفعالة لا تدخل المجاميع. حالات التدقيق الأخرى ومطابقة التنفيذ مع الرواتب متاحة في تدقيق الرواتب وكشف البنك."));
    }
}
