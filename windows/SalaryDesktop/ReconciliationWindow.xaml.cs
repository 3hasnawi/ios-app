using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SalaryDesktop.Core;

namespace SalaryDesktop;

public partial class ReconciliationWindow : Window
{
    private sealed record PeriodChoice(BankPeriod Period, string Label);
    private sealed record MovementRow(string Date, string Description, string Credit, string Debit, string Balance, string Status);
    private static string Number(long value) => value.ToString("N0", CultureInfo.GetCultureInfo("en-US"));
    public ReconciliationWindow(IReadOnlyList<BankPeriod> periods)
    {
        InitializeComponent();
        Periods.ItemsSource = periods.OrderByDescending(p => p.Start).Select(p => new PeriodChoice(p, $"{p.Month} · {p.Start:yyyy/MM/dd} — {p.End:yyyy/MM/dd}")).ToArray();
        if (periods.Count > 0) Periods.SelectedIndex = 0;
        else Summary.Text = "لا توجد فترات مطابقة محفوظة في النسخة المفتوحة.";
    }
    private void PeriodChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Periods.SelectedItem is not PeriodChoice choice) return;
        var p = choice.Period;
        var result = BankReconciliation.Calculate(p);
        Summary.Text = $"الرصيد الافتتاحي: {Number(p.OpeningBalance)}  ·  الدائن: {Number(result.Credits)}  ·  المدين: {Number(result.Debits)}  ·  الرصيد: {Number(result.Balance)}  ·  مبالغ مسترجعة لم تُعد بالكامل: {result.OutstandingRefunds.Count}";
        Lines.ItemsSource = result.Lines.Select(line => new MovementRow(line.Movement.Date.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture), line.Movement.Description,
            line.Movement.Direction == MovementDirection.Credit ? Number(line.Movement.Amount) : "—",
            line.Movement.Direction == MovementDirection.Debit ? Number(line.Movement.Amount) : "—", Number(line.Balance), line.Movement.Matched ? "مطابقة" : "غير مؤكدة")).ToArray();
    }
}
