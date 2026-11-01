using System.IO;
using System.Windows;
using SalaryDesktop.Core;
namespace SalaryDesktop;
public partial class GroupIssuanceWindow : Window
{
    private readonly BackupSnapshot source; private readonly LocalTransferLinks store; private readonly GroupIssuanceReview review; private readonly Func<bool> payrollCurrent; private bool working;
    public TransferLinksSnapshot State { get; private set; }
    public GroupIssuanceWindow(BackupSnapshot source, TransferLinksSnapshot state, LocalTransferLinks store, GroupIssuanceReview review, Func<bool> payrollCurrent)
    {
        InitializeComponent(); this.source = source; State = state; this.store = store; this.review = review; this.payrollCurrent = payrollCurrent;
        Heading.Text = "تدقيق إصدار " + (review.Group == TransferGroup.Rafidain ? "مجموعة الرافدين" : "مجموعة رشيد ومصارف أخرى") + " · " + review.Month;
        Status.Text = (review.BaselineMonth is null ? "لا يوجد إقرار سابق للمقارنة." : "المقارنة مع إقرار " + review.BaselineMonth + ".") + " الإقرار لا يثبت حصول التحويل المصرفي. بصمات الأسماء الصادرة من الآيفون تحتاج مراجعة عند أول تأكيد على ويندوز.";
        Changes.ItemsSource = review.Changes.Count == 0 ? new[] { "لا توجد تغييرات مسجلة للمراجعة." } : review.Changes;
        Reviewed.Visibility = review.Changes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Closing += (_, e) => { if (working) e.Cancel = true; };
    }
    private async void Confirm(object sender, RoutedEventArgs e)
    {
        if (working) return;
        if (review.Changes.Count > 0 && Reviewed.IsChecked != true) { MessageBox.Show(this, "راجع التغييرات ثم ضع علامة التأكيد.", "تدقيق المجموعة", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var note = NoteText.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        working = true; ConfirmButton.IsEnabled = false; NoteText.IsEnabled = false; Reviewed.IsEnabled = false;
        try
        {
            var expected = State;
            var next = await Task.Run(() => { if (!payrollCurrent()) throw new InvalidDataException("تغيّرت بيانات الشهر؛ أعد تدقيق المجموعة."); return store.ConfirmGroup(source, expected, review, note); });
            State = next;
            if (!payrollCurrent()) { MessageBox.Show(this, "حُفظ إقرار البيانات السابقة وتغيّرت بيانات الشهر؛ أعد التدقيق.", "تدقيق المجموعة", MessageBoxButton.OK, MessageBoxImage.Warning); }
            else MessageBox.Show(this, "حُفظ تأكيدك للمجموعة. احفظ نسخة احتياطية لتضمين الإقرار.", "تدقيق المجموعة", MessageBoxButton.OK, MessageBoxImage.Information);
            working = false; DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException)
        { MessageBox.Show(this, "تعذّر حفظ الإقرار.\n" + ex.Message, "تدقيق المجموعة", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { working = false; ConfirmButton.IsEnabled = true; NoteText.IsEnabled = true; Reviewed.IsEnabled = true; }
    }
}
