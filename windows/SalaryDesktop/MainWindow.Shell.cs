using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SalaryDesktop.Core;

namespace SalaryDesktop;

/// إطار النافذة: تذكير الحفظ في نسخة شاملة، المظهر، سحب النسخة إلى البرنامج، وتذكّر حجم النافذة وآخر قسم.
public partial class MainWindow
{
    // ——— تذكير الحفظ ———

    private int backupRequest;
    private DateTime backupChecked;

    /// بصمة التعديلات المحلية (الأشهر المستوردة، الروابط والحسابات، دفتر المطابقة) تُقارن بآخر نسخة شاملة حُفظت لهذه النسخة.
    private static string? LedgerRevisions(string? ledger, string? trust, string? archive = null) => ledger is null && trust is null && archive is null ? null : $"{ledger ?? "-"}/{trust ?? "-"}" + (archive is null ? "" : "/" + archive);
    private static string Fingerprint(string? payroll, string? links, string? ledger) => $"{payroll}|{links}|{ledger}";

    private void RememberBackup(BackupSnapshot root, string? payroll, string? links, string? ledger)
    {
        UiPreferences.Current.Backups![root.SourceDigest] = new BackupMark(Fingerprint(payroll, links, ledger), DateTime.Now);
        UiPreferences.Current.Pending![root.SourceDigest] = new PendingChanges(payroll, links, ledger, 0);
        if (UiPreferences.Save() is { } error) StatusBar.Text = "حُفظت النسخة، لكن تعذّر تسجيل وقتها: " + error;
    }

    private async void RefreshBackupReminder(bool force = false)
    {
        var seen = rootSnapshot is { } r ? UiPreferences.Current.Pending!.GetValueOrDefault(r.SourceDigest) : null;
        var changedInMemory = seen is null || seen.Payroll != payrollState?.Revision || seen.Links != linkState?.Revision;
        if (!force && !changedInMemory && DateTime.UtcNow - backupChecked < TimeSpan.FromSeconds(3)) return;
        backupChecked = DateTime.UtcNow;
        var ticket = ++backupRequest; var source = snapshot; var root = rootSnapshot; var payroll = payrollState; var links = linkState;
        RefreshBackupSetupNotice();
        if (source is null || root is null) { BackupReminder.Visibility = Visibility.Collapsed; AuditNoticeCard.Visibility = Visibility.Collapsed; ShowSidebarStatus(false); return; }
        string? ledger;
        try { ledger = await Task.Run(() => LedgerRevisions(reconciliationStore.Load(source).Revision, trustStore.Load(source).Revision, ArchiveRevision(source))); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException) { ledger = null; }
        if (ticket != backupRequest || !ReferenceEquals(source, snapshot)) return;
        var parts = new List<string>();
        if (payroll?.Revision is not null) parts.Add("أشهر مستوردة");
        if (links?.Revision is not null) parts.Add("حسابات وروابط");
        if (ledger is not null) parts.Add("المطابقة والأمانات والأرشيف");
        var mark = UiPreferences.Current.Backups!.GetValueOrDefault(root.SourceDigest);
        var pending = parts.Count > 0 && mark?.Fingerprint != Fingerprint(payroll?.Revision, links?.Revision, ledger);
        // عدد التعديلات: كل تغيّر في بصمة جزء (أشهر، روابط، مطابقة) منذ آخر نسخة شاملة يُعدّ تعديلاً.
        var last = UiPreferences.Current.Pending!.GetValueOrDefault(root.SourceDigest);
        var count = last?.Count ?? 0;
        if (last is null) count = pending ? parts.Count : 0;
        else
        {
            if (payroll?.Revision is not null && payroll.Revision != last.Payroll) count++;
            if (links?.Revision is not null && links.Revision != last.Links) count++;
            if (ledger is not null && ledger != last.Ledger) count++;
        }
        if (!pending) count = 0;
        var next = new PendingChanges(payroll?.Revision, links?.Revision, ledger, count);
        if (next != last) { UiPreferences.Current.Pending![root.SourceDigest] = next; UiPreferences.Save(); }
        BackupReminder.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        ShowSidebarStatus(pending);
        if (pending)
        {
            BackupReminderTitle.Text = count == 1 ? "تعديل واحد غير محفوظ في نسخة" : count == 2 ? "تعديلان غير محفوظين في نسخة" : $"{count:N0} تعديلات غير محفوظة في نسخة";
            BackupReminderText.Text = string.Join("، ", parts) + " على هذا الجهاز فقط · " + (mark is null ? "لم تُحفظ نسخة شاملة بعد" : "آخر نسخة شاملة: " + Ago(mark.At));
        }
        if (page == "settings") ShowBackupSettings();
        if (page == "home") { RenderNextSteps(); PageSubtitle.Text = HomeSubtitle(SelectedMonth); }
    }

    /// سطر الحالة أسفل القائمة الجانبية: آخر نسخة احتياطية، أو تنبيه بتعديلات لم تُحفظ في نسخة.
    private void ShowSidebarStatus(bool pending)
    {
        SidebarBackup.Text = pending ? "تعديلات لم تُحفظ في نسخة بعد" : UiPreferences.Current.BackupLast is { } last ? "آخر نسخة احتياطية " + Ago(last) : "لم تُحفظ نسخة احتياطية بعد";
        SidebarDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, pending ? "Warn" : "Primary");
        RefreshBackupIndicator();
    }

    public sealed record BackupIndicatorInfo(string Text, string Color);
    public static BackupIndicatorInfo DescribeBackupIndicator(bool busy, bool newer, string? error, bool hasData, bool configured, bool pending, DateTime? last)
    {
        if (busy) return new("جارٍ إنشاء النسخة الاحتياطية…", "Accent");
        if (newer) return new("توجد نسخة أحدث على السيرفر؛ افتح إعدادات النسخ لمراجعتها.", "Warn");
        if (!string.IsNullOrWhiteSpace(error)) return new("فشل آخر نسخ احتياطي؛ افتح إعدادات النسخ لمعرفة السبب.", "Warn");
        if (!hasData) return new("لا توجد بيانات مفتوحة للنسخ.", "Muted");
        if (!configured) return new("لم تُكتمل إعدادات النسخ الاحتياطي.", "Warn");
        if (pending) return new("توجد تعديلات لم تُحفظ في نسخة احتياطية.", "Warn");
        return last is { } at ? new("آخر نسخة مسجلة على هذا الجهاز: " + Ago(at) + ".", "Primary") : new("لم تُسجّل نسخة احتياطية ناجحة بعد.", "Muted");
    }
    private void RefreshBackupIndicator()
    {
        if (BackupIndicatorButton is null || BackupIndicatorIcon is null || BackupIndicatorDot is null) return;
        var saved = UiPreferences.Current;
        var state = DescribeBackupIndicator(backingUp, saved.ServerNewer && ServerReady, saved.BackupError,
            snapshot is not null, HasBackupDestination, BackupReminder?.Visibility == Visibility.Visible, saved.BackupLast);
        var text = !backingUp && saved.ServerNewer && ServerReady && !string.IsNullOrWhiteSpace(saved.ServerNewerText)
            ? state.Text + "\n" + saved.ServerNewerText : state.Text;
        BackupIndicatorButton.ToolTip = text;
        System.Windows.Automation.AutomationProperties.SetName(BackupIndicatorButton, "النسخ الاحتياطي: " + text);
        BackupIndicatorIcon.SetResourceReference(TextBlock.ForegroundProperty, state.Color);
        BackupIndicatorDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, state.Color);
    }
    private void OpenBackupFromIndicator(object sender, RoutedEventArgs e)
    {
        NavSettings.IsChecked = true;
        Dispatcher.BeginInvoke(() => BackupStatus?.BringIntoView());
    }

    private static string Ago(DateTime at)
    {
        var days = (DateTime.Now.Date - at.Date).Days;
        return days <= 0 ? "اليوم " + at.ToString("HH:mm", Numbers) : days == 1 ? "أمس" : days == 2 ? "قبل يومين" : days <= 10 ? $"قبل {days} أيام" : $"قبل {days} يوماً";
    }

    // ——— المظهر ———

    private bool syncingTheme;
    private void ShowTheme()
    {
        syncingTheme = true;
        try { ThemePicker.SelectedItem = ThemePicker.Items.OfType<ListBoxItem>().FirstOrDefault(i => Equals(i.Tag, Themes.Current)); }
        finally { syncingTheme = false; }
        ThemeIcon.Text = Themes.IsDark ? "" : "";
        ThemeToggle.ToolTip = Themes.IsDark ? "التبديل إلى الوضع الفاتح" : "التبديل إلى الوضع الداكن";
    }
    private void SetTheme(string theme)
    {
        Themes.Apply(theme);
        UiPreferences.Current.Theme = theme;
        if (UiPreferences.Save() is { } error) StatusBar.Text = "تعذّر حفظ المظهر: " + error;
        ShowTheme();
    }
    private void ThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!syncingTheme && ThemePicker.SelectedItem is ListBoxItem { Tag: string theme } && theme != Themes.Current) SetTheme(theme);
    }
    private void ToggleTheme(object sender, RoutedEventArgs e) => SetTheme(Themes.IsDark ? "light" : "dark");

    // ——— سحب النسخة إلى البرنامج ———

    private static string[] DroppedFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 and <= 50 } files ? files : [];
    private void WindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedFiles(e).Any(p => Path.GetExtension(p).ToLowerInvariant() is ".zip" or ".xlsx") && !loading && !Locker.IsLocked ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    /// الملفات المسحوبة تمر بالاستيراد الموحّد نفسه: يتعرف على النسخة وملفات الرواتب والاستقطاعات السنوية والحسابات.
    private async void WindowDrop(object sender, DragEventArgs e)
    {
        var files = DroppedFiles(e);
        if (files.Length == 0 || Locker.IsLocked) return;
        e.Handled = true;
        await ImportPaths(files);
    }

    // ——— حجم النافذة وآخر قسم ———

    /// يُستعاد الحجم والموضع إن كانت النافذة تقع ضمن الشاشات الحالية؛ وفحص GitHub يبدأ دائماً بالوضع الافتراضي.
    private void RestorePlacement()
    {
        var saved = UiPreferences.Current;
        if (App.Unattended || saved.Window is not [var left, var top, var width, var height]) return;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var work = SystemParameters.WorkArea;
        // حجم محفوظ أكبر من الشاشة الحالية (مثل لابتوب بعد شاشة كبيرة) لا يُستعاد؛ ScreenFit يضبط النافذة.
        if (width > Math.Max(work.Width, screen.Width) + 1 || height > Math.Max(work.Height, screen.Height) + 1) { if (saved.Maximized) WindowState = WindowState.Maximized; return; }
        if (width < MinWidth || height < MinHeight || !screen.IntersectsWith(new Rect(left, top, width, height)) || !screen.Contains(new Point(left + 40, top + 20))) return;
        WindowStartupLocation = WindowStartupLocation.Manual; Left = left; Top = top; Width = width; Height = height;
        if (saved.Maximized) WindowState = WindowState.Maximized;
    }
    private void RestorePage()
    {
        if (App.Unattended) return;
        if (UiPreferences.Current.Month is { } month && MonthPicker.Items.Contains(month)) MonthPicker.SelectedItem = month;
        if (UiPreferences.Current.Page is not { } last || last == "home") return;
        if (last == "trust") { OpenSalaryTrust(this, new RoutedEventArgs()); return; }
        if (NavItems.FirstOrDefault(item => Equals(item.Tag, last)) is { } radio) radio.IsChecked = true;
    }
    private void SavePlacement()
    {
        if (App.Unattended) return;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        UiPreferences.Current.Window = [bounds.Left, bounds.Top, bounds.Width, bounds.Height];
        UiPreferences.Current.Maximized = WindowState == WindowState.Maximized;
        UiPreferences.Current.Page = page;
        if (SelectedMonth.Length > 0) UiPreferences.Current.Month = SelectedMonth;
        UiPreferences.Save();
    }
}
