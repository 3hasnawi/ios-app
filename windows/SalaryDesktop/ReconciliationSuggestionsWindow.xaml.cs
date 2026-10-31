using System.Globalization;
using System.IO;
using System.Windows;
using SalaryDesktop.Core;

namespace SalaryDesktop;

public partial class ReconciliationSuggestionsWindow : Window
{
    private sealed record Row(ReconciliationSuggestion Suggestion, string Title, string AmountText, string Status);
    private readonly BankPeriod period;
    public BankPeriod? Result { get; private set; }
    public ReconciliationSuggestionsWindow(BankPeriod period, ReconciliationSuggestionResult data)
    {
        InitializeComponent(); this.period = period;
        var existing = period.Movements.Where(m => m.SourceId is not null).Select(m => m.SourceId!).ToHashSet(StringComparer.Ordinal);
        var usedLines = period.Movements.SelectMany(m => m.SourceLineIds ?? []).ToHashSet(StringComparer.Ordinal);
        Rows.ItemsSource = data.Items.Select(s => new Row(s, s.Title, s.Amount.ToString("N0", CultureInfo.GetCultureInfo("en-US")),
            existing.Contains(s.Id) ? "مضافة سابقاً" : s.SourceLineIds.Any(usedLines.Contains) ? "يتداخل مع مصدر مضاف" : "متاحة للمراجعة")).ToArray();
        var changed = ReconciliationSuggestions.Changed(period, data);
        var notes = new List<string> { "المقترحات للمراجعة فقط؛ تحقق من كشف المصرف وتجنب تكرار حركة أدخلتها يدوياً." };
        if (changed.Count > 0) notes.Add($"تنبيه: تغيرت أو اختفت مصادر {changed.Count} حركة مضافة؛ راجعها قبل الاعتماد: " + string.Join("، ", changed.Select(m => m.Description)) + ".");
        notes.AddRange(data.Warnings); Notice.Text = string.Join("\n", notes);
        MovementDate.SelectedDate = null;
    }
    private void Add(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Rows.SelectedItem is not Row row) throw new InvalidDataException("اختر مبلغاً من القائمة.");
            if (MovementDate.SelectedDate is not DateTime date) throw new InvalidDataException("اختر تاريخ تنفيذ الحركة من كشف المصرف.");
            Result = ReconciliationSuggestions.Add(period, row.Suggestion, DateOnly.FromDateTime(date));
            DialogResult = true;
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException) { MessageBox.Show(this, ex.Message, "راجع الحركة", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
