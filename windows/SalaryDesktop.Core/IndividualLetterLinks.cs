using System.Text.Json;
using Claunia.PropertyList;

namespace SalaryDesktop.Core;

public sealed record IndividualLetterLink(string Title, string? AccountKey, string? Iban, string? LocalAccount);

public static class IndividualLetterLinks
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static IndividualLetterLink FromEntry(TransferEntry entry) => new(PayrollAnalysis.Normalize(entry.Title),
        entry.Account is null ? null : PayrollAnalysis.Normalize(entry.Account.Entity),
        entry.Account is null ? null : TransferPreparation.NormalizeAccount(entry.Account.Iban),
        entry.Account?.LocalAccount is null ? null : TransferPreparation.NormalizeAccount(entry.Account.LocalAccount));
    public static Dictionary<string, IndividualLetterLink> Read(BackupSnapshot snapshot)
    {
        if (!snapshot.SettingsPlists.TryGetValue("transferLetterEntityLinks", out var plist)) return new(StringComparer.Ordinal);
        try
        {
            if (PropertyListParser.Parse(plist) is not NSData data) throw new InvalidDataException("صيغة ربط الكتب غير صالحة.");
            if (data.Bytes.Length == 0) return new(StringComparer.Ordinal);
            var result = JsonSerializer.Deserialize<Dictionary<string, IndividualLetterLink>>(data.Bytes, Options) ?? throw new JsonException();
            if (result.Count > 1000 || result.Any(p => p.Value is null || string.IsNullOrWhiteSpace(p.Value.Title) || p.Key.Length > 100 || p.Value.Title.Length > 1000)) throw new InvalidDataException("روابط الكتب غير صالحة.");
            return result;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or IndexOutOfRangeException or OverflowException)
        { throw new InvalidDataException("تعذّر قراءة روابط الكتب المحفوظة.", ex); }
    }
    public static TransferEntry? Resolve(BackupSnapshot snapshot, string id, IReadOnlyList<TransferEntry> candidates)
    {
        var links = Read(snapshot); if (!links.TryGetValue(id, out var link)) return null;
        var matches = candidates.Where(e => FromEntry(e) == link).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    public static byte[] Encode(Dictionary<string, IndividualLetterLink> links)
    {
        using var memory = new MemoryStream(); PropertyListParser.SaveAsBinary(new NSData(JsonSerializer.SerializeToUtf8Bytes(links, Options)), memory); return memory.ToArray();
    }
}
