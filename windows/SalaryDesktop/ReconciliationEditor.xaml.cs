using System.Globalization;
using System.IO;
using System.Windows;
using SalaryDesktop.Core;

namespace SalaryDesktop;

public partial class ReconciliationEditor : Window
{
    private readonly BankPeriod? period;
    private readonly BankMovement? old;
    private sealed record RefundOption(Guid? Id, string Label);
    public BankPeriod? ResultPeriod { get; private set; }
    public BankMovement? ResultMovement { get; private set; }
    private static long ParseAmount(string value)
    {
        var text = string.Concat(value.Trim().Select(c => c switch { >= '٠' and <= '٩' => (char)('0' + c - '٠'), >= '۰' and <= '۹' => (char)('0' + c - '۰'), _ => c }));
        var groups = text.Split([',', '٬']);
        if (groups.Any(g => g.Length == 0 || g.Any(c => !char.IsAsciiDigit(c))) ||
            groups.Length > 1 && (groups[0].Length > 3 || groups.Skip(1).Any(g => g.Length != 3)) ||
            !long.TryParse(string.Concat(groups), NumberStyles.None, CultureInfo.InvariantCulture, out var result))
            throw new InvalidDataException("أدخل مبلغاً صحيحاً بالدينار؛ ضع الفواصل بين كل ثلاث مراتب.");
        return result;
    }
    public ReconciliationEditor(BankPeriod? existing, string suggestedMonth)
    {
        InitializeComponent(); period = existing; TitleText.Text = existing is null ? "فترة مطابقة جديدة" : "تعديل فترة المطابقة";
        FirstLabel.Text = "شهر الرواتب المطابَق"; First.Text = existing?.Month ?? suggestedMonth; DateLabel.Text = "بداية الفترة · تاريخ التمويل";
        Date.SelectedDate = existing?.Start.ToDateTime(TimeOnly.MinValue) ?? DateTime.Today;
        EndDate.SelectedDate = existing?.End.ToDateTime(TimeOnly.MinValue) ?? DateTime.Today;
        Opening.Text = (existing?.OpeningBalance ?? 0).ToString(CultureInfo.InvariantCulture); MemberFirst.Text = existing?.FirstMember ?? ""; MemberSecond.Text = existing?.SecondMember ?? "";
        MovementFields.Visibility = Visibility.Collapsed;
    }
    public ReconciliationEditor(BankPeriod selected, BankMovement? movement)
    {
        InitializeComponent(); period = selected; old = movement; TitleText.Text = movement is null ? "حركة جديدة" : "تعديل الحركة";
        FirstLabel.Text = "البيان"; First.Text = movement?.Description ?? ""; DateLabel.Text = "تاريخ الحركة";
        Date.SelectedDate = (movement?.Date ?? selected.Start).ToDateTime(TimeOnly.MinValue);
        Amount.Text = movement?.Amount.ToString(CultureInfo.InvariantCulture) ?? ""; Direction.SelectedIndex = movement?.Direction == MovementDirection.Debit ? 1 : 0;
        Funding.IsChecked = movement?.IsFunding ?? false; Refund.IsChecked = movement?.IsRefund ?? false;
        var options = new List<RefundOption> { new(null, "بدون ربط") };
        options.AddRange(selected.Movements.Where(m => m.IsRefund && m.Id != movement?.Id).Select(m => new RefundOption(m.Id, $"{m.Description} · {m.Amount:N0}")));
        RefundOf.ItemsSource = options; RefundOf.SelectedItem = options.FirstOrDefault(o => o.Id == movement?.RefundOf) ?? options[0];
        PeriodFields.Visibility = Visibility.Collapsed;
    }
    private void Save(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Date.SelectedDate is not DateTime date) throw new InvalidDataException("اختر التاريخ.");
            if (PeriodFields.Visibility == Visibility.Visible)
            {
                if (EndDate.SelectedDate is not DateTime end) throw new InvalidDataException("اختر نهاية الفترة.");
                var value = new BankPeriod(period?.Id ?? Guid.NewGuid(), First.Text.Trim(), DateOnly.FromDateTime(date), DateOnly.FromDateTime(end), ParseAmount(Opening.Text), MemberFirst.Text.Trim(), MemberSecond.Text.Trim(), period?.AcceptedContractMissing, period?.AcceptedContractAmount, period?.Movements ?? []);
                _ = BankReconciliation.Calculate(value); ResultPeriod = value;
            }
            else
            {
                var value = new BankMovement(old?.Id ?? Guid.NewGuid(), DateOnly.FromDateTime(date), First.Text.Trim(), ParseAmount(Amount.Text), Direction.SelectedIndex == 0 ? MovementDirection.Credit : MovementDirection.Debit,
                    old?.SourceId, old?.SourceLineIds, (RefundOf.SelectedItem as RefundOption)?.Id, Refund.IsChecked == true, Funding.IsChecked == true, old?.Matched ?? false);
                ResultMovement = old is null ? value : BankReconciliation.Replace(old, value);
                var movements = period!.Movements.Where(m => m.Id != value.Id).Concat([ResultMovement]).ToArray();
                _ = BankReconciliation.Calculate(period with { Movements = movements });
            }
            DialogResult = true;
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException) { MessageBox.Show(this, ex.Message, "تحقق من البيانات", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
