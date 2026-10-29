using System.Text.Json;

namespace SalaryWindows;

internal sealed record WindowsSettings(string ServerAddress = "", bool HideZeroBankRows = false, string Theme = "light", int FontSize = 16, bool LockEnabled = false)
{
    public WindowsSettings Normalized() => this with {
        ServerAddress = ServerAddress ?? "",
        Theme = Theme is "light" or "dark" or "system" ? Theme : "light",
        FontSize = FontSize is 14 or 16 or 19 ? FontSize : 16
    };
}
internal static class WindowsSettingsStore
{
    private static string PathName => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SalaryWindows", "settings.json");
    public static WindowsSettings Load(string? filePath = null)
    {
        var path = filePath ?? PathName;
        var lockMarker = File.Exists(path + ".lock");
        try {
            var loaded = (JsonSerializer.Deserialize<WindowsSettings>(File.ReadAllText(path)) ?? new()).Normalized();
            return lockMarker ? loaded with { LockEnabled = true } : loaded;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new WindowsSettings(LockEnabled: lockMarker); }
    }
    public static void Save(WindowsSettings settings, string? filePath = null)
    {
        var path = filePath ?? PathName;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var marker = path + ".lock";
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            // يبقى القفل مفعلاً إذا تلف ملف الإعدادات أو انقطعت الكتابة.
            if (settings.LockEnabled && !File.Exists(marker)) { using var file = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read); file.Flush(true); }
            File.WriteAllText(temp, JsonSerializer.Serialize(settings));
            File.Move(temp, path, true);
            if (!settings.LockEnabled && File.Exists(marker)) File.Delete(marker);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
