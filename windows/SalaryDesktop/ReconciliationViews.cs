using System.Globalization;
using System.IO;
using System.Windows;
using SalaryDesktop.Core;

namespace SalaryDesktop;

/// بطاقة فترة مطابقة في صفحة كشف البنك ودفتر المطابقة (القالب PeriodCardTemplate في App.xaml).
public sealed class PeriodCard
{
    public required BankPeriod Period { get; init; }
    public string Month => Period.Month;
    public string Range => $"{Period.Start.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture)} — {Period.End.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture)}";
    public required string StatusText { get; init; }
    public required string Detail { get; init; }
    public bool IsConfirmed => Period.IsConfirmed;

    public static string Number(long value) => value.ToString("N0", CultureInfo.GetCultureInfo("en-US"));
    public static string When(BankPeriod period) => BankReconciliation.ConfirmedTime(period)?.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture) ?? "";

    public static PeriodCard From(BankPeriod period)
    {
        try
        {
            var totals = BankReconciliation.Calculate(period);
            var matched = period.Movements.Count(m => m.Matched);
            return period.IsConfirmed
                ? new() { Period = period, StatusText = "معتمدة " + When(period), Detail = $"{period.Movements.Count:N0} حركة · الرصيد {Number(totals.Balance)}" }
                : new() { Period = period, StatusText = period.Movements.Count == 0 ? "قيد العمل · بلا حركات" : $"قيد العمل · {matched:N0} من {period.Movements.Count:N0} مؤشرة", Detail = $"الرصيد {Number(totals.Balance)}" };
        }
        catch (InvalidDataException ex) { return new() { Period = period, StatusText = "تحتاج مراجعة", Detail = ex.Message }; }
    }
    public static IReadOnlyList<PeriodCard> List(IEnumerable<BankPeriod> periods) => periods.OrderByDescending(p => p.Start).ThenByDescending(p => MonthNames.SortKey(p.Month)).Select(From).ToArray();
}

/// كشف الفترة PDF بالشكل المعتمد (بيانات الحساب من إعدادات النسخة).
internal static class ReconciliationFiles
{
    public static string Name(BankPeriod period) => $"كشف مطابقة حساب البنك - {period.Month}";
    public static byte[] Statement(BackupSnapshot source, BankPeriod period, bool complete) =>
        ReconciliationPdf.Statement(period, TransferPreparation.Account(source), complete || period.IsConfirmed, ReportAssets.Load(Exports.WatermarkPath));
    public static Task Preview(Window owner, BackupSnapshot source, BankPeriod period, bool complete) => Exports.PreviewPdf(owner, Name(period), () => Statement(source, period, complete));
    public static async Task Print(Window owner, BackupSnapshot source, BankPeriod period, bool complete)
    {
        try { var bytes = await Task.Run(() => Statement(source, period, complete)); Exports.ShowBytes(owner, Name(period), bytes, print: true); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or DllNotFoundException or TypeInitializationException)
        { Ui.Message(owner, "تعذّر إعداد كشف الفترة.\n" + ex.Message, "كشف المطابقة", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    public static async Task Save(Window owner, BackupSnapshot source, BankPeriod period, bool complete)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "حفظ كشف المطابقة", Filter = "ملف PDF (*.pdf)|*.pdf", FileName = ReportSheet.FileName(Name(period)) + ".pdf", AddExtension = true, DefaultExt = ".pdf", OverwritePrompt = true };
        if (dialog.ShowDialog(owner) != true) return;
        try
        {
            var destination = dialog.FileName;
            await Task.Run(() => ReconciliationPdf.Save(Statement(source, period, complete), destination));
            if (Ui.Message(owner, "حُفظ كشف المطابقة:\n" + destination + "\n\nهل تريد فتحه الآن؟", "كشف المطابقة", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                Exports.ShowBytes(owner, Name(period), await File.ReadAllBytesAsync(destination));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or DllNotFoundException or TypeInitializationException)
        { Ui.Message(owner, "تعذّر حفظ كشف المطابقة.\n" + ex.Message, "كشف المطابقة", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
