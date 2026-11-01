using System.IO;
using System.Windows;
using Microsoft.Win32;
using SalaryDesktop.Core;

namespace SalaryDesktop;

/// الاستيراد الموحّد (مثل ImportCenter في الآيفون): اختيار ملف أو أكثر، التعرّف على نوع كل ملف، ثم مراجعة كل ملف في نافذته وحفظه.
public partial class MainWindow
{
    private bool importing;

    private async void ImportFiles(object sender, RoutedEventArgs e)
    {
        if (loading || importing) return;
        var dialog = new OpenFileDialog
        {
            Title = "استيراد الملفات",
            Filter = "نسخة احتياطية أو Excel (*.zip;*.xlsx)|*.zip;*.xlsx|كل الملفات (*.*)|*.*",
            CheckFileExists = true, Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true || dialog.FileNames.Length == 0) return;
        await ImportPaths(dialog.FileNames);
    }

    private async Task ImportPaths(IReadOnlyList<string> paths)
    {
        if (loading || importing || paths.Count == 0) return;
        importing = true; ImportFilesButton.IsEnabled = false;
        var results = new List<string>(); var imported = false;
        try
        {
            StatusBar.Text = "جارٍ التعرّف على الملفات…";
            var current = snapshot;
            var items = await Task.Run(() => ImportDetector.Order(paths.Distinct(StringComparer.OrdinalIgnoreCase).Select(p => ImportDetector.Detect(p, current))));
            StatusBar.Text = "";
            var review = new ImportCenterWindow(items, snapshot is not null) { Owner = this };
            if (review.ShowDialog() != true) return;

            var backupOpened = false;
            foreach (var item in items)
            {
                bool done;
                switch (item.Kind)
                {
                    case ImportKind.Backup:
                        if (backupOpened) { results.Add($"• {item.FileName}: لم تُفتح؛ تُفتح نسخة واحدة في كل استيراد."); continue; }
                        done = await OpenBackupFile(item.Path); backupOpened = done;
                        break;
                    case ImportKind.Accounts:
                        done = await ImportAccountsFrom(item.Path, quiet: true); imported |= done;
                        break;
                    case ImportKind.Payroll:
                        // لا توجد بيانات مفتوحة: يبدأ البرنامج بيانات جديدة من أول ملف رواتب، ثم تُستورد البقية إليها.
                        done = snapshot is null ? await StartFromExcelFile(item.Path, item.Month) : await ImportPayrollFrom(item.Path, item.Month, quiet: true);
                        imported |= done;
                        break;
                    case ImportKind.Annual:
                        if (snapshot is null) { results.Add($"• {item.FileName}: يحتاج بيانات مفتوحة أولاً (نسخة احتياطية أو ملف رواتب)."); continue; }
                        done = await ImportAnnualFrom(item.Path, quiet: true); imported |= done;
                        break;
                    default:
                        results.Add($"• {item.FileName}: لم يُستورد — {item.Note}"); continue;
                }
                results.Add($"{(done ? "✓" : "•")} {item.FileName}: {item.KindText}{(done ? "" : " — لم يُحفظ (أُلغي أو تعذّرت قراءته)")}");
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        { results.Add("تعذّر إكمال الاستيراد: " + ex.Message); }
        finally { importing = false; ImportFilesButton.IsEnabled = true; if (StatusBar.Text == "جارٍ التعرّف على الملفات…") StatusBar.Text = ""; }

        if (results.Count == 0) return;
        // النسخ التلقائي بعد الاستيراد (مثل الآيفون) إلى ملف النسخة المختار.
        string backupLine = "";
        if (imported)
        {
            if (UiPreferences.Current.BackupAuto && HasBackupDestination)
                backupLine = await BackupToFile(false) ? (ServerReady ? "\n\n☁ حُفظت نسخة احتياطية تلقائياً في السيرفر المحلي." : "\n\n☁ حُفظت نسخة احتياطية تلقائياً في ملف النسخة.") : "\n\n⚠ تعذّر النسخ التلقائي؛ راجع الإعدادات ← النسخ الاحتياطي.";
            else backupLine = "\n\nاحفظ نسخة احتياطية تشمل التغييرات من الإعدادات ← النسخ الاحتياطي.";
        }
        Ui.Message(this, string.Join("\n", results) + backupLine, "نتيجة الاستيراد", MessageBoxButton.OK, results.All(r => r.StartsWith('✓')) ? MessageBoxImage.Information : MessageBoxImage.Warning);
        if (page == "settings") ShowBackupSettings();
    }
}
