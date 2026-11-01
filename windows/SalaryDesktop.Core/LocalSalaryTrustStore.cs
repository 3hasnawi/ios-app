using System.Security.Cryptography;
using System.Text.Json;

namespace SalaryDesktop.Core;

public sealed record SalaryTrustSnapshot(SalaryTrustLedger Ledger, string? Revision);

/// تعديلات الجهاز مرتبطة ببصمة النسخة المفتوحة، وتُحفظ بعد المقارنة والتحقق.
public sealed class LocalSalaryTrustStore(string directory)
{
    private sealed record Envelope(int Version, string SourceDigest, SalaryTrustLedger Ledger);
    private readonly string root = Path.GetFullPath(directory);
    private static string Digest(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F') ? value : throw new InvalidDataException("بصمة النسخة غير صالحة.");
    private string FileFor(string digest) => Path.Combine(root, Digest(digest) + ".json");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static SalaryTrustSnapshot Read(string path, string digest)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var envelope = JsonSerializer.Deserialize<Envelope>(bytes) ?? throw new JsonException();
            if (envelope.Version != 1 || envelope.SourceDigest != digest || envelope.Ledger is null) throw new InvalidDataException("دفتر الأمانات المحلي غير صالح.");
            SalaryTrust.Validate(envelope.Ledger);
            return new(envelope.Ledger, Hash(bytes));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NullReferenceException or OverflowException)
        { throw new InvalidDataException("تعذّر قراءة دفتر الأمانات؛ لم تتغير البيانات.", ex); }
    }
    public SalaryTrustSnapshot Load(BackupSnapshot source)
    {
        var path = FileFor(source.SourceDigest);
        return File.Exists(path) ? Read(path, source.SourceDigest) : new(SalaryTrust.ReadFromBackup(source), null);
    }
    public SalaryTrustSnapshot Save(BackupSnapshot source, SalaryTrustSnapshot expected, SalaryTrustLedger ledger)
    {
        SalaryTrust.Validate(ledger); Directory.CreateDirectory(root);
        using var guard = new FileStream(Path.Combine(root, ".trust.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var path = FileFor(source.SourceDigest);
        var actual = File.Exists(path) ? Read(path, source.SourceDigest).Revision : null;
        if (actual != expected.Revision) throw new InvalidDataException("تغيّر دفتر الأمانات في نافذة أخرى؛ أعد فتحه.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, source.SourceDigest, ledger));
        var temporary = Path.Combine(root, ".trust-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { output.Write(bytes); output.Flush(true); }
            var verified = Read(temporary, source.SourceDigest);
            if (verified.Revision != Hash(bytes)) throw new InvalidDataException("تعذّر التحقق من دفتر الأمانات.");
            File.Move(temporary, path, true); return verified;
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    public SalaryTrustSnapshot AddCash(BackupSnapshot source, string month)
    {
        int period = MonthNames.SortKey(month);
        var current = Load(source);
        if (period <= SalaryTrust.OpeningCutoff || !current.Ledger.OpeningImported) return current;
        var cash = CashSalaryListPreparation.Prepare(source, month);
        var existing = current.Ledger.Entries.ToDictionary(e => $"{e.Year}-{e.Month}|{PersonProfiles.Key(e.Name)}", StringComparer.Ordinal);
        var batch = new HashSet<string>(StringComparer.Ordinal); var additions = new List<SalaryTrustEntry>();
        foreach (var person in cash.Rows)
        {
            if (person.CaseStatus is null) throw new InvalidDataException("عمود الملاحظات بعد صافي الراتب غير متاح؛ أعد استيراد رواتب الشهر وراجعها قبل إضافة الأمانات.");
            var key = $"{period / 100}-{period % 100}|{PersonProfiles.Key(person.Name)}";
            if (!batch.Add(key)) throw new InvalidDataException("توجد أسماء كاش مكررة في الشهر؛ راجعها قبل إضافتها للأمانات.");
            if (existing.TryGetValue(key, out var entry))
            {
                if (entry.Amount != person.Net || entry.CaseStatus != person.CaseStatus) throw new InvalidDataException("تغيّر مبلغ أمانة مسجلة أو حالتها؛ راجعها قبل التحديث.");
                continue;
            }
            additions.Add(new(Guid.NewGuid().ToString(), person.Name, person.Rank, person.Workplace, period / 100, period % 100,
                person.Net, person.CaseStatus, "", "", false, false, []));
        }
        if (current.Ledger.Entries.Any(e => e.Year == period / 100 && e.Month == period % 100 && !batch.Contains($"{e.Year}-{e.Month}|{PersonProfiles.Key(e.Name)}")))
            throw new InvalidDataException("تغيّرت قائمة كاش الشهر وفيها قيود محفوظة لم تعد موجودة؛ راجعها قبل التحديث.");
        return additions.Count == 0 ? current : Save(source, current, current.Ledger with { Entries = current.Ledger.Entries.Concat(additions).ToArray() });
    }
}
