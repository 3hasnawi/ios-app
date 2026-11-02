using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using SalaryDesktop.Core;

namespace SalaryDesktop;

/// صفحة كشف حساب البنك: بطاقات الحالة، جدول الكشف بأقسامه (المبالغ اليدوية تُعدّل من السطر نفسه)، وفترات المطابقة.
/// كل الإجراءات من الأزرار العلوية أو الزر الأيمن، وتفتح في نوافذ منبثقة.
public partial class MainWindow
{
    private sealed class BankLine
    {
        public required string Section { get; init; }
        public required string Title { get; init; }
        public required decimal Amount { get; init; }
        public string Value => Amount.ToString("#,0.##", Numbers);
        public string? ManualKey { get; init; }
        public bool IsTotal { get; init; }
        public bool IsBasis { get; init; }
        /// يدخل في كشف PDF/Excel (المجاميع الفرعية وأساس الحساب للعرض فقط).
        public bool InSheet { get; init; } = true;
        public Visibility ManualVisibility => ManualKey is null ? Visibility.Collapsed : Visibility.Visible;
    }
    private BankSummary? bankSummary;
    private string bankSummaryMonth = "";

    private async void RenderBank()
    {
        var ticket = ++bankRequest;
        var source = snapshot;
        var month = SelectedMonth;
        BankRows.ItemsSource = null; BankTotalText.Text = "—"; BankReconcileText.Text = ""; BankReconcileHint.Text = ""; bankSummary = null;
        RenderBankPeriods(source);
        if (source is null || month.Length == 0) { BankNotice.Text = "افتح نسخة الآيفون واختر الشهر أولاً."; return; }
        BankNotice.Text = "جارٍ احتساب بيانات الشهر محلياً…";
        BeginLoading("bank", "جارٍ احتساب كشف البنك…");
        try
        {
            var result = await Task.Run(() => BankStatementCalculator.Calculate(source, month));
            if (ticket != bankRequest || !ReferenceEquals(source, snapshot) || month != SelectedMonth || page != "bank") return;
            bankSummary = result; bankSummaryMonth = month;
            BankTotalText.Text = Amount(result.BankTotal);
            var warnings = result.Warnings.ToList();
            if (!result.ExecutionMatched) warnings.Add("مطابقة ملف جهات التنفيذ غير مكتملة أو المبلغ مختلف.");
            BankNotice.Text = warnings.Count == 0 ? "لا تنبيهات؛ راجع المبلغ مع كشف المصرف." : "مبلغ أولي · " + string.Join(" · ", warnings);
            BankNotice.ToolTip = BankNotice.Text;
            var lines = new List<BankLine>();
            void Add(string section, string title, decimal amount, string? manual = null, bool basis = false, bool inSheet = true) =>
                lines.Add(new BankLine { Section = section, Title = title, Amount = amount, ManualKey = manual, IsBasis = basis, InSheet = inSheet && !basis });
            const string net = "صافي الرواتب", manualSection = "مبالغ تُضاف للرواتب · يدوية لهذا الشهر", deductions = "الاستقطاعات المحتسبة", contracts = "التقاعد والضمان الاجتماعي · عقود";
            Add(net, "صافي الرواتب كاش", result.Cash); Add(net, "صافي الرواتب بطاقة", result.Card);
            if (result.Unclassified != 0 || result.UnclassifiedCount > 0) Add(net, $"صافي غير مصنف ({result.UnclassifiedCount:N0})", result.Unclassified);
            Add(net, "مجموع الصافي", result.Net, inSheet: false);
            Add(manualSection, "الإجازات المتراكمة", result.Manual.AccumulatedLeave, "leave");
            Add(manualSection, "مكافآت المنتسبين", result.Manual.EmployeeRewards, "rewards");
            Add(manualSection, "قائمة مفردة", result.Manual.SingleList, "single");
            Add(deductions, "مجموع استقطاعات المصارف", result.BankDeductions);
            foreach (var (title, amount) in result.Breakdown) Add(deductions, title, amount);
            Add(contracts, $"اسمي العقود · أساس الحساب ({result.ContractCount:N0})", result.ContractNominal, basis: true);
            Add(contracts, "12% من الاسمي", result.ContractNominalShare);
            Add(contracts, "توقيفات العقود", result.ContractRetirement);
            lines.Add(new BankLine { Section = "المجموع", Title = "المجموع للمراجعة مع المصرف", Amount = result.BankTotal, IsTotal = true, InSheet = false });
            var view = new ListCollectionView(lines);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(BankLine.Section)));
            BankRows.ItemsSource = view;
            BankReconcileText.Text = result.ExecutionMatched ? $"متطابقة · {Amount(result.ExecutionDeductions)}" : $"الفرق {Amount(result.ExecutionDifference)}";
            BankReconcileText.Foreground = (System.Windows.Media.Brush)FindResource(result.ExecutionMatched ? "Primary" : "Warn");
            BankReconcileHint.Text = result.ExecutionMatched ? "عمود المصارف = ملف جهات التنفيذ" :
                $"ملف جهات التنفيذ {Amount(result.ExecutionDeductions)} · قيود بلا مبلغ {result.MissingExecutionAmounts:N0}";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException or OverflowException)
        {
            if (ticket == bankRequest && ReferenceEquals(source, snapshot) && month == SelectedMonth && page == "bank")
                BankNotice.Text = "تعذّر احتساب كشف البنك: " + ex.Message;
        }
        finally { if (ticket == bankRequest) EndLoading("bank"); }
    }

    /// فترات المطابقة المحفوظة لهذه النسخة، وبطاقة «مطابقة الشهر السابق» للشهر المختار.
    private void RenderBankPeriods(BackupSnapshot? source)
    {
        BankPeriods.ItemsSource = null; BankLastPeriodText.Text = "—"; BankLastPeriodHint.Text = ""; BankLastPeriodText.Tag = null;
        if (source is null) { BankPeriodsEmpty.Visibility = Visibility.Visible; return; }
        try
        {
            var periods = reconciliationStore.Load(source).Periods;
            RefreshBankBadge(periods.Count(p => !p.IsConfirmed));
            var cards = PeriodCard.List(periods);
            BankPeriods.ItemsSource = cards;
            BankPeriodsEmpty.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            var previous = MonthNames.Previous(SelectedMonth);
            var latest = cards.FirstOrDefault(c => previous is not null && MonthNames.SortKey(c.Month) == previous) ?? cards.FirstOrDefault();
            if (latest is null) { BankLastPeriodText.Text = "لا توجد فترة بعد"; BankLastPeriodHint.Text = "أضف فترة من «فترة جديدة»."; return; }
            BankLastPeriodText.Text = $"{latest.Month} · " + (latest.IsConfirmed ? "معتمدة" : "قيد العمل");
            BankLastPeriodText.Foreground = (System.Windows.Media.Brush)FindResource(latest.IsConfirmed ? "Primary" : "Warn");
            BankLastPeriodHint.Text = latest.IsConfirmed ? $"اعتُمدت {PeriodCard.When(latest.Period)} · {latest.Detail}" : latest.StatusText.Replace("قيد العمل · ", "") + " · " + latest.Detail;
            BankLastPeriodText.Tag = latest.Period.Id;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { BankPeriodsEmpty.Visibility = Visibility.Visible; BankLastPeriodText.Text = "تعذّر قراءة الدفتر"; BankLastPeriodHint.Text = ex.Message; }
    }

    private BankLine? SelectedBankLine => BankRows.SelectedItem as BankLine;
    private void BankMenuOpened(object sender, RoutedEventArgs e)
    {
        BankEditManualItem.IsEnabled = SelectedBankLine?.ManualKey is not null;
        BankCopyAmountItem.IsEnabled = BankCopyLineItem.IsEnabled = SelectedBankLine is not null;
    }
    private void BankRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject node && ItemsControl.ContainerFromElement(BankRows, node) is ListBoxItem && SelectedBankLine?.ManualKey is not null) EditBankManual(sender, e);
    }
    private void BankRowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; CopyBankLine(sender, e); }
        else if (e.Key is Key.Enter or Key.F2 && SelectedBankLine?.ManualKey is not null) { e.Handled = true; EditBankManual(sender, e); }
    }
    private void CopyText(string text)
    {
        try { Clipboard.SetText(text); StatusBar.Text = "نُسخ إلى الحافظة."; }
        catch (System.Runtime.InteropServices.COMException) { StatusBar.Text = "تعذّر النسخ؛ الحافظة مشغولة."; }
    }
    private void CopyBankAmount(object sender, RoutedEventArgs e) { if (SelectedBankLine is { } line) CopyText(line.Amount.ToString("0.##", Numbers)); }
    private void CopyBankLine(object sender, RoutedEventArgs e) { if (SelectedBankLine is { } line) CopyText($"{line.Title}\t{line.Value}"); }
    private void CopyBankAll(object sender, RoutedEventArgs e)
    {
        if (BankRows.ItemsSource is not ListCollectionView view) return;
        var text = new StringBuilder($"كشف حساب البنك\t{bankSummaryMonth}\n");
        foreach (BankLine line in view.SourceCollection) text.Append(line.Title).Append('\t').Append(line.Value).Append('\n');
        CopyText(text.ToString());
    }

    // ——— الكشف PDF / Excel بالشكل المعتمد ———

    private (string Name, ReportSheet Sheet)? BankSheet()
    {
        if (snapshot is not { } source || bankSummary is null || BankRows.ItemsSource is not ListCollectionView view) { Ui.Message(this, "انتظر احتساب كشف الشهر أولاً.", "كشف حساب البنك"); return null; }
        var lines = view.SourceCollection.Cast<BankLine>().Where(l => l.InSheet).ToArray();
        var title = $"كشف حساب البنك · {bankSummaryMonth}";
        try
        {
            var sheet = TableReport.Build(ReportSheet.HeaderFor(source), title, ["البند", "المبلغ"], 1, lines.Select(l => new[] { l.Title, "" }).ToArray(), lines.Select(l => (decimal?)l.Amount).ToArray());
            return (title, sheet);
        }
        catch (InvalidDataException ex) { Ui.Message(this, ex.Message, "كشف حساب البنك"); return null; }
    }
    private async void PreviewBankStatement(object sender, RoutedEventArgs e) { if (BankSheet() is { } report) await Exports.Preview(this, report.Name, assets => ReportPdf.Generate([report.Sheet], assets, DateTime.Now)); }
    private async void PrintBankStatement(object sender, RoutedEventArgs e) { if (BankSheet() is { } report) await Exports.Print(this, report.Name, assets => ReportPdf.Generate([report.Sheet], assets, DateTime.Now)); }
    private async void SaveBankStatementPdf(object sender, RoutedEventArgs e) { if (BankSheet() is { } report) await Exports.SavePdf(this, report.Name, assets => ReportPdf.Generate([report.Sheet], assets, DateTime.Now)); }
    private async void SaveBankStatementExcel(object sender, RoutedEventArgs e) { if (BankSheet() is { } report) await Exports.SaveExcel(this, report.Name, () => XlsxReport.Generate(report.Sheet)); }

    // ——— بيانات الحساب والمبالغ اليدوية ———

    private void EditBankManual(object sender, RoutedEventArgs e) => EditBank(SelectedBankLine?.ManualKey);
    private void EditBankDetails(object sender, RoutedEventArgs e) => EditBank(null);
    private async void EditBank(string? focus)
    {
        var active = snapshot; var month = SelectedMonth; var original = baseSnapshot; var links = linkState; var root = rootSnapshot; var payroll = payrollState;
        if (active is null || original is null || links is null || root is null || payroll is null || month.Length == 0) { Ui.Message(this, "افتح نسخة الآيفون واختر الشهر أولاً.", "بيانات الحساب"); return; }
        try
        {
            var account = TransferPreparation.Account(active); var manual = BankStatementCalculator.Manual(active, month);
            var dialog = new BankDetailsWindow(month, account, manual, focus) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Result is not { } input) return;
            var updated = await Task.Run(() =>
            {
                if (payrollStore.Load(root).Revision != payroll.Revision) throw new InvalidDataException("تغيّرت بيانات الأشهر؛ أعد فتح النافذة.");
                return linkStore.SaveBankDetails(original, links, month, input.Name, input.Iban, input.Amounts, input.Branch, input.LocalAccount);
            });
            if (!ReferenceEquals(active, snapshot) || !ReferenceEquals(root, rootSnapshot)) { Ui.Message(this, "حُفظت البيانات للنسخة السابقة؛ لم تتغير النسخة المعروضة.", "بيانات الحساب"); return; }
            linkState = updated; snapshot = updated.Snapshot; RenderPage();
            StatusBar.Text = "حُفظت بيانات الحساب ومبالغ الشهر محلياً؛ تدخل في النسخة الاحتياطية التالية.";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException)
        { Ui.Message(this, "تعذّر حفظ بيانات الحساب.\n" + ex.Message, "بيانات الحساب", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    // ——— فترات المطابقة ———

    private PeriodCard? SelectedBankPeriod => BankPeriods.SelectedItem as PeriodCard;
    private void OpenReconciliation(object sender, RoutedEventArgs e) => OpenLedger(null, false);
    private void NewReconciliationPeriod(object sender, RoutedEventArgs e) => OpenLedger(null, true);
    private void OpenSelectedBankPeriod(object sender, RoutedEventArgs e) => OpenLedger(SelectedBankPeriod?.Period.Id, false);
    private void OpenLatestPeriod(object sender, MouseButtonEventArgs e) => OpenLedger(BankLastPeriodText.Tag as Guid?, false);
    private void BankPeriodDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject node && ItemsControl.ContainerFromElement(BankPeriods, node) is ListBoxItem) OpenSelectedBankPeriod(sender, e);
    }
    private void BankPeriodMenuOpened(object sender, RoutedEventArgs e) =>
        BankPeriodOpenItem.IsEnabled = BankPeriodPreviewItem.IsEnabled = BankPeriodPrintItem.IsEnabled = BankPeriodSaveItem.IsEnabled = SelectedBankPeriod is not null;
    private async void PreviewBankPeriod(object sender, RoutedEventArgs e) { if (snapshot is { } s && SelectedBankPeriod is { } c) await ReconciliationFiles.Preview(this, s, c.Period, false); }
    private async void PrintBankPeriod(object sender, RoutedEventArgs e) { if (snapshot is { } s && SelectedBankPeriod is { } c) await ReconciliationFiles.Print(this, s, c.Period, false); }
    private async void SaveBankPeriod(object sender, RoutedEventArgs e) { if (snapshot is { } s && SelectedBankPeriod is { } c) await ReconciliationFiles.Save(this, s, c.Period, false); }

    private void OpenLedger(Guid? period, bool create)
    {
        if (snapshot is null || rootSnapshot is not { } root || baseSnapshot is not { } original || payrollState is not { } payroll || linkState is not { } links) { Ui.Message(this, "افتح البيانات أولاً.", "دفتر المطابقة"); return; }
        bool IsCurrent() => payrollStore.Load(root).Revision == payroll.Revision && linkStore.Load(original).Revision == links.Revision;
        try { new ReconciliationWindow(snapshot, SelectedMonth, IsCurrent, period, create) { Owner = this }.ShowDialog(); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException or OverflowException)
        { Ui.Message(this, "تعذّر فتح دفتر المطابقة المحفوظ. لم تُغيّر البيانات.\n" + ex.Message, "مطابقة الحساب", MessageBoxButton.OK, MessageBoxImage.Warning); }
        RefreshBackupReminder(true);
        if (page == "bank") RenderBankPeriods(snapshot);
    }
}
