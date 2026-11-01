using System.IO;
using System.Text.Json;

namespace SalaryDesktop;

internal sealed record BackupMark(string Fingerprint, DateTime At);

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
