using System.Security.Cryptography;
using System.Text.Json;
using Claunia.PropertyList;

namespace SalaryDesktop.Core;

public sealed record TransferLinksSnapshot(BackupSnapshot Snapshot, IReadOnlyDictionary<string, string> Links, string? Revision, IReadOnlyDictionary<string, string> Categories);
/// روابط أسماء الاستقطاعات إلى الحسابات المحفوظة؛ تحفظ مستقلة عن ملف الآيفون.
public sealed class LocalTransferLinks(string directory)
{
    private sealed record LinkData(int Version, string SourceDigest, Dictionary<string, string> Links, Dictionary<string, string>? Categories = null, List<ExecutionAccount>? Accounts = null);
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
    private static BackupSnapshot WithLinks(BackupSnapshot source, Dictionary<string, string> links, Dictionary<string, string> categories, IReadOnlyList<ExecutionAccount> accounts)
    {
        using var plist = new MemoryStream(); PropertyListParser.SaveAsBinary(new NSData(JsonSerializer.SerializeToUtf8Bytes(links)), plist);
        var settings = source.SettingsPlists.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        settings["transferPayrollAccountLinks"] = plist.ToArray();
        using var categoryPlist = new MemoryStream(); PropertyListParser.SaveAsBinary(new NSData(JsonSerializer.SerializeToUtf8Bytes(categories)), categoryPlist);
        settings["transferEntityCategories"] = categoryPlist.ToArray();
        return source with { SettingsPlists = settings, Accounts = accounts };
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
            var accounts = data.Accounts ?? source.Accounts.ToList();
            Validate(data.Links); Validate(categories); ValidateAccounts(source, accounts);
            return new(WithLinks(source, data.Links, categories, accounts), data.Links, Hash(bytes), categories);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or NullReferenceException or OverflowException)
        { throw new InvalidDataException("تعذّر قراءة روابط الحسابات المحلية؛ لم تُغيّر البيانات.", e); }
    }
    public TransferLinksSnapshot SaveLink(BackupSnapshot source, TransferLinksSnapshot expected, TransferEntry entry, ExecutionAccount account)
    {
        if (!expected.Snapshot.Accounts.Any(a => a == account) || expected.Snapshot.Accounts.Count(a => PayrollAnalysis.Normalize(a.Entity) == PayrollAnalysis.Normalize(account.Entity)) != 1)
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
        return Write(source, links, current.Categories.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), current.Snapshot.Accounts);
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
        return Write(source, current.Links.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), categories, current.Snapshot.Accounts);
    }
    private static void ValidateAccounts(BackupSnapshot source, IReadOnlyList<ExecutionAccount> accounts)
    {
        if (accounts.Any(a => a is null || a.Entity is null || a.Iban is null || a.Branch is null || a.Swift is null) || accounts.Count < source.Accounts.Count ||
            accounts.Count > 100_000 || accounts.Select(a => PayrollAnalysis.Normalize(a.Entity)).Distinct(StringComparer.Ordinal).Count() != accounts.Count ||
            !accounts.Take(source.Accounts.Count).Select(a => a.Entity).SequenceEqual(source.Accounts.Select(a => a.Entity)))
            throw new InvalidDataException("قائمة الحسابات المحلية غير صالحة؛ لم تُستبدل البيانات.");
    }
    private static ExecutionAccount NormalizeAndValidate(ExecutionAccount updated)
    {
        if (string.IsNullOrWhiteSpace(updated.Entity) || updated.Entity.Length > 1000 || updated.Branch.Length > 1000 || updated.Entity.Any(char.IsControl) || updated.Branch.Any(char.IsControl))
            throw new InvalidDataException("اسم الحساب أو المصرف غير صالح.");
        var iban = TransferPreparation.NormalizeAccount(updated.Iban);
        var local = updated.LocalAccount is null || updated.LocalAccount.Length == 0 ? null : TransferPreparation.NormalizeAccount(updated.LocalAccount);
        var normalized = updated with { Iban = iban, LocalAccount = local, Branch = updated.Branch.Trim(), Swift = updated.Swift.Trim().ToUpperInvariant() };
        var directLocal = local is not null && (PayrollAnalysis.Normalize(updated.Entity).Contains(PayrollAnalysis.Normalize("صندوق الإسكان"), StringComparison.Ordinal) || PayrollAnalysis.Normalize(updated.Entity).Contains(PayrollAnalysis.Normalize("الموارد المائية"), StringComparison.Ordinal)) && TransferPreparation.Classify(normalized) == TransferGroup.Anbar;
        if (!TransferPreparation.ValidIban(iban) && !(iban.Length == 0 && directLocal && TransferPreparation.LocalAccount(normalized) is not null)) throw new InvalidDataException("IBAN غير صالح، أو الحساب المحلي غير متاح لهذه الجهة.");
        if (normalized.Swift.Length > 0 && !TransferPreparation.ValidSwift(normalized.Swift)) throw new InvalidDataException("رمز SWIFT غير صالح.");
        if (local is not null && TransferPreparation.LocalAccount(normalized) is null) throw new InvalidDataException("الحساب المحلي لا يطابق IBAN أو ليس 15 رقماً.");
        return normalized;
    }
    public TransferLinksSnapshot SaveAccount(BackupSnapshot source, TransferLinksSnapshot expected, ExecutionAccount old, ExecutionAccount updated)
    {
        if (source.Version < 2) throw new InvalidDataException("إصدار النسخة لا يدعم حفظ الحسابات؛ استورد نسخة أحدث أولاً.");
        if (old.Entity != updated.Entity) throw new InvalidDataException("لا يمكن تغيير اسم الحساب هنا؛ أعد استيراد قائمة الحسابات لتغيير الأسماء.");
        var normalized = NormalizeAndValidate(updated);
        Directory.CreateDirectory(root);
        using var guard = new FileStream(Path.Combine(root, ".links.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var current = Load(source);
        if (current.Revision != expected.Revision || !current.Snapshot.Accounts.Contains(old)) throw new InvalidDataException("تغيّرت بيانات الحساب؛ أعد تحميل القائمة قبل الحفظ.");
        var accounts = current.Snapshot.Accounts.Select(a => a == old ? normalized : a).ToArray(); ValidateAccounts(source, accounts);
        return Write(source, current.Links.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), current.Categories.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), accounts);
    }
    public TransferLinksSnapshot ImportAccounts(BackupSnapshot source, TransferLinksSnapshot expected, AccountImportPreview preview)
    {
        if (source.Version < 2 || preview.Problems.Count > 0 || preview.Accounts.Count == 0) throw new InvalidDataException("راجع أخطاء ملف الحسابات قبل الحفظ، وتأكد من استخدام نسخة تدعم الحسابات.");
        var imported = preview.Accounts.Select(NormalizeAndValidate).ToArray();
        if (imported.Select(a => PayrollAnalysis.Normalize(a.Entity)).Distinct(StringComparer.Ordinal).Count() != imported.Length) throw new InvalidDataException("توجد حسابات مكررة في الملف.");
        Directory.CreateDirectory(root);
        using var guard = new FileStream(Path.Combine(root, ".links.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var current = Load(source);
        if (current.Revision != expected.Revision) throw new InvalidDataException("تغيّرت الحسابات في نافذة أخرى؛ أعد فتح الملف قبل الاستيراد.");
        var accounts = current.Snapshot.Accounts.ToList();
        var positions = accounts.Select((a, i) => (key: PayrollAnalysis.Normalize(a.Entity), index: i)).ToDictionary(x => x.key, x => x.index, StringComparer.Ordinal);
        foreach (var account in imported)
        {
            var key = PayrollAnalysis.Normalize(account.Entity);
            if (!positions.TryGetValue(key, out var index)) { positions[key] = accounts.Count; accounts.Add(account); continue; }
            var old = accounts[index];
            // يحتفظ الاسم القديم بروابط الجهات، والحقول الفارغة لا تمحو ما سبق حفظه.
            accounts[index] = NormalizeAndValidate(old with { Iban = account.Iban.Length == 0 ? old.Iban : account.Iban,
                Branch = account.Branch.Length == 0 ? old.Branch : account.Branch, Swift = account.Swift.Length == 0 ? old.Swift : account.Swift,
                LocalAccount = account.LocalAccount ?? old.LocalAccount });
        }
        ValidateAccounts(source, accounts);
        return Write(source, current.Links.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), current.Categories.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), accounts);
    }
    private TransferLinksSnapshot Write(BackupSnapshot source, Dictionary<string, string> links, Dictionary<string, string> categories, IReadOnlyList<ExecutionAccount> accounts)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new LinkData(1, source.SourceDigest, links, categories, accounts.ToList()));
        var temp = Path.Combine(root, ".links-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { output.Write(bytes); output.Flush(true); }
            using (var input = File.OpenRead(temp)) if (!SHA256.HashData(input).AsSpan().SequenceEqual(SHA256.HashData(bytes))) throw new InvalidDataException("تعذّر التحقق من روابط الحسابات.");
            File.Move(temp, FileFor(source), true);
            return new(WithLinks(source, links, categories, accounts), links, Hash(bytes), categories);
        }
        finally { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
