namespace SalaryLocal.Core;

/// يمنع المرور عبر وصلة موجودة قبل فتح ملفات البيانات أو إنشائها.
/// لا يحاول تجاوز صلاحيات النظام، ولا يمثل قفلاً ضد تبديل المسار بواسطة عملية خارجية.
public static class LocalPathSafety
{
    public static void RejectReparsePoints(string path)
    {
        var parts = new Stack<string>();
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current)) parts.Push(current);
        while (parts.TryPop(out var component))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(component); }
            // إنشاء مسار جديد مسموح، مع استمرار فحص الآباء الموجودين من الجذر أولاً.
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("رُفض مسار يحتوي وصلة رمزية أو نقطة إعادة توجيه؛ لم تُفتح البيانات عبره.");
        }
    }

    public static void CheckDatabasePath(string workspace)
    {
        RejectReparsePoints(workspace);
        foreach (var name in new[] { "payroll.db", "payroll.db-wal", "payroll.db-shm", "payroll.db-journal" })
            RejectReparsePoints(Path.Combine(workspace, name));
    }
}
