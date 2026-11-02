using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SalaryDesktop.Core;

namespace SalaryDesktop;

/// دفتر مطابقة الحساب: الفترات يميناً، والفترة المختارة بملخصها وسطر تنبيهاتها وحركاتها؛ كل الإجراءات من الزر الأيمن وتفتح نوافذ منبثقة.
/// اعتماد المطابقة يحفظ الفترة «معتمدة» ويقفلها (تُفتح كشفها في أي وقت)، وإلغاء الاعتماد يعيدها للتعديل.
public partial class ReconciliationWindow : Window
{
    public sealed class MovementRow
    {
        public required Guid Id { get; init; }
        public required string Date { get; init; }
        public required string Description { get; init; }
        public required string Credit { get; init; }
        public required string Debit { get; init; }
        public required string Balance { get; init; }
        public required bool Matched { get; init; }
        public string Note { get; init; } = "";
        public Visibility NoteVisibility => Note.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    private readonly BackupSnapshot source;
    private readonly Func<bool> sourceIsCurrent;
    private readonly LocalReconciliationStore store = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SalaryDesktop", "Reconciliations"));
    private ReconciliationSnapshot state;
    private readonly string suggestedMonth;
    private readonly BankAccountDetails account;
    /// آخر فحص لمصادر الشهر للفترة المختارة (null = لم يُفحص بعد)؛ تنبيهات الفترة نفسها تُحسب في كل عرض.
    private ReconciliationSources? sources;
    private Guid sourcesPeriod;
    private int reviewRequest;
    private ReconciliationReview? Review(BankPeriod p)
    {
        try { return ReconciliationReview.Combine(p, sources is not null && sourcesPeriod == p.Id ? sources : ReconciliationSources.None, account); }
        catch (InvalidDataException) { return null; }
    }
    private static string Number(long value) => PeriodCard.Number(value);

    public ReconciliationWindow(BackupSnapshot source, string selectedMonth, Func<bool> sourceIsCurrent, Guid? select = null, bool createNew = false)
    {
        InitializeComponent(); this.source = source; this.sourceIsCurrent = sourceIsCurrent; state = store.Load(source);
        try { account = TransferPreparation.Account(source); }
        catch (InvalidDataException) { account = new BankAccountDetails("", "الأنبار", "", ""); }
        AccountText.Text = string.Join(" · ", new[] { account.Name, account.Branch, account.LocalAccount, account.Iban }.Where(v => v.Length > 0)) is { Length: > 0 } line ? line : "بيانات الحساب غير مكتملة؛ أكملها من «بيانات الحساب والمبالغ اليدوية» في صفحة كشف البنك.";
        var previous = MonthNames.Previous(selectedMonth);
        var matchingMonths = source.Months.Where(m => previous is not null && MonthNames.SortKey(m.Name) == previous).ToArray();
        suggestedMonth = matchingMonths.Length == 1 ? matchingMonths[0].Name : "";
        var preferred = select ?? state.Periods.Where(p => previous is not null && MonthNames.SortKey(p.Month) == previous).Select(p => (Guid?)p.Id).FirstOrDefault();
        Refresh(preferred);
        if (createNew) Loaded += (_, _) => AddPeriod(this, new RoutedEventArgs());
    }

    private BankPeriod? Current => (PeriodList.SelectedItem as PeriodCard)?.Period;
    private MovementRow? CurrentRow => Lines.SelectedItem as MovementRow;
    private BankMovement? CurrentMovement => CurrentRow is { } row ? Current?.Movements.FirstOrDefault(m => m.Id == row.Id) : null;

    private void Refresh(Guid? selected)
    {
        var cards = PeriodCard.List(state.Periods);
        PeriodList.ItemsSource = cards;
        PeriodList.SelectedItem = cards.FirstOrDefault(c => c.Period.Id == selected) ?? cards.FirstOrDefault();
        if (PeriodList.SelectedItem is null) ShowPeriod();
    }
    private void PeriodChanged(object sender, SelectionChangedEventArgs e) => ShowPeriod();

    private void ShowPeriod(Guid? keepMovement = null)
    {
        var p = Current;
        PeriodPanel.Visibility = p is null ? Visibility.Collapsed : Visibility.Visible;
        EmptyPanel.Visibility = p is null ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = state.Periods.Count == 0 ? "لا توجد فترات مطابقة بعد" : "اختر فترة من القائمة";
        PreviewButton.IsEnabled = p is { Movements.Count: > 0 };
        if (p is null) { ++reviewRequest; return; }
        PeriodTitle.Text = p.Month + "  "; RangeText.Text = "· " + new PeriodCard { Period = p, StatusText = "", Detail = "" }.Range;
        ReconciliationTotals totals;
        try { totals = BankReconciliation.Calculate(p); }
        catch (InvalidDataException ex) { AlertBar.Visibility = Visibility.Visible; AlertTitle.Text = "بيانات غير صالحة"; AlertText.Text = ex.Message; Lines.ItemsSource = null; return; }
        OpeningText.Text = Number(p.OpeningBalance);
        CreditLabel.Text = totals.Funding > 0 ? $"الدائن (منه التمويل {Number(totals.Funding)})" : "الدائن";
        CreditText.Text = Number(totals.Credits); DebitText.Text = Number(totals.Debits); BalanceText.Text = Number(totals.Balance);
        var matched = p.Movements.Count(m => m.Matched);
        MatchedText.Text = $"{matched:N0} من {p.Movements.Count:N0}";
        MatchedText.Foreground = (System.Windows.Media.Brush)FindResource(matched == p.Movements.Count && matched > 0 ? "Primary" : "Warn");
        var review = Review(p);
        var stale = review?.Stale.Select(m => m.Id).ToHashSet() ?? [];
        var rows = totals.Lines.Select(line => new MovementRow
        {
            Id = line.Movement.Id, Date = line.Movement.Date.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture),
            Description = line.Movement.Description + (line.Movement.IsFunding ? " · تمويل" : line.Movement.IsRefund ? " · مسترجع" : ""),
            Credit = line.Movement.Direction == MovementDirection.Credit ? Number(line.Movement.Amount) : "—",
            Debit = line.Movement.Direction == MovementDirection.Debit ? Number(line.Movement.Amount) : "—",
            Balance = Number(line.Balance), Matched = line.Movement.Matched,
            Note = stale.Contains(line.Movement.Id) ? "تغيّر المصدر؛ احذف الحركة وأعد إضافتها." : "",
        }).ToArray();
        var keep = keepMovement ?? CurrentRow?.Id;
        Lines.ItemsSource = rows;
        Lines.SelectedItem = rows.FirstOrDefault(r => r.Id == keep);
        NoMovements.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowState(p, review);
        if ((sources is null || sourcesPeriod != p.Id) && !p.IsConfirmed) ReviewSources(p);
    }

    /// حالة الفترة: معتمدة (مقفلة) أو قيد العمل مع سطر التنبيهات وزر الاعتماد.
    private void ShowState(BankPeriod p, ReconciliationReview? review)
    {
        var confirmed = p.IsConfirmed;
        StatusChip.Background = (System.Windows.Media.Brush)FindResource(confirmed ? "PrimarySoft" : "WarnSoft");
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(confirmed ? "Primary" : "Warn");
        StatusIcon.Visibility = confirmed ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = confirmed ? "معتمدة " + PeriodCard.When(p) : "قيد العمل · تُعدّل الحركات بالزر الأيمن";
        ConfirmedBar.Visibility = confirmed ? Visibility.Visible : Visibility.Collapsed;
        ConfirmedText.Text = $"اعتُمدت مطابقة هذه الفترة في {BankReconciliation.ConfirmedTime(p)?.ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture)} وهي مقفلة للقراءة؛ افتح كشفها أو اطبعه متى شئت. للتعديل: الزر الأيمن على الفترة ← «إلغاء الاعتماد».";
        var alerts = review?.Alerts.Where(a => a.Blocking).ToArray() ?? [];
        AlertBar.Visibility = !confirmed && alerts.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AlertTitle.Text = alerts.Length == 1 ? "تنبيه" : $"{alerts.Length:N0} تنبيهات";
        AlertText.Text = string.Join(" · ", alerts.Take(2).Select(a => a.Text));
        AlertBar.ToolTip = string.Join("\n", alerts.Select(a => "• " + a.Text));
        var unmatched = p.Movements.Count(m => !m.Matched);
        ConfirmText.Text = confirmed ? "فتح كشف الفترة المعتمد" : unmatched > 0 ? $"اعتماد مطابقة الفترة (بقي {unmatched:N0} غير مؤشرة)" : "اعتماد مطابقة الفترة";
        ConfirmIcon.Text = confirmed ? "" : "";
        ConfirmButton.IsEnabled = p.Movements.Count > 0 && (confirmed || unmatched == 0);
        FooterHint.Text = confirmed ? "الفترة معتمدة؛ الحركات للقراءة فقط." : "انقر الدائرة لتأشير الحركة بعد مطابقتها مع كشف المصرف الورقي · الزر الأيمن لخيارات الحركة";
    }

    /// فحص مصادر الشهر وبياناته في الخلفية لتنبيهات الفترة (المبالغ المقترحة، المصادر المتغيرة، الرواتب والعقود والتنفيذ).
    private async void ReviewSources(BankPeriod period)
    {
        var ticket = ++reviewRequest;
        if (period.IsConfirmed) { SourceLoading.Visibility = Visibility.Collapsed; return; }
        var delay = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        delay.Tick += (_, _) => { delay.Stop(); if (ticket == reviewRequest) SourceLoading.Visibility = Visibility.Visible; };
        delay.Start();
        try
        {
            var result = await Task.Run(() =>
            {
                if (!sourceIsCurrent()) throw new InvalidDataException("تغيّرت بيانات التطبيق؛ أعد فتح دفتر المطابقة.");
                ReconciliationSuggestionResult? data = null; string? error = null; BankSummary? summary = null;
                try { data = ReconciliationSuggestions.Prepare(source, period.Month); }
                catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException or OverflowException) { error = ex.Message; }
                if (source.Months.Any(m => m.Name == period.Month))
                    try { summary = BankStatementCalculator.Calculate(source, period.Month); }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException or OverflowException) { error ??= ex.Message; }
                return ReconciliationReview.CheckSources(period, data, summary, error);
            });
            if (ticket != reviewRequest || Current?.Id != period.Id) return;
            sources = result; sourcesPeriod = period.Id;
            ShowPeriod();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or OverflowException)
        {
            if (ticket != reviewRequest || Current?.Id != period.Id) return;
            sources = new([new ReconciliationAlert("تعذّر فحص مصادر الشهر: " + ex.Message)], [], 0); sourcesPeriod = period.Id;
            ShowPeriod();
        }
        finally { delay.Stop(); if (ticket == reviewRequest) SourceLoading.Visibility = Visibility.Collapsed; }
    }
    private void ShowAlerts(object sender, RoutedEventArgs e)
    {
        if (Current is not { } p || Review(p) is not { } review) return;
        var text = string.Join("\n", review.Alerts.Select(a => (a.Blocking ? "• " : "◦ ") + a.Text));
        Ui.Message(this, text.Length == 0 ? "لا توجد تنبيهات." : text, "تنبيهات الفترة " + Current?.Month, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// recheck: الإضافة والتعديل والحذف تعيد فحص المصادر؛ التأشير لا يغيّرها.
    private bool Save(BankPeriod updated, Guid? keepMovement = null, bool recheck = true)
    {
        try
        {
            if (!sourceIsCurrent()) throw new InvalidDataException("تغيّرت بيانات التطبيق؛ أعد فتح دفتر المطابقة قبل الحفظ.");
            var all = state.Periods.Where(p => p.Id != updated.Id).Concat([updated]).ToArray();
            state = store.Save(source, state, all);
            if (recheck) sources = null;
            Refresh(updated.Id); ShowPeriod(keepMovement);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException)
        { Ui.Message(this, "لم تُحفظ التغييرات.\n" + ex.Message, "مطابقة الحساب", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
    }
    /// الفترة المعتمدة مقفلة؛ تعديلها يحتاج إلغاء الاعتماد أولاً.
    private bool Editable(string title)
    {
        if (Current is not { } p) return false;
        if (!p.IsConfirmed) return true;
        Ui.Message(this, $"فترة {p.Month} معتمدة ومقفلة. لتعديلها اختر «إلغاء الاعتماد للتعديل» من الزر الأيمن على الفترة.", title, MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    // ——— الفترات ———

    private void PeriodMenuOpened(object sender, RoutedEventArgs e)
    {
        var p = Current;
        PeriodPreviewItem.IsEnabled = PeriodPrintItem.IsEnabled = PeriodSaveItem.IsEnabled = p is { Movements.Count: > 0 };
        PeriodConfirmItem.Visibility = p is { IsConfirmed: true } ? Visibility.Collapsed : Visibility.Visible;
        PeriodUnconfirmItem.Visibility = p is { IsConfirmed: true } ? Visibility.Visible : Visibility.Collapsed;
        PeriodConfirmItem.IsEnabled = p is { Movements.Count: > 0 } && p.Movements.All(m => m.Matched);
        PeriodEditItem.IsEnabled = PeriodDeleteItem.IsEnabled = p is { IsConfirmed: false };
        PeriodCopyItem.IsEnabled = p is not null;
    }
    private void AddPeriod(object sender, RoutedEventArgs e)
    {
        var template = state.Periods.OrderByDescending(p => p.Start).FirstOrDefault();
        var editor = new ReconciliationEditor(null, suggestedMonth, template) { Owner = this };
        if (editor.ShowDialog() == true && editor.ResultPeriod is { } value) Save(value);
    }
    private void EditPeriod(object sender, RoutedEventArgs e)
    {
        if (Current is not { } p || !Editable("تعديل الفترة")) return;
        var editor = new ReconciliationEditor(p, suggestedMonth, null) { Owner = this };
        if (editor.ShowDialog() == true && editor.ResultPeriod is { } value) Save(value);
    }
    private void DeletePeriod(object sender, RoutedEventArgs e)
    {
        if (Current is not { } p || !Editable("حذف الفترة")) return;
        if (Ui.Message(this, $"حذف فترة {p.Month} وكل حركاتها ({p.Movements.Count:N0})؟ لا يمكن التراجع إلا بإعادة فتح نسخة سابقة.", "حذف الفترة", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            if (!sourceIsCurrent()) throw new InvalidDataException("تغيّرت بيانات التطبيق؛ أعد فتح دفتر المطابقة قبل الحفظ.");
            state = store.Save(source, state, state.Periods.Where(x => x.Id != p.Id).ToArray()); sources = null; Refresh(null);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException)
        { Ui.Message(this, "لم تُحذف الفترة.\n" + ex.Message, "حذف الفترة", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void CopySummary(object sender, RoutedEventArgs e)
    {
        if (Current is not { } p) return;
        try
        {
            var t = BankReconciliation.Calculate(p);
            var text = new StringBuilder();
            text.AppendLine($"مطابقة حساب البنك · {p.Month}").AppendLine("الفترة: " + new PeriodCard { Period = p, StatusText = "", Detail = "" }.Range)
                .AppendLine($"الرصيد الافتتاحي: {Number(p.OpeningBalance)}").AppendLine($"الدائن: {Number(t.Credits)}").AppendLine($"المدين: {Number(t.Debits)}")
                .AppendLine($"الرصيد: {Number(t.Balance)}").AppendLine(p.IsConfirmed ? $"الحالة: معتمدة {PeriodCard.When(p)}" : $"الحالة: قيد العمل ({p.Movements.Count(m => m.Matched):N0} من {p.Movements.Count:N0} مؤشرة)");
            Clipboard.SetText(text.ToString());
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Runtime.InteropServices.COMException) { Ui.Message(this, "تعذّر النسخ.\n" + ex.Message, "نسخ الملخص"); }
    }

    // ——— الاعتماد ———

    private void ConfirmOrOpen(object sender, RoutedEventArgs e)
    {
        if (Current is { IsConfirmed: true }) PreviewPdf(sender, e); else ConfirmPeriod(sender, e);
    }
    private void ConfirmPeriod(object sender, RoutedEventArgs e)
    {
        if (Current is not { } p || p.IsConfirmed) return;
        if (p.Movements.Count == 0 || p.Movements.Any(m => !m.Matched))
        { Ui.Message(this, "أشّر كل الحركات بعد مطابقتها مع كشف المصرف الورقي قبل اعتماد الفترة.", "اعتماد المطابقة", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (Review(p) is not { } current) return;
        var dialog = new ReconciliationConfirmWindow(p, current, sources is not null && sourcesPeriod == p.Id) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (Save(BankReconciliation.Confirm(p, DateTime.UtcNow)))
            Ui.Message(this, $"اعتُمدت مطابقة فترة {p.Month} وحُفظت. تجدها في أي وقت في قائمة الفترات، ويُفتح كشفها المعتمد من الزر الأيمن.", "اعتماد المطابقة", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private void UnconfirmPeriod(object sender, RoutedEventArgs e)
    {
        if (Current is not { IsConfirmed: true } p) return;
        if (Ui.Message(this, $"إلغاء اعتماد فترة {p.Month} يعيدها «قيد العمل» للتعديل، ويلزم اعتمادها من جديد بعد المراجعة. هل تريد المتابعة؟", "إلغاء الاعتماد", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        Save(BankReconciliation.Unconfirm(p));
    }

    // ——— الحركات ———

    private void MovementMenuOpened(object sender, RoutedEventArgs e)
    {
        var editable = Current is { IsConfirmed: false }; var any = CurrentMovement is not null;
        ToggleItem.IsEnabled = EditMovementItem.IsEnabled = DeleteMovementItem.IsEnabled = editable && any;
        ToggleItem.Header = CurrentMovement is { Matched: true } ? "إلغاء التأشير" : "تأشير كمطابقة";
        CopyMovementItem.IsEnabled = any;
        AddMovementItem.IsEnabled = SuggestionsItem.IsEnabled = EditPeriodItem.IsEnabled = MarkAllItem.IsEnabled = editable;
        ConfirmItem.IsEnabled = editable && Current is { Movements.Count: > 0 } p && p.Movements.All(m => m.Matched);
    }
    private void AddMovement(object sender, RoutedEventArgs e)
    {
        if (Current is not { } p || !Editable("إضافة حركة")) return;
        var editor = new ReconciliationEditor(p, (BankMovement?)null) { Owner = this };
        if (editor.ShowDialog() == true && editor.ResultMovement is { } value) Save(p with { Movements = p.Movements.Concat([value]).ToArray() }, value.Id);
    }
    private void EditMovement(object sender, RoutedEventArgs e)
    {
        if (Current is not { } p || CurrentMovement is not { } m || !Editable("تعديل الحركة")) return;
        var editor = new ReconciliationEditor(p, m) { Owner = this };
        if (editor.ShowDialog() == true && editor.ResultMovement is { } value) Save(p with { Movements = p.Movements.Select(x => x.Id == value.Id ? value : x).ToArray() }, value.Id);
    }
    private void LinesDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject node && ItemsControl.ContainerFromElement(Lines, node) is DataGridRow) EditMovement(sender, e);
    }
    private void ToggleRow(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is MovementRow row) { Lines.SelectedItem = row; ToggleMatched(sender, e); }
    }
    private void ToggleMatched(object sender, RoutedEventArgs e)
    {
        if (Current is not { } p || CurrentMovement is not { } m || !Editable("تأشير الحركة")) return;
        Save(p with { Movements = p.Movements.Select(x => x.Id == m.Id ? x with { Matched = !x.Matched } : x).ToArray() }, m.Id, recheck: false);
    }
    private void MarkAll(object sender, RoutedEventArgs e)
    {
        if (Current is not { } p || !Editable("تأشير الحركات") || p.Movements.All(m => m.Matched)) return;
        if (Ui.Message(this, "تأشير كل الحركات كمطابقة؟ أشّرها فقط بعد مطابقة كل حركة مع كشف المصرف الورقي.", "تأشير الحركات", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Save(p with { Movements = p.Movements.Select(x => x with { Matched = true }).ToArray() }, recheck: false);
    }
    private void DeleteMovement(object sender, RoutedEventArgs e)
    {
        if (Current is not { } p || CurrentMovement is not { } m || !Editable("حذف الحركة")) return;
        if (Ui.Message(this, $"حذف الحركة «{m.Description}» ({Number(m.Amount)})؟", "حذف حركة", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Save(p with { Movements = p.Movements.Where(x => x.Id != m.Id).ToArray() });
    }
    private void CopyMovement(object sender, RoutedEventArgs e)
    {
        if (CurrentRow is not { } row) return;
        try { Clipboard.SetText(string.Join("\t", row.Date, row.Description, row.Credit, row.Debit, row.Balance)); }
        catch (System.Runtime.InteropServices.COMException) { }
    }
    private async void OpenSuggestions(object sender, RoutedEventArgs e)
    {
        if (Current is not { } period || !Editable("المبالغ المقترحة")) return;
        var revision = state.Revision; SuggestionsItem.IsEnabled = false;
        try
        {
            var data = await Task.Run(() => { if (!sourceIsCurrent()) throw new InvalidDataException("تغيّرت بيانات التطبيق؛ أعد فتح دفتر المطابقة."); return ReconciliationSuggestions.Prepare(source, period.Month); });
            if (Current?.Id != period.Id || state.Revision != revision) { Ui.Message(this, "تغيّرت الفترة أثناء احتساب المقترحات؛ أعد فتحها.", "المبالغ المقترحة"); return; }
            var dialog = new ReconciliationSuggestionsWindow(period, data) { Owner = this };
            if (dialog.ShowDialog() == true && dialog.Result is { } updated) Save(updated);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException or OverflowException)
        { Ui.Message(this, "تعذّر حساب المقترحات.\n" + ex.Message, "المبالغ المقترحة", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { SuggestionsItem.IsEnabled = true; }
    }

    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.N) { e.Handled = true; AddPeriod(sender, e); return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N) { e.Handled = true; AddMovement(sender, e); return; }
        if (!Lines.IsKeyboardFocusWithin || CurrentRow is null) return;
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; ToggleMatched(sender, e); }
        else if (e.Key == Key.Delete) { e.Handled = true; DeleteMovement(sender, e); }
        else if (e.Key == Key.Enter) { e.Handled = true; EditMovement(sender, e); }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; CopyMovement(sender, e); }
    }

    // ——— الكشف ———

    private bool Complete(BankPeriod period) => period.IsConfirmed || (sources is not null && sourcesPeriod == period.Id && Review(period) is { Complete: true });
    private async void PreviewPdf(object sender, RoutedEventArgs e) { if (Current is { Movements.Count: > 0 } p) await ReconciliationFiles.Preview(this, source, p, Complete(p)); }
    private async void PrintPdf(object sender, RoutedEventArgs e) { if (Current is { Movements.Count: > 0 } p) await ReconciliationFiles.Print(this, source, p, Complete(p)); }
    private async void ExportPdf(object sender, RoutedEventArgs e) { if (Current is { Movements.Count: > 0 } p) await ReconciliationFiles.Save(this, source, p, Complete(p)); }
}
