using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SalaryLocal.Core;

public enum BackupRelationship { Same, Newer, Older, Diverged, OtherDataset, NoCurrentData }
public sealed record BackupManifest(int FormatVersion, string Application, string AppVersion, DateTimeOffset CreatedAt,
    string DatasetId, string RevisionId, long DatabaseBytes, string Sha256, long RevisionCount, long EmployeeRowCount, long HeaderSchemaCount);
public sealed record BackupInspection(BackupManifest Manifest, StoreStatus Status, BackupRelationship Relationship,
    string RelationshipMessage, string Digest);

/// نسخة مستقلة من قاعدة SQLite، لا تضم النسخ السابقة أو ملفات Excel الأصلية.
public static class BackupService
{
    public const string Extension = ".salarybackup";
    private const long MaximumDatabaseBytes = 16L * 1024 * 1024 * 1024;
    private const string Application = "SalaryLocal";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    /// لا يستبدل ملفاً موجوداً؛ يحفظ المستخدم نسخة جديدة ذات اسم مستقل.
    public static BackupInspection Create(LocalPayrollStore store, string destination, CancellationToken cancellation = default)
    {
        destination = Path.GetFullPath(destination);
        if (!string.Equals(Path.GetExtension(destination), Extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("اختر امتداد .salarybackup للنسخة المحلية الجديدة.");
        if (File.Exists(destination)) throw new IOException("يوجد ملف بهذا الاسم؛ اختر اسماً جديداً للحفاظ على النسخة السابقة.");
        var parent = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("مجلد حفظ النسخة غير موجود.");
        var staging = NewTempDirectory();
        var temp = Path.Combine(parent, ".salary-backup-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var database = Path.Combine(staging, "payroll.db");
            store.CreateDatabaseSnapshot(database);
            var status = LocalPayrollStore.ValidateDatabase(database);
            if (status.DatabaseBytes > MaximumDatabaseBytes) throw new InvalidDataException("قاعدة البيانات أكبر من الحد المدعوم للنسخة (16 غيغابايت).");
            using (var frozen = new LocalPayrollStore(staging))
                if (!frozen.HasData()) throw new InvalidDataException("لا تُنشأ نسخة خالية من البيانات.");
            var hash = Hash(database, cancellation);
            var manifest = new BackupManifest(1, Application, "0.5.0", DateTimeOffset.UtcNow, status.DatasetId,
                status.RevisionId, new FileInfo(database).Length, hash, status.RevisionCount, status.EmployeeRowCount, status.HeaderSchemaCount);
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 131072, FileOptions.WriteThrough))
            {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    using (var entry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal).Open()) JsonSerializer.Serialize(entry, manifest, Json);
                    using var source = File.OpenRead(database);
                    using var target = zip.CreateEntry("payroll.db", CompressionLevel.Optimal).Open();
                    Copy(source, target, MaximumDatabaseBytes, cancellation);
                }
                output.Flush(true);
            }
            // نقرأ الملف المكتوب نفسه قبل تسليمه، وليس حالة قاعدة المصدر المتغيرة.
            var checkedBackup = Inspect(temp, store, cancellation);
            cancellation.ThrowIfCancellationRequested();
            File.Move(temp, destination, overwrite: false);
            return checkedBackup;
        }
        finally { DeleteFile(temp); DeleteDirectory(staging); }
    }

    public static BackupInspection Inspect(string path, LocalPayrollStore? current = null, CancellationToken cancellation = default)
    {
        using var verified = ExtractVerified(path, null, cancellation);
        return Describe(verified, current);
    }

    /// الاستعادة تنشئ مساحة عمل جديدة، حتى للنسخ الأقدم أو المتعارضة. الحالية لا تُمسّ.
    public static string RestoreAsNewWorkspace(string path, string workspacesDirectory, string expectedDigest, CancellationToken cancellation = default)
    {
        if (!IsHash(expectedDigest)) throw new InvalidDataException("راجع النسخة وحددها من جديد قبل الاستعادة.");
        var root = Path.GetFullPath(workspacesDirectory);
        Directory.CreateDirectory(root);
        using var verified = ExtractVerified(path, root, cancellation, expectedDigest);
        cancellation.ThrowIfCancellationRequested();
        // نضغط الصفوف في المساحة الجديدة فقط؛ المصدر والنسخة الأصلية لا يتغيران.
        LocalPayrollStore.PrepareRestoredDatabase(Path.Combine(verified.Directory, "payroll.db"));
        cancellation.ThrowIfCancellationRequested();
        var destination = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.Move(verified.Directory, destination);
        verified.Preserve = true;
        return destination;
    }

    private static BackupInspection Describe(VerifiedBackup backup, LocalPayrollStore? current)
    {
        var relation = BackupRelationship.NoCurrentData;
        if (current is not null && current.HasData())
        {
            var local = current.GetStatus();
            if (local.DatasetId != backup.Status.DatasetId) relation = BackupRelationship.OtherDataset;
            else if (local.RevisionId == backup.Status.RevisionId) relation = BackupRelationship.Same;
            else if (backup.History.Any(h => h.RevisionId == local.RevisionId || h.ParentRevisionId == local.RevisionId)) relation = BackupRelationship.Newer;
            else if (current.GetHistory().Any(h => h.RevisionId == backup.Status.RevisionId || h.ParentRevisionId == backup.Status.RevisionId)) relation = BackupRelationship.Older;
            else relation = BackupRelationship.Diverged;
        }
        var message = relation switch
        {
            BackupRelationship.Same => "النسخة مطابقة للإصدار الحالي. ستُفتح في مساحة مستقلة دون حذف الحالية.",
            BackupRelationship.Newer => "النسخة امتداد لبيانات هذا الجهاز وتضم تعديلات أحدث في السجل.",
            BackupRelationship.Older => "تنبيه: النسخة أقدم من بيانات هذا الجهاز. ستبقى التعديلات الحالية محفوظة في مساحة العمل السابقة.",
            BackupRelationship.Diverged => "تعارض: عُدّلت البيانات في الجهازين بعد نسخة مشتركة. لا دمج تلقائياً؛ ستُفتح هذه النسخة منفصلة وتبقى الحالية محفوظة.",
            BackupRelationship.OtherDataset => "هذه النسخة لقاعدة بيانات مختلفة. ستُفتح في مساحة مستقلة دون استبدال الحالية.",
            _ => "النسخة سليمة وجاهزة للفتح في مساحة عمل جديدة."
        };
        return new(backup.Manifest, backup.Status, relation, message, backup.Digest);
    }

    private static VerifiedBackup ExtractVerified(string path, string? stagingParent, CancellationToken cancellation, string? expectedDigest = null)
    {
        var directory = NewTempDirectory(stagingParent);
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.SequentialScan);
            if (file.Length == 0 || file.Length > MaximumDatabaseBytes + 1024 * 1024) throw new InvalidDataException("حجم النسخة غير صالح.");
            var digest = Hash(file, cancellation);
            if (expectedDigest is not null && !string.Equals(digest, expectedDigest, StringComparison.Ordinal))
                throw new InvalidDataException("تغيّر ملف النسخة منذ مراجعته؛ لم تتغير البيانات الحالية.");
            file.Position = 0;
            BackupManifest manifest;
            using (var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true))
            {
                // لا استخراج لأسماء أو مسارات مصدرها الملف: اسمان ثابتان فقط.
                if (zip.Entries.Count != 2 || zip.Entries.Count(e => e.FullName == "manifest.json") != 1 || zip.Entries.Count(e => e.FullName == "payroll.db") != 1)
                    throw new InvalidDataException("الملف ليس نسخة محلية مدعومة أو يحتوي ملفات إضافية/مكررة. نسخ الآيفون لا تُفتح هنا.");
                var index = zip.GetEntry("manifest.json")!;
                if (index.Length is <= 0 or > 65536) throw new InvalidDataException("فهرس النسخة غير صالح.");
                using (var input = index.Open())
                using (var memory = new MemoryStream())
                {
                    Copy(input, memory, 65536, cancellation);
                    manifest = JsonSerializer.Deserialize<BackupManifest>(memory.ToArray(), Json) ?? throw new InvalidDataException("فهرس النسخة غير صالح.");
                }
                if (manifest.FormatVersion != 1 || manifest.Application != Application || !IsHash(manifest.Sha256) ||
                    string.IsNullOrWhiteSpace(manifest.DatasetId) || string.IsNullOrWhiteSpace(manifest.RevisionId) ||
                    manifest.DatabaseBytes is <= 0 or > MaximumDatabaseBytes || manifest.RevisionCount < 0 || manifest.EmployeeRowCount < 0 || manifest.HeaderSchemaCount < 0)
                    throw new InvalidDataException("صيغة النسخة أو بيانات فهرسها غير مدعومة.");
                var entry = zip.GetEntry("payroll.db")!;
                if (entry.Length != manifest.DatabaseBytes) throw new InvalidDataException("حجم قاعدة البيانات لا يطابق الفهرس.");
                CheckSpace(directory, entry.Length);
                var database = Path.Combine(directory, "payroll.db");
                using (var input = entry.Open())
                using (var output = new FileStream(database, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.WriteThrough))
                { Copy(input, output, manifest.DatabaseBytes, cancellation); output.Flush(true); }
                if (new FileInfo(database).Length != manifest.DatabaseBytes || Hash(database, cancellation) != manifest.Sha256)
                    throw new InvalidDataException("فشل فحص سلامة النسخة؛ لم تتغير البيانات الحالية.");
            }
            var status = LocalPayrollStore.ValidateDatabase(Path.Combine(directory, "payroll.db"));
            if (status.DatasetId != manifest.DatasetId || status.RevisionId != manifest.RevisionId || status.RevisionCount != manifest.RevisionCount ||
                status.EmployeeRowCount != manifest.EmployeeRowCount || status.HeaderSchemaCount != manifest.HeaderSchemaCount)
                throw new InvalidDataException("محتوى قاعدة البيانات لا يطابق فهرس النسخة.");
            IReadOnlyList<HistoryEntry> history;
            using (var snapshot = new LocalPayrollStore(directory))
            {
                if (!snapshot.HasData()) throw new InvalidDataException("لا تحتوي النسخة على بيانات محفوظة.");
                history = snapshot.GetHistory();
            }
            file.Position = 0;
            if (Hash(file, cancellation) != digest) throw new InvalidDataException("تغيّر ملف النسخة أثناء فحصه.");
            return new(directory, manifest, status, history, digest);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or OverflowException)
        { DeleteDirectory(directory); throw new InvalidDataException("النسخة غير صالحة؛ لم تتغير بياناتك الحالية.", ex); }
        catch { DeleteDirectory(directory); throw; }
    }

    private sealed class VerifiedBackup(string directory, BackupManifest manifest, StoreStatus status, IReadOnlyList<HistoryEntry> history, string digest) : IDisposable
    {
        public string Directory { get; } = directory;
        public BackupManifest Manifest { get; } = manifest;
        public StoreStatus Status { get; } = status;
        public IReadOnlyList<HistoryEntry> History { get; } = history;
        public string Digest { get; } = digest;
        public bool Preserve { get; set; }
        public void Dispose() { if (!Preserve) DeleteDirectory(Directory); }
    }
    private static string NewTempDirectory(string? parent = null)
    {
        var path = Path.Combine(parent ?? Path.GetTempPath(), ".salary-local-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    private static string Hash(string path, CancellationToken cancellation) { using var input = File.OpenRead(path); return Hash(input, cancellation); }
    private static string Hash(Stream input, CancellationToken cancellation)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[131072]; int read;
        while ((read = input.Read(buffer)) > 0) { cancellation.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static void Copy(Stream input, Stream output, long maximum, CancellationToken cancellation)
    {
        var buffer = new byte[131072]; long total = 0; int read;
        while ((read = input.Read(buffer)) > 0)
        {
            cancellation.ThrowIfCancellationRequested(); total = checked(total + read);
            if (total > maximum) throw new InvalidDataException("تجاوز الملف الحجم المسموح؛ أُوقفت العملية بأمان.");
            output.Write(buffer, 0, read);
        }
    }
    private static void CheckSpace(string directory, long bytes)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!);
        if (drive.IsReady && drive.AvailableFreeSpace < checked(bytes * 2 + 32L * 1024 * 1024))
            throw new IOException("المساحة الحرة لا تكفي لفحص النسخة واستعادتها؛ لم تتغير البيانات الحالية.");
    }
    private static void DeleteFile(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private static void DeleteDirectory(string path) { try { Directory.Delete(path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
