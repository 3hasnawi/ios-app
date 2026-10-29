using System.IO.Compression;

namespace SalaryWindows;

internal sealed record StorageInventory(long ArchiveBytes, IReadOnlyDictionary<string, long> CompressedBytes)
{
    public static StorageInventory Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var sizes = new Dictionary<string, long> { ["months"] = 0, ["payroll"] = 0, ["deductions"] = 0, ["staff"] = 0, ["accounts"] = 0, ["books"] = 0, ["settings"] = 0, ["other"] = 0 };
        foreach (var entry in archive.Entries)
        {
            var key = entry.FullName switch {
                "months.jsonl" => "months", "employees.jsonl" => "payroll", "deductions.jsonl" => "deductions",
                "staff.jsonl" => "staff", "accounts.jsonl" => "accounts", "settings.json" or "manifest.json" => "settings",
                var name when name.StartsWith("books/", StringComparison.Ordinal) => "books", _ => "other"
            };
            sizes[key] = checked(sizes[key] + entry.CompressedLength);
        }
        return new(stream.Length, sizes);
    }
    public static string FormatBytes(long value)
    {
        string[] units = ["بايت", "كيلوبايت", "ميغابايت", "غيغابايت", "تيرابايت"];
        double amount = value; var index = 0;
        while (amount >= 1024 && index < units.Length - 1) { amount /= 1024; index++; }
        return amount.ToString("N1") + " " + units[index];
    }
}
