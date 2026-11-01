using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SalaryDesktop.Core;

namespace SalaryDesktop;

public partial class ReconciliationWindow : Window
{
    private sealed record PeriodChoice(BankPeriod Period, string Label);
    private sealed record MovementRow(Guid Id, string Date, string Description, string Credit, string Debit, string Balance, string Status);
    private readonly BackupSnapshot source;
    private readonly LocalReconciliationStore store = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SalaryDesktop", "Reconciliations"));
    private ReconciliationSnapshot state;
    private readonly string suggestedMonth;
    private int sourceRequest;
    private static string Number(long value) => value.ToString("N0", CultureInfo.GetCultureInfo("en-US"));
    public ReconciliationWindow(BackupSnapshot source, string selectedMonth)
    {
        InitializeComponent(); this.source = source; state = store.Load(source);
        var previous = MonthNames.Previous(selectedMonth);
        var matchingMonths = source.Months.Where(m => previous is not null && MonthNames.SortKey(m.Name) == previous).ToArray();
        suggestedMonth = matchingMonths.Length == 1 ? matchingMonths[0].Name : "";
        var matches = state.Periods.Where(p => previous is not null && MonthNames.SortKey(p.Month) == previous).ToArray();
        Refresh(matches.Length == 1 ? matches[0].Id : null);
    }
    private BankPeriod? Current => (Periods.SelectedItem as PeriodChoice)?.Period;
    private BankMovement? CurrentMovement => Lines.SelectedItem is MovementRow row ? Current?.Movements.FirstOrDefault(m => m.Id == row.Id) : null;
    private void Refresh(Guid? selected)
    {
        var choices = state.Periods.OrderByDescending(p => p.Start).Select(p => new PeriodChoice(p, $"{p.Month} · {p.Start:yyyy/MM/dd} — {p.End:yyyy/MM/dd}")).ToArray();
        Periods.ItemsSource = choices;
        Periods.SelectedItem = choices.FirstOrDefault(c => c.Period.Id == selected);
        if (Periods.SelectedItem is null) { ++sourceRequest; Lines.ItemsSource = null; SourceStatus.Text = ""; Summary.Text = choices.Length == 0 ? "لا توجد فترات مطابقة؛ أضف فترة جديدة." : "اختر فترة المطابقة من القائمة."; }
    }
    private void PeriodChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Current is not { } p) return;
        var result = BankReconciliation.Calculate(p);
        Summary.Text = $"الرصيد الافتتاحي: {Number(p.OpeningBalance)}  ·  الدائن: {Number(result.Credits)}  ·  المدين: {Number(result.Debits)}  ·  الرصيد: {Number(result.Balance)}  ·  مبالغ مسترجعة لم تُعد بالكامل: {result.OutstandingRefunds.Count}";
        Lines.ItemsSource = result.Lines.Select(line => new MovementRow(line.Movement.Id, line.Movement.Date.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture), line.Movement.Description,
            line.Movement.Direction == MovementDirection.Credit ? Number(line.Movement.Amount) : "—",
            line.Movement.Direction == MovementDirection.Debit ? Number(line.Movement.Amount) : "—", Number(line.Balance), line.Movement.Matched ? "مطابقة" : "غير مؤكدة")).ToArray();
        ReviewSources(p);
    }
    private async void ReviewSources(BankPeriod period)
    {
        var ticket = ++sourceRequest;
        SourceStatus.Text = "جارٍ فحص مصادر الشهر…";
        try
        {
            var data = await Task.Run(() => ReconciliationSuggestions.Prepare(source, period.Month));
            if (ticket != sourceRequest || Current?.Id != period.Id) return;
            var changed = ReconciliationSuggestions.Changed(period, data);
            var notes = data.Warnings.ToList();
            if (changed.Count > 0) notes.Insert(0, $"تنبيه: تغيّر أو اختفى مصدر {changed.Count} حركة: " + string.Join("، ", changed.Select(m => m.Description)) + ".");
            var used = period.Movements.Where(m => m.SourceId is not null).Select(m => m.SourceId!).ToHashSet(StringComparer.Ordinal);
            var missing = data.Items.Count(item => !used.Contains(item.Id));
            if (missing > 0) notes.Add($"{missing} مبالغ مقترحة لم تُدرج بعد.");
            SourceStatus.Text = notes.Count == 0 ? "راجع تأشير الحركات وتواريخها مع كشف المصرف." : string.Join(" · ", notes);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or OverflowException)
        { if (ticket == sourceRequest && Current?.Id == period.Id) SourceStatus.Text = "تعذّر فحص مصادر الشهر: " + ex.Message; }
    }
    private async void OpenSuggestions(object sender, RoutedEventArgs e)
    {
        if (Current is not { } period) return;
        var revision = state.Revision; SuggestionsButton.IsEnabled = false;
        try
        {
            var data = await Task.Run(() => ReconciliationSuggestions.Prepare(source, period.Month));
            if (Current?.Id != period.Id || state.Revision != revision) { MessageBox.Show(this, "تغيّرت الفترة أثناء احتساب المقترحات؛ أعد فتحها.", "المبالغ المقترحة"); return; }
            var dialog = new ReconciliationSuggestionsWindow(period, data) { Owner = this };
            if (dialog.ShowDialog() == true && dialog.Result is { } updated) Save(updated);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or OverflowException)
        { MessageBox.Show(this, "تعذّر حساب المقترحات.\n" + ex.Message, "المبالغ المقترحة", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { SuggestionsButton.IsEnabled = true; }
    }
    private void Save(BankPeriod updated)
    {
        try
        {
            var all = state.Periods.Where(p => p.Id != updated.Id).Concat([updated]).ToArray();
            state = store.Save(source, state, all); Refresh(updated.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException)
        { MessageBox.Show(this, "لم تُحفظ التغييرات.\n" + ex.Message, "مطابقة الحساب", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void AddPeriod(object sender, RoutedEventArgs e)
    {
        var editor = new ReconciliationEditor(null, suggestedMonth) { Owner = this };
        if (editor.ShowDialog() == true && editor.ResultPeriod is { } value) Save(value);
    }
    private void EditPeriod(object sender, RoutedEventArgs e)
    {
        if (Current is not { } p) return;
        var editor = new ReconciliationEditor(p, suggestedMonth) { Owner = this };
        if (editor.ShowDialog() == true && editor.ResultPeriod is { } value) Save(value);
    }
    private void AddMovement(object sender, RoutedEventArgs e)
    {
        if (Current is not { } p) return;
        var editor = new ReconciliationEditor(p, (BankMovement?)null) { Owner = this };
        if (editor.ShowDialog() == true && editor.ResultMovement is { } value) Save(p with { Movements = p.Movements.Concat([value]).ToArray() });
    }
    private void EditMovement(object sender, RoutedEventArgs e)
    {
        if (Current is not { } p || CurrentMovement is not { } m) return;
        var editor = new ReconciliationEditor(p, m) { Owner = this };
        if (editor.ShowDialog() == true && editor.ResultMovement is { } value) Save(p with { Movements = p.Movements.Select(x => x.Id == value.Id ? value : x).ToArray() });
    }
    private void ToggleMatched(object sender, RoutedEventArgs e)
    {
        if (Current is not { } p || CurrentMovement is not { } m) return;
        Save(p with { Movements = p.Movements.Select(x => x.Id == m.Id ? x with { Matched = !x.Matched } : x).ToArray() });
    }
    private void DeleteMovement(object sender, RoutedEventArgs e)
    {
        if (Current is not { } p || CurrentMovement is not { } m) return;
        if (MessageBox.Show(this, "حذف الحركة المختارة من هذه الفترة؟", "حذف حركة", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Save(p with { Movements = p.Movements.Where(x => x.Id != m.Id).ToArray() });
    }
    private async void ExportBackup(object sender, RoutedEventArgs e)
    {
        var captured = state;
        var dialog = new SaveFileDialog { Title = "حفظ نسخة تشمل المطابقة", Filter = "نسخة احتياطية (*.zip)|*.zip", FileName = "نسخة الرواتب والمطابقة.zip", AddExtension = true, DefaultExt = ".zip", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        var destination = dialog.FileName;
        ExportButton.IsEnabled = false;
        try
        {
            await Task.Run(() => {
                if (store.Load(source).Revision != captured.Revision) throw new InvalidDataException("تغيّر دفتر المطابقة؛ أعد فتحه قبل تصدير النسخة.");
                ReconciliationBackupExport.Write(source, captured.Periods, destination);
            });
            var changed = state.Revision != captured.Revision;
            MessageBox.Show(this, (changed ? "حُفظت لقطة الدفتر السابقة، وقد تغيّرت البيانات أثناء التصدير؛ أعد التصدير لحفظ آخر التغييرات. " : "حُفظت النسخة مع دفتر المطابقة بعد فحص محتواها. ") + "يلزم تجربة استعادتها على الآيفون قبل الاعتماد عليها.", "النسخة الاحتياطية", MessageBoxButton.OK, changed ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException)
        { MessageBox.Show(this, "تعذّر حفظ النسخة.\n" + ex.Message, "النسخة الاحتياطية", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { ExportButton.IsEnabled = true; }
    }
    private async void ExportPdf(object sender, RoutedEventArgs e)
    {
        if (Current is not { } period) return;
        var revision = state.Revision;
        var dialog = new SaveFileDialog { Title = "حفظ مسودة كشف المطابقة", Filter = "ملف PDF (*.pdf)|*.pdf", FileName = "مسودة مطابقة الحساب.pdf", AddExtension = true, DefaultExt = ".pdf", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        var destination = dialog.FileName;
        ExportPdfButton.IsEnabled = false;
        try
        {
            var font = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
            await Task.Run(() => ReconciliationPdf.SaveDraft(period, font, destination));
            var changed = state.Revision != revision || Current?.Id != period.Id;
            MessageBox.Show(this, changed ? "حُفظت مسودة الفترة السابقة، وقد تغيرت البيانات أثناء التصدير؛ أعد التصدير لآخر التغييرات." : "حُفظت المسودة. راجع الصفحات والرصيد والحركات والتواقيع مع كشف المصرف قبل الاعتماد.", "كشف المطابقة", MessageBoxButton.OK, changed ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException or DllNotFoundException or TypeInitializationException)
        { MessageBox.Show(this, "تعذّر إعداد مسودة الكشف.\n" + ex.Message, "كشف المطابقة", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { ExportPdfButton.IsEnabled = true; }
    }
}
