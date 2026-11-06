using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SalaryDesktop.Core;
using SalaryLocal.Core;

namespace SalaryLocal.App;

public partial class MainWindow
{
    private sealed record LetterChoice(string Id, string Title);
    private void LetterCurrent(BackupSnapshot source)
    { if (workspace.Store.GetStatus().RevisionId != source.SourceDigest) throw new InvalidDataException("تغيّرت بيانات مساحة العمل؛ أعد فتح ملفات التحويل والمراجعة."); }
    private void AddTransferLetterActions(BackupSnapshot source, TransferSnapshot transfer)
    {
        Page.Children.Add(Ui.Text("كتب التحويل • مسودات للمراجعة", 22));
        Page.Children.Add(Ui.Text("القوالب الأصلية للمجموعتين والكتب المنفردة. الحسابات تُطابق المراجع الخاصة، ولا تُرسل ملفات أو مبالغ إلى المصرف. يلزم فتح ملفات PDF ومراجعتها قبل حفظ حزمة جديدة."));
        var setup = new WrapPanel(); Page.Children.Add(setup);
        setup.Children.Add(Ui.Button("ترويسة الكتب واللجنة والمتن", () => EditTransferLetterSettings(source)));
        setup.Children.Add(Ui.Button("استيراد مراجع الكتب", () => ImportTransferReferences(source)));
        setup.Children.Add(Ui.Button("استيراد خط ساكال العريض", () => ImportTransferFont(source)));
        setup.Children.Add(Ui.Button("إعداد حساب مستفيد الكتاب", () => EditTransferRecipients(source, transfer)));
        setup.Children.Add(Ui.Button("مراجعة إقرارات الإصدار", () => ReviewTransferIssuance(source)));
        setup.Children.Add(Ui.Button("تجهيز دفعة كتب مختارة", () => OpenTransferLetterBatch(source, transfer)));
        var choices = new WrapPanel(); Page.Children.Add(choices);
        foreach (var choice in LocalTransferLetterSettings.Choices)
        { var selected = choice; choices.Children.Add(Ui.Button(selected.Title, () => OpenTransferLetter(source, transfer, selected.Id))); }
        Page.Children.Add(Ui.Text("الملفات مسودات محلية وليست كتباً معتمدة. يُحفظ الكتاب وقائمة الأسماء أو CSV في ZIP واحد. إقرار الإصدار مستقل للمجموعتين والكتب المنفردة، ولا يثبت استلام البنك. راجع الأسماء والتنسيق والتواقيع على Windows الفعلي."));
    }
    private async void OpenTransferLetter(BackupSnapshot source, TransferSnapshot transfer, string id)
    {
        if (busy) return;
        await Run(async () => {
            LetterCurrent(source); string? entryId = null;
            if (LocalTransferLetterSettings.IsEntity(id))
            {
                var candidates = transfer.Entries.Where(e => e.Group == TransferGroup.Anbar && e.Id.StartsWith("deduction|", StringComparison.Ordinal)).ToArray();
                var panel = new StackPanel { Margin = new Thickness(24) }; panel.Children.Add(Ui.Text(LocalTransferLetterSettings.Title(id), 23));
                panel.Children.Add(Ui.Text("اختر جهة الاستقطاع صراحةً. سيُفحص حساب المستفيد مقابل مرجع هذا الكتاب؛ لا يُخمن ربط بالاسم."));
                var picker = new ComboBox { ItemsSource = candidates, DisplayMemberPath = "Title", SelectedIndex = -1, Margin = new Thickness(0, 12, 0, 12) }; panel.Children.Add(picker);
                var detail = Ui.Text(""); panel.Children.Add(detail);
                picker.SelectionChanged += (_, _) => { if (picker.SelectedItem is TransferEntry entry) detail.Text = $"{ReportSheet.Money(entry.Amount)} د.ع • {entry.People} منتسب\n{entry.Account?.Entity}\n{entry.Account?.Iban}\n{entry.Account?.LocalAccount}"; };
                var dialog = Ui.Dialog(this, "اختيار جهة الكتاب", panel, 760, 520);
                panel.Children.Add(Ui.Button("مراجعة هذه الجهة", () => { if (picker.SelectedItem is not TransferEntry entry) { Ui.Message(dialog, "اختر جهة أولاً."); return; } entryId = entry.Id; dialog.Close(); }));
                panel.Children.Add(Ui.Button("إلغاء", dialog.Close)); dialog.ShowDialog(); if (entryId is null) return;
            }
            var chosenEntry = entryId;
            var preview = await Task.Run(() => { LetterCurrent(source); return LocalTransferLetters.Prepare(source, transfer.Month, id, chosenEntry, DateOnly.FromDateTime(DateTime.Today)); });
            LetterCurrent(source); ShowTransferLetterReview(source, preview); await LoadMonth();
        });
    }
    private void ShowTransferLetterReview(BackupSnapshot source, LocalLetterPreview preview)
    {
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(Ui.Text(preview.Title + " • " + preview.Month, 24));
        panel.Children.Add(Ui.Text($"المجموع: {ReportSheet.Money(preview.Total)} د.ع\nالحساب الدافع: {preview.Payer}\n{preview.PayerAccount}"));
        if (preview.BeneficiaryAccount is { } account) panel.Children.Add(Ui.Text("حساب المستفيد: " + account));
        if (preview.Entity is { } entity)
        {
            panel.Children.Add(Ui.Text($"الجهة: {entity.Entity} • المستفيد: {entity.BeneficiaryName} • {entity.People} منتسب"));
            if (entity.Id == "water") panel.Children.Add(Ui.Text("المنتسب في المتن: " + entity.Rows[0].Rank + " • " + entity.Rows[0].Name));
        }
        panel.Children.Add(Ui.Text("العدد: " + preview.Settings.Number + "\nاللجنة: " + preview.Settings.First + " • " + preview.Settings.Second + " • " + preview.Settings.Third));
        if (!string.IsNullOrWhiteSpace(preview.Settings.BodyMonth) && MonthNames.SortKey(preview.Settings.BodyMonth) != MonthNames.SortKey(preview.Month))
            panel.Children.Add(Ui.Text($"تنبيه: مبالغ المصدر من {preview.Month} بينما شهر المتن المعدل هو {preview.Settings.BodyMonth}. يلزم إقرار هذا الاختلاف عند المراجعة."));
        if (preview.Group is { } group)
        {
            panel.Children.Add(Ui.Text("تاريخ CSV: " + preview.Date.ToString("yyyy-MM-dd") + " • جدول المجموعة في صفحة واحدة مستقلة."));
            var grid = GridFor(("المستفيد", "Title"), ("IBAN", "Iban"), ("الفرع", "Branch"), ("المبلغ", "Amount")); grid.Height = 260; grid.ItemsSource = group.Lines; panel.Children.Add(grid);
        }
        if (preview.Payroll is { } payroll)
        {
            var grid = GridFor(("البند", "Title"), ("المبلغ", "Amount")); grid.Height = 150; grid.ItemsSource = payroll.Lines; panel.Children.Add(grid);
            foreach (var notice in payroll.Notices) panel.Children.Add(Ui.Text("⚠ " + notice));
        }
        if (preview.Names is { } names)
        {
            panel.Children.Add(Ui.Text($"قائمة أسماء ملحقة بالكتاب: {names.Rows.Count:N0} صف • {ReportSheet.Money(names.Total)} د.ع. تشمل الحزمة الكتاب مع القائمة كاملة ونسخة قائمة مستقلة."));
            var grid = GridFor(("ت", "Number"), ("الاسم", "Name"), ("الرتبة", "Rank"), ("محل العمل", "Workplace"), ("المبلغ", "Amount")); grid.Height = 230; grid.ItemsSource = names.Rows; panel.Children.Add(grid);
        }
        if (preview.Issuance is { } review)
        {
            panel.Children.Add(Ui.Text(review.BaselineMonth is null ? "لا يوجد إقرار مجموعة سابق للمقارنة." : "مقارنة مع الإقرار السابق: " + review.BaselineMonth));
            foreach (var change in review.Changes) panel.Children.Add(Ui.Text("⚠ " + change));
        }
        var dialog = Ui.Dialog(this, "مراجعة مسودة كتاب التحويل", new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, 1020, 860);
        var status = Ui.Text("لم تُنشأ ملفات بعد. لا يثبت فحص المبالغ صحة التنسيق أو ملكية الحساب."); panel.Children.Add(status);
        var consent = new CheckBox { Content = Ui.Text("فتحت جميع ملفات PDF وراجعت الأسماء والمبالغ والحسابات والشهر والعدد والتواقيع والتنسيق، وأوافق على حفظ مسودة."), IsEnabled = false, Margin = new Thickness(0, 14, 0, 14) }; panel.Children.Add(consent);
        LocalLetterPackage? package = null; bool working = false, saved = false, confirmed = false;
        var actions = new StackPanel(); panel.Children.Add(actions);
        dialog.Closing += (_, e) => { if (working) e.Cancel = true; };
        async Task Work(Func<Task> action)
        {
            if (working) return; working = true; actions.IsEnabled = false; consent.IsEnabled = false;
            try { LetterCurrent(source); await action(); }
            catch (Exception ex) { status.Text = "تعذّر إكمال العملية؛ لم يُرسل شيء للمصرف."; Ui.Message(dialog, ex.Message); }
            finally { working = false; actions.IsEnabled = !confirmed; consent.IsEnabled = package is not null && !confirmed; }
        }
        actions.Children.Add(Ui.Button("إنشاء وفتح جميع ملفات PDF للمراجعة", async () => await Work(async () => {
            consent.IsChecked = false; package = null;
            var made = await Task.Run(() => {
                LetterCurrent(source); var assets = LetterAssets(source);
                return LocalTransferLetters.Generate(preview, assets.Sakkal, assets.Arial, assets.ArialBold, assets.Logo);
            });
            LetterCurrent(source);
            foreach (var name in made.FileNames.Where(n => n.EndsWith(".pdf", StringComparison.Ordinal)))
                new LocalPdfPreviewWindow(dialog, preview.Title + " • " + name, made.ReadFile(name), () => LetterCurrent(source)).ShowDialog();
            package = made; status.Text = "فُتحت المعاينة داخل البرنامج. راجع PDF فعلياً ثم علّم خانة المراجعة. تتوفر الطباعة من المعاينة ولا تُعد إقرار إصدار.";
        })));
        actions.Children.Add(Ui.Button("حفظ حزمة المسودة الجديدة • ZIP", async () => {
            if (package is null || consent.IsChecked != true) { Ui.Message(dialog, "أنشئ وافتح جميع ملفات PDF، ثم أقر بمراجعتها أولاً."); return; }
            var captured = package;
            if (Ui.Message(dialog, $"حفظ مسودة {preview.Title} عن {preview.Month} بمبلغ {ReportSheet.Money(preview.Total)} د.ع؟\nالحزمة غير مشفرة وتحتوي حسابات وأسماء. لا تُسجل إصداراً ولا تُرسل للمصرف.", true) != MessageBoxResult.Yes) return;
            var picker = new SaveFileDialog { Filter = "حزمة مسودة (*.zip)|*.zip", DefaultExt = ".zip", AddExtension = true, OverwritePrompt = true, FileName = ReportSheet.FileName("مسودة-" + preview.Title + "-" + preview.Month) + ".zip" };
            if (Ui.ShowNativeDialog(() => picker.ShowDialog(dialog)) != true) return;
            await Work(async () => { await Task.Run(() => workspace.Store.SaveTransferLetterPackage(captured, picker.FileName)); saved = true; status.Text = "حُفظت حزمة المسودة وفُحصت بصمتها، دون تسجيل إصدار أو إرسال للمصرف.\n" + picker.FileName; });
        }));
        if (preview.Issuance is not null)
        {
            var note = Field(panel, "ملاحظة إقرار الإصدار (اختياري)", "");
            actions.Children.Add(Ui.Button("إقرار يدوي بأن المجموعة أُصدرت خارج التطبيق", async () => {
                if (!saved || consent.IsChecked != true) { Ui.Message(dialog, "احفظ الحزمة بعد مراجعتها أولاً. لا تسجّل إقراراً لمجرد تجهيز المسودة."); return; }
                if (Ui.Message(dialog, "هل أصدرت هذه المجموعة فعلاً خارج التطبيق بعد مراجعتها؟\nسيُحفظ إقرار محلي بتاريخ اليوم. لا يثبت استلام المصرف، ولا يرسل تحويلاً، ولا يضيف كتاباً للأرشيف.", true) != MessageBoxResult.Yes) return;
                await Work(async () => { var text = note.Text; await Task.Run(() => workspace.Store.ConfirmTransferLetterGroup(preview, text)); confirmed = true; status.Text = "حُفظ إقرار الإصدار المحلي. أغلق المراجعة لعرض البيانات المحدثة."; });
            }));
        }
        if (preview.Issuance is null)
            actions.Children.Add(Ui.Button("إقرار يدوي بأن الكتاب أُصدر خارج التطبيق", async () => {
                if (!saved || consent.IsChecked != true) { Ui.Message(dialog, "احفظ الحزمة بعد مراجعتها أولاً. تجهيز المسودة ليس إصداراً."); return; }
                await Work(async () => { if (await OpenSingleIssuance(source, preview, dialog)) { confirmed = true; status.Text = "حُفظ الإقرار اليدوي. أغلق المراجعة لعرض البيانات المحدثة."; } });
            }));
        panel.Children.Add(Ui.Button("إغلاق", dialog.Close));
        dialog.ShowDialog();
    }
}
