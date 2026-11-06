using SalaryLocal.Core;
using SalaryDesktop.Core;

internal static class LocalLegacyMigrationTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "salary-legacy-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        int checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is InvalidDataException or IOException or OperationCanceledException) { checks++; return; } throw new Exception("Unsafe legacy action accepted"); }
        try
        {
            var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
            var zip = Path.Combine(source, "export.zip");
            using (var store = new LocalPayrollStore(Path.Combine(root, "original")))
            {
                store.Import(new PayrollImportPreview("تشرين الأول 2026", [new Employee("تشرين الأول 2026", "اسم وهمي", "شرطي", "وحدة وهمية", 1200, null, null, null)], [], []), "synthetic.xlsx", new string('A', 64), store.GetStatus().RevisionId, false);
                IphoneInteropService.Export(store, zip);
            }
            File.WriteAllText(Path.Combine(source, "current.json"), "{\"notImported\":true}");
            Directory.CreateDirectory(Path.Combine(source, "nested")); File.WriteAllText(Path.Combine(source, "nested", "payroll.json"), "synthetic sidecar");
            var bytes = File.ReadAllBytes(zip); var review = LocalLegacyMigration.InspectSource(source);
            Check(review.Files.Count == 3 && review.Files.Count(f => f.IsZip) == 1, "Every source file listed without auto selection");
            Check(review.Files.Where(f => !f.IsZip).All(f => f.Kind.Contains("لن يُستورد")), "Excluded sources explicitly labeled");
            var inspected = LocalLegacyMigration.InspectZip(zip);
            var target = Path.Combine(root, "new-workspaces");
            var imported = LocalLegacyMigration.ImportNewWorkspace(zip, target, inspected.Digest);
            var second = LocalLegacyMigration.ImportNewWorkspace(zip, target, inspected.Digest);
            Check(imported != second && Directory.Exists(imported), "Always new workspaces");
            using (var saved = new LocalPayrollStore(imported)) Check(saved.GetSnapshot().Employees.Single().Net == 1200, "Imported amounts");
            Check(bytes.SequenceEqual(File.ReadAllBytes(zip)) && File.ReadAllText(Path.Combine(source, "current.json")) == "{\"notImported\":true}", "Source untouched");
            Reject(() => LocalLegacyMigration.ImportNewWorkspace(zip, Path.Combine(source, "new"), inspected.Digest));
            Reject(() => LocalLegacyMigration.ImportNewWorkspace(zip, target, new string('0', 64)));
            Check(Directory.GetDirectories(target).Length == 2, "Rejected import leaves no workspace");
            Reject(() => LocalLegacyMigration.InspectSource(source, new CancellationToken(true)));
            Reject(() => LocalLegacyMigration.InspectZip(Path.Combine(source, "current.json")));
            var broken = Path.Combine(source, "broken.zip"); File.WriteAllText(broken, "invalid"); Reject(() => LocalLegacyMigration.InspectZip(broken));
            if (!OperatingSystem.IsWindows())
            {
                var destinationLink = Path.Combine(root, "linked-destination");
                Directory.CreateSymbolicLink(destinationLink, source);
                var originalEntries = Directory.GetFileSystemEntries(source).Order().ToArray();
                Reject(() => LocalLegacyMigration.ImportNewWorkspace(zip, destinationLink, inspected.Digest));
                Reject(() => LocalLegacyMigration.ImportNewWorkspace(zip, Path.Combine(destinationLink, "new-child"), inspected.Digest));
                Check(originalEntries.SequenceEqual(Directory.GetFileSystemEntries(source).Order()) && bytes.SequenceEqual(File.ReadAllBytes(zip)), "Destination link rejection preserves source and creates nothing");
                Directory.Delete(destinationLink);
                var link = Path.Combine(source, "linked.zip"); File.CreateSymbolicLink(link, zip); Reject(() => LocalLegacyMigration.InspectZip(link)); Reject(() => LocalLegacyMigration.InspectSource(source)); File.Delete(link);
            }
            Console.WriteLine($"Legacy migration: {checks} assertions passed.");
        }
        finally { Directory.Delete(root, true); }
    }
}
