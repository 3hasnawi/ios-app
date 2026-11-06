using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Claunia.PropertyList;
using Microsoft.Win32;
using SalaryDesktop.Core;
using SalaryLocal.Core;

namespace SalaryLocal.App;

public partial class MainWindow
{
    private sealed record LocalPeriodRow(BankPeriod Period, string Month, string Dates, string State, string Balance, int Count);
    private sealed record LocalMovementRow(BankMovement Movement, string Date, string Description, string Credit, string Debit, string Balance, string Matched, string Source);

    private async Task Reconciliation()
    {
        var source = await Task.Run(() => workspace.Store.GetSnapshot());
        var rows = await Task.Run(() => LocalReconciliation.Read(source).OrderByDescending(p => p.End)
            .Select(p => new LocalPeriodRow(p, p.Month, $"{p.Start:yyyy/MM/dd} ← {p.End:yyyy/MM/dd}", p.IsConfirmed ? "معتمدة ومقفلة" : "مسودة / قيد العمل", ReportSheet.Money(BankReconciliation.Calculate(p).Balance), p.Movements.Count)).ToArray());
        Page.Children.Add(Ui.Text("مطابقة حساب البنك", 25));
        Page.Children.Add(Ui.Text("دفتر محلي مستقل. تأشير الحركة إقرار منك بمطابقتها مع كشف المصرف، ولا يعني التحقق الآلي أو إرسال شيء للمصرف. لا ربط تلقائي ولا حذف للحركات."));
        var grid = GridFor(("الشهر", "Month"), ("الفترة", "Dates"), ("الحالة", "State"), ("الحركات", "Count"), ("الرصيد / د.ع", "Balance"));
        grid.ItemsSource = rows; grid.Height = 350; Page.Children.Add(grid);
        Page.Children.Add(Ui.Button("فتح الفترة المحددة", () => { if (grid.SelectedItem is LocalPeriodRow row) OpenLocalPeriod(source, row.Period); else Ui.Message(this, "اختر فترة أولاً."); }));
        Page.Children.Add(Ui.Button("إنشاء فترة يدوية", () => CreateLocalPeriod(source)));
        if (rows.Length == 0) Page.Children.Add(Ui.Text("لا توجد فترات محفوظة. أنشئ فترة وأدخل رصيدها وحركاتها من المستندات الأصلية."));
    }

    private ReconciliationReview ReviewLocalPeriod(BackupSnapshot source, BankPeriod period, BankAccountDetails account)
    {
        try
        {
            if (!source.Months.Any(m => m.Name == period.Month))
                return ReconciliationReview.Prepare(period, null, null, account, "لا توجد بيانات مستوردة للشهر المرتبط بالفترة.");
            return ReconciliationReview.Prepare(period, ReconciliationSuggestions.Prepare(source, period.Month), BankStatementCalculator.Calculate(source, period.Month), account);
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or OverflowException or InvalidOperationException)
        { return ReconciliationReview.Prepare(period, null, null, account, ex.Message); }
    }

    private async Task SaveLocalLedger(BackupSnapshot source, BankPeriod period, bool add = false)
    {
        await Task.Run(() => {
            var patched = LocalReconciliation.Patch(source, period, add);
            workspace.Store.SaveSettings(new Dictionary<string, byte[]> { [BankReconciliation.SettingsKey] = patched }, source.SourceDigest, "تحديث مطابقة البنك: " + period.Month);
        });
        await LoadMonth();
    }

    private async void CreateLocalPeriod(BackupSnapshot source)
    {
        if (busy) return;
        await Run(async () => {
            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(Ui.Text("فترة مطابقة جديدة", 23));
            panel.Children.Add(Ui.Text("الأرقام والتواريخ يدوية من كشف المصرف. لا يُرحّل الرصيد أو التمويل تلقائياً."));
            var month = Field(panel, "الشهر المرتبط بالبيانات", Month);
            var start = new DatePicker { SelectedDate = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1) }; panel.Children.Add(Ui.Text("بداية الفترة")); panel.Children.Add(start);
            var end = new DatePicker { SelectedDate = DateTime.Today }; panel.Children.Add(Ui.Text("نهاية الفترة")); panel.Children.Add(end);
            var opening = Field(panel, "الرصيد الافتتاحي / د.ع", "0");
            var first = Field(panel, "اسم عضو اللجنة الأول", ""); var second = Field(panel, "اسم عضو اللجنة الثاني", "");
            var dialog = Ui.Dialog(this, "إنشاء فترة", new ScrollViewer { Content = panel }, 650, 720); BankPeriod? entered = null;
            panel.Children.Add(Ui.Button("مراجعة وإنشاء", () => { try {
                if (start.SelectedDate is not { } s || end.SelectedDate is not { } e) throw new InvalidDataException("اختر تاريخ البداية والنهاية.");
                var name = month.Text.Trim(); if (name.Length > 100 || name.Any(char.IsControl)) throw new InvalidDataException("اسم الشهر غير صالح.");
                var key = MonthNames.SortKey(name);
                if (key == 0) throw new InvalidDataException("أدخل شهراً وسنة مفهومين، مثل تشرين الثاني 2026.");
                var equivalents = source.Months.Where(m => MonthNames.SortKey(m.Name) == key).ToArray();
                if (equivalents.Length > 1) throw new InvalidDataException("توجد بيانات متعددة بأسماء تعادل هذا الشهر؛ راجع الأشهر المستوردة قبل إنشاء الفترة.");
                if (equivalents.Length == 1) name = equivalents[0].Name;
                var value = new BankPeriod(Guid.NewGuid(), name, DateOnly.FromDateTime(s), DateOnly.FromDateTime(e), checked((long)WholeAmount(opening.Text)), first.Text.Trim(), second.Text.Trim(), null, null, []);
                _ = BankReconciliation.Calculate(value);
                if (Ui.Message(dialog, $"إنشاء فترة {name} من {value.Start:yyyy/MM/dd} إلى {value.End:yyyy/MM/dd} برصيد افتتاحي {ReportSheet.Money(value.OpeningBalance)} د.ع؟{(equivalents.Length == 0 ? "\nتنبيه: لا توجد بيانات مستوردة لهذا الشهر؛ الفترة يدوية وفحص مصادرها غير مكتمل." : "")}", true) != MessageBoxResult.Yes) return;
                entered = value; dialog.Close();
            } catch (Exception ex) { Ui.Message(dialog, ex.Message); } }));
            panel.Children.Add(Ui.Button("إلغاء", dialog.Close)); dialog.ShowDialog();
            if (entered is not null) await SaveLocalLedger(source, entered, true);
        });
    }

    private async void OpenLocalPeriod(BackupSnapshot source, BankPeriod period)
    {
        if (busy) return;
        await Run(async () => {
            var account = await Task.Run(() => TransferPreparation.Account(source));
            var review = await Task.Run(() => ReviewLocalPeriod(source, period, account));
            var display = await Task.Run(() => review.Totals.Lines.Select(l => new LocalMovementRow(l.Movement, l.Movement.Date.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture), l.Movement.Description,
                l.Movement.Direction == MovementDirection.Credit ? ReportSheet.Money(l.Movement.Amount) : "", l.Movement.Direction == MovementDirection.Debit ? ReportSheet.Money(l.Movement.Amount) : "",
                ReportSheet.Money(l.Balance), l.Movement.Matched ? "مؤشرة" : "غير مؤشرة", l.Movement.SourceId is null ? "يدوي" : "مصدر محفوظ")).ToArray());
            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(Ui.Text($"{period.Month} • {(period.IsConfirmed ? "معتمدة ومقفلة" : "مسودة / قيد العمل")}", 24));
            panel.Children.Add(Ui.Text($"{period.Start:yyyy/MM/dd} ← {period.End:yyyy/MM/dd}\nافتتاحي: {ReportSheet.Money(period.OpeningBalance)} • دائن: {ReportSheet.Money(review.Totals.Credits)} • مدين: {ReportSheet.Money(review.Totals.Debits)} • ختامي: {ReportSheet.Money(review.Totals.Balance)} د.ع\nالتمويل: {ReportSheet.Money(review.Totals.Funding)} • غير المؤشر: {review.Unmatched}"));
            if (period.IsConfirmed) panel.Children.Add(Ui.Text($"وقت الاعتماد بتوقيت بغداد: {BankReconciliation.ConfirmedTime(period):yyyy/MM/dd HH:mm}\nالاعتماد قرار يدوي محفوظ ولا يلغي تنبيهات الفحص الحالي."));
            foreach (var alert in review.Alerts) panel.Children.Add(Ui.Text("⚠ " + alert.Text));
            var grid = GridFor(("التاريخ", "Date"), ("البيان", "Description"), ("دائن", "Credit"), ("مدين", "Debit"), ("الرصيد", "Balance"), ("المطابقة", "Matched"), ("المصدر", "Source"));
            grid.Height = 300; grid.ItemsSource = display; panel.Children.Add(grid);
            var dialog = Ui.Dialog(this, "تفاصيل المطابقة", new ScrollViewer { Content = panel }, 1100, 850);
            BankPeriod? updated = null; string? exportPath = null;
            if (!period.IsConfirmed)
            {
                panel.Children.Add(Ui.Button("إضافة حركة يدوية", () => { var movement = EnterLocalMovement(dialog, period); if (movement is null) return; updated = period with { Movements = period.Movements.Concat([movement]).ToArray() }; dialog.Close(); }));
                panel.Children.Add(Ui.Button("تعديل الحركة اليدوية المحددة", () => {
                    if (grid.SelectedItem is not LocalMovementRow row) { Ui.Message(dialog, "اختر حركة أولاً."); return; }
                    var movement = EnterLocalMovement(dialog, period, row.Movement);
                    if (movement is null) return;
                    updated = period with { Movements = period.Movements.Select(m => m.Id == movement.Id ? movement : m).ToArray() }; dialog.Close();
                }));
                panel.Children.Add(Ui.Button("تأشير / إلغاء تأشير الحركة المحددة", () => {
                    if (grid.SelectedItem is not LocalMovementRow row) { Ui.Message(dialog, "اختر حركة أولاً."); return; }
                    var m = row.Movement;
                    if (Ui.Message(dialog, $"{(m.Matched ? "إلغاء تأشير" : "تأكيد مطابقة")} حركة «{m.Description}» بمبلغ {ReportSheet.Money(m.Amount)} د.ع مع كشف المصرف؟", true) != MessageBoxResult.Yes) return;
                    updated = period with { Movements = period.Movements.Select(x => x.Id == m.Id ? x with { Matched = !x.Matched } : x).ToArray() }; dialog.Close();
                }));
                var confirm = Ui.Button("اعتماد الفترة وقفلها", () => {
                    if (Ui.Message(dialog, "هل راجعت جميع الحركات والمجاميع مع كشف المصرف وتريد اعتماد الفترة وقفلها؟", true) != MessageBoxResult.Yes) return;
                    if (!review.Complete && Ui.Message(dialog, "ما زالت تنبيهات مانعة في المراجعة:\n" + string.Join("\n", review.Alerts.Where(a => a.Blocking).Select(a => "• " + a.Text)) + "\n\nهل تقر بالاعتماد اليدوي رغم هذه التنبيهات؟", true) != MessageBoxResult.Yes) return;
                    updated = BankReconciliation.Confirm(period, DateTime.UtcNow); dialog.Close();
                }); confirm.IsEnabled = period.Movements.Count > 0 && review.Unmatched == 0; panel.Children.Add(confirm);
            }
            else panel.Children.Add(Ui.Button("إلغاء الاعتماد للتعديل", () => {
                if (Ui.Message(dialog, "إلغاء اعتماد الفترة وإعادة فتحها للتعديل؟ سيُحفظ إلغاء الاعتماد وحده دون تغيير الحركات.", true) != MessageBoxResult.Yes) return;
                updated = BankReconciliation.Unconfirm(period); dialog.Close();
            }));
            var export = Ui.Button(period.IsConfirmed ? "معاينة وطباعة الكشف المعتمد" : "معاينة وطباعة مسودة المطابقة", () => {
                var complete = period.IsConfirmed || review.Complete;
                if (Ui.Message(dialog, complete ? "حفظ كشف المطابقة بالتنسيق المعتمد؟ راجع بيانات الحساب والتواقيع قبل استخدامه." : "المطابقة غير مكتملة؛ سيُحفظ الكشف بعلامة مسودة. هل تريد المتابعة؟", true) != MessageBoxResult.Yes) return;
                exportPath = "preview"; dialog.Close();
            }); export.IsEnabled = period.Movements.Count > 0; panel.Children.Add(export);
            panel.Children.Add(Ui.Button("إغلاق", dialog.Close)); dialog.ShowDialog();
            // Work starts only after all child dialogs close. The owner's Run/Closing guard protects saving/export.
            if (updated is not null) await SaveLocalLedger(source, updated);
            else if (exportPath is not null)
            {
                void Current() { if (workspace.Store.GetStatus().RevisionId != source.SourceDigest) throw new InvalidDataException("تغيّرت البيانات؛ أعد فتح الفترة قبل تصديرها."); }
                var bytes = await Task.Run(() => { Current(); return ReconciliationPdf.Statement(period, account, period.IsConfirmed || review.Complete, ReportAssets.Load(Path.Combine(AppContext.BaseDirectory, "WatermarkLogo.png"))); });
                new LocalPdfPreviewWindow(this, "مطابقة-" + period.Month, bytes, Current).ShowDialog();
            }
        });
    }

    private BankMovement? EnterLocalMovement(Window owner, BankPeriod period, BankMovement? original = null)
    {
        if (period.IsConfirmed) { Ui.Message(owner, "الفترة معتمدة ومقفلة؛ ألغِ اعتمادها أولاً قبل التعديل."); return null; }
        if (original is not null && (original.SourceId is not null || original.SourceLineIds is { Count: > 0 } || original.IsRefund || original.RefundOf is not null))
        { Ui.Message(owner, "التعديل هنا للحركات اليدوية العادية فقط. الحركات المرتبطة بالمصادر والمسترجعات وروابط الإعادة محمية ولا يمكن تعديلها من هذه النافذة."); return null; }
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(Ui.Text(original is null ? "حركة يدوية من كشف المصرف" : "تعديل حركة يدوية", 22));
        panel.Children.Add(Ui.Text("لا تُربط هذه الحركة بمصدر رواتب أو استقطاع تلقائياً. المسترجعات وروابط الإعادة المحفوظة تبقى كما هي؛ إضافة روابط جديدة غير متاحة هنا."));
        panel.Children.Add(Ui.Text("التاريخ")); var date = new DatePicker { SelectedDate = (original?.Date ?? period.Start).ToDateTime(TimeOnly.MinValue) }; panel.Children.Add(date);
        var description = Field(panel, "البيان", original?.Description ?? ""); var amount = Field(panel, "المبلغ / د.ع", original is null ? "" : ReportSheet.Money(original.Amount));
        var direction = new ComboBox { ItemsSource = new[] { "دائن / وارد", "مدين / صادر" }, SelectedIndex = original?.Direction == MovementDirection.Debit ? 1 : 0 }; panel.Children.Add(direction);
        var funding = new CheckBox { Content = "تمويل الشهر (دائن فقط)", IsChecked = original?.IsFunding ?? false, Margin = new Thickness(0, 12, 0, 12) }; panel.Children.Add(funding);
        var dialog = Ui.Dialog(owner, original is null ? "إضافة حركة" : "تعديل حركة", new ScrollViewer { Content = panel }, 620, 660); BankMovement? entered = null;
        panel.Children.Add(Ui.Button(original is null ? "مراجعة وإضافة" : "مراجعة وحفظ التعديل", () => { try {
            if (date.SelectedDate is not { } day) throw new InvalidDataException("اختر تاريخ الحركة.");
            var value = new BankMovement(Guid.NewGuid(), DateOnly.FromDateTime(day), description.Text.Trim(), checked((long)WholeAmount(amount.Text)), direction.SelectedIndex == 0 ? MovementDirection.Credit : MovementDirection.Debit, IsFunding: funding.IsChecked == true);
            if (original is not null)
                value = BankReconciliation.Replace(original, original with { Date = value.Date, Description = value.Description, Amount = value.Amount, Direction = value.Direction, IsFunding = value.IsFunding });
            var updatedPeriod = period with { Movements = original is null ? period.Movements.Concat([value]).ToArray() : period.Movements.Select(m => m.Id == original.Id ? value : m).ToArray() };
            _ = BankReconciliation.Calculate(updatedPeriod);
            BankReconciliation.CheckLocked([period], [updatedPeriod]);
            var before = original is null ? "" : $"قبل التعديل: {original.Date:yyyy/MM/dd} • {original.Description} • {ReportSheet.Money(original.Amount)} د.ع • {(original.Direction == MovementDirection.Credit ? "دائن" : "مدين")} • {(original.IsFunding ? "تمويل" : "غير تمويل")}\n\n";
            if (Ui.Message(dialog, before + $"{(original is null ? "إضافة" : "حفظ تعديل")} حركة {(value.Direction == MovementDirection.Credit ? "دائنة" : "مدينة")} بتاريخ {value.Date:yyyy/MM/dd}:\n{value.Description}\n{ReportSheet.Money(value.Amount)} د.ع{(value.IsFunding ? " • تمويل" : "")}؟\n{(value.Matched ? "لم تتغير بيانات الحركة؛ يبقى تأشيرها السابق." : "ستبقى غير مؤشرة حتى مطابقتها.")}", true) != MessageBoxResult.Yes) return;
            entered = value; dialog.Close();
        } catch (Exception ex) { Ui.Message(dialog, ex.Message); } }));
        panel.Children.Add(Ui.Button("إلغاء", dialog.Close)); dialog.ShowDialog(); return entered;
    }
}
