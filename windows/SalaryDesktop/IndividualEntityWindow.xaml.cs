using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SalaryDesktop.Core;

namespace SalaryDesktop;

public partial class IndividualEntityWindow : Window
{
    private sealed record Choice(string Id, string Title);
    private sealed record Candidate(TransferEntry Entry, IndividualEntityPlan? Plan, string? Problem) { public string Title => Entry.Title + (Plan is null ? " · يحتاج مراجعة" : ""); }
    private readonly BackupSnapshot source; private readonly TransferSnapshot transfer; private readonly LocalTransferLinks store;
    private readonly IReadOnlyDictionary<string, byte[]> references; private readonly Func<bool> payrollCurrent;
    private bool working; private bool suppress; private IndividualEntityPlan? plan;
    public TransferLinksSnapshot State { get; private set; }
    public IndividualEntityWindow(BackupSnapshot source, TransferLinksSnapshot state, TransferSnapshot transfer, LocalTransferLinks store, IReadOnlyDictionary<string, byte[]> references, Func<bool> payrollCurrent)
    {
        InitializeComponent(); this.source = source; State = state; this.transfer = transfer; this.store = store; this.references = references; this.payrollCurrent = payrollCurrent;
        Heading.Text = "كتب جهات التنفيذ المنفردة · " + transfer.Month;
        Closing += (_, e) => { if (working) e.Cancel = true; };
        BookPicker.ItemsSource = new[] { new Choice("ramadi", "تنفيذ الرمادي"), new Choice("housing", "صندوق الإسكان"), new Choice("water", "الموارد المائية"), new Choice("rafidain_anbar", "مصرف الرافدين فرع الأنبار") };
        BookPicker.SelectedIndex = 0;
    }
    private void Busy(bool value) { working = value; BookPicker.IsEnabled = !value; EntityPicker.IsEnabled = !value; SettingsButton.IsEnabled = !value; ExportButton.IsEnabled = !value && plan is not null; }
    private async void BookChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BookPicker.SelectedItem is not Choice choice || EntityPicker is null) return;
        plan = null; suppress = true; EntityPicker.ItemsSource = null; suppress = false; AmountText.Text = "—"; AccountText.Text = "—"; Status.Text = "جارٍ مطابقة حسابات الجهات مع مرجع الكتاب…"; Busy(true);
        try
        {
            var state = State;
            var candidates = await Task.Run(() => {
                if (!payrollCurrent() || store.Load(source).Revision != state.Revision) throw new InvalidDataException("تغيّرت بيانات الشهر؛ أعد فتح الكتاب.");
                if (transfer.SourceWarnings.Count > 0) throw new InvalidDataException(string.Join("\n", transfer.SourceWarnings));
                _ = LetterReferencePack.IndividualAccount(references, choice.Id, transfer.PayerAccount);
                var result = new List<Candidate>();
                foreach (var entry in transfer.Entries.Where(e => e.Group == TransferGroup.Anbar && e.Id.StartsWith("deduction|", StringComparison.Ordinal)))
                    try { result.Add(new(entry, IndividualEntityPreparation.Prepare(state.Snapshot, transfer, choice.Id, entry.Id, references), null)); }
                    catch (InvalidDataException ex) { result.Add(new(entry, null, ex.Message)); }
                return result;
            });
            suppress = true; EntityPicker.ItemsSource = candidates;
            var hasSaved = IndividualLetterLinks.Read(State.Snapshot).ContainsKey(choice.Id);
            var valid = candidates.Where(c => c.Plan is not null).ToArray();
            var saved = IndividualLetterLinks.Resolve(State.Snapshot, choice.Id, valid.Select(c => c.Entry).ToArray());
            var selected = saved is null ? (!hasSaved && valid.Length == 1 ? valid[0] : null) : valid.SingleOrDefault(c => c.Entry == saved);
            EntityPicker.SelectedItem = selected; suppress = false;
            if (selected is null) Status.Text = candidates.Count == 0 ? "لا توجد جهات لفرع الأنبار في الشهر." : hasSaved ? "تغيّرت الجهة أو حسابها منذ الربط السابق؛ راجعها واخترها من جديد." : "اختر جهة الكتاب؛ الجهات التي تحتاج مراجعة موضحة في القائمة.";
            else { Busy(false); await Select(selected, choice); return; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException)
        { Status.Text = "تعذّر مطابقة الجهات: " + ex.Message; }
        finally { if (working) Busy(false); }
    }
    private async void EntityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppress || BookPicker.SelectedItem is not Choice choice || EntityPicker.SelectedItem is not Candidate selected) return;
        await Select(selected, choice);
    }
    private async Task Select(Candidate selected, Choice choice)
    {
        plan = null;
        if (selected.Plan is null)
        {
            AmountText.Text = selected.Entry.Amount.ToString("N0", CultureInfo.GetCultureInfo("en-US"));
            AccountText.Text = selected.Entry.Account?.Iban ?? "—";
            Status.Text = (selected.Problem ?? "راجع جهة الكتاب.") + " يمكن مراجعة حساب المستفيد من تخصيص الكتاب."; Busy(false); return;
        }
        Busy(true); Status.Text = "جارٍ حفظ ربط الجهة والتحقق من قيودها…";
        try
        {
            var state = State;
            var next = await Task.Run(() => {
                if (!payrollCurrent() || store.Load(source).Revision != state.Revision) throw new InvalidDataException("تغيّرت البيانات؛ أعد فتح الكتاب.");
                return IndividualLetterLinks.Resolve(state.Snapshot, choice.Id, [selected.Entry]) is null ? store.SaveBookLink(source, state, selected.Entry, choice.Id) : state;
            });
            State = next; plan = selected.Plan; AmountText.Text = plan.Total.ToString("N0", CultureInfo.GetCultureInfo("en-US")); AccountText.Text = plan.BeneficiaryAccount ?? "حساب مصرف الفرع";
            Status.Text = "حُفظ ربط الجهة. راجع الحساب والمبلغ قبل اعتماد المسودة، واحفظ نسخة احتياطية للتعديلات.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException)
        { Status.Text = "تعذّر حفظ الربط: " + ex.Message; }
        finally { Busy(false); }
    }
    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        if (working || BookPicker.SelectedItem is not Choice choice) return;
        try
        {
            if (!payrollCurrent() || store.Load(source).Revision != State.Revision) throw new InvalidDataException("تغيّرت البيانات؛ أعد فتح الكتاب.");
            var selected = EntityPicker.SelectedItem as Candidate;
            var displayed = plan?.BeneficiaryAccount;
            if (displayed is null && selected?.Entry.Account is { } account)
                displayed = choice.Id == "ramadi" ? account.Iban : choice.Id is "housing" or "water" ? TransferPreparation.LocalAccount(account) : null;
            var window = new IndividualSettingsWindow(source, State, store, choice.Id, payrollCurrent, transfer.Month, selected?.Entry.Id, displayed, selected is { Plan: null } && displayed is not null) { Owner = this };
            window.ShowDialog(); State = window.State;
            BookChanged(BookPicker, null!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException)
        { MessageBox.Show(this, ex.Message, "تخصيص الكتاب", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void Export(object sender, RoutedEventArgs e)
    {
        if (working || plan is null || BookPicker.SelectedItem is not Choice choice || choice.Id != plan.Id) return;
        var captured = plan; var state = State;
        var dialog = new SaveFileDialog { Title = "حفظ مسودة الكتاب", Filter = "ملف PDF (*.pdf)|*.pdf", FileName = "مسودة " + choice.Title + ".pdf", AddExtension = true, DefaultExt = ".pdf", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        Busy(true);
        try
        {
            var settings = GroupLetterSettingsReader.Read(state.Snapshot, captured.Id);
            var font = GroupLetterFont.Read(state.Snapshot) ?? throw new InvalidDataException("خط ساكال العريض غير محفوظ؛ استورده من تخصيص كتاب المجموعة أو افتح نسخة تتضمنه.");
            if (!string.IsNullOrWhiteSpace(settings.BodyMonth) && MonthNames.SortKey(settings.BodyMonth) != MonthNames.SortKey(captured.Month) && MessageBox.Show(this, "شهر متن الكتاب يختلف عن شهر الاستقطاعات. هل تريد المتابعة؟", "اختلاف شهر الكتاب", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var logo = Path.Combine(AppContext.BaseDirectory, "Assets", "IraqEmblem.png");
            var changed = await Task.Run(() => { if (!payrollCurrent() || store.Load(source).Revision != state.Revision) throw new InvalidDataException("تغيّرت البيانات؛ أعد فتح الكتاب."); IndividualEntityPdf.Save(captured, settings, font, File.ReadAllBytes(logo), dialog.FileName); return !payrollCurrent() || store.Load(source).Revision != state.Revision; });
            MessageBox.Show(this, changed ? "حُفظت مسودة سابقة وتغيّرت البيانات أثناء الحفظ؛ أعد التصدير." : "حُفظت المسودة بعد فحص حساباتها ومبلغها. راجع النص والتواقيع وقائمة الأسماء قبل الاعتماد.", "الكتب المنفردة", MessageBoxButton.OK, changed ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException or DllNotFoundException or TypeInitializationException)
        { MessageBox.Show(this, "تعذّر إعداد المسودة.\n" + ex.Message, "الكتب المنفردة", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { Busy(false); }
    }
}
