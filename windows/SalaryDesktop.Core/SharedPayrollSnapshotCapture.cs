using System.Security.Cryptography;

namespace SalaryDesktop.Core;

/// تجريبي وغير موصول بالواجهة: يجمع مراجعات مخازن ويندوز ويتحقق منها قبل تسليم مرشح مستقل.
/// يجب التحقق مجدداً بعد الرفع وقبل اعتبار التعديلات محفوظة؛ لم يُختبر استرجاع المرشح على الجهاز.
public sealed class SharedPayrollSnapshotCapture(
    LocalArchiveStore archiveStore, LocalPayrollImports payrollStore, LocalTransferLinks linkStore,
    LocalReconciliationStore reconciliationStore, LocalSalaryTrustStore trustStore)
{
    public sealed record Token(string SourceDigest, string? Archive, string? Payroll, string? Links,
        string? Reconciliation, string? Trust, IReadOnlyDictionary<Guid, string> LocalBookHashes);

    private static string FileHash(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(input));
    }
    private static BackupSnapshot ApplyArchive(BackupSnapshot source, ArchiveState archive) => archive.Revision is null ? source :
        source with { Books = archive.Books, LocalBookFiles = archive.Files };
    private static IReadOnlyDictionary<Guid, string> BookHashes(ArchiveState archive) =>
        archive.Files.ToDictionary(p => p.Key, p => FileHash(p.Value));

    /// تحقق ثانٍ مطلوب أيضاً بعد رفع الملف وقبل إقرار النسخ؛ أي اختلاف يبقي التعديلات معلقة.
    public void VerifyCurrent(BackupSnapshot source, Token token)
    {
        if (source.SourceDigest != token.SourceDigest || FileHash(source.SourcePath) != token.SourceDigest)
            throw new InvalidDataException("تغيّرت النسخة الأصلية أثناء إعداد المرشح.");
        var archive = archiveStore.Load(source);
        if (archive.Revision != token.Archive) throw new InvalidDataException("تغيّر الأرشيف أثناء إعداد المرشح.");
        var currentFiles = BookHashes(archive);
        if (currentFiles.Count != token.LocalBookHashes.Count || currentFiles.Any(p => !token.LocalBookHashes.TryGetValue(p.Key, out var hash) || hash != p.Value))
            throw new InvalidDataException("تغيّر ملف كتاب أثناء إعداد المرشح.");
        var archived = ApplyArchive(source, archive);
        payrollStore.VerifyRevision(archived, token.Payroll);
        if (linkStore.Load(archived).Revision != token.Links) throw new InvalidDataException("تغيّرت الحسابات أو إعداداتها أثناء إعداد المرشح.");
        if (reconciliationStore.Load(archived).Revision != token.Reconciliation) throw new InvalidDataException("تغيّر دفتر المطابقة أثناء إعداد المرشح.");
        if (trustStore.Load(archived).Revision != token.Trust) throw new InvalidDataException("تغيّر دفتر الأمانات أثناء إعداد المرشح.");
    }

    public Token Create(BackupSnapshot source, string destination)
    {
        var target = Path.GetFullPath(destination);
        if (File.Exists(target) || string.Equals(target, Path.GetFullPath(source.SourcePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("اختر ملفاً جديداً لحماية النسخة الأصلية.");
        var directory = Path.GetDirectoryName(target) ?? throw new InvalidDataException("مجلد غير صالح.");
        var temp = Path.Combine(directory, ".salary-capture-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var archive = archiveStore.Load(source); var archived = ApplyArchive(source, archive);
            var payroll = payrollStore.Load(archived); var links = linkStore.Load(payroll.Snapshot);
            var reconciliation = reconciliationStore.Load(links.Snapshot); var trust = trustStore.Load(links.Snapshot);
            var token = new Token(source.SourceDigest, archive.Revision, payroll.Revision, links.Revision,
                reconciliation.Revision, trust.Revision, BookHashes(archive));
            VerifyCurrent(source, token);
            SharedPayrollOverlay.CreateIncludingSettings(source, payroll, links.Snapshot, reconciliation.Periods, trust.Ledger, temp);
            VerifyCurrent(source, token);
            File.Move(temp, target, false);
            return token;
        }
        finally { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
