using System.IO.Compression;
using System.Text.Json;
using Claunia.PropertyList;

namespace AnbarPayroll;

public sealed partial class Store
{
    const string PendingFile = "portable-settings.json";
    static readonly string[] PreferenceKeys = ["homePinnedWorkItems", "homeHiddenWorkReports", "appAppearance", "appNumerals", "appTextSize", "windowsContentTextLevel"];

    static byte[]? PendingSettings(string? revision)
    {
        if (revision == null) return null;
        var path = Path.Combine(revision, PendingFile);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    static Dictionary<string, byte[]> Changes(byte[]? data) => data == null ? new() :
        JsonSerializer.Deserialize<Dictionary<string, byte[]>>(data) ?? throw new InvalidDataException("تعذرت قراءة إعدادات النسخة.");

    public Dictionary<string, string> ReadWorkPreferences()
    {
        var revision = Active;
        if (revision == null) return new();
        using var zip = ZipFile.OpenRead(Path.Combine(revision, BackupName));
        var stored = ReadJson<Dictionary<string, byte[]>>(zip, "settings.json") ?? new();
        var changes = Changes(PendingSettings(revision));
        var result = new Dictionary<string, string>();
        foreach (string key in PreferenceKeys) {
            if (!changes.TryGetValue(key, out var data) && !stored.TryGetValue(key, out data)) continue;
            var value = PropertyListParser.Parse(data);
            if (value is NSString text) result[key] = text.Content;
            if (value is NSNumber number) result[key] = number.ToString();
        }
        return result;
    }

    public void SaveWorkPreferences(string revision, Dictionary<string, string> values)
    {
        using var lease = new FileStream(Path.Combine(Root, "import.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (Active != revision) throw new IOException("تغيّرت النسخة. أعد فتح الإعدادات.");
        var changes = Changes(PendingSettings(revision));
        foreach (var (key, value) in values) {
            if (!PreferenceKeys.Contains(key)) throw new ArgumentException(nameof(values));
            changes[key] = Plist(key is "appTextSize" or "windowsContentTextLevel" ? new NSNumber(int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)) : new NSString(value));
        }
        SalaryDesktop.Core.ReportFiles.Save(JsonSerializer.SerializeToUtf8Bytes(changes), Path.Combine(revision, PendingFile), ".json");
    }

    public void ExportBackup(string destination)
    {
        var revision = Active ?? throw new InvalidOperationException("اختر نسخة أولاً.");
        if (PendingSettings(revision) != null) revision = Rewrite(revision, null, null);
        using var lease = new FileStream(Path.Combine(Root, "import.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (Active != revision || PendingSettings(revision) != null) throw new IOException("تغيّرت الإعدادات أثناء الحفظ. أعد حفظ النسخة.");
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            File.Copy(Archive, temp);
            File.Move(temp, destination, true);
        } finally { File.Delete(temp); }
    }
}
