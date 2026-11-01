using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace SalaryDesktop;

/// الإعدادات: الأمان (القفل وتشفير الملفات)، النسخ المحلية، والإصدار والتحديث.
public partial class MainWindow
{
    private bool syncingSettings;
    private static string LocalRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SalaryDesktop");

    private void ShowSettings()
    {
        syncingSettings = true;
        try
        {
            var saved = UiPreferences.Current;
            LockEnabledBox.IsChecked = Locker.Enabled;
            LockOptions.IsEnabled = Locker.Enabled;
            LockHelloBox.IsChecked = saved.LockHello;
            if (FontPicker.ItemsSource is null) FontPicker.ItemsSource = AppFonts.Options;
            FontPicker.SelectedItem = AppFonts.Find(AppFonts.CurrentFont);
            DigitsPicker.SelectedIndex = AppFonts.EasternDigits ? 1 : 0;
            LockMinutesBox.SelectedItem = LockMinutesBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, saved.LockMinutes.ToString(Numbers))) ?? LockMinutesBox.Items[1];
        }
        finally { syncingSettings = false; }
        EncryptStatus.Text = Locker.DataEncrypted ? "✓ بيانات البرنامج مشفّرة لحساب ويندوز الحالي." : "بيانات البرنامج غير مشفّرة.";
        EncryptButton.IsEnabled = !Locker.DataEncrypted;
        ShowCopies();
        VersionStatus.Text = $"الإصدار الحالي {Updates.Current}. التحديثات تُنشر في صفحة الإصدارات على GitHub.";
    }

    private void SaveSettings(string what) { if (UiPreferences.Save() is { } error) Ui.Message(this, $"تعذّر حفظ {what}.\n" + error, "الإعدادات", MessageBoxButton.OK, MessageBoxImage.Warning); }

    // ——— الخط والأرقام ———

    private void FontChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingSettings || FontPicker.SelectedItem is not AppFonts.Option option || option.Key == AppFonts.CurrentFont) return;
        UiPreferences.Current.Font = option.Key;
        AppFonts.Apply(option.Key, UiPreferences.Current.Digits);
        SaveSettings("الخط");
    }
    private void DigitsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingSettings || DigitsPicker.SelectedItem is not ListBoxItem { Tag: string digits }) return;
        UiPreferences.Current.Digits = digits;
        AppFonts.Apply(AppFonts.CurrentFont, digits);
        SaveSettings("شكل الأرقام");
    }

    // ——— القفل ———

    private void LockEnabledClicked(object sender, RoutedEventArgs e)
    {
        if (syncingSettings) return;
        if (LockEnabledBox.IsChecked == true)
        {
            var dialog = new PasswordWindow(false) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Password is not { } password) { LockEnabledBox.IsChecked = false; return; }
            Locker.Enable(password);
            SaveSettings("كلمة السر");
            Ui.Message(this, "فُعّل القفل. سيُطلب إدخال كلمة السر عند فتح البرنامج وبعد مدة عدم الاستخدام، ويمكن القفل فوراً بـ Ctrl+L.", "الأمان", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            // إيقاف القفل يتطلب كلمة السر الحالية.
            var check = new PasswordWindow(false, verifyOnly: true) { Owner = this, Title = "إيقاف القفل" };
            if (check.ShowDialog() != true) { LockEnabledBox.IsChecked = true; return; }
            Locker.Disable();
            SaveSettings("إعداد القفل");
        }
        ShowSettings();
    }
    private void LockMinutesChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingSettings || LockMinutesBox.SelectedItem is not ComboBoxItem { Tag: string tag } || !int.TryParse(tag, out var minutes)) return;
        UiPreferences.Current.LockMinutes = minutes; SaveSettings("مدة القفل");
    }
    private async void LockHelloClicked(object sender, RoutedEventArgs e)
    {
        if (syncingSettings) return;
        if (LockHelloBox.IsChecked == true)
        {
            if (!await Locker.HelloAvailable())
            { LockHelloBox.IsChecked = false; Ui.Message(this, "Windows Hello غير مُعدّ على هذا الجهاز. فعّله من إعدادات ويندوز (خيارات تسجيل الدخول) ثم أعد المحاولة.", "الأمان"); return; }
            if (!await Locker.Hello(this, "تأكيد استعمال Windows Hello لفتح تطبيق الرواتب")) { LockHelloBox.IsChecked = false; return; }
        }
        UiPreferences.Current.LockHello = LockHelloBox.IsChecked == true; SaveSettings("إعداد Windows Hello");
    }
    private void ChangePassword(object sender, RoutedEventArgs e)
    {
        var dialog = new PasswordWindow(true) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Password is not { } password) return;
        Locker.SetPassword(password); SaveSettings("كلمة السر");
        Ui.Message(this, "تغيّرت كلمة السر.", "الأمان", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private void LockNow(object sender, RoutedEventArgs e)
    {
        if (!Locker.Enabled) { Ui.Message(this, "فعّل قفل البرنامج من الإعدادات أولاً.", "الأمان"); return; }
        Locker.Lock();
    }

    private async void EncryptData(object sender, RoutedEventArgs e)
    {
        if (Ui.Message(this, "ستُشفَّر ملفات البرنامج المحلية لحساب ويندوز الحالي، فلا يفتحها حساب آخر على الجهاز. احتفظ بنسخة احتياطية شاملة قبل المتابعة. هل تريد التشفير؟", "تشفير البيانات", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        EncryptButton.IsEnabled = false;
        try
        {
            var (files, skipped) = await Task.Run(Locker.EncryptData);
            Ui.Message(this, $"شُفّر مجلد البيانات و{files:N0} ملف." + (skipped > 0 ? $" تعذّر تشفير {skipped:N0} ملف قيد الاستعمال؛ سيُشفَّر عند إعادة التشغيل والتشفير مجدداً." : ""), "تشفير البيانات", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        { Ui.Message(this, "تعذّر التشفير. قد لا يدعم إصدار ويندوز هذا تشفير الملفات (مثل ويندوز Home).\n" + ex.Message, "تشفير البيانات", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { ShowSettings(); }
    }

    // ——— النسخ المحلية ———

    private static string SizeText(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} غيغابايت" : $"{bytes / (double)(1L << 20):0.0} ميغابايت";
    private bool HasLocalEdits(string digest) =>
        Directory.Exists(Path.Combine(LocalRoot, "PayrollImports", digest)) || File.Exists(Path.Combine(LocalRoot, "TransferLinks", digest + ".json")) || File.Exists(Path.Combine(LocalRoot, "Reconciliations", digest + ".json"));

    private void ShowCopies()
    {
        try
        {
            var copies = store.Copies();
            CopiesText.Text = copies.Count == 0 ? "لا توجد نسخ محلية بعد." : $"{copies.Count:N0} نسخة محفوظة · الحجم {SizeText(copies.Sum(c => c.Size))}" + (copies.Count > 5 ? $" · يمكن حذف {copies.Skip(5).Count(c => !c.Current && !HasLocalEdits(c.Digest)):N0} نسخة أقدم" : "");
            PruneButton.IsEnabled = copies.Count > 5;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { CopiesText.Text = "تعذّر قراءة النسخ المحلية: " + ex.Message; PruneButton.IsEnabled = false; }
    }
    private async void PruneCopies(object sender, RoutedEventArgs e)
    {
        if (Ui.Message(this, "ستُحذف النسخ المحلية الأقدم ويبقى آخر 5 نسخ. لا تُحذف النسخة المفتوحة ولا أي نسخة مرتبطة بتعديلات محلية، ولا تتأثر ملفات الآيفون الأصلية. هل تريد المتابعة؟", "النسخ المحلية", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        PruneButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => store.Prune(5, HasLocalEdits));
            Ui.Message(this, $"حُذفت {result.Deleted:N0} نسخة ووُفّر {SizeText(result.Freed)}." + (result.Protected > 0 ? $" أُبقيت {result.Protected:N0} نسخة أقدم لأنها مرتبطة بتعديلات محلية أو قيد الاستعمال." : ""), "النسخ المحلية", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { Ui.Message(this, "تعذّر حذف النسخ.\n" + ex.Message, "النسخ المحلية", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { ShowCopies(); }
    }

    // ——— الإصدار والتحديث ———

    private Version? latestVersion;
    private async Task FindUpdate(bool manual)
    {
        if (manual) { CheckUpdateButton.IsEnabled = false; VersionStatus.Text = "جارٍ التحقق…"; }
        var latest = await Updates.Latest();
        if (manual) CheckUpdateButton.IsEnabled = true;
        if (latest is null)
        {
            if (manual) VersionStatus.Text = $"الإصدار الحالي {Updates.Current}. تعذّر الاتصال بصفحة الإصدارات؛ قد تمنع الشبكة الوصول إلى GitHub.";
            return;
        }
        latestVersion = latest;
        var newer = latest > Updates.Current;
        UpdateCard.Visibility = newer ? Visibility.Visible : Visibility.Collapsed;
        UpdateTitle.Text = $"إصدار أحدث متاح · {latest}";
        if (manual) VersionStatus.Text = newer ? $"الإصدار الحالي {Updates.Current}، والأحدث {latest}. نزّله من «تنزيل التحديث» في القائمة الجانبية." : $"لديك أحدث إصدار ({Updates.Current}).";
    }
    private async void CheckUpdate(object sender, RoutedEventArgs e) => await FindUpdate(true);
    private void OpenReleasePage(object sender, RoutedEventArgs e) => Updates.Open(Updates.Page);
    private void DownloadUpdate(object sender, RoutedEventArgs e) => Updates.Open(Updates.Download);
}
