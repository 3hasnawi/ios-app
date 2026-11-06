using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SalaryDesktop.Core;
using SalaryLocal.Core;

namespace SalaryLocal.App;
public partial class MainWindow
{
    private static (byte[] Sakkal, byte[] Arial, byte[] ArialBold, byte[] Logo) LetterAssets(BackupSnapshot source)
    {
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var sakkal = GroupLetterFont.Read(source);
        if (sakkal is null)
        {
            var file = Path.Combine(fonts, "majallab.ttf");
            if (!File.Exists(file)) throw new InvalidDataException("استورد خط Sakkal Majalla العريض أولاً؛ لا يُستخدم خط بديل.");
            sakkal = File.ReadAllBytes(file);
        }
        return (sakkal, File.ReadAllBytes(Path.Combine(fonts, "arial.ttf")), File.ReadAllBytes(Path.Combine(fonts, "arialbd.ttf")), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "IraqEmblem.png")));
    }
    private async void OpenTransferLetterBatch(BackupSnapshot source, TransferSnapshot transfer)
    {
        if (busy) return;
        await Run(async () => {
            LetterCurrent(source);
            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(Ui.Text("اختيار دفعة كتب • " + transfer.Month, 24));
            panel.Children.Add(Ui.Text("اختر الكتب المطلوبة فقط، وحدد جهة كل كتاب منفرد. تتوقف الدفعة كاملة عند وجود خطأ، ولا يُستخدم الاستقطاع نفسه في كتابين. لا تُصدر كتب تلقائياً ولا تُرسل للمصرف."));
            var rows = new List<(string Id, CheckBox Selected, ComboBox? Entity)>();
            var entities = transfer.Entries.Where(e => e.Group == TransferGroup.Anbar && e.Id.StartsWith("deduction|", StringComparison.Ordinal)).ToArray();
            foreach (var choice in LocalTransferLetterSettings.Choices)
            {
                var check = new CheckBox { Content = choice.Title, Margin = new Thickness(0, 10, 0, 6) }; panel.Children.Add(check);
                ComboBox? picker = null;
                if (LocalTransferLetterSettings.IsEntity(choice.Id))
                {
                    picker = new ComboBox { ItemsSource = entities, DisplayMemberPath = "Title", SelectedIndex = -1, IsEnabled = false };
                    var capture = picker; check.Checked += (_, _) => capture.IsEnabled = true; check.Unchecked += (_, _) => capture.IsEnabled = false;
                    panel.Children.Add(picker);
                }
                rows.Add((choice.Id, check, picker));
            }
            var dialog = Ui.Dialog(this, "تجهيز دفعة كتب", new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, 840, 800);
            LocalLetterRequest[]? requests = null;
            panel.Children.Add(Ui.Button("مراجعة جميع الكتب المحددة", () => {
                var selected = rows.Where(r => r.Selected.IsChecked == true).ToArray();
                if (selected.Length == 0 || selected.Any(r => r.Entity is not null && r.Entity.SelectedItem is not TransferEntry)) { Ui.Message(dialog, "اختر كتاباً واحداً على الأقل وجهة كل كتاب منفرد محدد."); return; }
                requests = selected.Select(r => new LocalLetterRequest(r.Id, (r.Entity?.SelectedItem as TransferEntry)?.Id)).ToArray(); dialog.Close();
            }));
            panel.Children.Add(Ui.Button("إلغاء", dialog.Close)); dialog.ShowDialog(); if (requests is null) return;
            var date = DateOnly.FromDateTime(DateTime.Today);
            var review = await Task.Run(() => LocalTransferLetters.PrepareBatch(source, transfer.Month, requests, date));
            LetterCurrent(source); ShowTransferBatchReview(source, review); await LoadMonth();
        });
    }
    private void ShowTransferBatchReview(BackupSnapshot source, LocalLetterBatchPreview review)
    {
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(Ui.Text($"مراجعة دفعة من {review.Letters.Count} كتب • {review.Month}", 24));
        panel.Children.Add(Ui.Text("كل كتاب وقائمة أسمائه ضمن PDF واحد، مع نسخة قائمة مستقلة. يجب فتح كل كتاب ومراجعته قبل حفظ الدفعة. لا تُجمع مبالغ الكتب كمبلغ محاسبي جديد، ولا تُسجَّل إقرارات إصدار تلقائياً."));
        var grid = GridFor(("الكتاب", "Title"), ("المبلغ", "Total"), ("المستفيد", "BeneficiaryAccount")); grid.ItemsSource = review.Letters; grid.Height = 240; panel.Children.Add(grid);
        foreach (var letter in review.Letters)
        {
            if (letter.Payroll is { } p) foreach (var n in p.Notices) panel.Children.Add(Ui.Text("⚠ " + letter.Title + ": " + n));
            if (letter.Settings.BodyMonth is { Length: > 0 } month && MonthNames.SortKey(month) != MonthNames.SortKey(letter.Month)) panel.Children.Add(Ui.Text("⚠ " + letter.Title + ": شهر المتن " + month + " يختلف عن شهر مبالغ المصدر " + letter.Month));
        }
        var dialog = Ui.Dialog(this, "مراجعة الدفعة", new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, 1050, 840);
        var status = Ui.Text("لم تُنشأ الملفات بعد."); panel.Children.Add(status);
        var consent = new CheckBox { Content = Ui.Text("راجعت جميع ملفات PDF وCSV والأسماء والمبالغ والحسابات والشهر واللجنة والتنسيق، وأوافق على حفظ مسودات الدفعة."), IsEnabled = false }; panel.Children.Add(consent);
        var actions = new StackPanel(); var viewers = new WrapPanel(); panel.Children.Add(actions); panel.Children.Add(viewers);
        LocalLetterBatchPackage? package = null; bool working = false; var opened = new HashSet<int>();
        dialog.Closing += (_, e) => { if (working) e.Cancel = true; };
        async Task Work(Func<Task> action)
        {
            if (working) return; working = true; actions.IsEnabled = viewers.IsEnabled = consent.IsEnabled = false;
            try { LetterCurrent(source); await action(); }
            catch (Exception ex) { status.Text = "توقفت العملية؛ لا حزمة جزئية ولا إرسال للمصرف."; Ui.Message(dialog, ex.Message); }
            finally { working = false; actions.IsEnabled = viewers.IsEnabled = true; consent.IsEnabled = package is not null && opened.Count == package.Packages.Count; }
        }
        actions.Children.Add(Ui.Button("إنشاء ملفات الدفعة كاملة", async () => await Work(async () => {
            package = null; consent.IsChecked = false; opened.Clear(); viewers.Children.Clear();
            package = await Task.Run(() => { var assets = LetterAssets(source); return LocalTransferLetters.GenerateBatch(review, assets.Sakkal, assets.Arial, assets.ArialBold, assets.Logo); });
            LetterCurrent(source);
            for (int i = 0; i < package.Packages.Count; i++)
            {
                var index = i; var item = package.Packages[i];
                viewers.Children.Add(Ui.Button("معاينة وطباعة • " + item.Preview.Title, async () => await Work(() => {
                    new LocalPdfPreviewWindow(dialog, item.Preview.Title, item.ReadFile("مسودة الكتاب.pdf"), () => workspace.Store.VerifyTransferLetterBatch(review)).ShowDialog();
                    opened.Add(index); status.Text = $"فُتحت {opened.Count} من {review.Letters.Count} كتب. راجع المحتوى فعلياً قبل الإقرار."; return Task.CompletedTask;
                })));
                if (item.Preview.Group is { } group)
                    viewers.Children.Add(Ui.Button("عرض CSV • " + item.Preview.Title, () => {
                        var text = new TextBox { Text = System.Text.Encoding.UTF8.GetString(group.Csv), IsReadOnly = true, AcceptsReturn = true, FlowDirection = FlowDirection.LeftToRight, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                        Ui.Dialog(dialog, "ملف التحويل للمراجعة فقط", text, 1050, 650).ShowDialog();
                    }));
            }
            status.Text = "اكتملت كل الملفات في الذاكرة. افتح كل كتاب من الأزرار أدناه، وراجع CSV للمجموعتين. لم يُحفظ شيء خارج التطبيق.";
        })));
        actions.Children.Add(Ui.Button("حفظ الدفعة كاملة • ZIP جديد", async () => {
            if (package is null || opened.Count != review.Letters.Count || consent.IsChecked != true) { Ui.Message(dialog, "أنشئ الدفعة وافتح كل كتاب، ثم أقر بمراجعتها."); return; }
            if (Ui.Message(dialog, "حفظ جميع المسودات في ZIP جديد؟ يحتوي أسماء وحسابات وغير مشفر؛ لا يسجل إصداراً ولا يرسل للمصرف.", true) != MessageBoxResult.Yes) return;
            var picker = new SaveFileDialog { Filter = "حزمة مسودات (*.zip)|*.zip", DefaultExt = ".zip", AddExtension = true, OverwritePrompt = true, FileName = ReportSheet.FileName("دفعة-كتب-" + review.Month) + ".zip" };
            if (Ui.ShowNativeDialog(() => picker.ShowDialog(dialog)) != true) return; var captured = package;
            await Work(async () => { await Task.Run(() => workspace.Store.SaveTransferLetterBatch(captured, picker.FileName)); status.Text = "حُفظت الدفعة كاملة وفُحصت بصمتها دون إقرار إصدار.\n" + picker.FileName; });
        }));
        panel.Children.Add(Ui.Button("إغلاق دون حذف البيانات", dialog.Close)); dialog.ShowDialog();
    }
}
