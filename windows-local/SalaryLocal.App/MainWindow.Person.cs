using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SalaryDesktop.Core;

namespace SalaryLocal.App;

public partial class MainWindow
{
    private sealed record PersonPeriod(int Key, string Label);

    private async void ShowPerson(string name)
    {
        if (busy) return;
        BackupSnapshot? source = null;
        PersonProfile? profile = null;
        var preferredMonth = Month;
        await Run(async () =>
        {
            var loaded = await Task.Run(() =>
            {
                var data = workspace.Store.GetSnapshot();
                return (Data: data, Profile: PersonProfiles.Build(data, name, null));
            });
            source = loaded.Data; profile = loaded.Profile;
        });
        if (source is null || profile is null) return;
        try
        {
            if (profile.Months.Count == 0) { Ui.Message(this, "لا توجد سجلات لهذا الاسم."); return; }
            var data = source; var person = profile;
            var root = new DockPanel { Margin = new Thickness(22), FlowDirection = FlowDirection.RightToLeft };
            var status = Ui.Text("الربط بالاسم الموحّد فقط؛ تشابه الأسماء لا يثبت تطابق الأشخاص.");
            DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
            var body = new StackPanel();
            root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            var dialog = Ui.Dialog(this, "ملف المنتسب", root, 1040, 820);
            bool working = false, updatingPeriods = false;
            ProfileMonth? current = null;
            Payslip? currentSlip = null;
            SalaryReportData? currentReport = null;
            EmployeeDeductionStatement? statement = null;
            dialog.Closing += (_, e) => { if (working) { e.Cancel = true; Ui.Message(dialog, "انتظر انتهاء العملية قبل إغلاق الملف."); } };
            async Task Work(Func<Task> action)
            {
                if (working) return;
                working = true; body.IsEnabled = false; status.Text = "جارٍ تجهيز البيانات محلياً…";
                try { await action(); status.Text = "جاهز • الربط بالاسم فقط، راجع هوية المنتسب قبل استخدام الكشف."; }
                catch (Exception ex) { status.Text = "تعذّر إكمال العملية"; Ui.Message(dialog, "تعذّر إكمال العملية.\n" + ex.Message); }
                finally { working = false; body.IsEnabled = true; }
            }
            async Task SavePdf(string title, Func<byte[]> generate)
            {
                if (working) return;
                try
                {
                    await Work(async () => { var bytes = await Task.Run(generate); new LocalPdfPreviewWindow(dialog, title, bytes).ShowDialog(); });
                }
                catch (Exception ex) { Ui.Message(dialog, "تعذّر حفظ الملف.\n" + ex.Message); }
            }
            ReportAssets Assets() => ReportAssets.Load(Path.Combine(AppContext.BaseDirectory, "WatermarkLogo.png"));
            byte[] Logo() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "HomeLogo.png"));
            body.Children.Add(Ui.Text(person.Name, 26));
            body.Children.Add(Ui.Text("لا يُخصم تنفيذ الجهات مرة ثانية من صافي ملف الرواتب. القيم الناقصة ليست صفراً."));
            body.Children.Add(Ui.Text("شهر الراتب / التنفيذ"));
            var monthBox = new ComboBox { ItemsSource = person.Months, DisplayMemberPath = "Month", Margin = new Thickness(0, 0, 0, 14) };
            body.Children.Add(monthBox);
            var monthContent = new StackPanel(); body.Children.Add(monthContent);
            var exports = new WrapPanel(); body.Children.Add(exports);
            var salaryButton = Ui.Button("تقرير الراتب PDF", async () =>
            {
                if (currentReport is not { } report || current is not { } selected || working) return;
                await SavePdf(SalaryReports.FileName(person.Name, selected.Month), () => SalaryReportPdf.Generate([report], Assets(), Logo(), DateTime.Now));
            });
            var slipButton = Ui.Button("قسيمة الراتب PDF", async () =>
            {
                if (currentSlip is not { } slip || current?.Employee is not { } employee || working) return;
                var selectedMonth = current.Month;
                // القالب القديم لا يقسّم جدول المخصصات؛ نمنع القسيمة إذا تجاوزت مساحة الصفحة.
                var lineCount = Math.Max(1, Math.Max(slip.Allowances.Count + (slip.Gross is null ? 0 : 1), slip.Deductions.Count + (slip.TotalDeductions is null ? 0 : 1)));
                var firstPageHeight = 28 + 24 + 30 + 10 + Math.Ceiling((3 + slip.Info.Count) / 2.0) * 19 + 14 + 19 + lineCount * 19 + 14 + 19 * 1.6 + 14;
                if (firstPageHeight > 784 || slip.EntityDeductions.Count > 30) { Ui.Message(dialog, "تفاصيل القسيمة تتجاوز مساحة القالب المعتمد؛ استخدم تقرير الراتب وكشف الاستقطاعات لكل جهة بدلاً منها."); return; }
                await SavePdf("قسيمة راتب - " + person.Name + " - " + selectedMonth, () => PayslipPdf.Generate(ReportSheet.HeaderFor(data), employee.Name, employee.Rank, employee.Workplace, selectedMonth, slip, Assets(), DateTime.Now));
            });
            exports.Children.Add(salaryButton); exports.Children.Add(slipButton);
            exports.Children.Add(Ui.Button("جميع أعمدة صف المصدر", async () =>
            {
                if (working || current?.Employee is not { } employee) return;
                await Work(async () => {
                    var columns = await Task.Run(() => {
                        if (employee.RawColumns is null) throw new InvalidDataException("هذه البيانات القديمة لا تتضمن لقطة جميع الأعمدة. أعد استيراد Excel لإضافتها؛ التفاصيل المعروفة ما زالت ظاهرة.");
                        using var raw = JsonDocument.Parse(employee.RawColumns); var headers = raw.RootElement.GetProperty("headers").EnumerateArray().Select(x => x.GetString() ?? "").ToArray(); var values = raw.RootElement.GetProperty("values").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
                        if (headers.Length != values.Length) throw new InvalidDataException("أعمدة المصدر لا تطابق القيم؛ أُوقف العرض.");
                        return headers.Select((h, i) => new { Position = i + 1, Header = h, Value = values[i] }).ToArray();
                    });
                    var fields = GridFor(("موضع العمود", "Position"), ("العنوان الأصلي", "Header"), ("القيمة الأصلية", "Value")); fields.ItemsSource = columns;
                    Ui.Dialog(dialog, "أعمدة المصدر بالترتيب • الفراغات والتكرار محفوظان", fields, 960, 690).ShowDialog();
                });
            }));
            void Lines(string title, IReadOnlyList<ProfileLine> lines)
            {
                monthContent.Children.Add(Ui.Text(title, 18));
                if (lines.Count == 0) { monthContent.Children.Add(Ui.Text("لا توجد تفاصيل محفوظة لهذا القسم.")); return; }
                var grid = GridFor(("البند", "Title"), ("القيمة", "Value"), ("الملاحظة", "Note"));
                grid.ItemsSource = lines; grid.Height = Math.Min(310, 45 + lines.Count * 34); monthContent.Children.Add(grid);
            }
            async Task LoadSelectedMonth()
            {
                current = null; currentSlip = null; currentReport = null;
                salaryButton.IsEnabled = slipButton.IsEnabled = false; monthContent.Children.Clear();
                if (monthBox.SelectedItem is not ProfileMonth selected) return;
                if (selected.Ambiguous)
                {
                    monthContent.Children.Add(Ui.Text("يوجد أكثر من سجل راتب بالاسم نفسه في هذا الشهر. أُوقف دمج التفاصيل وتصدير الراتب؛ راجع الأسماء في المصدر.", 18)); return;
                }
                var prepared = await Task.Run(() => (Slip: PersonProfiles.Payslip(selected), Report: selected.Employee is null ? null : SalaryReports.Make(selected, false, false)));
                current = selected; currentSlip = prepared.Slip; currentReport = prepared.Report;
                var slip = prepared.Slip;
                if (slip.Net < 0) monthContent.Children.Add(Ui.Text("تنبيه: صافي سالب محفوظ من المصدر؛ راجع البيانات قبل استخدام التقرير."));
                monthContent.Children.Add(Ui.Text(selected.Employee is { } employee ? employee.Rank + " • " + employee.Workplace : "لا يوجد سجل راتب لهذا الشهر؛ المعروض قيود التنفيذ فقط."));
                monthContent.Children.Add(Ui.Text("صافي ملف الرواتب: " + (slip.Net is { } net ? ReportSheet.Money(net) + " د.ع" : "غير متاح"), 24));
                Lines("المخصصات • من تفاصيل الرواتب", slip.Allowances.Concat(slip.Gross is { } gross ? new[] { gross } : []).ToArray());
                Lines("استقطاعات الرواتب • لا تُجمع مع التنفيذ مرة ثانية", slip.Deductions.Concat(slip.TotalDeductions is { } total ? new[] { total } : []).ToArray());
                Lines("قيود ملف التنفيذ • عرض مستقل", slip.EntityDeductions);
                Lines("بيانات المنتسب المحفوظة", slip.Info);
                bool invalidExecution = selected.Deductions.Any(d => !PayrollAnalysis.IsInactive(d) && (d.Amount is null || d.Amount < 0));
                if (invalidExecution) monthContent.Children.Add(Ui.Text("تنبيه: توجد مبالغ تنفيذ ناقصة أو سالبة. أُوقف تصدير الراتب والقسيمة حتى مراجعتها؛ لا تُعامل كأصفار."));
                if (prepared.Report is { } report)
                {
                    if (report.MissingDetails) monthContent.Children.Add(Ui.Text("تفاصيل الراتب غير محفوظة؛ التقرير يعرض المتوفر فقط. أعد استيراد المصدر لاستكمال التفاصيل."));
                    foreach (var warning in report.Mismatches) monthContent.Children.Add(Ui.Text("⚠ " + warning));
                }
                salaryButton.IsEnabled = slipButton.IsEnabled = selected.Employee is not null && !invalidExecution;
            }
            body.Children.Add(Ui.Text("كشف استقطاعات الموظف خلال فترة", 23));
            body.Children.Add(Ui.Text("اختر جهة محددة وبداية الفترة ونهايتها. الأشهر غير المستوردة تظهر «غير متحقق»، ولا تعني عدم وجود استقطاع."));
            var entityBox = new ComboBox { Margin = new Thickness(0, 5, 0, 12) };
            var fromBox = new ComboBox { DisplayMemberPath = "Label", Margin = new Thickness(0, 5, 0, 12) };
            var toBox = new ComboBox { DisplayMemberPath = "Label", Margin = new Thickness(0, 5, 0, 12) };
            body.Children.Add(Ui.Text("جهة التنفيذ")); body.Children.Add(entityBox);
            body.Children.Add(Ui.Text("من شهر")); body.Children.Add(fromBox);
            body.Children.Add(Ui.Text("إلى شهر")); body.Children.Add(toBox);
            var periodNotice = Ui.Text(""); body.Children.Add(periodNotice);
            var statementSummary = Ui.Text("اختر الجهة والفترة ثم اعرض الكشف.");
            var statementGrid = GridFor(("الشهر", "Label"), ("المبلغ / الحالة", "ValueText")); statementGrid.Height = 300;
            var statementExport = Ui.Button("حفظ كشف الاستقطاعات PDF", async () =>
            {
                if (statement is not { } prepared || working) return;
                await SavePdf(prepared.FileName, () => EmployeeDeductionPdf.Generate(prepared, Assets(), Logo(), DateTime.Now));
            });
            statementExport.IsEnabled = false;
            void InvalidateStatement() { statement = null; statementGrid.ItemsSource = null; statementSummary.Text = "تغيّر الاختيار؛ اعرض الكشف للفترة الحالية قبل تصديره."; statementExport.IsEnabled = false; }
            body.Children.Add(Ui.Button("عرض كشف الفترة", async () =>
            {
                if (working) return; InvalidateStatement();
                if (entityBox.SelectedItem is not string entity || fromBox.SelectedItem is not PersonPeriod from || toBox.SelectedItem is not PersonPeriod to) { Ui.Message(dialog, "اختر الجهة وشهري البداية والنهاية."); return; }
                await Work(async () =>
                {
                    var prepared = await Task.Run(() => EmployeeDeductions.Statement(data, new DeductionPerson(person.Key, person.Name, ""), entity, from.Key, to.Key));
                    statement = prepared; statementGrid.ItemsSource = prepared.Rows;
                    statementSummary.Text = (prepared.Incomplete ? "⚠ مجموع المتوفر فقط: " : "مجموع الفترة: ") + ReportSheet.Money(prepared.Total) + " د.ع" + (prepared.Incomplete ? "\nالكشف غير مكتمل: توجد أشهر غير متحققة أو أسماء أشهر غير مفهومة. لا يُعد هذا المجموع تسوية نهائية." : "");
                    statementExport.IsEnabled = true;
                });
            }));
            body.Children.Add(statementSummary); body.Children.Add(statementGrid); body.Children.Add(statementExport);
            fromBox.SelectionChanged += (_, _) => { if (!updatingPeriods) InvalidateStatement(); };
            toBox.SelectionChanged += (_, _) => { if (!updatingPeriods) InvalidateStatement(); };
            entityBox.SelectionChanged += async (_, _) =>
            {
                if (working) return;
                InvalidateStatement();
                if (entityBox.SelectedItem is not string entity) return;
                await Work(async () =>
                {
                    var available = await Task.Run(() => EmployeeDeductions.AvailableMonths(data, person.Key, entity));
                    var periods = available.Keys.Select(k => new PersonPeriod(k, EmployeeDeductions.Label(k))).ToArray();
                    updatingPeriods = true;
                    try { fromBox.ItemsSource = periods; toBox.ItemsSource = periods; fromBox.SelectedItem = periods.FirstOrDefault(); toBox.SelectedItem = periods.LastOrDefault(); }
                    finally { updatingPeriods = false; }
                    periodNotice.Text = available.UnrecognizedCount > 0 ? $"تنبيه: {available.UnrecognizedCount} أسماء أشهر غير مفهومة؛ لا تدخل الفترة ويكون المجموع جزئياً." : periods.Length == 0 ? "لا توجد أشهر قابلة للاختيار لهذه الجهة." : "الحدان الافتراضيان أول وآخر شهر مسجل للشخص والجهة؛ يمكنك تغييرهما.";
                });
            };
            monthBox.SelectionChanged += async (_, _) => { if (!working) await Work(LoadSelectedMonth); };
            dialog.Loaded += async (_, _) =>
            {
                await Work(async () =>
                {
                    entityBox.ItemsSource = await Task.Run(() => EmployeeDeductions.Entities(data, person.Key));
                    monthBox.SelectedItem = person.Months.FirstOrDefault(m => m.Month == preferredMonth) ?? person.Months[0];
                    await LoadSelectedMonth();
                });
            };
            dialog.ShowDialog();
        }
        catch (Exception ex) { Ui.Message(this, "تعذّر فتح ملف المنتسب.\n" + ex.Message); }
    }
}
