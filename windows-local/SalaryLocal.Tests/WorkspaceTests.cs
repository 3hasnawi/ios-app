using SalaryLocal.App;
using SalaryLocal.Core;

internal static class WorkspaceTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } catch (IOException) { return; } throw new Exception("Unsafe workspace operation accepted"); }
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "salary-workspace-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string workspace;
            using (var first = new Workspace(root))
            {
                first.Open(); workspace = first.DirectoryPath;
                Check(File.Exists(Path.Combine(workspace, "payroll.db")), "First run did not create store");
                using var second = new Workspace(root); Reject(second.Open);
                var pointer = File.ReadAllText(Path.Combine(root, "active-workspace.txt"));
                Reject(() => first.Switch(Path.Combine(root, Guid.NewGuid().ToString("N"))));
                Check(File.ReadAllText(Path.Combine(root, "active-workspace.txt")) == pointer, "Rejected switch changed pointer");
            }
            var original = File.ReadAllBytes(Path.Combine(workspace, "payroll.db"));
            using (var reopened = new Workspace(root)) { reopened.Open(); Check(reopened.DirectoryPath == workspace, "Reopen chose different dataset"); }
            var db = Path.Combine(workspace, "payroll.db"); File.Move(db, db + ".kept");
            using (var missing = new Workspace(root)) { Reject(missing.Open); Check(!File.Exists(db), "Missing database silently recreated"); }
            File.Move(db + ".kept", db);
            File.Delete(Path.Combine(root, "active-workspace.txt"));
            using (var missingPointer = new Workspace(root)) { Reject(missingPointer.Open); Check(Directory.GetDirectories(missingPointer.Workspaces).Length == 1, "Missing pointer created blank branch"); missingPointer.EnsureSessionLock(); missingPointer.Switch(workspace); }
            File.WriteAllText(Path.Combine(root, "active-workspace.txt"), "../../outside");
            using (var bad = new Workspace(root)) { Reject(bad.Open); Check(File.ReadAllText(Path.Combine(root, "active-workspace.txt")) == "../../outside", "Invalid pointer overwritten"); }
            Check(File.ReadAllBytes(db).SequenceEqual(original), "Recovery checks modified saved database");
            if (!OperatingSystem.IsWindows()) LinkGuards(root);
            Console.WriteLine("WorkspaceTests: passed (first run, single instance, reopen, missing database/pointer, invalid pointer, explicit recovery, preservation).");
        }
        finally { Directory.Delete(root, true); }
    }
    private static void LinkGuards(string root)
    {
        var external = Path.Combine(root, "external");
        using (var original = new LocalPayrollStore(external)) { }
        var database = Path.Combine(external, "payroll.db"); var originalBytes = File.ReadAllBytes(database);
        var isolatedRoot = Path.Combine(root, "link-tests");
        using var target = new Workspace(isolatedRoot); target.Open();
        var pointer = File.ReadAllBytes(Path.Combine(isolatedRoot, "active-workspace.txt"));
        var linkWorkspace = Path.Combine(target.Workspaces, Guid.NewGuid().ToString("N"));
        Directory.CreateSymbolicLink(linkWorkspace, external);
        Reject(() => target.Switch(linkWorkspace)); Directory.Delete(linkWorkspace);
        var fileWorkspace = Path.Combine(target.Workspaces, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fileWorkspace);
        var linkedDatabase = Path.Combine(fileWorkspace, "payroll.db"); File.CreateSymbolicLink(linkedDatabase, database);
        Reject(() => target.Switch(fileWorkspace)); File.Delete(linkedDatabase);
        Check(pointer.SequenceEqual(File.ReadAllBytes(Path.Combine(isolatedRoot, "active-workspace.txt"))), "Link rejection changed active pointer");
        Check(originalBytes.SequenceEqual(File.ReadAllBytes(database)), "Database symlink changed external source");
        var rootLink = Path.Combine(root, "root-link"); Directory.CreateSymbolicLink(rootLink, external);
        using (var linked = new Workspace(rootLink)) { Reject(linked.Open); Reject(linked.EnsureSessionLock); }
        Directory.Delete(rootLink);
        var unsafeRoot = Path.Combine(root, "unsafe-workspaces"); Directory.CreateDirectory(unsafeRoot);
        var workspacesLink = Path.Combine(unsafeRoot, "workspaces"); Directory.CreateSymbolicLink(workspacesLink, external);
        using (var linked = new Workspace(unsafeRoot)) { Reject(linked.Open); Reject(linked.EnsureSessionLock); }
        Directory.Delete(workspacesLink);
        Check(!File.Exists(Path.Combine(external, "session.lock")) && !Directory.Exists(Path.Combine(external, "workspaces")), "Root link wrote into external directory");
        var dangling = Path.Combine(root, "dangling"); Directory.CreateSymbolicLink(dangling, Path.Combine(root, "absent"));
        Reject(() => LocalPathSafety.RejectReparsePoints(Path.Combine(dangling, "child"))); File.Delete(dangling);
        var sessionRoot = Path.Combine(root, "session-test"); Directory.CreateDirectory(sessionRoot);
        File.CreateSymbolicLink(Path.Combine(sessionRoot, "session.lock"), database);
        using (var linked = new Workspace(sessionRoot)) { Reject(linked.Open); Reject(linked.EnsureSessionLock); }
        File.Delete(Path.Combine(sessionRoot, "session.lock"));
        File.CreateSymbolicLink(Path.Combine(sessionRoot, "active-workspace.txt"), database);
        using (var linked = new Workspace(sessionRoot)) Reject(linked.Open);
        File.Delete(Path.Combine(sessionRoot, "active-workspace.txt"));
        Check(originalBytes.SequenceEqual(File.ReadAllBytes(database)), "Control-file links changed external database");
        LocalPathSafety.RejectReparsePoints(Path.Combine(root, "new", "missing", "payroll.db"));
    }

}
