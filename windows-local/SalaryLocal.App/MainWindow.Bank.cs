using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Claunia.PropertyList;
using Microsoft.Win32;
using SalaryDesktop.Core;
namespace SalaryLocal.App;
public partial class MainWindow
{
    private static byte[] Plist(NSObject value) { using var memory = new MemoryStream(); PropertyListParser.SaveAsBinary(value, memory); return memory.ToArray(); }
    private static JsonObject SettingObject(BackupSnapshot source, string key)
    {
        if (!source.SettingsPlists.TryGetValue(key, out var bytes)) return new JsonObject();
        if (PropertyListParser.Parse(bytes) is not NSData data) throw new InvalidDataException("صيغة الإعداد المحفوظ غير صالحة: " + key);
        return JsonNode.Parse(data.Bytes)?.AsObject() ?? throw new InvalidDataException("تعذّر قراءة الإعداد؛ لم تُفترض قيمة فارغة.");
    }
    private static decimal WholeAmount(string text)
    {
        var value = PayrollSpreadsheet.ParseMoney(text);
        if (value is null || value != decimal.Truncate(value.Value)) throw new InvalidDataException("أدخل مبلغاً صحيحاً بالدينار، صفراً أو أكبر، دون كسور.");
        return value.Value;
    }
    private static TextBox Field(StackPanel panel, string title, string value)
    { panel.Children.Add(Ui.Text(title)); var field = new TextBox { Text = value, Margin = new Thickness(0, 0, 0, 10) }; panel.Children.Add(field); return field; }
    private async void EditBankAmounts()
    {
        if (busy || snapshot is null) return; var source = snapshot; var month = Month;
        await Run(async () => {
            var original = await Task.Run(() => BankStatementCalculator.Manual(source, month)); var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(Ui.Text("إضافات كشف البنك • " + month, 23)); panel.Children.Add(Ui.Text("هذه مبالغ يدوية مستقلة عن ملف الرواتب، تدخل في مبلغ البنك وتُحفظ للشهر المحدد فقط وفي النسخة الاحتياطية."));
            var leave = Field(panel, "الإجازات المتراكمة / د.ع", ReportSheet.Money(original.AccumulatedLeave));
            var rewards = Field(panel, "مكافآت المنتسبين / د.ع", ReportSheet.Money(original.EmployeeRewards));
            var single = Field(panel, "القائمة المفردة / د.ع", ReportSheet.Money(original.SingleList));
            var dialog = Ui.Dialog(this, "إضافات البنك", new ScrollViewer { Content = panel }, 600, 610); BankManualAmounts? entered = null;
            panel.Children.Add(Ui.Button("مراجعة وحفظ", () => { try { var amounts = new BankManualAmounts(WholeAmount(leave.Text), WholeAmount(rewards.Text), WholeAmount(single.Text)); if (Ui.Message(dialog, $"حفظ إضافات {month} بمجموع {ReportSheet.Money(amounts.Total)} د.ع؟", true) != MessageBoxResult.Yes) return; entered = amounts; dialog.Close(); } catch (Exception ex) { Ui.Message(dialog, ex.Message); } })); panel.Children.Add(Ui.Button("إلغاء", dialog.Close)); dialog.ShowDialog(); if (entered is null) return;
            var amountsToSave = entered;
            await Task.Run(() => { var all = SettingObject(source, "bankStatementManualAmounts"); var entry = all[month] is JsonObject old ? (JsonObject)old.DeepClone() : new JsonObject(); entry["accumulatedLeave"] = amountsToSave.AccumulatedLeave; entry["employeeRewards"] = amountsToSave.EmployeeRewards; entry["singleList"] = amountsToSave.SingleList; all[month] = entry; workspace.Store.SaveSettings(new Dictionary<string, byte[]> { ["bankStatementManualAmounts"] = Plist(new NSData(JsonSerializer.SerializeToUtf8Bytes(all))) }, source.SourceDigest, "تعديل إضافات البنك: " + month); }); await LoadMonth();
        });
    }
    private async void EditContractAmount()
    {
        if (busy || snapshot is null) return; var source = snapshot; var month = Month;
        await Run(async () => {
            var bank = await Task.Run(() => BankStatementCalculator.Calculate(source, month)); var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(Ui.Text("مبلغ العقود • " + month, 23)); panel.Children.Add(Ui.Text($"المحسوب: {ReportSheet.Money(bank.CalculatedContractContribution)} د.ع\nعدد العقود: {bank.ContractCount} • اسمي ناقص: {bank.MissingContractNominal}\nالتعديل مرتبط بأساس الحساب الحالي، ويُبطل إذا تغيّر."));
            if (bank.ContractOverrideStale) panel.Children.Add(Ui.Text("تنبيه: التعديل القديم غير صالح بعد تغير أساس الحساب؛ لا يدخل المبلغ الحالي."));
            var total = Field(panel, "المبلغ النهائي اليدوي / د.ع", ReportSheet.Money(bank.ContractContribution)); var dialog = Ui.Dialog(this, "تعديل العقود", new ScrollViewer { Content = panel }, 620, 540); ContractOverride? value = null; bool accepted = false;
            var save = Ui.Button("حفظ التعديل", () => { try { var amount = WholeAmount(total.Text); if (Ui.Message(dialog, $"تغيير المبلغ النهائي من {ReportSheet.Money(bank.ContractContribution)} إلى {ReportSheet.Money(amount)} د.ع لهذا الشهر؟", true) != MessageBoxResult.Yes) return; value = new(amount, bank.ContractNominal, bank.ContractRetirement, bank.ContractCount, bank.MissingContractNominal); accepted = true; dialog.Close(); } catch (Exception ex) { Ui.Message(dialog, ex.Message); } }); save.IsEnabled = bank.ContractCount > 0 && bank.MissingContractNominal == 0; panel.Children.Add(save);
            if (bank.ManualContractOverride is not null) panel.Children.Add(Ui.Button("إلغاء التعديل والعودة إلى المحسوب", () => { if (Ui.Message(dialog, "إلغاء تعديل هذا الشهر؟ سيبقى في سجل الإصدارات السابق.", true) == MessageBoxResult.Yes) { accepted = true; dialog.Close(); } }));
            panel.Children.Add(Ui.Button("إلغاء", dialog.Close)); dialog.ShowDialog(); if (!accepted) return;
            await Task.Run(() => workspace.Store.SaveSettings(new Dictionary<string, byte[]> { [ContractOverrides.SettingsKey] = ContractOverrides.Set(source.SettingsPlists.GetValueOrDefault(ContractOverrides.SettingsKey), month, value) }, source.SourceDigest, "تعديل مبلغ العقود: " + month)); await LoadMonth();
        });
    }
    private async void EditPayer()
    {
        if (busy) return;
        await Run(async () => {
            var source = await Task.Run(() => workspace.Store.GetSnapshot()); var account = await Task.Run(() => TransferPreparation.Account(source)); var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(Ui.Text("الحساب الدافع", 24)); panel.Children.Add(Ui.Text("هذه البيانات تظهر في ملفات التحويل والكشوف. فحص IBAN لا يثبت الملكية؛ راجع الحساب مع مستند البنك."));
            var name = Field(panel, "اسم الحساب", account.Name); var iban = Field(panel, "IBAN", account.Iban); iban.FlowDirection = FlowDirection.LeftToRight; var branch = Field(panel, "الفرع", account.Branch); var local = Field(panel, "الحساب المحلي (اختياري)", account.LocalAccount);
            var dialog = Ui.Dialog(this, "بيانات الحساب الدافع", new ScrollViewer { Content = panel }, 650, 700); Dictionary<string, byte[]>? updates = null;
            panel.Children.Add(Ui.Button("تأكيد وحفظ بيانات الحساب", () => { try {
                var n = name.Text.Trim(); var i = TransferPreparation.NormalizeAccount(iban.Text); var b = branch.Text.Trim(); var l = TransferPreparation.NormalizeAccount(local.Text);
                if (string.IsNullOrWhiteSpace(n) || n.Length > 200 || n.Any(c => char.IsControl(c) || c == '|') || !TransferPreparation.ValidIban(i) || b.Length > 200 || b.Any(char.IsControl) || l.Length > 30 || l.Any(c => !char.IsAsciiDigit(c))) throw new InvalidDataException("راجع الاسم وIBAN والفرع والحساب المحلي؛ الحقول غير صالحة.");
                if (Ui.Message(dialog, "حفظ بيانات الحساب الدافع المعروضة؟", true) != MessageBoxResult.Yes) return;
                updates = new() { ["bankStatementAccountName"] = Plist(new NSString(n)), ["bankStatementIBAN"] = Plist(new NSString(i)), ["bankStatementBranch"] = Plist(new NSString(b)), ["bankStatementLocalAccount"] = Plist(new NSString(l)) }; dialog.Close();
            } catch (Exception ex) { Ui.Message(dialog, ex.Message); } })); panel.Children.Add(Ui.Button("إلغاء", dialog.Close)); dialog.ShowDialog(); if (updates is null) return;
            await Task.Run(() => workspace.Store.SaveSettings(updates, source.SourceDigest, "تعديل الحساب الدافع")); await LoadMonth();
        });
    }
    private static string GroupName(TransferGroup group) => group switch { TransferGroup.Rafidain => "الرافدين", TransferGroup.OtherBanks => "رشيد ومصارف أخرى", TransferGroup.Anbar => "فرع الأنبار / كتب منفردة", _ => "غير مصنف" };
    private async Task Transfers()
    {
        if (snapshot is null) return; var source = snapshot; var month = Month; var transfer = await Task.Run(() => TransferPreparation.Prepare(source, month));
        Page.Children.Add(Ui.Text("تجهيز التحويلات", 25)); Page.Children.Add(Ui.Text("تجهيز محلي فقط؛ لا إرسال للمصرف. راجع الحسابات والمصادر قبل CSV أو كتب التحويل. الجهات غير المصنفة ومصادر المبلغ الناقصة تمنع التصدير."));
        Page.Children.Add(Ui.Button("مراجعة الحساب الدافع", EditPayer));
        foreach (var warning in transfer.SourceWarnings) Page.Children.Add(Ui.Text("⚠ " + warning));
        var grid = GridFor(("الجهة", "Title"), ("المبلغ", "Amount"), ("المجموعة", "GroupText"), ("المستفيد", "Beneficiary"), ("IBAN", "Iban"), ("الملاحظات", "Problems"));
        var rows = transfer.Entries.Select(e => new TransferDisplay(e, e.Title, ReportSheet.Money(e.Amount), GroupName(e.Group), e.Beneficiary ?? e.Account?.Entity ?? "", e.Account?.Iban ?? "", string.Join(" • ", TransferPreparation.Issues(e)))).ToArray(); grid.ItemsSource = rows; grid.Height = 390; Page.Children.Add(grid);
        Page.Children.Add(Ui.Button("ربط الجهة المحددة بحساب مستفيد", () => { if (grid.SelectedItem is TransferDisplay row) LinkBeneficiary(source, row.Entry); else Ui.Message(this, "اختر جهة من الجدول أولاً."); }));
        Page.Children.Add(Ui.Button("مراجعة تصنيف مجموعة الجهة المحددة", () => { if (grid.SelectedItem is TransferDisplay row) ClassifyTransfer(source, row.Entry); else Ui.Message(this, "اختر جهة من الجدول أولاً."); }));
        foreach (var group in new[] { TransferGroup.Rafidain, TransferGroup.OtherBanks }) { var g = group; var count = transfer.Entries.Count(e => e.Group == g); var button = Ui.Button($"مراجعة وتصدير CSV • {GroupName(g)} • {count} جهة", () => ExportTransfer(source, transfer, g)); button.IsEnabled = count > 0; Page.Children.Add(button); }
        AddTransferLetterActions(source, transfer);
        Page.Children.Add(Ui.Text("فرع الأنبار للكتب المنفردة فقط ولا يدخل CSV. فحص الملف والمبالغ لا يثبت استلام البنك أو صلاحية حساب المستفيد له."));
    }
    private sealed record TransferDisplay(TransferEntry Entry, string Title, string Amount, string GroupText, string Beneficiary, string Iban, string Problems);
    private async void LinkBeneficiary(BackupSnapshot source, TransferEntry entry)
    {
        if (busy) return;
        await Run(async () => {
            if (source.Accounts.Count == 0) { Ui.Message(this, "استورد ملف حسابات الجهات أولاً."); return; }
            var panel = new StackPanel { Margin = new Thickness(24) }; panel.Children.Add(Ui.Text(entry.Title, 23)); panel.Children.Add(Ui.Text("اختيار صريح للمستفيد. الربط يستمر لكل الأشهر ولا يغيّر الحساب نفسه؛ راجع اسم المستفيد وIBAN."));
            var accounts = new ComboBox { ItemsSource = source.Accounts, DisplayMemberPath = "Entity", SelectedIndex = -1 }; panel.Children.Add(accounts); var detail = Ui.Text(""); panel.Children.Add(detail); accounts.SelectionChanged += (_, _) => { if (accounts.SelectedItem is ExecutionAccount a) detail.Text = $"{a.Branch}\n{a.Iban}\nSWIFT: {a.Swift}"; };
            var dialog = Ui.Dialog(this, "ربط حساب مستفيد", panel, 680, 500); string? selected = null; bool accepted = false;
            panel.Children.Add(Ui.Button("تأكيد الربط", () => { if (accounts.SelectedItem is not ExecutionAccount a) { Ui.Message(dialog, "اختر حساباً."); return; } if (Ui.Message(dialog, $"ربط {entry.Title} بالمستفيد {a.Entity}؟\n{a.Iban}", true) != MessageBoxResult.Yes) return; selected = PayrollAnalysis.Normalize(a.Entity); accepted = true; dialog.Close(); }));
            panel.Children.Add(Ui.Button("إزالة الربط الصريح", () => { if (Ui.Message(dialog, "إزالة الربط الصريح والعودة للمطابقة الأصلية بالاسم؟", true) == MessageBoxResult.Yes) { accepted = true; dialog.Close(); } })); panel.Children.Add(Ui.Button("إلغاء", dialog.Close)); dialog.ShowDialog(); if (!accepted) return;
            await Task.Run(() => { var links = SettingObject(source, "transferPayrollAccountLinks"); var key = entry.Id.StartsWith("payroll|", StringComparison.Ordinal) ? entry.Id[8..] : "deduction|" + PayrollAnalysis.Normalize(entry.Title); if (selected is null) links.Remove(key); else links[key] = selected; workspace.Store.SaveSettings(new Dictionary<string, byte[]> { ["transferPayrollAccountLinks"] = Plist(new NSData(JsonSerializer.SerializeToUtf8Bytes(links))) }, source.SourceDigest, "تعديل ربط مستفيد"); }); await LoadMonth();
        });
    }
    private async void ClassifyTransfer(BackupSnapshot source, TransferEntry entry)
    {
        if (busy) return;
        await Run(async () => {
            var panel = new StackPanel { Margin = new Thickness(24) }; panel.Children.Add(Ui.Text(entry.Title, 23)); panel.Children.Add(Ui.Text("التصنيف اليدوي لا يصلح حساباً ناقصاً ولا يزيل تحذيرات المصدر. فرع الأنبار للكتب المنفردة فقط. راجع مستند الحساب قبل التغيير."));
            var groups = new[] { TransferGroup.Unresolved, TransferGroup.Rafidain, TransferGroup.OtherBanks, TransferGroup.Anbar }; var select = new ComboBox { ItemsSource = groups.Select(g => new { Value = g, Title = GroupName(g) }).ToArray(), DisplayMemberPath = "Title", SelectedIndex = Array.IndexOf(groups, entry.Group) }; panel.Children.Add(select);
            var dialog = Ui.Dialog(this, "تصنيف مجموعة المصرف", panel, 660, 430); bool accepted = false; string? value = null;
            panel.Children.Add(Ui.Button("تأكيد التصنيف", () => { var group = groups[Math.Max(0, select.SelectedIndex)]; if (Ui.Message(dialog, $"تصنيف {entry.Title} ضمن {GroupName(group)}؟", true) != MessageBoxResult.Yes) return; value = group switch { TransferGroup.Rafidain => "rafidain", TransferGroup.OtherBanks => "otherBanks", TransferGroup.Anbar => "anbar", _ => "unresolved" }; accepted = true; dialog.Close(); }));
            panel.Children.Add(Ui.Button("العودة إلى التصنيف من بيانات الحساب", () => { if (Ui.Message(dialog, "إلغاء التصنيف اليدوي لهذه الجهة والحساب؟", true) == MessageBoxResult.Yes) { accepted = true; dialog.Close(); } })); panel.Children.Add(Ui.Button("إلغاء", dialog.Close)); dialog.ShowDialog(); if (!accepted) return;
            await Task.Run(() => { var categories = SettingObject(source, "transferEntityCategories"); var key = $"{PayrollAnalysis.Normalize(entry.Title)}|{entry.Account?.Iban ?? ""}|{PayrollAnalysis.Normalize(entry.Account?.Branch ?? "")}"; if (value is null) categories.Remove(key); else categories[key] = value; workspace.Store.SaveSettings(new Dictionary<string, byte[]> { ["transferEntityCategories"] = Plist(new NSData(JsonSerializer.SerializeToUtf8Bytes(categories))) }, source.SourceDigest, "تعديل تصنيف مجموعة مصرف"); }); await LoadMonth();
        });
    }
    private async void ExportTransfer(BackupSnapshot source, TransferSnapshot transfer, TransferGroup group)
    {
        if (busy) return; var date = DateOnly.FromDateTime(DateTime.Today); var entries = transfer.Entries.Where(e => e.Group == group).ToArray();
        if (Ui.Message(this, $"إنشاء ملف {GroupName(group)} لشهر {transfer.Month} بتاريخ {date:yyyy-MM-dd}؟\n{entries.Length} جهة • {ReportSheet.Money(entries.Sum(e => e.Amount))} د.ع\nالحساب الدافع: {transfer.Payer}\n{transfer.PayerAccount}\nراجع المستفيدين والمبالغ قبل تسليم الملف للمصرف. لن يُرسل شيء من التطبيق.", true) != MessageBoxResult.Yes) return;
        var picker = new SaveFileDialog { Filter = "ملف تحويل (*.csv)|*.csv", FileName = ReportSheet.FileName($"تحويل-{GroupName(group)}-{transfer.Month}") + ".csv" }; if (Ui.ShowNativeDialog(() => picker.ShowDialog(this)) != true) return;
        await Run(async () => { await Task.Run(() => { if (workspace.Store.GetStatus().RevisionId != source.SourceDigest) throw new InvalidDataException("تغيّرت البيانات؛ أعد تجهيز التحويلات."); var bytes = TransferPreparation.MakeCsv(transfer, group, date); TransferPreparation.VerifyCsv(bytes, transfer, group, date); ReportFiles.Save(bytes, picker.FileName, ".csv"); }); Ui.Message(this, "حُفظ ملف التحويل محلياً وفُحصت مبالغه. راجعه قبل التسليم؛ لم يُرسل إلى البنك."); });
    }
}
