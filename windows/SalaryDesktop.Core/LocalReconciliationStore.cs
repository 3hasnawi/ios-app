using System.Security.Cryptography;
using System.Text.Json;

namespace SalaryDesktop.Core;

public sealed record ReconciliationSnapshot(IReadOnlyList<BankPeriod> Periods, string? Revision);

/// دفتر منفصل مرتبط ببصمة نسخة الآيفون؛ لا يغيّر أرشيف المصدر ولا يدمج نسختين بصمت.
public sealed class LocalReconciliationStore(string directory)
{
    private sealed record Ledger(int Version, string SourceDigest, List<BankPeriod> Periods);
    private readonly string root = Path.GetFullPath(directory);
    private static string ValidDigest(string digest)
    {
        if (digest.Length != 64 || digest.Any(c => c is not (>= '0' and <= '9' or >= 'A' and <= 'F'))) throw new InvalidDataException("بصمة النسخة غير صالحة.");
        return digest;
    }
    private string FileFor(string digest) => Path.Combine(root, ValidDigest(digest) + ".json");
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    private static void Validate(IReadOnlyList<BankPeriod> periods)
    {
        if (periods.Count > 5000 || periods.Select(p => p.Id).Distinct().Count() != periods.Count) throw new InvalidDataException("فترات المطابقة مكررة أو كثيرة جداً.");
        foreach (var period in periods) _ = BankReconciliation.Calculate(period);
    }
    private ReconciliationSnapshot ReadFile(string path, string digest)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var ledger = JsonSerializer.Deserialize<Ledger>(bytes) ?? throw new JsonException();
            if (ledger.Version != 1 || ledger.SourceDigest != digest || ledger.Periods is null) throw new InvalidDataException("دفتر المطابقة المحلي غير صالح؛ لم تُغيّر البيانات.");
            Validate(ledger.Periods);
            return new(ledger.Periods, Hash(bytes));
        }
        catch (Exception e) when (e is JsonException or ArgumentException or NullReferenceException or OverflowException)
        { throw new InvalidDataException("تعذّر قراءة دفتر المطابقة المحلي؛ لم تُغيّر البيانات.", e); }
    }
    public ReconciliationSnapshot Load(BackupSnapshot source)
    {
        var path = FileFor(source.SourceDigest);
        if (File.Exists(path)) return ReadFile(path, source.SourceDigest);
        var periods = BankReconciliation.ReadFromBackup(source);
        Validate(periods);
        return new(periods, null);
    }
    public ReconciliationSnapshot Save(BackupSnapshot source, ReconciliationSnapshot expected, IReadOnlyList<BankPeriod> periods)
    {
        Validate(periods);
        Directory.CreateDirectory(root);
        var path = FileFor(source.SourceDigest);
        using var guard = new FileStream(Path.Combine(root, ".ledger.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var actual = File.Exists(path) ? ReadFile(path, source.SourceDigest).Revision : null;
        if (actual != expected.Revision) throw new InvalidDataException("تغيّر دفتر المطابقة في نافذة أخرى؛ أعد فتحه قبل الحفظ.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Ledger(1, source.SourceDigest, periods.ToList()));
        var temp = Path.Combine(root, ".ledger-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { output.Write(bytes); output.Flush(true); }
            var verified = ReadFile(temp, source.SourceDigest);
            if (verified.Revision != Hash(bytes)) throw new InvalidDataException("تعذّر التحقق من دفتر المطابقة.");
            File.Move(temp, path, true);
            return verified;
        }
        finally { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
