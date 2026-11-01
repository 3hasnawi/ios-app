using System.IO;
using System.Text.Json;

namespace SalaryDesktop;

internal sealed record BackupMark(string Fingerprint, DateTime At);
/// آخر بصمة رآها البرنامج لكل جزء من التعديلات المحلية، وعدد مرات تغيّرها منذ آخر نسخة شاملة.
internal sealed record PendingChanges(string? Payroll, string? Links, string? Ledger, int Count);

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
