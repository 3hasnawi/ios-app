using System.Text;
using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using SalaryDesktop.Core;
using SalaryLocal.Core;

internal static class StoreTests
{
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (InvalidDataException) { return; } catch (IOException) { return; }
        throw new Exception(message);
    }
    public static void Run()
    {
        AuxiliaryStoreTests.Run();
        string root = Path.Combine(Path.GetTempPath(), "salary-store-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using var store = new LocalPayrollStore(Path.Combine(root, "main"));
            string hash = new('A', 64), month = "2026-11";
            var raw = Encoding.UTF8.GetBytes("{ \"unknown\":123456789123456789.1234567,\"headers\":[\"اسم\",\"مكرر\",\"مكرر\",\"\"],\"values\":[\"وهمي\",\"0\",\"\",\"\"] }");
            var e = new Employee(month, "موظف وهمي", "شرطي", "قسم وهمي", 123456.1234567890123456789m, Encoding.UTF8.GetBytes("{\"مجهول\":0}"), null, raw);
            PayrollImportPreview Preview(Employee[] employees, Deduction[]? deductions = null) => new(month, employees, deductions ?? [], []);
            var initial = store.GetStatus(); Assert(initial.RevisionCount == 0, "new store");
            var first = store.Import(Preview([e, e with { Name = "موظف تجريبي" }]), "synthetic.xlsx", hash, initial.RevisionId, false);
            Assert(first.Changed && store.GetStatus().HeaderSchemaCount == 1, "shared headers");
            var snap = store.GetSnapshot(month);
            Assert(store.GetMonths().Single().NetTotal == e.Net * 2, "exact summary total");
            Assert(snap.Employees[0].Net == e.Net && snap.Employees[0].RawColumns!.SequenceEqual(raw), "decimal and exact raw round trip");
            var before = store.GetStatus();
            var noop = store.Import(Preview([e, e with { Name = "موظف تجريبي" }]), "renamed.xlsx", hash, first.RevisionId, false);
            Assert(!noop.Changed && store.GetStatus().RevisionCount == 1 && before.EmployeeRowCount == store.GetStatus().EmployeeRowCount, "repeat no-op");
            Reject(() => store.Import(Preview([e]), "x", hash, initial.RevisionId, true), "stale review accepted");
            Reject(() => store.Import(Preview([e with { Net = 0 }]), "x", hash, first.RevisionId, false), "unconfirmed replacement accepted");
            var d = new Deduction(month, "موظف وهمي", "جهة وهمية", 5.1234567890123456789012345678m, "", "شرطي");
            var second = store.Import(Preview([], [d, d]), "deductions.xlsx", hash, first.RevisionId, false);
            Assert(store.GetSnapshot().Employees.Count == 2 && store.GetSnapshot().Deductions.Count == 2, "independent sections and duplicates");
            var third = store.Import(Preview([e, e with { Name = "موظف تجريبي", Net = 99m }]), "changed.xlsx", hash, second.RevisionId, true);
            Assert(store.GetStatus().EmployeeRowCount == 3 && store.GetHistory().Count == 3, "unchanged rows deduplicated, history retained");
            Assert(store.GetSnapshot().Deductions[0].Amount == d.Amount, "deductions precision preserved");
            byte[] malformed = [0xFF, 0x00, 0x7B];
            var fourth = store.Import(Preview([e with { RawColumns = malformed }]), "opaque.xlsx", hash, third.RevisionId, true);
            Assert(store.GetSnapshot().Employees.Single().RawColumns!.SequenceEqual(malformed), "opaque bytes lost");
            Reject(() => store.Import(Preview([e with { Net = -1 }]), "bad.xlsx", hash, fourth.RevisionId, true), "negative amount accepted");
            Assert(LocalPayrollStore.ValidateDatabase(store.DatabasePath).RevisionId == fourth.RevisionId, "validate live store");
            string copy = Path.Combine(root, "snapshot.db"); store.CreateDatabaseSnapshot(copy);
            Assert(LocalPayrollStore.ValidateDatabase(copy).RevisionCount == 4, "snapshot includes history");
            Assert(CodecCount(copy, 1) == 0 && CodecCount(copy, 0) > 0, "snapshot must use raw rows for ZIP compression");
            var snapshotIdentity = LocalPayrollStore.ValidateDatabase(copy);
            LocalPayrollStore.PrepareRestoredDatabase(copy);
            Assert(CodecCount(copy, 0) == 0 && CodecCount(copy, 1) > 0, "restored copy must use Brotli rows");
            var restoredIdentity = LocalPayrollStore.ValidateDatabase(copy);
            Assert(snapshotIdentity with { DatabaseBytes = restoredIdentity.DatabaseBytes } == restoredIdentity, "transcode changed semantic identity");
            string restoredFolder = Path.Combine(root, "roundtrip"); Directory.CreateDirectory(restoredFolder);
            File.Copy(copy, Path.Combine(restoredFolder, "payroll.db"));
            using (var restoredStore = new LocalPayrollStore(restoredFolder))
            {
                Assert(restoredStore.GetSnapshot().Employees.Single().RawColumns!.SequenceEqual(malformed), "transcode lost opaque raw bytes");
                Assert(restoredStore.GetSnapshot().Employees.Single().Net == e.Net, "transcode changed decimal precision");
                Assert(restoredStore.GetHistory().Count == 4, "transcode lost history");
            }
            var bytes = File.ReadAllBytes(copy); Reject(() => store.CreateDatabaseSnapshot(copy), "overwrote snapshot");
            Assert(bytes.SequenceEqual(File.ReadAllBytes(copy)), "existing destination changed");
            Assert(store.GetSnapshot("2026-10").Employees.Count == 0, "filtered snapshot");
            Tamper(copy, "UPDATE metadata SET current='BAD'"); Reject(() => LocalPayrollStore.ValidateDatabase(copy), "bad pointer accepted");
            File.Delete(copy); store.CreateDatabaseSnapshot(copy);
            Tamper(copy, "UPDATE objects SET payload=x'7B7D' WHERE kind=0"); Reject(() => LocalPayrollStore.ValidateDatabase(copy), "changed immutable row accepted");
            File.Delete(copy); store.CreateDatabaseSnapshot(copy);
            Tamper(copy, "CREATE TABLE unexpected(x TEXT)"); Reject(() => LocalPayrollStore.ValidateDatabase(copy), "unknown schema accepted");
            File.Delete(copy); store.CreateDatabaseSnapshot(copy);
            Tamper(copy, "INSERT INTO headers VALUES('ORPHAN',x'5B5D')"); Reject(() => LocalPayrollStore.ValidateDatabase(copy), "orphan header accepted");
            File.Delete(copy); store.CreateDatabaseSnapshot(copy);
            Tamper(copy, "UPDATE sections SET net_total='0' WHERE kind=0"); Reject(() => LocalPayrollStore.ValidateDatabase(copy), "wrong summary total accepted");
            File.Delete(copy); store.CreateDatabaseSnapshot(copy);
            LocalPayrollStore.PrepareRestoredDatabase(copy);
            Tamper(copy, "UPDATE objects SET payload=CAST(payload || x'00' AS BLOB) WHERE kind=0");
            Reject(() => LocalPayrollStore.ValidateDatabase(copy), "trailing compressed bytes accepted");
            File.Delete(copy); store.CreateDatabaseSnapshot(copy);
            Tamper(copy, "UPDATE objects SET raw_length=1 WHERE kind=0");
            Reject(() => LocalPayrollStore.ValidateDatabase(copy), "decompression length mismatch accepted");
            var stable = store.GetStatus();
            var oversized = e with { RawColumns = new byte[16 * 1024 * 1024] };
            Reject(() => store.Import(Preview([oversized]), "oversized.xlsx", hash, stable.RevisionId, true), "oversized row accepted");
            Assert(store.GetStatus().RevisionId == stable.RevisionId, "oversized import changed current revision");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString()))
            {
                connection.Open(); using var query = connection.CreateCommand();
                query.CommandText = "SELECT sum(length(payload)) < sum(raw_length) FROM objects";
                Assert(Convert.ToInt32(query.ExecuteScalar()) == 1, "row payloads did not compress");
            }
            File.Delete(copy); store.CreateDatabaseSnapshot(copy);
            Tamper(copy, "UPDATE objects SET codec=1 WHERE codec=0");
            Reject(() => LocalPayrollStore.ValidateDatabase(copy), "invalid codec/payload combination accepted");
            var raceRevision = store.GetStatus().RevisionId;
            using var start = new Barrier(2); var outcomes = new ConcurrentBag<bool>();
            Task Writer(decimal value) => Task.Run(() =>
            {
                using var other = new LocalPayrollStore(Path.Combine(root, "main")); start.SignalAndWait();
                try { other.Import(Preview([e with { Net = value }]), "parallel.xlsx", hash, raceRevision, true); outcomes.Add(true); }
                catch (InvalidDataException) { outcomes.Add(false); }
            });
            Task.WaitAll(Writer(17m), Writer(18m));
            Assert(outcomes.Count(x => x) == 1 && outcomes.Count(x => !x) == 1, "concurrent review allowed two writers");
            Assert(LocalPayrollStore.ValidateDatabase(store.DatabasePath).RevisionCount == 5, "concurrent commit lineage");
            var same = store.GetSnapshot(month); var beforeNext = store.GetStatus();
            var next = "2026-12";
            store.Import(new(next, same.Employees.Select(x => x with { Month = next }).ToArray(), same.Deductions.Select(x => x with { Month = next }).ToArray(), []), "next-month.xlsx", hash, beforeNext.RevisionId, false);
            Assert(store.GetStatus().EmployeeRowCount == beforeNext.EmployeeRowCount && store.GetStatus().HeaderSchemaCount == beforeNext.HeaderSchemaCount, "unchanged rows in next month duplicated");
            Assert(store.GetSnapshot(next).Employees.All(x => x.Month == next) && store.GetSnapshot(month).Employees.All(x => x.Month == month), "shared rows lost month identity");
            LocalPayrollStore.ValidateDatabase(store.DatabasePath);
            using (var version = new SqliteConnection("Data Source=:memory:"))
            {
                version.Open(); using var query = version.CreateCommand(); query.CommandText = "SELECT sqlite_version()";
                Console.WriteLine("Native SQLite: " + query.ExecuteScalar());
            }
            Console.WriteLine("StoreTests: passed (precision, raw fidelity, deduplication, history, section preservation, revision locks, validation, consistent snapshots).");
        }
        finally { Directory.Delete(root, true); }
    }
    private static long CodecCount(string path, int codec)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); c.Open();
        using var query = c.CreateCommand(); query.CommandText = "SELECT count(*) FROM objects WHERE codec=$codec"; query.Parameters.AddWithValue("$codec", codec);
        return (long)query.ExecuteScalar()!;
    }
    private static void Tamper(string path, string sql)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery();
    }
}
