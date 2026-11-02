using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using SalaryDesktop.Core;

namespace SalaryDesktop;

/// سيرفر النسخ المحلي (مثل «السيرفر المحلي» في صفحة النسخ بالآيفون): نفس الإضافة ونفس الرمز ونفس قواعد العنوان،
/// ليُرفع إليه من ويندوز والآيفون. عند اختياره يصبح وجهة «نسخ الآن» والنسخ التلقائي والدوري بدل الملف.
public partial class MainWindow
{
    private static readonly HttpClient ServerHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private bool syncingServer;

    // ——— الرمز مشفّراً بـ DPAPI لحساب ويندوز الحالي (نظير سلسلة المفاتيح في الآيفون) ———
    private static readonly byte[] TokenEntropy = Encoding.UTF8.GetBytes("SalaryDesktop.BackupServer");
    public static string ServerToken
    {
        get
        {
            if (UiPreferences.Current.ServerToken is not { Length: > 0 } stored) return "";
            try { return Dpapi.Unprotect(Convert.FromBase64String(stored), TokenEntropy) is { } plain ? Encoding.UTF8.GetString(plain) : ""; }
            catch (FormatException) { return ""; }
        }
    }
    public static bool SaveServerToken(string token)
    {
        if (token.Length == 0) { UiPreferences.Current.ServerToken = null; return true; }
        if (Dpapi.Protect(Encoding.UTF8.GetBytes(token), TokenEntropy) is not { } cipher) return false;
        UiPreferences.Current.ServerToken = Convert.ToBase64String(cipher);
        return true;
    }

    private static bool ServerSelected => UiPreferences.Current.BackupService == "server";
    private static bool ServerConfigured => BackupServer.Url(UiPreferences.Current.ServerAddress) is not null && BackupServer.ValidToken(ServerToken);
    internal static bool ServerReady => UiPreferences.Current.ServerEnabled && ServerConfigured;
    /// وجهة النسخ التلقائي جاهزة: السيرفر مفعّل ومضبوط، أو ملف نسخة مختار.
    internal static bool HasBackupDestination => ServerReady || (!ServerSelected && UiPreferences.Current.BackupPath is not null);

    private void ShowServerSettings()
    {
        var saved = UiPreferences.Current;
        syncingServer = true;
        try
        {
            BackupServicePicker.SelectedItem = BackupServicePicker.Items.OfType<ListBoxItem>().FirstOrDefault(i => Equals(i.Tag, ServerSelected ? "server" : "file"));
            ServerPanel.Visibility = ServerSelected ? Visibility.Visible : Visibility.Collapsed;
            ChooseFileButton.Visibility = ServerSelected ? Visibility.Collapsed : Visibility.Visible;
            RestoreServerButton.Visibility = ServerSelected ? Visibility.Visible : Visibility.Collapsed;
            if (!ServerAddressBox.IsKeyboardFocusWithin) ServerAddressBox.Text = saved.ServerAddress ?? "";
            if (!ServerTokenBox.IsKeyboardFocusWithin && ServerTokenBox.Password.Length == 0) ServerTokenBox.Password = ServerToken;
            ServerEnabledBox.IsChecked = saved.ServerEnabled;
            ServerNotifyBox.IsChecked = saved.ServerNotify;
        }
        finally { syncingServer = false; }
        RestoreServerButton.IsEnabled = ServerConfigured && !loading;
    }

    private void BackupServiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingServer || BackupServicePicker.SelectedItem is not ListBoxItem { Tag: string service }) return;
        UiPreferences.Current.BackupService = service;
        // اختيار الملف يوقف السيرفر كما في الآيفون؛ واختيار السيرفر لا يفعّله حتى تُحفظ إعداداته.
        if (service == "file") { UiPreferences.Current.ServerEnabled = false; UiPreferences.Current.ServerNewer = false; }
        SaveSettings("خدمة النسخ"); ShowBackupSettings(); RefreshBackupSetupNotice();
    }

    private async void SaveServerSettings(object sender, RoutedEventArgs e)
    {
        var address = ServerAddressBox.Text.Trim(); var token = ServerTokenBox.Password; var enabled = ServerEnabledBox.IsChecked == true;
        if ((enabled || address.Length > 0) && BackupServer.Url(address) is null)
        { Ui.Message(this, "أدخل عنوان IP محلياً بالشكل http://192.168.1.10:8765\n(شبكة محلية فقط: 10.x أو 172.16–31.x أو 192.168.x).", "سيرفر النسخ الاحتياطي", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (enabled && !BackupServer.ValidToken(token))
        { Ui.Message(this, "أدخل رمز الوصول نفسه المضبوط في إضافة Home Assistant (٣٢ حرفاً إنجليزياً على الأقل بلا مسافات).", "سيرفر النسخ الاحتياطي", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var saved = UiPreferences.Current; var previousToken = ServerToken;
        if (!SaveServerToken(token)) { Ui.Message(this, "تعذّر حفظ رمز الوصول بشكل آمن على هذا الجهاز.", "سيرفر النسخ الاحتياطي", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (saved.ServerAddress != (address.Length > 0 ? address : null) || previousToken != token) { saved.ServerSeen = null; saved.ServerNewer = false; }
        if (enabled && (!saved.ServerEnabled || saved.ServerAddress != address || previousToken != token)) saved.ServerPending = true;
        saved.ServerAddress = address.Length > 0 ? address : null; saved.ServerEnabled = enabled; saved.BackupService = "server";
        if (enabled) saved.BackupError = null;
        SaveSettings("إعدادات السيرفر"); ShowBackupSettings(); RefreshBackupSetupNotice();
        if (enabled && saved.ServerPending && !backingUp) await FirstServerLink();
    }

    /// أول ربط بالسيرفر: إن كان فارغاً تُرفع نسخة ويندوز فوراً (مثل الآيفون)؛ وإن كانت عليه نسخة (من الآيفون مثلاً)
    /// لا يُرفع شيء فوقها، ويُعرض تنزيلها وفتحها هنا أولاً.
    private async Task FirstServerLink()
    {
        var url = BackupServer.Url(UiPreferences.Current.ServerAddress); var token = ServerToken;
        if (url is null) return;
        BackupServerStatus status;
        try { status = await BackupServer.Status(ServerHttp, url, token); }
        catch (InvalidDataException ex) { Ui.Message(this, "حُفظت الإعدادات، لكن تعذّر الاتصال بالسيرفر الآن.\n" + ex.Message + "\n\nلن يُرفع شيء قبل التحقق من نسخة السيرفر.", "سيرفر النسخ الاحتياطي", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (status.Latest is not { } latest)
        {
            if (snapshot is not null) await BackupToFile(true);
            return;
        }
        UiPreferences.Current.ServerPending = false; UiPreferences.Save();
        if (UiPreferences.Current.ServerSeen is { } seen && latest <= seen.AddSeconds(2)) return;
        var answer = Ui.Message(this,
            $"على السيرفر نسخة رُفعت {Ago(latest.LocalDateTime)} ({SizeText(status.Bytes)}). لم يُرفع شيء فوقها.\n\nنعم: نزّلها وافتحها هنا الآن (إذا كانت أحدث من بيانات هذا الجهاز).\nلا: أبقِ بيانات ويندوز؛ لن يُرفع إلى السيرفر تلقائياً حتى تسترجع نسخته أو تؤكد الاستبدال من «نسخ الآن».",
            "نسخة موجودة على السيرفر", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes) await RestoreServerCopy();
        else MarkServerNewer($"على السيرفر نسخة رُفعت {Ago(latest.LocalDateTime)} ({SizeText(status.Bytes)}). لم يُرفع شيء فوقها؛ استرجعها أولاً.");
    }

    // ——— «نسخة أحدث على السيرفر» (مطابق للآيفون) ———
    private static bool IsNewer(BackupServerStatus status) =>
        status.Latest is { } latest && (UiPreferences.Current.ServerSeen is not { } seen || latest > seen.AddSeconds(2));
    private string When(BackupServerStatus status) => status.Latest is { } latest ? $"{Ago(latest.LocalDateTime)} ({SizeText(status.Bytes)})" : "";

    private void MarkServerNewer(string text)
    {
        var saved = UiPreferences.Current;
        saved.ServerNewer = true; saved.ServerNewerText = text; UiPreferences.Save();
        RefreshBackupSetupNotice();
        TrayNotice("نسخة أحدث على السيرفر", text);
    }
    private void ClearServerNewer()
    {
        UiPreferences.Current.ServerNewer = false; UiPreferences.Current.ServerNewerText = null;
    }

    /// أول ربط لم يُحسم بعد والسيرفر فيه نسخة: لا رفع فوقها (البند ٣ في AGENTS.md ٨.١).
    private bool FirstLinkBlocked(BackupServerStatus status)
    {
        if (!UiPreferences.Current.ServerPending || status.Count == 0) return false;
        UiPreferences.Current.ServerPending = false;
        MarkServerNewer($"على السيرفر نسخة رُفعت {When(status)}. لم يُرفع شيء فوقها؛ استرجعها أولاً.");
        return true;
    }

    /// فحص دوري (كل ربع ساعة أثناء تشغيل البرنامج) لنسخة رفعها جهاز آخر؛ النتيجة تظهر في جرس الإشعارات.
    private DateTime? serverChecked;
    private async Task CheckServerNewer(bool force = false)
    {
        var saved = UiPreferences.Current;
        if (!ServerReady || !saved.ServerNotify || saved.ServerPending || backingUp || App.Unattended) return;
        if (!force && serverChecked is { } last && DateTime.Now - last < TimeSpan.FromMinutes(15)) return;
        serverChecked = DateTime.Now;
        var url = BackupServer.Url(saved.ServerAddress); var token = ServerToken; var address = saved.ServerAddress;
        if (url is null) return;
        try
        {
            var status = await BackupServer.Status(ServerHttp, url, token);
            // تغيّرت الإعدادات أو بدأ نسخ أثناء الفحص: تُهمل النتيجة.
            if (backingUp || !ServerReady || UiPreferences.Current.ServerAddress != address || ServerToken != token) return;
            if (IsNewer(status)) { if (!saved.ServerNewer) MarkServerNewer($"رفع جهاز آخر نسخة جديدة {When(status)}. استرجعها قبل النسخ من ويندوز."); }
            else if (saved.ServerNewer) { ClearServerNewer(); UiPreferences.Save(); RefreshBackupSetupNotice(); }
        }
        catch (InvalidDataException) { /* انقطاع السيرفر لا يُنشئ تنبيهاً كاذباً. */ }
    }

    private void ServerNotifyChanged(object sender, RoutedEventArgs e)
    {
        if (syncingServer) return;
        UiPreferences.Current.ServerNotify = ServerNotifyBox.IsChecked == true;
        if (!UiPreferences.Current.ServerNotify) ClearServerNewer();
        SaveSettings("إشعار النسخة الأحدث"); RefreshBackupSetupNotice();
        if (UiPreferences.Current.ServerNotify) _ = CheckServerNewer(true);
    }
    private async void RestoreFromNotification(object sender, RoutedEventArgs e) { NotificationPanel.Visibility = Visibility.Collapsed; await RestoreServerCopy(); }

    private async void TestServer(object sender, RoutedEventArgs e)
    {
        var url = BackupServer.Url(ServerAddressBox.Text); var token = ServerTokenBox.Password;
        if (url is null || !BackupServer.ValidToken(token)) { ServerTestText.Text = "⚠ أدخل عنواناً محلياً صحيحاً ورمز وصول لا يقل عن ٣٢ حرفاً."; ServerTestText.SetResourceReference(TextBlock.ForegroundProperty, "Warn"); return; }
        TestServerButton.IsEnabled = false; ServerTestText.Text = "جارٍ الاتصال بالسيرفر…"; ServerTestText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        try
        {
            var status = await BackupServer.Status(ServerHttp, url, token);
            ServerTestText.Text = "✓ متصل بسيرفر النسخ · " + (status.Latest is { } latest ? $"آخر نسخة {Ago(latest.LocalDateTime)} ({SizeText(status.Bytes)})" : "لا توجد نسخة بعد")
                + (status.DiskFree > 0 ? $" · المساحة المتاحة {SizeText(status.DiskFree)}" : "");
            ServerTestText.SetResourceReference(TextBlock.ForegroundProperty, "Primary");
        }
        catch (InvalidDataException ex) { ServerTestText.Text = "⚠ " + ex.Message; ServerTestText.SetResourceReference(TextBlock.ForegroundProperty, "Warn"); }
        finally { TestServerButton.IsEnabled = true; }
    }

    /// النسخ إلى السيرفر: فحص الوصول أولاً، ثم تجهيز النسخة في ملف مؤقت وفحصها، ثم رفعها ومطابقة ما أكّده السيرفر.
    private async Task<bool> BackupToServer(bool manual)
    {
        var source = snapshot; var original = baseSnapshot; var links = linkState; var root = rootSnapshot; var payroll = payrollState;
        var url = BackupServer.Url(UiPreferences.Current.ServerAddress); var token = ServerToken;
        if (source is null || original is null || links is null || root is null || payroll is null || url is null || backingUp) return false;
        backingUp = true; if (page == "settings") ShowBackupSettings();
        StatusBar.Text = "جارٍ النسخ إلى السيرفر المحلي…";
        var temporary = Path.Combine(Path.GetTempPath(), $"salary-server-{Guid.NewGuid():N}.zip");
        try
        {
            await BackupServer.CheckReachability(ServerHttp, url);
            // لا تُستبدل نسخة رفعها جهاز آخر بعد آخر رفع أو استرجاع من هذا الجهاز دون علمك.
            var current = await BackupServer.Status(ServerHttp, url, token);
            if (FirstLinkBlocked(current)) throw new InvalidDataException(UiPreferences.Current.ServerNewerText!);
            DateTimeOffset? approved = null;
            if (IsNewer(current))
            {
                var when = When(current);
                var reason = $"على السيرفر نسخة أحدث من جهاز آخر (الآيفون غالباً) رُفعت {when}؛ استرجعها من «استرجاع من السيرفر» قبل رفع نسخة ويندوز.";
                if (!manual) { MarkServerNewer(reason); throw new InvalidDataException(reason); }
                var replace = Ui.Message(this,
                    $"على السيرفر نسخة رُفعت {when} من جهاز آخر (الآيفون غالباً) بعد آخر نسخة رفعها أو استرجعها هذا الجهاز.\nاستبدالها بنسخة ويندوز قد يُضيّع تعديلات الآيفون.\n\nنعم: استبدلها بنسخة ويندوز.\nلا: إلغاء، ثم «استرجاع من السيرفر» (موصى به).",
                    "نسخة أحدث على السيرفر", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (replace != MessageBoxResult.Yes) { MarkServerNewer(reason); StatusBar.Text = "لم يُرفع شيء؛ نسخة السيرفر أحدث. استرجعها من الإعدادات ← النسخ الاحتياطي."; return false; }
                approved = current.Latest;
            }
            var prepared = await Task.Run(() =>
            {
                if (linkStore.Load(original).Revision != links.Revision) throw new InvalidDataException("تغيّرت روابط الحسابات أثناء النسخ؛ أعد المحاولة.");
                if (payrollStore.Load(root).Revision != payroll.Revision) throw new InvalidDataException("تغيّرت بيانات الأشهر أثناء النسخ؛ أعد المحاولة.");
                var ledger = reconciliationStore.Load(source);
                var trust = trustStore.Load(source);
                ReconciliationBackupExport.Write(source, ledger.Periods, temporary, trust.Ledger);
                var written = BackupReader.Read(temporary);
                if (source.Months.Any(m => !written.Months.Any(w => w.Name == m.Name))) throw new InvalidDataException("تعذّر التحقق من النسخة بعد تجهيزها.");
                return (Ledger: ledger.Revision, Trust: trust.Revision, Archive: ArchiveRevision(source), Size: new FileInfo(temporary).Length);
            });
            // فحص ثانٍ بعد تجهيز الملف (مثل الآيفون): التأكيد اليدوي لا يشمل نسخة رُفعت أثناء التجهيز.
            var beforeUpload = await BackupServer.Status(ServerHttp, url, token);
            if (FirstLinkBlocked(beforeUpload)) throw new InvalidDataException(UiPreferences.Current.ServerNewerText!);
            if (IsNewer(beforeUpload) && beforeUpload.Latest != approved)
            {
                var reason = $"رُفعت إلى السيرفر نسخة أحدث من جهاز آخر {When(beforeUpload)} أثناء تجهيز نسخة ويندوز؛ لم يُرفع شيء. استرجعها أولاً.";
                MarkServerNewer(reason); throw new InvalidDataException(reason);
            }
            var size = await BackupServer.Upload(ServerHttp, url, token, temporary);
            if (size != prepared.Size) throw new InvalidDataException("حجم النسخة على السيرفر لا يطابق الملف المرفوع؛ أعد المحاولة.");
            var changed = await Task.Run(() => linkStore.Load(original).Revision != links.Revision || payrollStore.Load(root).Revision != payroll.Revision || reconciliationStore.Load(source).Revision != prepared.Ledger || trustStore.Load(source).Revision != prepared.Trust);
            if (!changed && ReferenceEquals(source, snapshot)) RememberBackup(root, payroll.Revision, links.Revision, LedgerRevisions(prepared.Ledger, prepared.Trust, prepared.Archive));
            var saved = UiPreferences.Current;
            saved.BackupLast = DateTime.Now; saved.BackupError = null; saved.ServerPending = false; saved.BackupPlace = BackupServer.Place; ClearServerNewer();
            try { saved.ServerSeen = (await BackupServer.Status(ServerHttp, url, token)).Latest ?? DateTimeOffset.UtcNow; }
            catch (InvalidDataException) { saved.ServerSeen = DateTimeOffset.UtcNow; }
            UiPreferences.Save();
            StatusBar.Text = $"☁ حُفظت النسخة في السيرفر المحلي · {SizeText(size)} · {DateTime.Now:HH:mm}";
            if (manual) Ui.Message(this, changed ? "حُفظت النسخة في السيرفر، لكن البيانات تغيّرت أثناء النسخ؛ أعد النسخ." : "حُفظت النسخة في السيرفر المحلي بعد فحصها وتأكيد السيرفر لبصمتها.", "النسخ الاحتياطي", MessageBoxButton.OK, changed ? MessageBoxImage.Warning : MessageBoxImage.Information);
            return !changed;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            UiPreferences.Current.BackupError = ex.Message; UiPreferences.Save();
            TrayNotice("تعذّر النسخ إلى السيرفر", ex.Message, true);
            StatusBar.Text = "⚠ تعذّر النسخ إلى السيرفر: " + ex.Message;
            if (manual) Ui.Message(this, "تعذّر النسخ إلى السيرفر.\n" + ex.Message, "النسخ الاحتياطي", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            backingUp = false; RefreshBackupReminder(true); RefreshBackupSetupNotice(); if (page == "settings") ShowBackupSettings();
        }
    }

    /// استرجاع آخر نسخة من السيرفر: تُنزّل إلى ملف مؤقت ثم تُفتح بنفس فحوص «استرجاع من نسخة».
    private async void RestoreFromServer(object sender, RoutedEventArgs e) => await RestoreServerCopy();
    private async Task RestoreServerCopy()
    {
        var url = BackupServer.Url(UiPreferences.Current.ServerAddress); var token = ServerToken;
        if (loading || url is null || !BackupServer.ValidToken(token)) { Ui.Message(this, "أكمل إعدادات السيرفر (العنوان ورمز الوصول) أولاً.", "استرجاع من السيرفر"); return; }
        var temporary = Path.Combine(Path.GetTempPath(), $"salary-server-restore-{Guid.NewGuid():N}.zip");
        RestoreServerButton.IsEnabled = false; BeginLoading("*", "جارٍ تنزيل النسخة من السيرفر…");
        try
        {
            var latest = (await BackupServer.Status(ServerHttp, url, token)).Latest ?? throw new InvalidDataException("لا توجد نسخة على السيرفر بعد.");
            await BackupServer.DownloadLatest(ServerHttp, url, token, temporary);
            // لا يُنسب التنزيل إلى تاريخ نسخة تغيّرت أثناء النقل (مثل الآيفون).
            if ((await BackupServer.Status(ServerHttp, url, token)).Latest != latest) throw new InvalidDataException("تغيّرت نسخة السيرفر أثناء التنزيل؛ أعد المحاولة.");
            DateTimeOffset? fileCreated;
            try { fileCreated = await Task.Run(() => BackupReader.CreatedAt(temporary)); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException) { fileCreated = null; }
            EndLoading("*");
            // وقت الرفع من السيرفر وتاريخ الإنشاء من فهرس الملف شيئان مختلفين (مثل نافذة الآيفون)؛ لا يُنسب أي منهما إلى جهاز.
            static string When(DateTimeOffset moment) => moment.ToLocalTime().ToString("yyyy/MM/dd HH:mm", Numbers);
            var answer = Ui.Message(this,
                $"وقت رفع الملف إلى السيرفر: {When(latest)}\nتاريخ الإنشاء المسجّل داخل الملف: {(fileCreated is { } created ? When(created) : "غير مسجّل")}\n" +
                "النسخ التي حفظتها نسخ ويندوز السابقة قد تحمل تاريخ إنشاء أقدم من وقت حفظها؛ ووقت الرفع لا يحدد الجهاز أو محتوى التعديلات.\n\n" +
                "ستُفحص النسخة ثم تُفتح هنا، وتبقى تعديلات النسخة الحالية محفوظة على هذا الجهاز. هل تريد فتحها؟",
                "استرجاع من السيرفر", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            // بعد فتحها هنا يصبح هذا الجهاز على آخر نسخة، فيُسمح بالرفع بعد ذلك.
            if (await OpenBackupFile(temporary))
            {
                var saved = UiPreferences.Current;
                saved.ServerSeen = latest; saved.BackupError = null; saved.ServerPending = false; ClearServerNewer(); UiPreferences.Save(); RefreshBackupSetupNotice();
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        { Ui.Message(this, "تعذّر تنزيل النسخة من السيرفر.\n" + ex.Message, "استرجاع من السيرفر", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally
        {
            EndLoading("*");
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (page == "settings") ShowBackupSettings();
        }
    }

    /// تشفير ويندوز (DPAPI) لحساب المستخدم الحالي؛ لا يُفك على جهاز أو حساب آخر.
    private static class Dpapi
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Blob { public int Size; public IntPtr Data; }
        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref Blob input, string? description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
        private const int UiForbidden = 0x1;

        public static byte[]? Protect(byte[] data, byte[] entropy) => Run(data, entropy, true);
        public static byte[]? Unprotect(byte[] data, byte[] entropy) => Run(data, entropy, false);

        private static byte[]? Run(byte[] data, byte[] entropy, bool protect)
        {
            var input = GCHandle.Alloc(data, GCHandleType.Pinned); var salt = GCHandle.Alloc(entropy, GCHandleType.Pinned);
            try
            {
                var inBlob = new Blob { Size = data.Length, Data = input.AddrOfPinnedObject() };
                var saltBlob = new Blob { Size = entropy.Length, Data = salt.AddrOfPinnedObject() };
                Blob output;
                var ok = protect ? CryptProtectData(ref inBlob, "SalaryDesktop", ref saltBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                                 : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref saltBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
                if (!ok) return null;
                try { var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, output.Size); return result; }
                finally { LocalFree(output.Data); }
            }
            finally { input.Free(); salt.Free(); }
        }
    }
}
