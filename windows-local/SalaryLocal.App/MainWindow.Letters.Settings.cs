using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SalaryDesktop.Core;
using SalaryLocal.Core;

namespace SalaryLocal.App;

public partial class MainWindow
{
    private async void EditTransferLetterSettings(BackupSnapshot source)
    {
        if (busy) return;
        await Run(async () => {
            LetterCurrent(source);
            var choosePanel = new StackPanel { Margin = new Thickness(24) }; choosePanel.Children.Add(Ui.Text("اختر الكتاب المراد تخصيصه", 22));
            var choices = new ComboBox { ItemsSource = LocalTransferLetterSettings.Choices.Select(c => new LetterChoice(c.Id, c.Title)).ToArray(), DisplayMemberPath = "Title", SelectedIndex = 0 }; choosePanel.Children.Add(choices);
            var choose = Ui.Dialog(this, "تخصيص الكتب", choosePanel, 660, 300); string? id = null;
            choosePanel.Children.Add(Ui.Button("فتح الحقول", () => { id = ((LetterChoice)choices.SelectedItem).Id; choose.Close(); })); choosePanel.Children.Add(Ui.Button("إلغاء", choose.Close)); choose.ShowDialog(); if (id is null) return;
            var initial = await Task.Run(() => GroupLetterSettingsReader.Read(source, id));
            var panel = new StackPanel { Margin = new Thickness(24) }; panel.Children.Add(Ui.Text(LocalTransferLetterSettings.Title(id), 24));
            panel.Children.Add(Ui.Text("العدد ومرجع عقد الإطعام مشتركان دائماً. بقية التغييرات لهذا الكتاب إلا عند اختيار تطبيق الحقول المعدلة على الجميع. المتن خاص بهذا الكتاب ولا يجوز حذف حقوله المحاسبية."));
            TextBox Multi(string title, string value) { var field = Field(panel, title, value); field.AcceptsReturn = true; field.TextWrapping = TextWrapping.Wrap; field.MinHeight = 70; field.MaxHeight = 160; field.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; return field; }
            var header = Multi("الترويسة", initial.Header); var number = Field(panel, "العدد الموحد", initial.Number);
            var first = Field(panel, "عضو اللجنة الأول", initial.First); var second = Field(panel, "عضو اللجنة الثاني", initial.Second); var third = Field(panel, "عضو اللجنة الثالث", initial.Third);
            var footer = Multi("نسخة منه إلى / الهامش", initial.Footer); var bodyMonth = Field(panel, "شهر المتن (فارغ = شهر المصدر)", initial.BodyMonth ?? "");
            var outgoing = Field(panel, "شهر الصادر 1–12 (فارغ = شهر المصدر، واليوم فارغ)", initial.OutgoingMonth?.ToString(CultureInfo.InvariantCulture) ?? "");
            var offset = Field(panel, "إزاحة التواقيع بين -60 و60", initial.SignatureOffset.ToString(CultureInfo.InvariantCulture));
            var food = Field(panel, "رقم وتاريخ عقد الإطعام", initial.FoodContractReference ?? "");
            var body = Multi("متن الكتاب • احتفظ بكل الحقول بين {{ }}", initial.BodyTemplate ?? LocalTransferLetterSettings.DefaultBody(id)); body.MinHeight = 120;
            var common = new CheckBox { Content = Ui.Text("تطبيق تغييرات الترويسة واللجنة والهامش والشهر والإزاحة على جميع الكتب (المتن لهذا الكتاب فقط)"), Margin = new Thickness(0, 12, 0, 12) }; panel.Children.Add(common);
            var dialog = Ui.Dialog(this, "تخصيص كتاب التحويل", new ScrollViewer { Content = panel }, 880, 850); GroupLetterSettings? entered = null; bool shared = false;
            string Clean(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
            string Latin(string value) => string.Concat(value.Trim().Select(c => c switch { >= '٠' and <= '٩' => (char)('0' + c - '٠'), >= '۰' and <= '۹' => (char)('0' + c - '۰'), _ => c }));
            panel.Children.Add(Ui.Button("مراجعة وحفظ التغييرات", () => { try {
                int? month = null; if (outgoing.Text.Trim().Length > 0) { if (!int.TryParse(Latin(outgoing.Text), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed is < 1 or > 12) throw new InvalidDataException("شهر الصادر من 1 إلى 12."); month = parsed; }
                if (!int.TryParse(Latin(offset.Text), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var position)) throw new InvalidDataException("إزاحة التواقيع غير صالحة.");
                var value = new GroupLetterSettings(Clean(header.Text), Clean(number.Text), Clean(first.Text), Clean(second.Text), Clean(third.Text), Clean(footer.Text), Clean(bodyMonth.Text), month, position, Clean(body.Text), Clean(food.Text));
                LocalTransferLetterSettings.Validate(id, value);
                if (Ui.Message(dialog, "حفظ الحقول المعدلة؟\n" + (common.IsChecked == true ? "تُطبّق الحقول المشتركة المعدلة على جميع الكتب، دون تغيير متونها أو حساباتها." : "العدد ومرجع عقد الإطعام مشتركان. بقية الحقول لهذا الكتاب فقط."), true) != MessageBoxResult.Yes) return;
                entered = value; shared = common.IsChecked == true; dialog.Close();
            } catch (Exception ex) { Ui.Message(dialog, ex.Message); } }));
            panel.Children.Add(Ui.Button("إلغاء دون حفظ", dialog.Close)); dialog.ShowDialog(); if (entered is null) return;
            await Task.Run(() => workspace.Store.SaveTransferLetterSettings(id, entered, shared, source.SourceDigest)); await LoadMonth();
            Ui.Message(this, "حُفظت تخصيصات الكتب محلياً مع الإبقاء على بقية الإعدادات. افتح الكتاب من جديد لمراجعة النتيجة، وأنشئ نسخة احتياطية.");
        });
    }
    private async void ImportTransferReferences(BackupSnapshot source)
    {
        if (busy) return;
        await Run(async () => {
            LetterCurrent(source); var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(Ui.Text("مراجع كتب التحويل الأصلية", 23));
            panel.Children.Add(Ui.Text("المراجع تبقى داخل مساحة العمل ونسختها الاحتياطية. اختر حزمة ZIP المفهرسة أو مجلد ملفات PDF الأصلية الأحد عشر بأسمائها المعروفة. لا يغيّر الاستيراد حساب الدفع أو حسابات المستفيدين، وتُفحص المطابقة عند تجهيز كل كتاب."));
            var dialog = Ui.Dialog(this, "مصدر المراجع", panel, 760, 410); string? path = null; bool folder = false;
            panel.Children.Add(Ui.Button("اختيار حزمة ZIP للمراجع", () => { var picker = new OpenFileDialog { Filter = "مراجع الكتب (*.zip)|*.zip", CheckFileExists = true }; if (Ui.ShowNativeDialog(() => picker.ShowDialog(dialog)) == true) { path = picker.FileName; dialog.Close(); } }));
            panel.Children.Add(Ui.Button("اختيار مجلد ملفات PDF الأصلية", () => { var picker = new OpenFolderDialog { Title = "مجلد نماذج الكتب الأصلية" }; if (Ui.ShowNativeDialog(() => picker.ShowDialog(dialog)) == true) { path = picker.FolderName; folder = true; dialog.Close(); } }));
            panel.Children.Add(Ui.Button("إلغاء", dialog.Close)); dialog.ShowDialog(); if (path is null) return;
            var selected = path; var bytes = await Task.Run(() => {
                if (folder) return ReferenceArchiveBuilder.Create(selected);
                var info = new FileInfo(selected); if (info.Length is <= 0 or > 10_000_000) throw new InvalidDataException("حجم حزمة المراجع غير صالح.");
                var data = File.ReadAllBytes(selected); _ = LetterReferencePack.ReadArchive(data); return data;
            });
            var references = await Task.Run(() => LetterReferencePack.ReadArchive(bytes));
            if (Ui.Message(this, $"حُقّقت بصمات {references.Count} مرجعاً وأحجام صفحاتها. هذا لا يثبت أن المراجع صحيحة محاسبياً.\nهل تحفظ هذه المراجع في مساحة العمل؟ ستبقى النسخة السابقة ضمن التاريخ المحلي، وتُبطل المعاينات المفتوحة.", true) != MessageBoxResult.Yes) return;
            await Task.Run(() => workspace.Store.ImportTransferLetterReferences(bytes, source.SourceDigest)); await LoadMonth();
            Ui.Message(this, "حُفظت المراجع محلياً. راجع حساب الدفع والتخصيصات قبل تجهيز الكتب. لم تُرفع أي ملفات.");
        });
    }
    private async void ImportTransferFont(BackupSnapshot source)
    {
        if (busy) return; var picker = new OpenFileDialog { Filter = "خط ساكال العريض (*.ttf)|*.ttf", Title = "اختيار MAJALLAB.TTF", CheckFileExists = true }; if (Ui.ShowNativeDialog(() => picker.ShowDialog(this)) != true) return;
        await Run(async () => {
            LetterCurrent(source); var bytes = await Task.Run(() => { var info = new FileInfo(picker.FileName); if (info.Length is <= 0 or > 3_000_000) throw new InvalidDataException("حجم الخط غير صالح."); var font = File.ReadAllBytes(picker.FileName); GroupLetterFont.Validate(font); return font; });
            if (Ui.Message(this, "حفظ خط Sakkal Majalla العريض داخل مساحة العمل ونسختها الاحتياطية؟ استخدم ملفاً تملك حق استعماله.", true) != MessageBoxResult.Yes) return;
            await Task.Run(() => workspace.Store.ImportTransferLetterFont(bytes, source.SourceDigest)); await LoadMonth(); Ui.Message(this, "حُفظ خط الكتاب دون تغيير خطوط الكشوف المعتمدة.");
        });
    }
}
