using System.Text.Json;

namespace SalaryWindows;

internal sealed record WindowsSettings(string ServerAddress = "", bool HideZeroBankRows = false);
internal static class WindowsSettingsStore
{
    private static string PathName => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SalaryWindows", "settings.json");
    public static WindowsSettings Load(string? filePath = null)
    {
        try { return JsonSerializer.Deserialize<WindowsSettings>(File.ReadAllText(filePath ?? PathName)) ?? new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public static void Save(WindowsSettings settings, string? filePath = null)
    {
        var path = filePath ?? PathName;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(settings)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
