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
    private readonly BackupSnapshot source; private readonly BankSummary summary; private readonly string payer; private readonly string payerAccount;
    private readonly IReadOnlyDictionary<string, byte[]> references; private readonly Func<bool> current;
    private IndividualLetterPlan? plan; private CashSalaryList? cash; private int ticket; private bool working;
    public IndividualPayrollWindow(BackupSnapshot source, BankSummary summary, string payer, string payerAccount, IReadOnlyDictionary<string, byte[]> references, Func<bool> current)
    {
        InitializeComponent(); this.source = source; this.summary = summary; this.payer = payer; this.payerAccount = payerAccount; this.references = references; this.current = current;
        Heading.Text = "الكتب المنفردة من الرواتب · " + summary.Month;
        ContractText.Text = GroupLetterSettingsReader.FoodContract(source) ?? "";
        Closing += (_, e) => { if (working) e.Cancel = true; };
        BookPicker.ItemsSource = new[] { new Choice("food_company", "شركة عراق الجوال"), new Choice("food_tax", "ضريبة عقد الإطعام"), new Choice("tax_social", "الضريبة والحماية الاجتماعية"), new Choice("contracts", "ضمان العقود"), new Choice("cash_salary", "أمانات رواتب الكاش") };
        BookPicker.SelectedIndex = 0;
    }
    private async void BookChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BookPicker.SelectedItem is not Choice choice || ExportButton is null) return;
        var request = ++ticket; plan = null; cash = null; ExportButton.IsEnabled = false; AmountText.Text = "—"; AccountText.Text = "—"; Status.Text = "جارٍ فحص مبلغ الكتاب وحسابه…";
        try
        {
            var result = await Task.Run(() => {
                if (!current()) throw new InvalidDataException("تغيّرت البيانات؛ أعد فتح الكتب.");
                var prepared = IndividualLetterPreparation.Payroll(choice.Id, summary, payer, payerAccount, references, acceptMissingContractNominal: true);
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
    }
    private async void Export(object sender, RoutedEventArgs e)
    {
        if (working || plan is null || BookPicker.SelectedItem is not Choice choice || choice.Id != plan.Id) return;
        var captured = plan; var capturedCash = cash;
        if (captured.Notices.Count > 0 && MessageBox.Show(this, "يوجد عقد بلا راتب اسمي. هل راجعت المبلغ وتريد تجاوز التنبيه لهذه المسودة؟", "مراجعة ضمان العقود", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var dialog = new SaveFileDialog { Title = "حفظ مسودة الكتاب", Filter = "ملف PDF (*.pdf)|*.pdf", FileName = "مسودة " + choice.Title + ".pdf", AddExtension = true, DefaultExt = ".pdf", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        working = true; BookPicker.IsEnabled = false; ExportButton.IsEnabled = false; ContractText.IsEnabled = false;
        try
        {
            var settings = GroupLetterSettingsReader.Read(source, captured.Id);
            var font = GroupLetterFont.Read(source) ?? throw new InvalidDataException("خط ساكال العريض غير محفوظ؛ استورده من تخصيص كتاب المجموعة أو افتح نسخة تتضمنه.");
            var tableFont = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf"); var logo = Path.Combine(AppContext.BaseDirectory, "Assets", "IraqEmblem.png"); var reference = ContractText.Text.Trim();
            if (!string.IsNullOrWhiteSpace(settings.BodyMonth) && MonthNames.SortKey(settings.BodyMonth) != MonthNames.SortKey(captured.Month) && MessageBox.Show(this, "شهر متن الكتاب يختلف عن شهر مبالغ الرواتب. هل تريد المتابعة؟", "اختلاف شهر الكتاب", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var changed = await Task.Run(() => { if (!current()) throw new InvalidDataException("تغيّرت بيانات الشهر؛ أعد فتح الكتاب."); IndividualPayrollPdf.Save(captured, settings, font, File.ReadAllBytes(tableFont), File.ReadAllBytes(logo), reference, capturedCash, dialog.FileName); return !current(); });
            MessageBox.Show(this, changed ? "حُفظت مسودة سابقة وتغيّرت البيانات أثناء الحفظ؛ أعد التصدير." : "حُفظت المسودة بعد فحص حساباتها ومبلغها. راجع النص والتواقيع قبل الاعتماد.", "الكتب المنفردة", MessageBoxButton.OK, changed ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException or DllNotFoundException or TypeInitializationException)
        { MessageBox.Show(this, "تعذّر إعداد المسودة.\n" + ex.Message, "الكتب المنفردة", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { working = false; BookPicker.IsEnabled = true; ExportButton.IsEnabled = plan is not null; ContractText.IsEnabled = true; }
    }
}
