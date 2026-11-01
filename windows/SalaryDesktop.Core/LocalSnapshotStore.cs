using System.Security.Cryptography;
using System.Text.Json;

namespace SalaryDesktop.Core;

/// يحتفظ بلقطات المصدر الأصلية؛ لا يستبدل النسخة القديمة عند فشل الاستيراد.
public sealed class LocalSnapshotStore(string directory)
{
    private sealed record Pointer(int Version, string Digest);
    private readonly string root = Path.GetFullPath(directory);
    private string PointerPath => Path.Combine(root, "current.json");
    private static bool ValidDigest(string digest) => digest.Length == 64 && digest.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    private string FileFor(string digest)
    {
        if (!ValidDigest(digest)) throw new InvalidDataException("مؤشر النسخة المحلية غير صالح.");
        return Path.Combine(root, digest + ".zip");
    }
    /// يصدر نسخة مطابقة للمصدر المفتوح دون إعادة كتابة أرشيف الآيفون أو إسقاط محتواه.
    public static void ExportOriginal(BackupSnapshot source, string destination)
    {
        var target = Path.GetFullPath(destination);
        var protectedDirectory = Path.GetDirectoryName(source.SourcePath);
        var targetName = Path.GetFileNameWithoutExtension(target);
        if (!string.Equals(Path.GetExtension(target), ".zip", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target, source.SourcePath, StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(Path.GetDirectoryName(target), protectedDirectory, StringComparison.OrdinalIgnoreCase) && ValidDigest(targetName)))
            throw new InvalidDataException("وجهة النسخة غير صالحة.");
        var directory = Path.GetDirectoryName(target) ?? throw new InvalidDataException("مجلد النسخة غير صالح.");
        using var input = new FileStream(source.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexString(SHA256.HashData(input)) != source.SourceDigest) throw new InvalidDataException("تغيّرت النسخة المحلية؛ لم يُحفظ الملف.");
        input.Position = 0;
        var temporary = Path.Combine(directory, ".salary-backup-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool created = false;
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
            { created = true; input.CopyTo(output); output.Flush(true); }
            using (var copied = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read))
                if (Convert.ToHexString(SHA256.HashData(copied)) != source.SourceDigest) throw new InvalidDataException("تعذّر التحقق من النسخة المكتوبة.");
            input.Position = 0;
            if (Convert.ToHexString(SHA256.HashData(input)) != source.SourceDigest) throw new InvalidDataException("تغيّر مصدر النسخة أثناء الحفظ.");
            File.Move(temporary, target, true); created = false;
        }
        finally { if (created) { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }
    }
    public sealed record LocalCopy(string Digest, long Size, DateTime Modified, bool Current);
    public sealed record PruneResult(int Deleted, long Freed, int Protected);

    private string? CurrentDigest()
    {
        try { return File.Exists(PointerPath) && JsonSerializer.Deserialize<Pointer>(File.ReadAllBytes(PointerPath)) is { Version: 1, Digest: { } digest } && ValidDigest(digest) ? digest : null; }
        catch (JsonException) { return null; }
    }

    /// النسخ المحلية المحفوظة (كل نسخة آيفون فُتحت تبقى ملفاً مستقلاً)، الأحدث أولاً.
    public IReadOnlyList<LocalCopy> Copies()
    {
        if (!Directory.Exists(root)) return [];
        var current = CurrentDigest();
        return Directory.GetFiles(root, "*.zip").Select(path => new FileInfo(path))
            .Where(file => ValidDigest(Path.GetFileNameWithoutExtension(file.Name)))
            .Select(file => new LocalCopy(Path.GetFileNameWithoutExtension(file.Name), file.Length, file.LastWriteTimeUtc, Path.GetFileNameWithoutExtension(file.Name) == current))
            .OrderByDescending(copy => copy.Current).ThenByDescending(copy => copy.Modified).ToArray();
    }

    /// يحذف النسخ الأقدم ويبقي آخر keep نسخ (منها المفتوحة دائماً). النسخة المرتبطة بتعديلات محلية (isProtected) لا تُحذف أبداً.
    public PruneResult Prune(int keep, Func<string, bool> isProtected)
    {
        if (keep < 1) throw new InvalidDataException("يجب إبقاء نسخة واحدة على الأقل.");
        if (!Directory.Exists(root)) return new(0, 0, 0);
        using var storeLock = new FileStream(Path.Combine(root, ".store.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        int deleted = 0, kept = 0; long freed = 0;
        foreach (var copy in Copies().Skip(keep))
        {
            if (copy.Current || isProtected(copy.Digest)) { kept++; continue; }
            try { File.Delete(FileFor(copy.Digest)); deleted++; freed += copy.Size; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { kept++; }
        }
        return new(deleted, freed, kept);
    }

    public BackupSnapshot? LoadCurrent()
    {
        if (!File.Exists(PointerPath)) return null;
        Pointer pointer;
        try { pointer = JsonSerializer.Deserialize<Pointer>(File.ReadAllBytes(PointerPath)) ?? throw new JsonException(); }
        catch (JsonException ex) { throw new InvalidDataException("تعذّر قراءة مؤشر النسخة المحلية؛ لم تُستبدل البيانات.", ex); }
        if (pointer.Version != 1 || pointer.Digest is null || !ValidDigest(pointer.Digest)) throw new InvalidDataException("مؤشر النسخة المحلية غير صالح.");
        var snapshot = BackupReader.Read(FileFor(pointer.Digest));
        if (snapshot.SourceDigest != pointer.Digest) throw new InvalidDataException("تغيّر ملف النسخة المحلية؛ لم تُستبدل البيانات.");
        _ = BankReconciliation.ReadFromBackup(snapshot);
        _ = SalaryTrust.ReadFromBackup(snapshot);
        return snapshot;
    }
    public BackupSnapshot Import(string path, CancellationToken cancellation = default, string? expectedDigest = null, bool requireEmptyStore = false)
    {
        var original = BackupReader.Read(path, cancellation);
        if (expectedDigest is not null && original.SourceDigest != expectedDigest) throw new InvalidDataException("تغيّرت النسخة بعد مراجعتها؛ لم تُستبدل البيانات.");
        Directory.CreateDirectory(root);
        using var storeLock = new FileStream(Path.Combine(root, ".store.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var previous = LoadCurrent();
        if (requireEmptyStore && previous is not null) throw new InvalidDataException("توجد بيانات محلية بالفعل؛ لم تُنشأ قاعدة جديدة ولم تُستبدل البيانات.");
        if (previous is not null)
        {
            var months = original.Months.Select(month => month.Name).ToHashSet(StringComparer.Ordinal);
            bool lostMonth = previous.Months.Any(month => !months.Contains(month.Name));
            bool emptiedCategory = previous.Employees.Count > 0 && original.Employees.Count == 0 ||
                previous.Deductions.Count > 0 && original.Deductions.Count == 0 || previous.StaffCount > 0 && original.StaffCount == 0 ||
                previous.Accounts.Count > 0 && original.Accounts.Count == 0 || previous.Books.Count > 0 && original.Books.Count == 0;
            if (lostMonth || emptiedCategory) throw new InvalidDataException("النسخة الجديدة تفتقد أشهرًا أو فئة بيانات موجودة محلياً؛ لم تُستبدل النسخة السابقة.");
        }
        var target = FileFor(original.SourceDigest);
        var temporary = Path.Combine(root, ".import-" + Guid.NewGuid().ToString("N") + ".tmp");
        var pointerTemp = Path.Combine(root, ".pointer-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool tempCreated = false, pointerCreated = false;
        try
        {
            if (!File.Exists(target))
            {
                using (var input = new FileStream(original.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
                {
                    tempCreated = true;
                    var buffer = new byte[65536];
                    int count;
                    while ((count = input.Read(buffer)) > 0)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, count);
                    }
                    output.Flush(true);
                }
                var verified = BackupReader.Read(temporary, cancellation);
                if (verified.SourceDigest != original.SourceDigest) throw new InvalidDataException("تغيّر ملف المصدر أثناء الاستيراد؛ لم تُستبدل البيانات.");
                File.Move(temporary, target, false); tempCreated = false;
            }
            var stored = BackupReader.Read(target, cancellation);
            if (stored.SourceDigest != original.SourceDigest) throw new InvalidDataException("تعذّر التحقق من النسخة المحلية؛ لم تُستبدل البيانات.");
            _ = BankReconciliation.ReadFromBackup(stored);
            _ = SalaryTrust.ReadFromBackup(stored);
            cancellation.ThrowIfCancellationRequested();
            var pointer = JsonSerializer.SerializeToUtf8Bytes(new Pointer(1, stored.SourceDigest));
            using (var output = new FileStream(pointerTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                pointerCreated = true; output.Write(pointer); output.Flush(true);
            }
            // المؤشر يتغير بعد التحقق بالكامل؛ اللقطات السابقة لا تُحذف.
            File.Move(pointerTemp, PointerPath, true); pointerCreated = false;
            return stored;
        }
        finally
        {
            if (tempCreated) { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            if (pointerCreated) { try { File.Delete(pointerTemp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }
}
