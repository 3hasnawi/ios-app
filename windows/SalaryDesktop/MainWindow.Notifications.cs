using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SalaryDesktop;

/// جرس الإشعارات في الرأس بجانب زر الوضع الليلي: يجمع كل التنبيهات (تعديلات غير محفوظة في نسخة، إعداد النسخ أو فشله،
/// إصدار أحدث، تنبيهات تدقيق الشهر) ويعرض عدّاداً أحمر واضحاً، ويهتز الجرس عند ظهور تنبيه جديد.
public partial class MainWindow
{
    private int shownNotifications;
    private bool notificationsHooked;

    private Border[] NotificationCards => [ServerNewerCard, BackupReminder, BackupSetupCard, UpdateCard, AuditNoticeCard];

    private void HookNotifications()
    {
        if (notificationsHooked) return;
        notificationsHooked = true;
        var descriptor = DependencyPropertyDescriptor.FromProperty(VisibilityProperty, typeof(Border));
        foreach (var card in NotificationCards) descriptor.AddValueChanged(card, (_, _) => RefreshNotifications());
        // النقر خارج اللوحة يغلقها.
        PreviewMouseDown += (_, e) =>
        {
            if (NotificationPanel.Visibility != Visibility.Visible || e.OriginalSource is not DependencyObject source) return;
            for (var node = source; node is not null; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
                if (ReferenceEquals(node, NotificationPanel) || ReferenceEquals(node, BellButton)) return;
            NotificationPanel.Visibility = Visibility.Collapsed;
        };
        RefreshNotifications();
    }

    private void RefreshNotifications()
    {
        if (BellBadge is null) return;
        var count = NotificationCards.Count(c => c.Visibility == Visibility.Visible);
        var warning = ServerNewerCard.Visibility == Visibility.Visible || BackupReminder.Visibility == Visibility.Visible || BackupSetupCard.Visibility == Visibility.Visible;
        NoNotifications.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BellBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BellCount.Text = count.ToString(Numbers);
        BellIcon.SetResourceReference(TextBlock.ForegroundProperty, warning ? "Warn" : count > 0 ? "Accent" : "Primary");
        BellButton.ToolTip = count == 0 ? "لا توجد إشعارات" : $"{count:N0} إشعار يحتاج انتباهك";
        // اهتزاز قصير عند زيادة عدد التنبيهات حتى يلفت الانتباه.
        if (count > shownNotifications && IsLoaded)
        {
            var swing = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(900) };
            foreach (var (angle, ms) in new[] { (0d, 0), (-18d, 120), (16d, 260), (-12d, 400), (8d, 540), (-4d, 680), (0d, 900) })
                swing.KeyFrames.Add(new EasingDoubleKeyFrame(angle, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms))));
            BellSwing.BeginAnimation(RotateTransform.AngleProperty, swing);
        }
        shownNotifications = count;
    }

    private void ToggleNotifications(object sender, RoutedEventArgs e) =>
        NotificationPanel.Visibility = NotificationPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    private void CloseNotifications(object sender, RoutedEventArgs e) => NotificationPanel.Visibility = Visibility.Collapsed;

    /// بطاقة إعداد النسخ: لا يوجد ملف نسخة مختار، أو فشل آخر نسخ تلقائي.
    private void RefreshBackupSetupNotice()
    {
        RefreshBackupIndicator();
        if (BackupSetupCard is null) return;
        var saved = UiPreferences.Current;
        // نسخة أحدث على السيرفر: بطاقة مستقلة مع زر الاسترجاع، وتُغني عن بطاقة «فشل آخر نسخ».
        var newer = saved.ServerNewer && ServerReady;
        ServerNewerText.Text = saved.ServerNewerText ?? "رفع جهاز آخر نسخة جديدة. استرجعها قبل النسخ من ويندوز.";
        ServerNewerCard.Visibility = newer ? Visibility.Visible : Visibility.Collapsed;
        if (newer) { BackupSetupCard.Visibility = Visibility.Collapsed; return; }
        if (snapshot is null) { BackupSetupCard.Visibility = Visibility.Collapsed; return; }
        if (saved.BackupError is { } error)
        {
            BackupSetupTitle.Text = "فشل آخر نسخ احتياطي";
            BackupSetupText.Text = error;
            BackupSetupButton.Content = "نسخ الآن";
            BackupSetupCard.Visibility = Visibility.Visible;
        }
        else if (ServerSelected && !ServerReady)
        {
            BackupSetupTitle.Text = "إعدادات سيرفر النسخ غير مكتملة";
            BackupSetupText.Text = "أدخل عنوان السيرفر ورمز الوصول نفسيهما المستعملين في الآيفون وفعّل النسخ إلى السيرفر.";
            BackupSetupButton.Content = "فتح إعدادات النسخ";
            BackupSetupCard.Visibility = Visibility.Visible;
        }
        else if (!ServerSelected && saved.BackupPath is null)
        {
            BackupSetupTitle.Text = "لم يُختر ملف النسخة الاحتياطية";
            BackupSetupText.Text = "اختر ملفاً مرة واحدة (يُفضّل في OneDrive أو Google Drive) ليُكتب فوقه تلقائياً بعد كل استيراد.";
            BackupSetupButton.Content = "اختيار ملف النسخة";
            BackupSetupCard.Visibility = Visibility.Visible;
        }
        else BackupSetupCard.Visibility = Visibility.Collapsed;
    }
    private async void BackupSetupClicked(object sender, RoutedEventArgs e)
    {
        if (UiPreferences.Current.BackupError is not null && HasBackupDestination) await BackupToFile(true);
        else if (ServerSelected) { NotificationPanel.Visibility = Visibility.Collapsed; NavSettings.IsChecked = true; }
        else await ChooseAndBackup();
        RefreshBackupSetupNotice();
    }

    /// تنبيهات تدقيق الشهر المهمة من ملخص الرئيسية.
    private void ShowAuditNotice(string month, int severe, IEnumerable<string> kinds)
    {
        if (AuditNoticeCard is null) return;
        if (severe == 0) { AuditNoticeCard.Visibility = Visibility.Collapsed; return; }
        AuditNoticeTitle.Text = $"تنبيهات تدقيق {month}";
        AuditNoticeText.Text = string.Join("، ", kinds.Take(4)) + " — راجعها قبل اعتماد الشهر.";
        AuditNoticeCard.Visibility = Visibility.Visible;
    }
    private void OpenAuditFromNotification(object sender, RoutedEventArgs e) { NotificationPanel.Visibility = Visibility.Collapsed; OpenSalaryReview((string?)null); }
}
