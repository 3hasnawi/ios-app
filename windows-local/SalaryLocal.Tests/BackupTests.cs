using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SalaryDesktop.Core;
using SalaryLocal.Core;

public static class BackupTests
{
    private static int assertions;
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "salary-backup-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { Exercise(root); Console.WriteLine($"Backup safety: {assertions} assertions passed."); }
        finally { Directory.Delete(root, true); }
    }
    private static void Exercise(string root)
    {
        var original = Path.Combine(root, "synthetic-original.csv");
        File.WriteAllText(original, "الاسم,الصافي\nموظف وهمي,100.125\n", Encoding.UTF8);
        var originalBytes = File.ReadAllBytes(original);
        var sourceHash = Hash(originalBytes);
        using var store = new LocalPayrollStore(Path.Combine(root, "current"));
        var preview = Fixture("2026-01", 100.125m);
        Import(store, preview, sourceHash);
        Equal(1L, store.GetStatus().HeaderSchemaCount, "Shared headers stored once despite duplicate column labels");
        Equal(2L, store.GetStatus().EmployeeRowCount, "Both distinct employee objects retained");
        var before = Snapshot(store);
        var dbBefore = File.ReadAllBytes(store.DatabasePath);
        var backup = Path.Combine(root, "first.salarybackup");
        var inspected = BackupService.Create(store, backup);
        Equal(BackupRelationship.Same, inspected.Relationship, "Create verifies against current revision");
        Equal(BackupRelationship.NoCurrentData, BackupService.Inspect(backup).Relationship, "No current workspace");
        using (var empty = new LocalPayrollStore(Path.Combine(root, "empty")))
        {
            Equal(BackupRelationship.NoCurrentData, BackupService.Inspect(backup, empty).Relationship, "Empty current workspace");
            Throws<InvalidDataException>(() => BackupService.Create(empty, Path.Combine(root, "empty.salarybackup")), "Refuse empty backup");
            var emptyStatus = empty.GetStatus();
            var emptyDb = File.ReadAllBytes(empty.DatabasePath);
            var emptyManifest = inspected.Manifest with { DatasetId = emptyStatus.DatasetId, RevisionId = emptyStatus.RevisionId,
                RevisionCount = 0, EmployeeRowCount = 0, HeaderSchemaCount = 0, DatabaseBytes = emptyDb.Length, Sha256 = Hash(emptyDb) };
            Reject(root, "empty-database", emptyManifest, emptyDb);
        }
        var database = Entry(backup, "payroll.db");
        using (var zip = ZipFile.OpenRead(backup))
            True(zip.Entries.Select(x => x.FullName).Order().SequenceEqual(new[] { "manifest.json", "payroll.db" }), "Exactly manifest and database, no nested originals or backups");
        Equal(database.LongLength, inspected.Manifest.DatabaseBytes, "Manifest byte size exact");
        Equal(Hash(database), inspected.Manifest.Sha256, "Manifest checksum exact");
        Equal(Hash(File.ReadAllBytes(backup)), inspected.Digest, "Review digest covers entire archive");
        for (var i = 0; i < 5; i++)
        {
            var next = Path.Combine(Path.GetDirectoryName(store.DatabasePath)!, $"repeat-{i}.salarybackup");
            var repeated = BackupService.Create(store, next);
            Equal(inspected.Manifest.DatabaseBytes, repeated.Manifest.DatabaseBytes, "Repeated backups do not include earlier backups or grow database");
            Equal(inspected.Manifest.Sha256, repeated.Manifest.Sha256, "Repeated backup database content stable");
            True(Math.Abs(new FileInfo(next).Length - new FileInfo(backup).Length) < 128, "ZIP size stable except timestamp metadata");
        }
        True(dbBefore.SequenceEqual(File.ReadAllBytes(store.DatabasePath)), "Backup leaves source database bytes unchanged");
        True(originalBytes.SequenceEqual(File.ReadAllBytes(original)), "Original source unchanged");
        var archiveBytes = File.ReadAllBytes(backup);
        Throws<IOException>(() => BackupService.Create(store, backup), "Never overwrite existing backup");
        True(archiveBytes.SequenceEqual(File.ReadAllBytes(backup)), "Existing backup preserved after rejected overwrite");
        Throws<InvalidDataException>(() => BackupService.Create(store, Path.Combine(root, "wrong.zip")), "Native format extension enforced");
        var workspaces = Path.Combine(root, "workspaces");
        var restoredPath = BackupService.RestoreAsNewWorkspace(backup, workspaces, inspected.Digest);
        using var restored = new LocalPayrollStore(restoredPath);
        Equal(before, Snapshot(restored), "Import backup restore retains all values and binary JSON exactly");
        Equal(JsonSerializer.Serialize(store.GetHistory()), JsonSerializer.Serialize(restored.GetHistory()), "Full history preserved");
        Equal(1L, restored.GetStatus().HeaderSchemaCount, "Shared header schema retained on restore");
        var secondRestore = BackupService.RestoreAsNewWorkspace(backup, workspaces, inspected.Digest);
        True(secondRestore != restoredPath, "Repeated restores use different workspaces");
        True(Directory.Exists(restoredPath), "Existing restore retained");
        Equal(before, Snapshot(store), "Restoration never modifies current values");
        AssertCalculationRoundTrip(store.GetSnapshot(), restored.GetSnapshot());

        // كل تعديل اختباري مستقل؛ يبقى المصدر والنسخة السليمة محفوظين.
        Reject(root, "checksum", inspected.Manifest with { Sha256 = new string('0', 64) }, database);
        Reject(root, "size", inspected.Manifest with { DatabaseBytes = database.LongLength + 1 }, database);
        Reject(root, "version", inspected.Manifest with { FormatVersion = 99 }, database);
        Reject(root, "application", inspected.Manifest with { Application = "OtherApp" }, database);
        Reject(root, "dataset", inspected.Manifest with { DatasetId = "wrong" }, database);
        Reject(root, "revision", inspected.Manifest with { RevisionId = new string('A', 64) }, database);
        Reject(root, "row-count", inspected.Manifest with { EmployeeRowCount = 99 }, database);
        Reject(root, "header-count", inspected.Manifest with { HeaderSchemaCount = 99 }, database);
        Reject(root, "history-count", inspected.Manifest with { RevisionCount = 99 }, database);
        Reject(root, "zero-database", inspected.Manifest with { DatabaseBytes = 0, Sha256 = Hash([]) }, []);
        var invalidDb = Encoding.UTF8.GetBytes("not a SQLite database; synthetic fixture only");
        Reject(root, "invalid-database", inspected.Manifest with { DatabaseBytes = invalidDb.Length, Sha256 = Hash(invalidDb) }, invalidDb);
        var corruptDb = database.ToArray(); corruptDb[0] ^= 0xFF;
        Reject(root, "corrupt-database", inspected.Manifest with { Sha256 = Hash(corruptDb) }, corruptDb);
        foreach (var badEntry in new[] { "manifest.json", "payroll.db", "../escape.txt", "/absolute.txt", "nested/payroll.db", "..\\escape.txt" })
        {
            var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".salarybackup");
            WriteZip(path, ("manifest.json", JsonSerializer.SerializeToUtf8Bytes(inspected.Manifest)), ("payroll.db", database), (badEntry, new byte[] { 1 }));
            Throws<InvalidDataException>(() => BackupService.Inspect(path, store), "Reject extra/duplicate/path traversal entry " + badEntry);
        }
        foreach (var badName in new[] { "../payroll.db", "/payroll.db", "nested/payroll.db", "PAYROLL.DB" })
        {
            var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".salarybackup");
            WriteZip(path, ("manifest.json", JsonSerializer.SerializeToUtf8Bytes(inspected.Manifest)), (badName, database));
            Throws<InvalidDataException>(() => BackupService.Inspect(path), "Reject renamed database entry " + badName);
        }
        var unknownManifest = Path.Combine(root, "unknown-field.salarybackup");
        var json = JsonSerializer.Serialize(inspected.Manifest);
        WriteZip(unknownManifest, ("manifest.json", Encoding.UTF8.GetBytes(json[..^1] + ",\"UnknownField\":true}")), ("payroll.db", database));
        Throws<InvalidDataException>(() => BackupService.Inspect(unknownManifest), "Reject unknown manifest fields");
        var hugeManifest = Path.Combine(root, "huge-manifest.salarybackup");
        WriteZip(hugeManifest, ("manifest.json", new byte[65537]), ("payroll.db", database));
        Throws<InvalidDataException>(() => BackupService.Inspect(hugeManifest), "Bound manifest allocation");
        var badJson = Path.Combine(root, "bad-json.salarybackup");
        WriteZip(badJson, ("manifest.json", "{"u8.ToArray()), ("payroll.db", database));
        Throws<InvalidDataException>(() => BackupService.Inspect(badJson), "Reject malformed manifest");
        var noZip = Path.Combine(root, "zero.salarybackup"); File.WriteAllBytes(noZip, []);
        Throws<InvalidDataException>(() => BackupService.Inspect(noZip), "Reject empty archive");
        File.WriteAllBytes(noZip, invalidDb);
        Throws<InvalidDataException>(() => BackupService.Inspect(noZip), "Reject non-ZIP archive");
        var workspaceList = Directory.GetDirectories(workspaces).Order().ToArray();
        var cancelled = new CancellationToken(true);
        Throws<OperationCanceledException>(() => BackupService.Create(store, Path.Combine(root, "cancelled.salarybackup"), cancelled), "Cancel create");
        True(!File.Exists(Path.Combine(root, "cancelled.salarybackup")), "Cancellation publishes no backup");
        Throws<OperationCanceledException>(() => BackupService.Inspect(backup, store, cancelled), "Cancel inspect");
        Throws<OperationCanceledException>(() => BackupService.RestoreAsNewWorkspace(backup, workspaces, inspected.Digest, cancelled), "Cancel restore");
        Throws<InvalidDataException>(() => BackupService.RestoreAsNewWorkspace(backup, workspaces, ""), "Missing reviewed digest rejected");
        True(workspaceList.SequenceEqual(Directory.GetDirectories(workspaces).Order()), "Failed restore leaves no staging workspace");
        True(!Directory.EnumerateFiles(root, ".salary-backup-*.tmp", SearchOption.AllDirectories).Any(), "No staging ZIP leaks");

        // يتفرع جهازان من نسخة مشتركة؛ لا تدمج المراجعات ولا تحذف إحداها.
        Import(store, Fixture("2026-02", 200.25m), sourceHash);
        var newer = Path.Combine(root, "newer.salarybackup");
        var newerInfo = BackupService.Create(store, newer);
        Equal(BackupRelationship.Newer, BackupService.Inspect(newer, restored).Relationship, "Newer is descendant, not timestamp comparison");
        Equal(BackupRelationship.Older, BackupService.Inspect(backup, store).Relationship, "Older ancestor detected");
        var currentValues = Snapshot(store);
        var olderPath = BackupService.RestoreAsNewWorkspace(backup, workspaces, inspected.Digest);
        using (var older = new LocalPayrollStore(olderPath)) Equal(before, Snapshot(older), "Old restore contains original data, no silent merge");
        Equal(currentValues, Snapshot(store), "Old restore cannot delete new month");
        Import(restored, Fixture("2026-03", 300.375m), sourceHash);
        Equal(BackupRelationship.Diverged, BackupService.Inspect(newer, restored).Relationship, "Diverged branches detected");
        var divergedBefore = Snapshot(restored);
        var branchPath = BackupService.RestoreAsNewWorkspace(newer, workspaces, newerInfo.Digest);
        using (var branch = new LocalPayrollStore(branchPath)) Equal(currentValues, Snapshot(branch), "Diverged restore imports chosen branch only");
        Equal(divergedBefore, Snapshot(restored), "Diverged current branch untouched");
        using (var unrelated = new LocalPayrollStore(Path.Combine(root, "unrelated")))
        {
            Import(unrelated, preview, sourceHash);
            Equal(BackupRelationship.OtherDataset, BackupService.Inspect(backup, unrelated).Relationship, "Identical values do not imply same dataset");
        }
        var swapped = Path.Combine(root, "swapped.salarybackup"); File.Copy(backup, swapped);
        var reviewed = BackupService.Inspect(swapped);
        File.Copy(newer, swapped, true);
        workspaceList = Directory.GetDirectories(workspaces).Order().ToArray();
        Throws<InvalidDataException>(() => BackupService.RestoreAsNewWorkspace(swapped, workspaces, reviewed.Digest), "Changed file since inspect rejected even when valid");
        True(workspaceList.SequenceEqual(Directory.GetDirectories(workspaces).Order()), "Changed-file rejection publishes nothing");
        True(originalBytes.SequenceEqual(File.ReadAllBytes(original)), "Source file unchanged after all operations");
        Equal(currentValues, Snapshot(store), "All invalid/cancelled inputs leave current data intact");
    }
    private static PayrollImportPreview Fixture(string month, decimal net)
    {
        var items = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, decimal> { ["صندوق الشهداء"] = 4000, ["الرعاية الصحية"] = 3500 });
        byte[] Details(string payment) => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["طريقة الدفع"] = payment, ["الاسمي"] = "101", ["توقيفات تقاعدية"] = "2", ["مجموع استقطاعات المصارف"] = "7" });
        byte[] Raw(string name) => JsonSerializer.SerializeToUtf8Bytes(new { headers = new[] { "اسم", "مكرر", "مكرر", "", "غير معروف" }, values = new[] { name, "0", "", " ", "١٢٣٫٤٥" }, extra = "محفوظ" });
        return new(month,
            [new(month, "موظف وهمي أول", "عقود", "موقع وهمي", net, items, Details("كاش"), Raw("وهمي أول")),
             new(month, "موظف وهمي ثان", "عقد", "موقع وهمي", net + 1m, items, Details("بطاقة"), Raw("وهمي ثان"))],
            [new(month, "موظف وهمي أول", "جهة وهمية", 7m, "إضبارة وهمية", "عقود"), new(month, "موظف وهمي ثان", "جهة وهمية", 7m, "إضبارة وهمية", "عقد")], []);
    }
    private static void AssertCalculationRoundTrip(BackupSnapshot before, BackupSnapshot after)
    {
        var month = before.Months.Single().Name;
        var a = BankStatementCalculator.Calculate(before, month);
        var b = BankStatementCalculator.Calculate(after, month);
        Equal(JsonSerializer.Serialize(a), JsonSerializer.Serialize(b), "Reused bank calculation unchanged by storage roundtrip");
        Equal(100.125m, b.Cash, "Cash decimal retained"); Equal(101.125m, b.Card, "Card decimal retained");
        Equal(1, b.ContractCount, "Only عقود included, not عقد"); Equal(12m, b.ContractContribution, "Nearest-ten 12 percent nominal plus retirement");
        Equal(14m, b.BankDeductions, "Bank deductions counted once"); Equal(14m, b.ExecutionDeductions, "Execution total unchanged");
        Equal(15227.25m, b.BankTotal, "Bank total includes net, deductions, special items, corrected contracts once");
    }
    private static void Import(LocalPayrollStore store, PayrollImportPreview preview, string hash) => store.Import(preview, "synthetic-original.csv", hash, store.GetStatus().RevisionId, true);
    private static string Snapshot(LocalPayrollStore store)
    {
        var s = store.GetSnapshot(); return JsonSerializer.Serialize(new { s.Months, s.Employees, s.Deductions });
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static byte[] Entry(string path, string name) { using var zip = ZipFile.OpenRead(path); using var stream = zip.GetEntry(name)!.Open(); using var output = new MemoryStream(); stream.CopyTo(output); return output.ToArray(); }
    private static void WriteZip(string path, params (string Name, byte[] Bytes)[] entries)
    { using var zip = ZipFile.Open(path, ZipArchiveMode.Create); foreach (var entry in entries) { using var stream = zip.CreateEntry(entry.Name).Open(); stream.Write(entry.Bytes); } }
    private static void Reject(string root, string name, BackupManifest manifest, byte[] database)
    {
        var path = Path.Combine(root, name + ".salarybackup");
        WriteZip(path, ("manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest)), ("payroll.db", database));
        Throws<InvalidDataException>(() => BackupService.Inspect(path), "Reject " + name);
        var workspaceRoot = Path.Combine(root, "rejected-restores");
        Throws<InvalidDataException>(() => BackupService.RestoreAsNewWorkspace(path, workspaceRoot, Hash(File.ReadAllBytes(path))), "Reject restore " + name);
        True(!Directory.Exists(workspaceRoot) || !Directory.EnumerateFileSystemEntries(workspaceRoot).Any(), "Invalid restore cleans staging " + name);
    }
    private static void True(bool condition, string message) { assertions++; if (!condition) throw new Exception("Backup test failed: " + message); }
    private static void Equal<T>(T expected, T actual, string message) => True(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}; expected {expected}, actual {actual}");
    private static void Throws<T>(Action action, string message) where T : Exception
    { try { action(); } catch (T) { assertions++; return; } throw new Exception("Backup test expected " + typeof(T).Name + ": " + message); }
}
