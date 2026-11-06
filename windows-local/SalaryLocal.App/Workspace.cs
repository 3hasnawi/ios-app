using System.IO;
using SalaryLocal.Core;
namespace SalaryLocal.App;
// لا يُقرأ أي مسار أو إعداد للتطبيق القديم.
internal sealed class Workspace : IDisposable
{
    public string Root { get; }
    public Workspace(string? root = null) => Root = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SalaryLocal"));
    public string Workspaces => Path.Combine(Root, "workspaces");
    public string DirectoryPath { get; private set; } = "";
    public LocalPayrollStore Store { get; private set; } = null!;
    private FileStream? sessionLock;
    private void CheckControlPaths()
    {
        LocalPathSafety.RejectReparsePoints(Workspaces);
        LocalPathSafety.RejectReparsePoints(Path.Combine(Root, "session.lock"));
        LocalPathSafety.RejectReparsePoints(Path.Combine(Root, "active-workspace.txt"));
    }
    public void EnsureSessionLock()
    {
        CheckControlPaths();
        if (sessionLock is null) { Directory.CreateDirectory(Root); CheckControlPaths(); sessionLock = new FileStream(Path.Combine(Root, "session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
    }
    public void Open()
    {
        CheckControlPaths();
        Directory.CreateDirectory(Workspaces);
        CheckControlPaths();
        // منع نافذتين من تبديل مؤشر مساحة العمل فوق بعضهما.
        sessionLock?.Dispose(); sessionLock = null;
        sessionLock = new FileStream(Path.Combine(Root, "session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var pointer = Path.Combine(Root, "active-workspace.txt");
        if (File.Exists(pointer)) { var id = File.ReadAllText(pointer).Trim(); if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("مؤشر مساحة العمل غير صالح؛ لم تُفتح بيانات أخرى."); Switch(Path.Combine(Workspaces, id)); }
        else { if (Directory.EnumerateDirectories(Workspaces).Any(p => File.Exists(Path.Combine(p, "payroll.db")))) throw new InvalidDataException("مؤشر المساحة مفقود وتوجد قواعد محفوظة. اختر مساحة موجودة من خيارات الاستعادة؛ لم تُنشأ بيانات فارغة."); Switch(Path.Combine(Workspaces, Guid.NewGuid().ToString("N")), create: true); }
    }
    public void Switch(string path, bool create = false)
    {
        CheckControlPaths();
        var full = Path.GetFullPath(path); var id = Path.GetFileName(full);
        LocalPathSafety.CheckDatabasePath(full);
        if (!Guid.TryParseExact(id, "N", out _) || Path.GetDirectoryName(full) != Path.GetFullPath(Workspaces)) throw new InvalidDataException("مساحة العمل خارج المجلد المحلي المخصص.");
        if (!create && (!Directory.Exists(full) || !File.Exists(Path.Combine(full, "payroll.db")))) throw new InvalidDataException("مساحة العمل المحفوظة مفقودة؛ لم تُنشأ قاعدة فارغة بدلاً منها. أعد المجلد أو مؤشره الصحيح ثم أعد المحاولة.");
        if (create && Directory.Exists(full)) throw new InvalidDataException("مساحة العمل الجديدة موجودة مسبقاً.");
        var next = new LocalPayrollStore(full);
        try { next.GetStatus(); CheckControlPaths(); var temporary = Path.Combine(Root, "pointer-" + Guid.NewGuid().ToString("N")); using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { var bytes = System.Text.Encoding.UTF8.GetBytes(id); output.Write(bytes); output.Flush(true); } File.Move(temporary, Path.Combine(Root, "active-workspace.txt"), true); }
        catch { next.Dispose(); throw; }
        Store?.Dispose(); Store = next; DirectoryPath = full;
    }
    public void Dispose() { Store?.Dispose(); sessionLock?.Dispose(); }
}
