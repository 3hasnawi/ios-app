using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SalaryDesktop.Core;

namespace SalaryDesktop;

/// ملف المنتسب الموحّد: القسيمة لكل شهر، استقطاعاته عبر الأشهر، سجل الملاك، والتغيّر عن الشهر السابق.
public partial class PersonWindow : Window
{
    private sealed record StaffLine(string Section, string Label, string Value);
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
        MonthPicker.ItemsSource = profile.Months.Select(m => m.Month).ToArray();
        var initial = month is not null && profile.Months.Any(m => m.Month == month) ? month : profile.Months.FirstOrDefault(m => m.Employee is not null)?.Month ?? profile.Months.FirstOrDefault()?.Month;
        if (initial is not null) MonthPicker.SelectedItem = initial; else Render();

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
        Loaded += (_, _) => DrawTrend();
        Trend.SizeChanged += (_, _) => DrawTrend();
    }

    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
    private ProfileMonth? Current => profile.Months.FirstOrDefault(m => m.Month == MonthPicker.SelectedItem as string);
    private void MonthChanged(object sender, SelectionChangedEventArgs e) => Render();

    private void Render()
    {
        var month = Current;
        PdfButton.IsEnabled = ReportButton.IsEnabled = month?.Employee is not null;
        if (month is null)
        {
            Notice.Text = "لا توجد رواتب أو استقطاعات بهذا الاسم في الأشهر المحفوظة.";
            AllowanceList.ItemsSource = DeductionList.ItemsSource = EntityList.ItemsSource = InfoList.ItemsSource = null;
            NetText.Text = "—"; DeltaChip.Visibility = Visibility.Collapsed; return;
        }
        try
        {
            var slip = PersonProfiles.Payslip(month);
            AllowanceList.ItemsSource = slip.Allowances; GrossText.Text = slip.Gross?.Value ?? "—";
            DeductionList.ItemsSource = slip.Deductions; DeductionTotalText.Text = slip.TotalDeductions?.Value ?? "—";
            EntityList.ItemsSource = slip.EntityDeductions; EntityHeader.Visibility = slip.EntityDeductions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            InfoList.ItemsSource = slip.Info;
            NetLabel.Text = "صافي " + month.Month;
            NetText.Text = slip.Net is { } net ? ReportSheet.Money(net) : "غير متاح";
            var previous = PersonProfiles.Previous(profile, month.Month);
            if (slip.Net is { } now && previous?.Employee?.Net is { } before)
            {
                var difference = now - before;
                DeltaText.Text = difference == 0 ? $"بلا تغيير عن {previous.Month}" : $"{(difference > 0 ? "▲" : "▼")} {ReportSheet.Money(Math.Abs(difference))} عن {previous.Month}";
                DeltaChip.Visibility = Visibility.Visible;
            }
            else DeltaChip.Visibility = Visibility.Collapsed;
            Notice.Text = month.Ambiguous ? "الاسم مكرر في كشف رواتب هذا الشهر؛ لم تُعرض القسيمة حتى لا تختلط بيانات شخصين." :
                month.Employee is null ? "لا يوجد راتب لهذا الاسم في الشهر المختار؛ تظهر استقطاعات ملف التنفيذ فقط." : "";
            var changes = PersonProfiles.Changes(profile, month.Month);
            ChangesTable.ItemsSource = changes;
            ChangesStatus.Text = previous is null ? "لا يوجد شهر سابق لهذا الاسم للمقارنة." :
                changes.Count == 0 ? $"لا تغيّر عن {previous.Month}." : $"{changes.Count:N0} تغيّر مقارنة بـ {previous.Month}.";
        }
        catch (InvalidDataException ex) { Notice.Text = ex.Message; }
    }

    /// خط الصافي لآخر ستة أشهر: الأقدم يساراً والأحدث يميناً.
    private void DrawTrend()
    {
        Trend.Children.Clear();
        var points = profile.Months.Where(m => m.Employee?.Net is not null).Take(6).Reverse().Select(m => (m.Month, Net: m.Employee!.Net!.Value)).ToArray();
        TrendOld.Text = points.Length > 0 ? points[0].Month : ""; TrendNew.Text = points.Length > 1 ? points[^1].Month : "";
        if (points.Length < 2 || Trend.ActualWidth < 20) return;
        double min = (double)points.Min(p => p.Net), max = (double)points.Max(p => p.Net), w = Trend.ActualWidth - 12, h = Trend.ActualHeight - 12;
        var line = new Polyline { Stroke = (Brush)FindResource("Primary"), StrokeThickness = 2.5, StrokeLineJoin = PenLineJoin.Round };
        for (int i = 0; i < points.Length; i++)
        {
            var x = 6 + w * i / (points.Length - 1); var y = 6 + (max == min ? h / 2 : h - h * ((double)points[i].Net - min) / (max - min));
            line.Points.Add(new Point(x, y));
            var dot = new Ellipse { Width = i == points.Length - 1 ? 9 : 6, Height = i == points.Length - 1 ? 9 : 6, Fill = (Brush)FindResource("Primary"), ToolTip = $"{points[i].Month}: {ReportSheet.Money(points[i].Net)}" };
            Canvas.SetLeft(dot, x - dot.Width / 2); Canvas.SetTop(dot, y - dot.Height / 2); Trend.Children.Add(dot);
        }
        Trend.Children.Insert(0, line);
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
            lines.AddRange(slip.Deductions);
            if (slip.TotalDeductions is not null) lines.Add(slip.TotalDeductions);
            foreach (var line in lines) text.AppendLine($"{line.Title}: {line.Value}");
            foreach (var line in slip.EntityDeductions) text.AppendLine($"{line.Title}: {line.Value}" + (line.Note.Length > 0 ? " · " + line.Note : ""));
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
    private static byte[]? HeaderLogo()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "HomeLogo.png");
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
