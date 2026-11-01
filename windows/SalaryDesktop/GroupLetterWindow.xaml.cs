using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using SalaryDesktop.Core;
namespace SalaryDesktop;

public partial class GroupLetterWindow : Window
{
    private readonly BackupSnapshot source;
    private readonly LocalTransferLinks store;
    private readonly GroupLetterPlan plan;
    private readonly Func<bool> payrollCurrent;
    private GroupLetterSettings initial;
    private bool working;
    public TransferLinksSnapshot State { get; private set; }
    public GroupLetterWindow(BackupSnapshot source, TransferLinksSnapshot state, LocalTransferLinks store, GroupLetterPlan plan, Func<bool> payrollCurrent)
    {
        InitializeComponent(); this.source = source; State = state; this.store = store; this.plan = plan; this.payrollCurrent = payrollCurrent;
        initial = GroupLetterSettingsReader.Read(state.Snapshot, plan.Group);
        Heading.Text = (plan.Group == TransferGroup.Rafidain ? "كتاب مجموعة الرافدين" : "كتاب مجموعة رشيد ومصارف أخرى") + " · " + plan.Month;
        HeaderText.Text = initial.Header; NumberText.Text = initial.Number; FirstText.Text = initial.First; SecondText.Text = initial.Second; ThirdText.Text = initial.Third;
        FooterText.Text = initial.Footer; BodyMonthText.Text = initial.BodyMonth ?? ""; OutgoingText.Text = initial.OutgoingMonth?.ToString(CultureInfo.InvariantCulture) ?? "";
        OffsetText.Text = initial.SignatureOffset.ToString(CultureInfo.InvariantCulture); BodyText.Text = initial.BodyTemplate ?? GroupLetterPdf.DefaultBodyTemplate;
        Notice.Text = "مسودة للمراجعة. يتحقق التطبيق من جدول الجهات وحساباتها ومبالغها مع بيانات ملف التحويل؛ راجع التنسيق والتوقيعات قبل الاعتماد.";
        Closing += (_, e) => { if (working) e.Cancel = true; };
    }
    private static string Clean(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
    private static string Latin(string text) => string.Concat(text.Trim().Select(c => c switch { >= '٠' and <= '٩' => (char)('0' + c - '٠'), >= '۰' and <= '۹' => (char)('0' + c - '۰'), _ => c }));
    private GroupLetterSettings Fields()
    {
        var outgoing = Latin(OutgoingText.Text); int? month = null;
        if (outgoing.Length > 0) { if (!int.TryParse(outgoing, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n is < 1 or > 12) throw new InvalidDataException("شهر الصادر يجب أن يكون من ١ إلى ١٢."); month = n; }
        if (!int.TryParse(Latin(OffsetText.Text), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var offset) || offset is < -60 or > 60) throw new InvalidDataException("موضع اللجنة يجب أن يكون بين -٦٠ و٦٠.");
        return new(Clean(HeaderText.Text), Clean(NumberText.Text), Clean(FirstText.Text), Clean(SecondText.Text), Clean(ThirdText.Text), Clean(FooterText.Text),
            Clean(BodyMonthText.Text), month, offset, Clean(BodyText.Text));
    }
    private HashSet<string> Changes(GroupLetterSettings value)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);
        void Add(bool changed, string name) { if (changed) fields.Add(name); }
        Add(value.Header != initial.Header, "header"); Add(value.Number != initial.Number, "fixedNumber"); Add(value.First != initial.First, "committeeFirst");
        Add(value.Second != initial.Second, "committeeSecond"); Add(value.Third != initial.Third, "committeeThird"); Add(value.Footer != initial.Footer, "footer");
        Add((value.BodyMonth ?? "") != (initial.BodyMonth ?? ""), "bodyMonth"); Add(value.OutgoingMonth != initial.OutgoingMonth, "outgoingMonth");
        Add(value.SignatureOffset != initial.SignatureOffset, "signatureOffset"); Add(value.BodyTemplate != (initial.BodyTemplate ?? GroupLetterPdf.DefaultBodyTemplate), "bodyTemplate");
        return fields;
    }
    private GroupLetterSettings Persist()
    {
        if (!payrollCurrent() || store.Load(source).Revision != State.Revision) throw new InvalidDataException("تغيّرت بيانات الشهر أو الحسابات؛ أعد فتح الكتاب.");
        var value = Fields(); var changes = Changes(value);
        if (changes.Count > 0)
        {
            State = store.SaveGroupSettings(source, State, plan.Group, value, Scope.SelectedIndex == 1, changes);
            initial = GroupLetterSettingsReader.Read(State.Snapshot, plan.Group);
        }
        return value;
    }
    private void SaveSettings(object sender, RoutedEventArgs e)
    {
        try { _ = Persist(); Notice.Text = "حُفظت الحقول المعدلة. احفظ نسخة احتياطية لتضمين التخصيصات."; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException) { Ui.Message(this, ex.Message, "تخصيص الكتاب", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void SelectFont(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "اختيار خط ساكال العريض", Filter = "خط TrueType (*.ttf)|*.ttf", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (!payrollCurrent()) throw new InvalidDataException("تغيّرت بيانات الشهر؛ أعد فتح الكتاب.");
            var info = new FileInfo(dialog.FileName); if (info.Length > 3_000_000) throw new InvalidDataException("حجم ملف الخط غير صالح.");
            State = store.SaveGroupFont(source, State, File.ReadAllBytes(dialog.FileName)); Notice.Text = "حُفظ خط ساكال العريض مع تخصيصات النسخة المحلية.";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException or DllNotFoundException or TypeInitializationException)
        { Ui.Message(this, ex.Message, "استيراد الخط", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void Busy(bool value) { working = value; EditorScroll.IsEnabled = !value; SaveButton.IsEnabled = !value; FontButton.IsEnabled = !value; PreviewButton.IsEnabled = !value; ExportButton.IsEnabled = !value; CloseButton.IsEnabled = !value; }
    /// المعاينة داخل البرنامج: يُنشأ الكتاب في الذاكرة ويُعرض في نافذة المعاينة (منها الحفظ والطباعة).
    private async void Preview(object sender, RoutedEventArgs e) => await Generate(null, true);
    private async void Export(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "حفظ مسودة كتاب المجموعة", Filter = "ملف PDF (*.pdf)|*.pdf", FileName = "مسودة كتاب المجموعة.pdf", AddExtension = true, DefaultExt = ".pdf", OverwritePrompt = true };
        if (dialog.ShowDialog(this) == true) await Generate(dialog.FileName, false);
    }
    private async Task Generate(string? destination, bool open)
    {
        Busy(true);
        try
        {
            var fields = Persist(); var captured = State; var font = GroupLetterFont.Read(captured.Snapshot) ?? throw new InvalidDataException("استورد خط ساكال العريض أولاً أو افتح نسخة تتضمنه.");
            if (!string.IsNullOrWhiteSpace(fields.BodyMonth) && MonthNames.SortKey(fields.BodyMonth) != MonthNames.SortKey(plan.Month) &&
                Ui.Message(this, $"مبالغ الكتاب وملف التحويل من شهر {plan.Month}، لكن الشهر المكتوب في المتن هو {fields.BodyMonth}. هل تريد متابعة المسودة؟", "اختلاف شهر المتن", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var fontPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
            var logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "IraqEmblem.png");
            var (changed, bytes) = await Task.Run(() => {
                if (!payrollCurrent() || store.Load(source).Revision != captured.Revision) throw new InvalidDataException("تغيّرت بيانات التحويل؛ أعد فتح الكتاب.");
                byte[]? made = null;
                if (destination is null) made = GroupLetterPdf.Generate(plan, fields, font, File.ReadAllBytes(fontPath), File.ReadAllBytes(logoPath));
                else GroupLetterPdf.SaveDraft(plan, fields, font, File.ReadAllBytes(fontPath), File.ReadAllBytes(logoPath), destination);
                return (!payrollCurrent() || store.Load(source).Revision != captured.Revision, made);
            });
            if (changed) { Ui.Message(this, "تغيّرت البيانات أثناء إعداد المسودة؛ أعد فتح الكتاب.", "كتاب المجموعة", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            if (open && bytes is not null) Exports.ShowBytes(this, "مسودة كتاب المجموعة", bytes);
            else Ui.Message(this, "حُفظت المسودة بعد فحص جدولها. راجع النص والتنسيق والتوقيعات على ويندوز قبل الاعتماد.", "كتاب المجموعة", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException or DllNotFoundException or TypeInitializationException or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        { Ui.Message(this, "تعذّر إعداد مسودة الكتاب.\n" + ex.Message, "كتاب المجموعة", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { Busy(false); }
    }
}
