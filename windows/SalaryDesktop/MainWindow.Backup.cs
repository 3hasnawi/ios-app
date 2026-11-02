using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using SalaryDesktop.Core;

namespace SalaryDesktop;

/// النسخ الاحتياطي (مثل صفحة النسخ في الآيفون): ملف نسخة واحد يختاره المستخدم مرة ويُكتب فوقه بعد كل استيراد أو دورياً،
/// مع «نسخ الآن» والحفظ في مكان آخر والاسترجاع. لا يُستبدل الملف بنسخة فقدت أشهراً أو بيانات.
public partial class MainWindow
{
    private DispatcherTimer? backupTimer;
    private bool backingUp;
    private bool syncingBackup;

    private void StartBackupTimer()
    {
        if (backupTimer is not null || App.Unattended) return;
        backupTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        backupTimer.Tick += async (_, _) =>
        {
            await CheckServerNewer();
            var saved = UiPreferences.Current;
            var interval = saved.BackupSchedule switch { "hourly" => TimeSpan.FromHours(1), "daily" => TimeSpan.FromDays(1), _ => (TimeSpan?)null };
            if (!HasBackupDestination || snapshot is null || loading || importing || backingUp) return;
            // أول نسخة إلى السيرفر بعد تفعيله تُرفع دون انتظار الموعد (مثل الآيفون).
            if (ServerReady && saved.ServerPending) { await BackupToFile(false); return; }
            if (interval is null) return;
            // دوري: عند حلول الموعد وعند وجود تعديلات لم تدخل آخر نسخة (أو لم تُؤخذ نسخة بعد).
            var due = saved.BackupLast is not { } last || DateTime.Now - last >= interval.Value;
            if (due && (saved.BackupLast is null || BackupReminder.Visibility == Visibility.Visible)) await BackupToFile(false);
        };
        backupTimer.Start();
        _ = CheckServerNewer(true);
    }

    private static string ScheduleText(string? schedule) => schedule switch { "hourly" => "كل ساعة", "daily" => "كل يوم", _ => "بعد الاستيراد" };

    private void ShowBackupSettings()
    {
        var saved = UiPreferences.Current;
        syncingBackup = true;
        try
        {
            BackupAutoBox.IsChecked = saved.BackupAuto;
            CloseToTrayBox.IsChecked = saved.CloseToTray;
            BackupSchedulePicker.SelectedItem = BackupSchedulePicker.Items.OfType<ListBoxItem>().FirstOrDefault(i => Equals(i.Tag, saved.BackupSchedule ?? "off")) ?? BackupSchedulePicker.Items[0];
        }
        finally { syncingBackup = false; }
        ShowServerSettings();
        var pending = BackupReminder.Visibility == Visibility.Visible;
        var automatic = saved.BackupAuto || (saved.BackupSchedule ?? "off") != "off";
        var missing = !HasBackupDestination;
        BackupStatus.Text =
            missing ? (ServerSelected ? "⚠ أكمل إعدادات السيرفر وفعّله لتفعيل النسخ التلقائي" : "⚠ اختر ملف النسخة الاحتياطية لتفعيل النسخ التلقائي") :
            saved.BackupError is { } error ? "⚠ فشل آخر نسخ: " + error :
            pending ? "⚠ تغيّرت البيانات منذ آخر نسخة" :
            !automatic ? (saved.BackupLast is { } offLast ? "متوقف · آخر نسخة " + Ago(offLast) : "متوقف") :
            saved.BackupLast is { } last ? $"{ScheduleText(saved.BackupSchedule)} · آخر نسخة {Ago(last)} ✓" : "تلقائي · لم تُؤخذ نسخة بعد";
        BackupStatus.SetResourceReference(TextBlock.ForegroundProperty, missing || saved.BackupError is not null || pending ? "Warn" : "Primary");
        string size = "";
        try { if (saved.BackupPath is { } path && File.Exists(path)) size = " · " + SizeText(new FileInfo(path).Length); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { size = ""; }
        BackupFileText.Text = ServerSelected
            ? (BackupServer.Url(saved.ServerAddress) is { } server ? $"السيرفر المحلي: {server.Authority}" + (saved.ServerEnabled ? "" : " · غير مفعّل") : "أدخل عنوان السيرفر ورمز الوصول نفسيهما المستعملين في الآيفون.")
            : saved.BackupPath is { } file ? "ملف النسخة: " + file + size : "لم يُختر ملف بعد. «نسخ الآن» يطلب مكان الحفظ أول مرة ثم يكتب فوق الملف نفسه.";
        BackupNowButton2.IsEnabled = snapshot is not null && !backingUp;
        BackupExport.IsEnabled = snapshot is not null;
    }

    private void BackupOptionChanged(object sender, RoutedEventArgs e)
    {
        if (syncingBackup) return;
        UiPreferences.Current.BackupAuto = BackupAutoBox.IsChecked == true;
        SaveSettings("خيار النسخ التلقائي"); ShowBackupSettings();
    }
    private void BackupScheduleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingBackup || BackupSchedulePicker.SelectedItem is not ListBoxItem { Tag: string schedule }) return;
        UiPreferences.Current.BackupSchedule = schedule == "off" ? null : schedule;
        SaveSettings("النسخ الدوري"); ShowBackupSettings();
    }

    /// يختار ملف النسخة (جديداً أو موجوداً) ثم ينسخ إليه فوراً.
    private async void ChooseBackupFile(object sender, RoutedEventArgs e) => await ChooseAndBackup();
    private async Task<bool> ChooseAndBackup()
    {
        var current = UiPreferences.Current.BackupPath;
        var dialog = new SaveFileDialog
        {
            Title = "اختيار ملف النسخة الاحتياطية", Filter = "نسخة احتياطية (*.zip)|*.zip", AddExtension = true, DefaultExt = ".zip", OverwritePrompt = false,
            FileName = current is not null ? Path.GetFileName(current) : "نسخة الرواتب.zip",
            InitialDirectory = current is not null && Directory.Exists(Path.GetDirectoryName(current)) ? Path.GetDirectoryName(current) : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(this) != true) return false;
        // اختيار ملف يجعل الملف خدمة النسخ ويوقف السيرفر (مثل الآيفون).
        UiPreferences.Current.BackupPath = dialog.FileName; UiPreferences.Current.BackupError = null;
        UiPreferences.Current.BackupService = "file"; UiPreferences.Current.ServerEnabled = false;
        SaveSettings("ملف النسخة"); RefreshBackupSetupNotice();
        if (snapshot is null) { ShowBackupSettings(); return false; }
        return await BackupToFile(true);
    }

    private async void BackupNow(object sender, RoutedEventArgs e)
    {
        if (snapshot is null) { Ui.Message(this, "لا توجد بيانات مفتوحة للنسخ. استورد نسخة احتياطية أو ملفات Excel أولاً.", "النسخ الاحتياطي"); return; }
        if (HasBackupDestination) await BackupToFile(true);
        else if (ServerSelected) Ui.Message(this, "أكمل إعدادات السيرفر (العنوان ورمز الوصول) وفعّله من الإعدادات ← النسخ الاحتياطي.", "النسخ الاحتياطي");
        else await ChooseAndBackup();
    }

    /// يكتب نسخة شاملة فوق ملف النسخة المختار بعد فحصها؛ manual يعرض النتيجة في رسالة، والتلقائي يكتفي بالحالة.
    private async Task<bool> BackupToFile(bool manual)
    {
        if (ServerReady) return await BackupToServer(manual);
        var source = snapshot; var original = baseSnapshot; var links = linkState; var root = rootSnapshot; var payroll = payrollState;
        var destination = UiPreferences.Current.BackupPath;
        if (source is null || original is null || links is null || root is null || payroll is null || destination is null || backingUp) return false;
        backingUp = true; if (page == "settings") ShowBackupSettings();
        StatusBar.Text = "جارٍ النسخ الاحتياطي…";
        try
        {
            var saved = await Task.Run(() =>
            {
                if (linkStore.Load(original).Revision != links.Revision) throw new InvalidDataException("تغيّرت روابط الحسابات أثناء النسخ؛ أعد المحاولة.");
                if (payrollStore.Load(root).Revision != payroll.Revision) throw new InvalidDataException("تغيّرت بيانات الأشهر أثناء النسخ؛ أعد المحاولة.");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                BackupDestination.Guard(destination, source);
                var ledger = reconciliationStore.Load(source);
                var trust = trustStore.Load(source);
                ReconciliationBackupExport.Write(source, ledger.Periods, destination, trust.Ledger);
                // فحص الملف المكتوب: يُقرأ فهرسه ويجب أن يحتوي كل الأشهر.
                var written = BackupReader.Read(destination);
                if (source.Months.Any(m => !written.Months.Any(w => w.Name == m.Name))) throw new InvalidDataException("تعذّر التحقق من النسخة بعد كتابتها.");
                return (Changed: linkStore.Load(original).Revision != links.Revision || payrollStore.Load(root).Revision != payroll.Revision || reconciliationStore.Load(source).Revision != ledger.Revision || trustStore.Load(source).Revision != trust.Revision, Ledger: LedgerRevisions(ledger.Revision, trust.Revision, ArchiveRevision(source)));
            });
            if (!saved.Changed && ReferenceEquals(source, snapshot)) RememberBackup(root, payroll.Revision, links.Revision, saved.Ledger);
            UiPreferences.Current.BackupLast = DateTime.Now; UiPreferences.Current.BackupError = null; UiPreferences.Current.BackupPlace = Path.GetFileName(destination); UiPreferences.Save();
            StatusBar.Text = $"☁ حُفظت نسخة احتياطية: {Path.GetFileName(destination)} · {DateTime.Now:HH:mm}";
            if (manual) Ui.Message(this, saved.Changed ? "حُفظت النسخة، لكن البيانات تغيّرت أثناء النسخ؛ أعد النسخ." : "حُفظت النسخة الاحتياطية بعد فحصها في:\n" + destination, "النسخ الاحتياطي", MessageBoxButton.OK, saved.Changed ? MessageBoxImage.Warning : MessageBoxImage.Information);
            return !saved.Changed;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            UiPreferences.Current.BackupError = ex.Message; UiPreferences.Save();
            TrayNotice("تعذّر النسخ الاحتياطي", ex.Message, true);
            StatusBar.Text = "⚠ تعذّر النسخ الاحتياطي: " + ex.Message;
            if (manual) Ui.Message(this, "تعذّر حفظ النسخة.\n" + ex.Message, "النسخ الاحتياطي", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally { backingUp = false; RefreshBackupReminder(true); RefreshBackupSetupNotice(); if (page == "settings") ShowBackupSettings(); }
    }
}
