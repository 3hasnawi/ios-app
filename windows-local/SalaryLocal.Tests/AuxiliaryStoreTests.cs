using System.Text;
using Microsoft.Data.Sqlite;
using SalaryDesktop.Core;
using SalaryLocal.Core;

internal static class AuxiliaryStoreTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action, string message) { try { action(); } catch (InvalidDataException) { return; } throw new Exception(message); }
    private static string Sha => new('C', 64);
    private static void Sql(string path, string sql)
    { using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "salary-aux-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using var store = new LocalPayrollStore(Path.Combine(root, "main"));
            var book = new ArchiveBook(Guid.NewGuid(), "كتاب وهمي", "تاريخ غير معروف", 1, null, 123.25);
            byte[] opaque = [0xFF, 0x00, 0x7B, 0x22]; byte[] pdf = Encoding.ASCII.GetBytes("%PDF-1.4\nsynthetic fixture only");
            var attachments = new Dictionary<string, byte[]> { [$"books/{book.Id:D}.pdf"] = pdf, ["interop/metadata.json"] = opaque, ["interop/extra/opaque.bin"] = new byte[2 * 1024 * 1024 + 7] };
            var staff = new[] { new StaffMember("موظف وهمي", "", "", "", "", "", "", opaque) };
            var employee = new Employee("2026-11", "موظف وهمي", "", "", -12.1234567890123456789m, opaque, opaque, opaque);
            var source = new BackupSnapshot("synthetic.zip", Sha, 10,
                [new("2026-11", 1, 2), new("تشرين الثاني 2026", 1, 0), new("شهر قديم غير محدد", 0, 0)],
                [employee, employee with { Month = "تشرين الثاني 2026", Net = null }],
                [new("2026-11", "اسم وهمي", "جهة وهمية", -2.1234567890123456789m, "", ""), new("2026-11", "اسم وهمي", "جهة وهمية", null, "حالة قديمة", "")],
                1, [new("جهة قديمة", "حساب قديم غير معروف", "", "", null)], [book], new Dictionary<string, byte[]> { ["unknown"] = opaque }) { CreatedAt = 123.125 };
            var imported = store.ImportDataset(source, staff, attachments, store.GetStatus().RevisionId);
            Check(imported.Changed && store.GetStatus().RevisionCount == 4, "atomic dataset revisions");
            var snapshot = store.GetSnapshot(); Check(snapshot.Version == 10 && snapshot.CreatedAt == source.CreatedAt, "source version/date lost");
            Check(snapshot.Months.Count == 3 && snapshot.Months.Any(m => m.Name == "شهر قديم غير محدد"), "historical empty month/alias lost");
            Check(snapshot.Employees[0].Net == employee.Net && snapshot.Employees[1].Net is null, "historical signed/null decimal lost");
            Check(snapshot.Deductions[0].Amount == source.Deductions[0].Amount && snapshot.Deductions[1].Amount is null, "historical deductions changed");
            Check(snapshot.StaffCount == 1 && store.GetStaff()[0].Details!.SequenceEqual(opaque), "staff details changed");
            Check(snapshot.SettingsPlists["unknown"].SequenceEqual(opaque) && snapshot.Accounts[0] == source.Accounts[0], "opaque settings/account changed");
            Check(store.ReadAttachment("interop/metadata.json").SequenceEqual(opaque) && store.ReadAttachment(attachments.Keys.Last()).Length == 2 * 1024 * 1024 + 7, "chunked attachment fidelity");
            Reject(() => store.ImportDataset(source, staff, attachments, imported.RevisionId), "dataset import merged nonempty workspace");
            Reject(() => store.Import(new("2026-12", [employee with { Month = "2026-12" }], [], []), "negative.xlsx", Sha, imported.RevisionId, false), "Excel import accepted historic negative");
            var changed = store.SaveSettings(new Dictionary<string, byte[]> { ["new"] = [1, 2, 3] }, imported.RevisionId, "إعداد وهمي");
            Check(changed.Changed && store.GetSnapshot().SettingsPlists["unknown"].SequenceEqual(opaque), "settings patch dropped opaque keys");
            Reject(() => store.SaveSettings(new Dictionary<string, byte[]> { ["new"] = [3] }, imported.RevisionId, "قديم"), "stale auxiliary revision accepted");
            Check(!store.SaveSettings(new Dictionary<string, byte[]> { ["new"] = [1, 2, 3] }, changed.RevisionId, "same").Changed, "repeated settings duplicated history");
            var first = new PayrollImportPreview("2026-12", [], [new("2026-12", "وهمي", "جهة", 8.5m, "", "")], []);
            var existing = new PayrollImportPreview("2026-11", [], [new("2026-11", "وهمي", "جهة", 9.5m, "", "")], []);
            var annual = new AnnualDeductionPreview([first, existing], []);
            Reject(() => store.ImportAnnualDeductions(annual, new Dictionary<string, bool> { [first.Month] = false, [existing.Month] = false }, "annual.xlsx", Sha, changed.RevisionId), "annual replace without confirmation accepted");
            Check(store.GetMonths().Count == 3 && store.GetStatus().RevisionId == changed.RevisionId, "annual failed operation partially committed");
            var annualSaved = store.ImportAnnualDeductions(annual, new Dictionary<string, bool> { [first.Month] = false }, "annual.xlsx", Sha, changed.RevisionId);
            Check(store.GetSnapshot("2026-11").Deductions[0].Amount == source.Deductions[0].Amount && store.GetSnapshot(first.Month).Deductions.Count == 1, "annual skip did not preserve month");
            Check(store.GetSnapshot("2026-11").Employees.Single().Net == employee.Net, "annual operation dropped payroll");
            Check(LocalPayrollStore.ValidateDatabase(store.DatabasePath).RevisionId == annualSaved.RevisionId, "v2 semantic validation failed");
            string archive = Path.Combine(root, "complete.salarybackup"); BackupService.Create(store, archive);
            var inspection = BackupService.Inspect(archive, store);
            string restored = BackupService.RestoreAsNewWorkspace(archive, Path.Combine(root, "restored"), inspection.Digest);
            using (var opened = new LocalPayrollStore(restored))
            {
                Check(opened.ReadAttachment("interop/metadata.json").SequenceEqual(opaque), "native backup lost attachments");
                Check(opened.GetStaff()[0].Details!.SequenceEqual(opaque) && opened.GetSnapshot().Version == 10, "native backup lost auxiliaries");
                Check(opened.GetStatus().RevisionId == annualSaved.RevisionId, "restore changed revision");
            }
            var auxiliaryOnlyDirectory = Path.Combine(root, "aux-only");
            using (var auxiliaryOnly = new LocalPayrollStore(auxiliaryOnlyDirectory))
            {
                var result = auxiliaryOnly.ImportStaff(staff, "synthetic staff", Sha, auxiliaryOnly.GetStatus().RevisionId, false);
                Check(auxiliaryOnly.HasData() && auxiliaryOnly.GetMonths().Count == 0, "staff-only presence");
                string backup = Path.Combine(root, "staff-only.salarybackup"); BackupService.Create(auxiliaryOnly, backup);
                Check(BackupService.Inspect(backup, auxiliaryOnly).Relationship == BackupRelationship.Same, "aux-only backup rejected");
                Check(!auxiliaryOnly.ImportStaff(staff, "same", Sha, result.RevisionId, false).Changed, "staff no-op changed history");
            }
            using (var editable = new LocalPayrollStore(Path.Combine(root, "editable")))
            {
                var a = new ExecutionAccount("جهة وهمية ألف", "IQ06TEST123000000000001", "فرع وهمي", "TESTIQBA", null);
                var b = new ExecutionAccount("جهة وهمية باء", "IQ76TEST123000000000002", "فرع وهمي", "TESTIQBA", null);
                var one = editable.ImportAccounts(new([a, b], []), "accounts.xlsx", Sha, editable.GetStatus().RevisionId);
                var blank = editable.ImportAccounts(new([a with { Branch = "", Swift = "" }], []), "accounts-blank.xlsx", Sha, one.RevisionId);
                Check(editable.GetSnapshot().Accounts.Count == 2 && editable.GetSnapshot().Accounts[0].Swift == a.Swift && editable.GetSnapshot().Accounts[0].Branch == a.Branch && editable.GetSnapshot().Accounts[1] == b, "account merge dropped missing account or blank SWIFT/branch");
                Check(!blank.Changed && blank.RevisionId == one.RevisionId, "blank account fields should not create a revision");
                var two = editable.ImportAccounts(new([a with { Branch = "فرع جديد", Swift = "" }], []), "accounts2.xlsx", Sha, one.RevisionId);
                Check(two.Changed && editable.GetSnapshot().Accounts[0].Branch == "فرع جديد" && editable.GetSnapshot().Accounts[0].Swift == a.Swift, "nonblank branch update failed");
                Reject(() => editable.ImportAccounts(new([a], []), "stale.xlsx", Sha, one.RevisionId), "account stale revision accepted");
                Reject(() => editable.ImportAccounts(new([a, a], []), "duplicate.xlsx", Sha, two.RevisionId), "duplicate accounts accepted");
                Reject(() => editable.SaveSettings(new Dictionary<string, byte[]> { ["bankStatementManualAmounts"] = opaque }, two.RevisionId, "bad"), "malformed edited bank amounts accepted");
                var overrides = ContractOverrides.Set(null, "2026-11", new ContractOverride(100, 500, 40, 1, 0));
                var setting = editable.SaveSettings(new Dictionary<string, byte[]> { [ContractOverrides.SettingsKey] = overrides }, two.RevisionId, "عقود وهمية");
                Check(editable.GetSnapshot().Version == 6 && ContractOverrides.Read(editable.GetSnapshot(), "2026-11")!.Total == 100, "contract setting version/precision");
                Check(LocalPayrollStore.ValidateDatabase(editable.DatabasePath).RevisionId == setting.RevisionId, "edited auxiliary lineage invalid");
            }
            using (var old = new LocalPayrollStore(Path.Combine(root, "old-source")))
            {
                var p = new PayrollImportPreview("2026-10", [employee with { Month = "2026-10", Net = 10m }], [], []);
                old.Import(p, "old.xlsx", Sha, old.GetStatus().RevisionId, false);
                string migrationDirectory = Path.Combine(root, "migration"); Directory.CreateDirectory(migrationDirectory);
                string path = Path.Combine(migrationDirectory, "payroll.db"); old.CreateDatabaseSnapshot(path);
                Sql(path, "DROP TABLE auxiliary_revisions; DROP TABLE auxiliary_entries; DROP TABLE auxiliary_sets; DROP TABLE auxiliary_values; DROP TABLE auxiliary_chunks; PRAGMA user_version=1;");
                var before = LocalPayrollStore.ValidateDatabase(path);
                using var migrated = new LocalPayrollStore(migrationDirectory);
                Check(migrated.GetStatus().RevisionId == before.RevisionId && migrated.GetHistory().Count == 1, "migration rewrote history");
                Check(migrated.GetSnapshot().Employees.Single().RawColumns!.SequenceEqual(opaque), "migration changed raw data");
                var added = migrated.SaveSettings(new Dictionary<string, byte[]> { ["afterMigration"] = opaque }, before.RevisionId, "ترحيل");
                Check(LocalPayrollStore.ValidateDatabase(path).RevisionId == added.RevisionId, "migration cannot append auxiliary lineage");
            }
            string corrupt = Path.Combine(root, "corrupt.db"); store.CreateDatabaseSnapshot(corrupt);
            Sql(corrupt, "UPDATE auxiliary_chunks SET payload=x'00' WHERE id=(SELECT id FROM auxiliary_chunks LIMIT 1)");
            Reject(() => LocalPayrollStore.ValidateDatabase(corrupt), "corrupt auxiliary payload accepted");
            Console.WriteLine("AuxiliaryStoreTests: passed (v1 migration, global lineage, atomic annual imports, signed/null history, settings, staff, attachments, auxiliary-only backups).");
        }
        finally { Directory.Delete(root, true); }
    }
}
