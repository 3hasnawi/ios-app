using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using SalaryDesktop.Core;

namespace SalaryDesktop;

/// ملف المنتسب الموحّد (لوحة المنتسب): الأشهر يميناً، القسيمة مع استقطاعات جهات التنفيذ فقط في الوسط،
/// وتنبيه التغيّر وبيانات الراتب والإجراءات يساراً؛ ثم الاستقطاعات عبر الأشهر وسجل الملاك والتغيّرات في تبويبات.
public partial class PersonWindow : Window
{
    private sealed record StaffLine(string Section, string Label, string Value);
    /// شهر في قائمة الأشهر: الصافي والسهم مقارنة بالشهر السابق (up/down/same/none).
    public sealed record MonthItem(string Month, string NetText, string Arrow, string Trend);
    private readonly BackupSnapshot source;
    private readonly PersonProfile profile;
    private static readonly CultureInfo Numbers = CultureInfo.GetCultureInfo("en-US");

    public PersonWindow(BackupSnapshot source, PersonProfile profile, string? month = null)
    {
        InitializeComponent();
        this.source = source; this.profile = profile;
        Title = "ملف المنتسب · " + profile.Name;
        NameText.Text = profile.Name;
        Initial.Text = profile.Name.Trim().Length > 0 ? profile.Name.Trim()[..1] : "؟";
        var latest = profile.Months.Select(m => m.Employee).FirstOrDefault(e => e is not null);
        var staff = profile.Staff;
        var identity = new[] { latest?.Rank ?? staff?.Rank, latest?.Workplace ?? staff?.Workplace,
            staff?.Unified is { Length: > 0 } u ? "الموحد " + u : null, staff?.Stat is { Length: > 0 } s ? "الإحصائي " + s : null, staff?.Status }
            .Where(v => !string.IsNullOrWhiteSpace(v));
        IdentityText.Text = string.Join(" · ", identity);
        var items = profile.Months.Select(MonthEntry).ToArray();
        MonthList.ItemsSource = items;
        var initial = month is not null && profile.Months.Any(m => m.Month == month) ? month : profile.Months.FirstOrDefault(m => m.Employee is not null)?.Month ?? profile.Months.FirstOrDefault()?.Month;
        if (items.FirstOrDefault(i => i.Month == initial) is { } chosen) { MonthList.SelectedItem = chosen; MonthList.ScrollIntoView(chosen); } else Render();

        var history = PersonProfiles.History(profile);
        HistoryTable.ItemsSource = history;
        HistoryStatus.Text = history.Count == 0 ? "لا توجد استقطاعات من ملف التنفيذ لهذا الاسم." :
            $"{history.Count:N0} قيد في {history.Select(h => h.Month).Distinct().Count():N0} شهر · مجموع المحتسب {ReportSheet.Money(history.Sum(h => h.Amount ?? 0))}";
        if (staff is null) StaffStatus.Text = "لا يوجد سجل ملاك بهذا الاسم في النسخة المفتوحة، أو لم تُقرأ سجلات الملاك بعد.";
        else
        {
            try
            {
                StaffTable.ItemsSource = StaffRecords.Sections(staff).SelectMany(section => section.Fields.Select(field => new StaffLine(section.Title, field.Label, field.Value))).ToArray();
                StaffStatus.Text = $"اسم الأم: {(staff.Mother.Length > 0 ? staff.Mother : "—")} · الحالة: {(staff.Status.Length > 0 ? staff.Status : "—")}";
            }
            catch (InvalidDataException ex) { StaffStatus.Text = ex.Message; }
        }
    }

    private MonthItem MonthEntry(ProfileMonth month)
    {
        var net = month.Ambiguous ? null : month.Employee?.Net;
        var before = PersonProfiles.Previous(profile, month.Month) is { Ambiguous: false } p ? p.Employee?.Net : null;
        var text = month.Ambiguous ? "الاسم مكرر" : net is { } n ? ReportSheet.Money(n) : month.Employee is null ? "استقطاعات فقط" : "—";
        if (net is null || before is null) return new MonthItem(month.Month, text, "", "none");
        return net > before ? new MonthItem(month.Month, text, "▲", "up") : net < before ? new MonthItem(month.Month, text, "▼", "down") : new MonthItem(month.Month, text, "=", "same");
    }

    /// استقطاعات جهات التنفيذ من ملف الاستقطاعات للشهر: غير المحتسب يظهر «غير محتسب» ولا يدخل المجموع.
    private static (IReadOnlyList<ProfileLine> Lines, decimal Total) EntityLines(ProfileMonth month, Payslip slip)
    {
        var lines = new List<ProfileLine>(); decimal total = 0;
        for (int i = 0; i < month.Deductions.Count; i++)
        {
            var row = month.Deductions[i]; var line = i < slip.EntityDeductions.Count ? slip.EntityDeductions[i] : new ProfileLine(row.Title, "", row.Amount);
            if (PayrollAnalysis.IsInactive(row))
            {
                var note = string.Join(" · ", new[] { line.Note, row.Amount is { } a ? "المبلغ " + ReportSheet.Money(a) : "" }.Where(v => v.Length > 0));
                lines.Add(line with { Value = "غير محتسب", Note = note });
            }
            else { lines.Add(line); total += row.Amount ?? 0; }
        }
        return (lines, total);
    }

    private void CopyValue(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string value || value.Length == 0) return;
        try { Clipboard.SetText(value); Notice.Text = "نُسخ: " + value; }
        catch (System.Runtime.InteropServices.COMException ex) { Notice.Text = "تعذّر النسخ: " + ex.Message; }
    }
    private void ShowStaff(object sender, RoutedEventArgs e) => Tabs.SelectedItem = StaffTab;
    private void ShowChanges(object sender, RoutedEventArgs e) => Tabs.SelectedItem = ChangesTab;

    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
    /// كشف استقطاعات المنتسب: الجهة المحددة في جدول الاستقطاعات عبر الأشهر، أو جهة الشهر المعروض إن كانت واحدة.
    private void OpenDeductionStatement(object sender, RoutedEventArgs e)
    {
        var entity = (HistoryTable.SelectedItem as DeductionHistoryRow)?.Title
            ?? (Current?.Deductions.Select(d => d.Title).Distinct().ToArray() is [var only] ? only : null);
        var detail = string.Join(" · ", new[] { Current?.Employee?.Rank, Current?.Employee?.Workplace }.Where(v => !string.IsNullOrWhiteSpace(v)));
        new EmployeeDeductionWindow(source, new DeductionPerson(profile.Key, profile.Name, detail), entity) { Owner = this }.ShowDialog();
    }
    private ProfileMonth? Current => profile.Months.FirstOrDefault(m => m.Month == (MonthList.SelectedItem as MonthItem)?.Month);
    private void MonthChanged(object sender, SelectionChangedEventArgs e) => Render();

    private void Render()
    {
        var month = Current;
        PdfButton.IsEnabled = ReportButton.IsEnabled = month?.Employee is not null;
        ChangeAlert.Visibility = Visibility.Collapsed;
        if (month is null)
        {
            Notice.Text = "لا توجد رواتب أو استقطاعات بهذا الاسم في الأشهر المحفوظة.";
            SlipTitle.Text = "القسيمة";
            AllowanceList.ItemsSource = EntityList.ItemsSource = InfoList.ItemsSource = null;
            GrossText.Text = DeductionTotalText.Text = "—"; NoEntities.Visibility = Visibility.Visible;
            NetText.Text = "—"; DeltaText.Text = ""; return;
        }
        try
        {
            SlipTitle.Text = "قسيمة " + month.Month;
            var slip = PersonProfiles.Payslip(month);
            AllowanceList.ItemsSource = slip.Allowances; GrossText.Text = slip.Gross?.Value ?? "—";
            var (entities, total) = EntityLines(month, slip);
            EntityList.ItemsSource = entities;
            NoEntities.Visibility = entities.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            DeductionTotalText.Text = entities.Count == 0 ? "—" : ReportSheet.Money(total);
            InfoList.ItemsSource = slip.Info;
            NetLabel.Text = "صافي " + month.Month;
            NetText.Text = slip.Net is { } net ? ReportSheet.Money(net) : "غير متاح";
            var previous = PersonProfiles.Previous(profile, month.Month);
            if (slip.Net is { } now && previous?.Employee?.Net is { } before)
            {
                var difference = now - before;
                DeltaText.Text = difference == 0 ? $"بلا تغيير عن {previous.Month}" : $"{(difference > 0 ? "▲" : "▼")} {ReportSheet.Money(Math.Abs(difference))} عن {previous.Month}";
            }
            else DeltaText.Text = "";
            Notice.Text = month.Ambiguous ? "الاسم مكرر في كشف رواتب هذا الشهر؛ لم تُعرض القسيمة حتى لا تختلط بيانات شخصين." :
                month.Employee is null ? "لا يوجد راتب لهذا الاسم في الشهر المختار؛ تظهر استقطاعات ملف التنفيذ فقط." : "";
            var changes = PersonProfiles.Changes(profile, month.Month);
            ChangesTable.ItemsSource = changes;
            ChangesStatus.Text = previous is null ? "لا يوجد شهر سابق لهذا الاسم للمقارنة." :
                changes.Count == 0 ? $"لا تغيّر عن {previous.Month}." : $"{changes.Count:N0} تغيّر مقارنة بـ {previous.Month}.";
            if (previous is not null && changes.Count > 0)
            {
                ChangeTitle.Text = $"تغيّر عن {previous.Month} ({changes.Count:N0})";
                ChangeText.Text = string.Join("\n", changes.Take(3).Select(c => $"{c.Title}: {c.Difference}")) + (changes.Count > 3 ? "\n…" : "");
                ChangeAlert.Visibility = Visibility.Visible;
            }
        }
        catch (InvalidDataException ex) { Notice.Text = ex.Message; }
    }

    private void CopySummary(object sender, RoutedEventArgs e)
    {
        var month = Current; if (month is null) return;
        try
        {
            var slip = PersonProfiles.Payslip(month); var text = new StringBuilder();
            text.AppendLine(profile.Name).AppendLine(IdentityText.Text).AppendLine("الشهر: " + month.Month);
            if (slip.Net is { } net) text.AppendLine("الصافي: " + ReportSheet.Money(net));
            var lines = new List<ProfileLine>(slip.Allowances);
            if (slip.Gross is not null) lines.Add(slip.Gross);
            foreach (var line in lines) text.AppendLine($"{line.Title}: {line.Value}");
            var (entities, total) = EntityLines(month, slip);
            if (entities.Count > 0) text.AppendLine("استقطاعات جهات التنفيذ:");
            foreach (var line in entities) text.AppendLine($"{line.Title}: {line.Value}" + (line.Note.Length > 0 ? " · " + line.Note : ""));
            if (entities.Count > 0) text.AppendLine("مجموع استقطاعات الجهات: " + ReportSheet.Money(total));
            Clipboard.SetText(text.ToString());
            Notice.Text = "نُسخ ملخص القسيمة.";
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Runtime.InteropServices.COMException) { Notice.Text = "تعذّر النسخ: " + ex.Message; }
    }

    private void OpenPayslipMenu(object sender, RoutedEventArgs e)
    {
        PayslipMenu.PlacementTarget = PdfButton; PayslipMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; PayslipMenu.IsOpen = true;
    }
    /// اسم ملف القسيمة ومنشئها للشهر المعروض؛ null إذا لم يوجد راتب أو تعذرت قراءة تفاصيله.
    private (string Name, Func<ReportAssets, byte[]> Make)? PayslipFile()
    {
        var month = Current; var employee = month?.Employee; if (month is null || employee is null) return null;
        Payslip slip;
        try { slip = PersonProfiles.Payslip(month); }
        catch (InvalidDataException ex) { Notice.Text = ex.Message; return null; }
        var header = ReportSheet.HeaderFor(source);
        return (ReportSheet.FileName($"قسيمة {profile.Name} - {month.Month}"), assets => PayslipPdf.Generate(header, profile.Name, employee.Rank, employee.Workplace, month.Month, slip, assets, DateTime.Now));
    }
    private async void PreviewPayslip(object sender, RoutedEventArgs e) { if (PayslipFile() is { } f) await Exports.Preview(this, f.Name, f.Make); }
    private async void ExportPayslip(object sender, RoutedEventArgs e) { if (PayslipFile() is { } f) await Exports.SavePdf(this, f.Name, f.Make); }
    private async void PrintPayslip(object sender, RoutedEventArgs e) { if (PayslipFile() is { } f) await Exports.Print(this, f.Name, f.Make); }

    // ——— تقرير الراتب (نقل من الآيفون) ———

    private void OpenReportMenu(object sender, RoutedEventArgs e)
    {
        HideZerosItem.IsChecked = UiPreferences.Current.ReportHideZeros;
        HidePersonalItem.IsChecked = UiPreferences.Current.ReportHidePersonal;
        ReportMenu.PlacementTarget = ReportButton; ReportMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; ReportMenu.IsOpen = true;
    }
    private void ReportOptionChanged(object sender, RoutedEventArgs e)
    {
        UiPreferences.Current.ReportHideZeros = HideZerosItem.IsChecked;
        UiPreferences.Current.ReportHidePersonal = HidePersonalItem.IsChecked;
        if (UiPreferences.Save() is { } error) Notice.Text = "تعذّر حفظ خيارات التقرير: " + error;
    }
    private SalaryReportData? BuildReport()
    {
        var month = Current; if (month?.Employee is null) return null;
        try
        {
            var report = SalaryReports.Make(month, UiPreferences.Current.ReportHideZeros, UiPreferences.Current.ReportHidePersonal);
            // تنبيه مثل الآيفون عند عدم تطابق المجاميع، دون منع التقرير.
            Notice.Text = report.Mismatches.Count > 0 ? string.Join("\n", report.Mismatches) : report.MissingDetails ? "هذا الشهر مستورد قبل حفظ تفاصيل الراتب؛ أعد استيراد ملف رواتبه لإظهار المخصصات." : "";
            return report;
        }
        catch (InvalidDataException ex) { Notice.Text = ex.Message; return null; }
    }
    internal static byte[]? HeaderLogo()
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "HomeLogo.png");
        try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
    private async void PreviewSalaryReport(object sender, RoutedEventArgs e)
    {
        if (BuildReport() is not { } report) return;
        var logo = HeaderLogo();
        await Exports.Preview(this, SalaryReports.FileName(report.Name, report.Month), assets => SalaryReportPdf.Generate([report], assets, logo, DateTime.Now));
    }
    private async void SaveSalaryReport(object sender, RoutedEventArgs e)
    {
        if (BuildReport() is not { } report) return;
        var logo = HeaderLogo();
        await Exports.SavePdf(this, SalaryReports.FileName(report.Name, report.Month), assets => SalaryReportPdf.Generate([report], assets, logo, DateTime.Now));
    }
    private async void PrintSalaryReport(object sender, RoutedEventArgs e)
    {
        if (BuildReport() is not { } report) return;
        var logo = HeaderLogo();
        await Exports.Print(this, SalaryReports.FileName(report.Name, report.Month), assets => SalaryReportPdf.Generate([report], assets, logo, DateTime.Now));
    }
}
