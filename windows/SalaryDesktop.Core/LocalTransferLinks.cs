using System.Security.Cryptography;
using System.Text.Json;
using Claunia.PropertyList;

namespace SalaryDesktop.Core;

public sealed record TransferLinksSnapshot(BackupSnapshot Snapshot, IReadOnlyDictionary<string, string> Links, string? Revision, IReadOnlyDictionary<string, string> Categories);
/// روابط أسماء الاستقطاعات إلى الحسابات المحفوظة؛ تحفظ مستقلة عن ملف الآيفون.
public sealed class LocalTransferLinks(string directory)
{
    private sealed record LinkData(int Version, string SourceDigest, Dictionary<string, string> Links, Dictionary<string, string>? Categories = null);
    private readonly string root = Path.GetFullPath(directory);
    private static string Digest(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F') ? value : throw new InvalidDataException("بصمة النسخة غير صالحة.");
    private string FileFor(BackupSnapshot source) => Path.Combine(root, Digest(source.SourceDigest) + ".json");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void Validate(Dictionary<string, string> links)
    {
        if (links.Count > 100_000 || links.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Key.Length > 1000 || string.IsNullOrWhiteSpace(p.Value) || p.Value.Length > 1000))
            throw new InvalidDataException("روابط الحسابات غير صالحة.");
    }
    private static Dictionary<string, string> Original(BackupSnapshot source, string key)
    {
        if (!source.SettingsPlists.TryGetValue(key, out var bytes)) return new(StringComparer.Ordinal);
        try
        {
            if (PropertyListParser.Parse(bytes) is not NSData data) throw new InvalidDataException("صيغة روابط الحسابات غير صالحة.");
            var links = JsonSerializer.Deserialize<Dictionary<string, string>>(data.Bytes) ?? throw new JsonException(); Validate(links); return links;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or IndexOutOfRangeException or OverflowException)
        { throw new InvalidDataException("تعذّر قراءة روابط الحسابات من النسخة.", e); }
    }
    private static BackupSnapshot WithLinks(BackupSnapshot source, Dictionary<string, string> links, Dictionary<string, string> categories)
    {
        using var plist = new MemoryStream(); PropertyListParser.SaveAsBinary(new NSData(JsonSerializer.SerializeToUtf8Bytes(links)), plist);
        var settings = source.SettingsPlists.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        settings["transferPayrollAccountLinks"] = plist.ToArray();
        using var categoryPlist = new MemoryStream(); PropertyListParser.SaveAsBinary(new NSData(JsonSerializer.SerializeToUtf8Bytes(categories)), categoryPlist);
        settings["transferEntityCategories"] = categoryPlist.ToArray();
        return source with { SettingsPlists = settings };
    }
    public TransferLinksSnapshot Load(BackupSnapshot source)
    {
        var path = FileFor(source);
        if (!File.Exists(path)) return new(source, Original(source, "transferPayrollAccountLinks"), null, Original(source, "transferEntityCategories"));
        try
        {
            var bytes = File.ReadAllBytes(path);
            var data = JsonSerializer.Deserialize<LinkData>(bytes) ?? throw new JsonException();
            if (data.Version != 1 || data.SourceDigest != source.SourceDigest || data.Links is null) throw new InvalidDataException("ملف الروابط المحلي غير صالح.");
            var categories = data.Categories ?? Original(source, "transferEntityCategories");
            Validate(data.Links); Validate(categories); return new(WithLinks(source, data.Links, categories), data.Links, Hash(bytes), categories);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or NullReferenceException or OverflowException)
        { throw new InvalidDataException("تعذّر قراءة روابط الحسابات المحلية؛ لم تُغيّر البيانات.", e); }
    }
    public TransferLinksSnapshot SaveLink(BackupSnapshot source, TransferLinksSnapshot expected, TransferEntry entry, ExecutionAccount account)
    {
        if (!source.Accounts.Any(a => a == account) || source.Accounts.Count(a => PayrollAnalysis.Normalize(a.Entity) == PayrollAnalysis.Normalize(account.Entity)) != 1)
            throw new InvalidDataException("الحساب المختار غير موجود أو اسمه مكرر في النسخة.");
        if (!entry.Id.StartsWith("payroll|", StringComparison.Ordinal) && !entry.Id.StartsWith("deduction|", StringComparison.Ordinal)) throw new InvalidDataException("مصدر الجهة غير صالح.");
        var key = entry.Id.StartsWith("payroll|", StringComparison.Ordinal) ? entry.Id[8..] : "deduction|" + PayrollAnalysis.Normalize(entry.Title);
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidDataException("جهة الاستقطاع غير صالحة.");
        Directory.CreateDirectory(root);
        using var guard = new FileStream(Path.Combine(root, ".links.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var current = Load(source);
        if (current.Revision != expected.Revision) throw new InvalidDataException("تغيّرت روابط الحسابات في نافذة أخرى؛ أعد فتح البيانات.");
        var actual = TransferPreparation.Prepare(current.Snapshot, entry.Month).Entries.SingleOrDefault(e => e.Id == entry.Id && e.Title == entry.Title);
        if (actual is null || actual.Amount != entry.Amount || actual.MissingAmounts != entry.MissingAmounts) throw new InvalidDataException("تغيّرت بيانات الجهة؛ أعد تحميلها قبل الربط.");
        var links = current.Links.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        links[key] = PayrollAnalysis.Normalize(account.Entity); Validate(links);
        return Write(source, links, current.Categories.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
    }
    public TransferLinksSnapshot SaveCategory(BackupSnapshot source, TransferLinksSnapshot expected, TransferEntry entry, TransferGroup category)
    {
        Directory.CreateDirectory(root);
        using var guard = new FileStream(Path.Combine(root, ".links.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var current = Load(source);
        if (current.Revision != expected.Revision) throw new InvalidDataException("تغيّرت بيانات الجهات في نافذة أخرى؛ أعد فتح البيانات.");
        var actual = TransferPreparation.Prepare(current.Snapshot, entry.Month).Entries.SingleOrDefault(e => e.Id == entry.Id && e.Title == entry.Title);
        if (actual is null || actual.Amount != entry.Amount || actual.MissingAmounts != entry.MissingAmounts || actual.Account != entry.Account) throw new InvalidDataException("تغيّرت بيانات الجهة؛ أعد تحميلها قبل التصنيف.");
        var value = category switch { TransferGroup.Rafidain => "rafidain", TransferGroup.OtherBanks => "otherBanks", TransferGroup.Anbar => "anbar", TransferGroup.Unresolved => "unresolved", _ => throw new InvalidDataException("التصنيف غير صالح.") };
        var key = $"{PayrollAnalysis.Normalize(actual.Title)}|{actual.Account?.Iban ?? ""}|{PayrollAnalysis.Normalize(actual.Account?.Branch ?? "")}";
        var categories = current.Categories.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        categories[key] = value; Validate(categories);
        return Write(source, current.Links.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), categories);
    }
    private TransferLinksSnapshot Write(BackupSnapshot source, Dictionary<string, string> links, Dictionary<string, string> categories)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new LinkData(1, source.SourceDigest, links, categories));
        var temp = Path.Combine(root, ".links-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { output.Write(bytes); output.Flush(true); }
            using (var input = File.OpenRead(temp)) if (!SHA256.HashData(input).AsSpan().SequenceEqual(SHA256.HashData(bytes))) throw new InvalidDataException("تعذّر التحقق من روابط الحسابات.");
            File.Move(temp, FileFor(source), true);
            return new(WithLinks(source, links, categories), links, Hash(bytes), categories);
        }
        finally { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
