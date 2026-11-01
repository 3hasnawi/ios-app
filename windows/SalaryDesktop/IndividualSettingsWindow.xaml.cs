using System.Globalization;
using System.IO;
using System.Windows;
using SalaryDesktop.Core;

namespace SalaryDesktop;

public partial class IndividualSettingsWindow : Window
{
    private readonly BackupSnapshot source; private readonly LocalTransferLinks store; private readonly string id; private readonly Func<bool> payrollCurrent;
    private GroupLetterSettings initial;
    public TransferLinksSnapshot State { get; private set; }
    public IndividualSettingsWindow(BackupSnapshot source, TransferLinksSnapshot state, LocalTransferLinks store, string id, Func<bool> payrollCurrent)
    {
        InitializeComponent(); this.source = source; State = state; this.store = store; this.id = id; this.payrollCurrent = payrollCurrent;
        initial = GroupLetterSettingsReader.Read(state.Snapshot, id);
        Heading.Text = "تخصيص الكتاب"; Notice.Text = "تسري التغييرات المحفوظة على الأشهر القادمة. حقل العدد وعقد الإطعام موحدان لجميع الكتب.";
        HeaderText.Text = initial.Header; NumberText.Text = initial.Number; FoodContractText.Text = initial.FoodContractReference ?? "";
        FirstText.Text = initial.First; SecondText.Text = initial.Second; ThirdText.Text = initial.Third; FooterText.Text = initial.Footer;
        BodyMonthText.Text = initial.BodyMonth ?? ""; OutgoingText.Text = initial.OutgoingMonth?.ToString(CultureInfo.InvariantCulture) ?? "";
        OffsetText.Text = initial.SignatureOffset.ToString(CultureInfo.InvariantCulture); BodyText.Text = initial.BodyTemplate ?? IndividualLetterBody.Default(id);
    }
    private static string Clean(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
    private static string Latin(string text) => string.Concat(text.Trim().Select(c => c switch { >= '٠' and <= '٩' => (char)('0' + c - '٠'), >= '۰' and <= '۹' => (char)('0' + c - '۰'), _ => c }));
    private GroupLetterSettings Fields()
    {
        var text = Latin(OutgoingText.Text); int? month = null;
        if (text.Length > 0) { if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n is < 1 or > 12) throw new InvalidDataException("شهر الصادر يجب أن يكون من ١ إلى ١٢."); month = n; }
        if (!int.TryParse(Latin(OffsetText.Text), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var offset) || offset is < -60 or > 60) throw new InvalidDataException("موضع اللجنة يجب أن يكون بين -٦٠ و٦٠.");
        return new(Clean(HeaderText.Text), Clean(NumberText.Text), Clean(FirstText.Text), Clean(SecondText.Text), Clean(ThirdText.Text), Clean(FooterText.Text), Clean(BodyMonthText.Text), month, offset, Clean(BodyText.Text), Clean(FoodContractText.Text));
    }
    private HashSet<string> Changes(GroupLetterSettings value)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);
        void Add(bool changed, string name) { if (changed) fields.Add(name); }
        Add(value.Header != initial.Header, "header"); Add(value.Number != initial.Number, "fixedNumber"); Add(value.First != initial.First, "committeeFirst");
        Add(value.Second != initial.Second, "committeeSecond"); Add(value.Third != initial.Third, "committeeThird"); Add(value.Footer != initial.Footer, "footer");
        Add((value.BodyMonth ?? "") != (initial.BodyMonth ?? ""), "bodyMonth"); Add(value.OutgoingMonth != initial.OutgoingMonth, "outgoingMonth");
        Add(value.SignatureOffset != initial.SignatureOffset, "signatureOffset"); Add(value.BodyTemplate != (initial.BodyTemplate ?? IndividualLetterBody.Default(id)), "bodyTemplate");
        Add((value.FoodContractReference ?? "") != (initial.FoodContractReference ?? ""), "foodContractReference"); return fields;
    }
    private void SaveSettings(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!payrollCurrent() || store.Load(source).Revision != State.Revision) throw new InvalidDataException("تغيّرت البيانات؛ أعد فتح التخصيص.");
            var value = Fields(); var changes = Changes(value);
            if (changes.Count > 0) { State = store.SaveIndividualSettings(source, State, id, value, Scope.SelectedIndex == 1, changes); initial = GroupLetterSettingsReader.Read(State.Snapshot, id); }
            Notice.Text = "حُفظت التغييرات. احفظ نسخة احتياطية لتضمين التخصيصات.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException)
        { MessageBox.Show(this, ex.Message, "تخصيص الكتاب", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
