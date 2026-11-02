using System.Globalization;
using System.Windows;
using SalaryDesktop.Core;

namespace SalaryDesktop;

/// تأكيد اعتماد مطابقة الفترة: ملخصها وتنبيهاتها؛ مع وجود تنبيهات يلزم إقرار ثانٍ بالاعتماد اليدوي (مثل الآيفون).
public partial class ReconciliationConfirmWindow : Window
{
    public ReconciliationConfirmWindow(BankPeriod period, ReconciliationReview review, bool sourcesChecked)
    {
        InitializeComponent();
        PeriodText.Text = $"{period.Month} · {period.Start.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture)} — {period.End.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture)}";
        OpeningText.Text = PeriodCard.Number(period.OpeningBalance); CreditText.Text = PeriodCard.Number(review.Totals.Credits);
        DebitText.Text = PeriodCard.Number(review.Totals.Debits); BalanceText.Text = PeriodCard.Number(review.Totals.Balance);
        MovementsText.Text = $"{period.Movements.Count:N0} حركة · كلها مؤشرة";
        var alerts = review.Alerts.Where(a => a.Blocking).ToList();
        if (!sourcesChecked) alerts.Add(new ReconciliationAlert("لم يكتمل فحص مصادر الشهر بعد."));
        AlertsText.Text = alerts.Count == 0 ? "لا توجد" : $"{alerts.Count:N0} تنبيه — راجعها قبل الاعتماد";
        AlertsText.Foreground = (System.Windows.Media.Brush)FindResource(alerts.Count == 0 ? "Primary" : "Warn");
        AlertsBox.Visibility = AcceptBox.Visibility = alerts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        AlertsList.Text = string.Join("\n", alerts.Select(a => "• " + a.Text));
    }
    private void BoxChanged(object sender, RoutedEventArgs e) =>
        ConfirmButton.IsEnabled = ReviewedBox.IsChecked == true && (AcceptBox.Visibility != Visibility.Visible || AcceptBox.IsChecked == true);
    private void Confirm(object sender, RoutedEventArgs e) { if (ConfirmButton.IsEnabled) DialogResult = true; }
}
