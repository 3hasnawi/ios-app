namespace SalaryLocal.Core;

public sealed record LegacySourceFile(string RelativePath, long Bytes, string Kind, string FullPath, bool IsZip);
public sealed record LegacySourceReview(string Source, IReadOnlyList<LegacySourceFile> Files);

/// جرد قراءة فقط. لا نستنتج الحالة الحالية من ملفات مخازن التطبيق القديم.
public static class LocalLegacyMigration
{
    public const string ScopeWarning = "الترحيل يشمل ZIP الذي تختاره فقط، وليس كل حالة البرنامج القديم. ملفات التعديلات المحلية وExcel والإعدادات في المجلد لا تُدمج. افتح البرنامج القديم واختر «حفظ نسخة في مكان آخر…»، ثم راجع النسخة المصدرة هنا. لا تختَر ZIP محفوظاً داخلياً على أنه نسخة شاملة حديثة.";
    public static LegacySourceReview InspectSource(string path, CancellationToken cancellation = default)
    {
        var root = Path.GetFullPath(path); LocalPathSafety.RejectReparsePoints(root);
        if (File.Exists(root))
        {
            if (!string.Equals(Path.GetExtension(root), ".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("اختر ZIP مصدراً أو مجلد نسخة/تصدير.");
            return new(root, new[] { Describe(root, Path.GetFileName(root)) });
        }
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("مجلد المصدر غير موجود.");
        var files = new List<LegacySourceFile>(); var pending = new Stack<(string Path, int Depth)>(); pending.Push((root, 0)); int entries = 0;
        while (pending.TryPop(out var item))
        {
            cancellation.ThrowIfCancellationRequested();
            foreach (var entry in Directory.EnumerateFileSystemEntries(item.Path))
            {
                cancellation.ThrowIfCancellationRequested(); if (++entries > 10000 || item.Depth > 24) throw new InvalidDataException("المجلد أكبر من حد الجرد؛ اختر مجلد تصدير أصغر.");
                LocalPathSafety.RejectReparsePoints(entry);
                if (Directory.Exists(entry)) pending.Push((entry, item.Depth + 1));
                else files.Add(Describe(entry, Path.GetRelativePath(root, entry)));
            }
        }
        return new(root, files.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray());
    }
    private static LegacySourceFile Describe(string path, string relative)
    {
        var zip = string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);
        return new(relative, new FileInfo(path).Length, zip ? "ZIP مرشح للفحص، لا يُختار تلقائياً" : "لن يُستورد: ملف محلي أو مصدر منفصل", path, zip);
    }
    public static IphoneInspection InspectZip(string path, CancellationToken cancellation = default)
    { _ = InspectSource(path, cancellation); return IphoneInteropService.Inspect(path, cancellation); }
    public static string ImportNewWorkspace(string path, string destination, string expectedDigest, CancellationToken cancellation = default)
    {
        _ = InspectSource(path, cancellation);
        var sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var target = Path.GetFullPath(destination);
        LocalPathSafety.RejectReparsePoints(target);
        if (target.Equals(sourceDirectory, StringComparison.OrdinalIgnoreCase) || target.StartsWith(sourceDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("مساحة العمل الجديدة يجب أن تكون خارج مجلد المصدر.");
        return IphoneInteropService.ImportNewWorkspace(path, destination, expectedDigest, cancellation);
    }
}
