using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SalaryDesktop.Core;

namespace SalaryDesktop;

public partial class IndividualPayrollWindow : Window
{
    private sealed record Choice(string Id, string Title);
    private BackupSnapshot source; private readonly BackupSnapshot original; private readonly LocalTransferLinks store; private readonly Func<bool> payrollCurrent; private readonly BankSummary summary; private readonly string payer; private readonly string payerAccount;
    private readonly IReadOnlyDictionary<string, byte[]> references;
    public TransferLinksSnapshot State { get; private set; }
    private bool Current() => payrollCurrent() && store.Load(original).Revision == State.Revision;
    private IndividualLetterPlan? plan; private CashSalaryList? cash; private int ticket; private bool working;
    public IndividualPayrollWindow(BackupSnapshot original, TransferLinksSnapshot state, LocalTransferLinks store, BankSummary summary, string payer, string payerAccount, IReadOnlyDictionary<string, byte[]> references, Func<bool> payrollCurrent)
    {
        InitializeComponent(); this.original = original; State = state; source = state.Snapshot; this.store = store; this.payrollCurrent = payrollCurrent; this.summary = summary; this.payer = payer; this.payerAccount = payerAccount; this.references = references;
        Heading.Text = "الكتب المنفردة من الرواتب · " + summary.Month;
        ContractText.Text = GroupLetterSettingsReader.FoodContract(source) ?? "";
        Closing += (_, e) => { if (working) e.Cancel = true; };
        BookPicker.ItemsSource = new[] { new Choice("food_company", "شركة عراق الجوال"), new Choice("food_tax", "ضريبة عقد الإطعام"), new Choice("tax_social", "الضريبة والحماية الاجتماعية"), new Choice("contracts", "ضمان العقود"), new Choice("cash_salary", "أمانات رواتب الكاش") };
        BookPicker.SelectedIndex = 0;
    }
    private async void BookChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BookPicker.SelectedItem is not Choice choice || ExportButton is null) return;
        var request = ++ticket; plan = null; cash = null; ExportButton.IsEnabled = false; SettingsButton.IsEnabled = false; AmountText.Text = "—"; AccountText.Text = "—"; Status.Text = "جارٍ فحص مبلغ الكتاب وحسابه…";
        try
        {
            var result = await Task.Run(() => {
                if (!Current()) throw new InvalidDataException("تغيّرت البيانات؛ أعد فتح الكتب.");
                var prepared = IndividualLetterPreparation.Payroll(choice.Id, summary, payer, payerAccount, references, acceptMissingContractNominal: true, customization: source);
                var list = choice.Id == "cash_salary" ? CashSalaryListPreparation.Prepare(source, summary.Month) : null;
                return (prepared, list);
            });
            if (request != ticket) return;
            plan = result.prepared; cash = result.list;
            AmountText.Text = plan.Total.ToString("N0", CultureInfo.GetCultureInfo("en-US")); AccountText.Text = plan.BeneficiaryAccount ?? "—";
            Status.Text = plan.Notices.Count > 0 ? string.Join("\n", plan.Notices) + " يجب تأكيد مراجعة المبلغ قبل التصدير." : "راجع المبلغ والحساب وتنسيق المسودة قبل اعتمادها.";
            ExportButton.IsEnabled = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException)
        { if (request == ticket) Status.Text = "تعذّر إعداد الكتاب: " + ex.Message; }
        finally { if (request == ticket) SettingsButton.IsEnabled = true; }
    }
    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        if (working || BookPicker.SelectedItem is not Choice choice) return;
        try
        {
            if (!Current()) throw new InvalidDataException("تغيّرت البيانات؛ أعد فتح الكتاب.");
            var window = new IndividualSettingsWindow(original, State, store, choice.Id, payrollCurrent, summary.Month, null, plan?.BeneficiaryAccount) { Owner = this };
            window.ShowDialog(); State = window.State; source = State.Snapshot; ContractText.Text = GroupLetterSettingsReader.FoodContract(source) ?? "";
            BookChanged(BookPicker, null!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException)
        { MessageBox.Show(this, ex.Message, "تخصيص الكتاب", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void Export(object sender, RoutedEventArgs e)
    {
        if (working || plan is null || BookPicker.SelectedItem is not Choice choice || choice.Id != plan.Id) return;
        var captured = plan; var capturedCash = cash;
        if (captured.Notices.Count > 0 && MessageBox.Show(this, "يوجد عقد بلا راتب اسمي. هل راجعت المبلغ وتريد تجاوز التنبيه لهذه المسودة؟", "مراجعة ضمان العقود", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var dialog = new SaveFileDialog { Title = "حفظ مسودة الكتاب", Filter = "ملف PDF (*.pdf)|*.pdf", FileName = "مسودة " + choice.Title + ".pdf", AddExtension = true, DefaultExt = ".pdf", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        working = true; BookPicker.IsEnabled = false; ExportButton.IsEnabled = false; ContractText.IsEnabled = false; SettingsButton.IsEnabled = false;
        try
        {
            var settings = GroupLetterSettingsReader.Read(source, captured.Id);
            var font = GroupLetterFont.Read(source) ?? throw new InvalidDataException("خط ساكال العريض غير محفوظ؛ استورده من تخصيص كتاب المجموعة أو افتح نسخة تتضمنه.");
            var tableFont = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf"); var logo = Path.Combine(AppContext.BaseDirectory, "Assets", "IraqEmblem.png"); var reference = ContractText.Text.Trim();
            if (!string.IsNullOrWhiteSpace(settings.BodyMonth) && MonthNames.SortKey(settings.BodyMonth) != MonthNames.SortKey(captured.Month) && MessageBox.Show(this, "شهر متن الكتاب يختلف عن شهر مبالغ الرواتب. هل تريد المتابعة؟", "اختلاف شهر الكتاب", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var changed = await Task.Run(() => { if (!Current()) throw new InvalidDataException("تغيّرت بيانات الشهر؛ أعد فتح الكتاب."); IndividualPayrollPdf.Save(captured, settings, font, File.ReadAllBytes(tableFont), File.ReadAllBytes(logo), reference, capturedCash, dialog.FileName); return !Current(); });
            MessageBox.Show(this, changed ? "حُفظت مسودة سابقة وتغيّرت البيانات أثناء الحفظ؛ أعد التصدير." : "حُفظت المسودة بعد فحص حساباتها ومبلغها. راجع النص والتواقيع قبل الاعتماد.", "الكتب المنفردة", MessageBoxButton.OK, changed ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException or DllNotFoundException or TypeInitializationException)
        { MessageBox.Show(this, "تعذّر إعداد المسودة.\n" + ex.Message, "الكتب المنفردة", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { working = false; BookPicker.IsEnabled = true; ExportButton.IsEnabled = plan is not null; ContractText.IsEnabled = true; SettingsButton.IsEnabled = true; }
    }
}
