using System.IO;
using System.Text.Json;

namespace SalaryDesktop;

internal sealed record BackupMark(string Fingerprint, DateTime At);
/// آخر بصمة رآها البرنامج لكل جزء من التعديلات المحلية، وعدد مرات تغيّرها منذ آخر نسخة شاملة.
internal sealed record PendingChanges(string? Payroll, string? Links, string? Ledger, int Count);
/// عنصر فُتح مؤخراً في الرئيسية (منتسب، جهة، كتاب) لكل نسخة مفتوحة؛ يبقى على هذا الجهاز فقط.
internal sealed record RecentItem(string Digest, string Kind, string Key, string Label, DateTime At);

/// تفضيلات الواجهة لهذا المستخدم في %LOCALAPPDATA%\SalaryDesktop\ui.json: الأعمدة الظاهرة، آخر نسخة شاملة لكل نسخة مفتوحة،
/// المظهر، وحجم النافذة وآخر قسم. فقدان الملف أو تلفه لا يوقف البرنامج؛ تُستعمل القيم الافتراضية.
internal sealed class UiPreferences
{
    public Dictionary<string, string[]>? Columns { get; set; }
    public Dictionary<string, BackupMark>? Backups { get; set; }
    public string? Theme { get; set; }
    public double[]? Window { get; set; }
    public bool Maximized { get; set; }
    public string? Page { get; set; }
    public string? Month { get; set; }
    /// خط الواجهة (مفتاح من AppFonts.Options) وشكل الأرقام: latin أو arabic.
    public string? Font { get; set; }
    public string? Digits { get; set; }
    public Dictionary<string, PendingChanges>? Pending { get; set; }
    /// قفل البرنامج: كلمة السر مخزنة بصمةً (PBKDF2) لا نصاً.
    public bool LockEnabled { get; set; }
    public string? LockHash { get; set; }
    public string? LockSalt { get; set; }
    public int LockMinutes { get; set; } = 10;
    public bool LockHello { get; set; }
    /// خيارا تقرير الراتب (مثل الآيفون): إخفاء البنود الصفرية، وإخفاء IBAN والأرقام الشخصية.
    public bool ReportHideZeros { get; set; }
    public bool ReportHidePersonal { get; set; }
    /// النسخ الاحتياطي (مثل الآيفون): ملف واحد يُكتب فوقه، ونسخ تلقائي بعد الاستيراد ودوري (off/hourly/daily).
    public string? BackupPath { get; set; }
    public bool BackupAuto { get; set; } = true;
    public string? BackupSchedule { get; set; }
    public DateTime? BackupLast { get; set; }
    public string? BackupError { get; set; }
    /// خدمة النسخ (مثل الآيفون): "file" ملف يُكتب فوقه (مجلد OneDrive أو Google Drive أو هذا الجهاز)، أو "server" السيرفر المحلي.
    public string? BackupService { get; set; }
    /// سيرفر النسخ المحلي: العنوان والتفعيل، والرمز مشفّراً بـ DPAPI لحساب ويندوز الحالي (لا يدخل في النسخة الاحتياطية).
    public string? ServerAddress { get; set; }
    public bool ServerEnabled { get; set; }
    public string? ServerToken { get; set; }
    /// أول نسخة إلى السيرفر بعد تفعيله أو تغيير عنوانه أو رمزه.
    public bool ServerPending { get; set; }
    /// تاريخ نسخة السيرفر التي رفعها هذا الجهاز أو استرجعها آخر مرة؛ نسخة أحدث منه تعني أن جهازاً آخر (الآيفون) رفع بعده.
    public DateTimeOffset? ServerSeen { get; set; }
    /// تنبيه «نسخة أحدث على السيرفر» (مثل إشعار الآيفون): فحص دوري كل ربع ساعة، وحالته ونصه للجرس.
    public bool ServerNotify { get; set; } = true;
    public bool ServerNewer { get; set; }
    public string? ServerNewerText { get; set; }
    public string? BackupPlace { get; set; }
    /// القائمة الجانبية العائمة: مطويّة إلى أيقونات أو موسّعة.
    public bool SidebarCollapsed { get; set; }
    /// الرئيسية: ما فُتح مؤخراً، وعدد مرات استعمال كل أداة، والأدوات المثبتة والمخفية.
    public List<RecentItem>? Recent { get; set; }
    public Dictionary<string, int>? ToolUse { get; set; }
    public List<string>? ToolPins { get; set; }
    public List<string>? ToolHidden { get; set; }

    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SalaryDesktop", "ui.json");
    private static UiPreferences? current;
    public static UiPreferences Current => current ??= Load();

    private static UiPreferences Load()
    {
        UiPreferences? loaded = null;
        try { if (File.Exists(FilePath)) loaded = JsonSerializer.Deserialize<UiPreferences>(File.ReadAllBytes(FilePath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { loaded = null; }
        loaded ??= new UiPreferences();
        loaded.Columns = loaded.Columns is null ? new(StringComparer.Ordinal) : new(loaded.Columns, StringComparer.Ordinal);
        loaded.Backups = loaded.Backups is null ? new(StringComparer.Ordinal) : new(loaded.Backups, StringComparer.Ordinal);
        loaded.Pending = loaded.Pending is null ? new(StringComparer.Ordinal) : new(loaded.Pending, StringComparer.Ordinal);
        loaded.Recent = loaded.Recent?.Where(r => r is { Digest: not null, Kind: not null, Key: not null, Label: not null }).Take(40).ToList() ?? [];
        loaded.ToolUse = loaded.ToolUse is null ? new(StringComparer.Ordinal) : new(loaded.ToolUse, StringComparer.Ordinal);
        loaded.ToolPins = loaded.ToolPins?.Where(k => k is not null).Distinct(StringComparer.Ordinal).ToList() ?? [];
        loaded.ToolHidden = loaded.ToolHidden?.Where(k => k is not null).Distinct(StringComparer.Ordinal).ToList() ?? [];
        if (loaded.LockHash is null || loaded.LockSalt is null) loaded.LockEnabled = false;
        if (loaded.LockMinutes is < 0 or > 240) loaded.LockMinutes = 10;
        if (loaded.Theme is not ("light" or "dark" or "system")) loaded.Theme = "light";
        return loaded;
    }

    /// يعيد رسالة الخطأ إن تعذّر الحفظ، وإلا null.
    public static string? Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporary = FilePath + ".tmp";
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(Current));
            File.Move(temporary, FilePath, true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ex.Message; }
    }
}
