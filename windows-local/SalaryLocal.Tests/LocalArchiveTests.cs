using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SalaryDesktop.Core;
using SalaryLocal.Core;

internal static class LocalArchiveTests
{
    private static int checks;
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { checks++; return; } throw new Exception("Archive mutation was not rejected"); }
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "local-archive-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using var store = new LocalPayrollStore(Path.Combine(root, "fresh"));
            string Revision() => store.GetStatus().RevisionId;
            var empty = Revision(); byte[] pdf = Pdf(1);
            var add = store.AddArchiveBook("كتاب وهمي", "2026-11", "جهة وهمية", pdf, empty);
            var book = store.GetSnapshot().Books.Single(); string key = LocalPayrollStore.ArchiveAttachmentKey(book.Id);
            Check(add.Changed && book.Pages == 1 && book.CreatedAt > 0 && store.ReadAttachment(key).SequenceEqual(pdf), "Add book");
            var history = JsonSerializer.Serialize(store.GetHistory());
            Check(!store.EditArchiveBook(book.Id, book.Title, book.Month, book.Entity, Revision()).Changed, "No-op edit");
            Check(!store.ReplaceArchivePdf(book.Id, pdf, Revision()).Changed && history == JsonSerializer.Serialize(store.GetHistory()), "No-op replacement");
            Reject(() => store.EditArchiveBook(book.Id, "stale", book.Month, "", empty));
            Reject(() => store.AddArchiveBook("", book.Month, "", pdf, Revision()));
            Reject(() => store.AddArchiveBook("Scan_0001.pdf", book.Month, "", pdf, Revision()));
            Reject(() => store.AddArchiveBook("bad", book.Month, "", Encoding.ASCII.GetBytes("%PDF-1.4 garbage"), Revision()));
            Reject(() => store.ReplaceArchivePdf(book.Id, Pdf(4), Revision()));
            Check(history == JsonSerializer.Serialize(store.GetHistory()), "Rejected changes rolled back");
            store.EditArchiveBook(book.Id, "عنوان تجريبي معدل", "2026-12", "جهة جديدة", Revision());
            store.ReplaceArchivePdf(book.Id, Pdf(3), Revision());
            var changed = store.GetSnapshot().Books.Single();
            Check(changed.Id == book.Id && changed.CreatedAt == book.CreatedAt && changed.Pages == 3 && changed.Title == "عنوان تجريبي معدل", "Replace preserves identity and metadata");
            var exported = Path.Combine(root, "fresh.zip"); IphoneInteropService.Export(store, exported);
            Check(IphoneInteropService.Inspect(exported).Books == 1, "Fresh interchange export");
            using (var roundtrip = new LocalPayrollStore(IphoneInteropService.ImportNewWorkspace(exported, Path.Combine(root, "roundtrip"), IphoneInteropService.Inspect(exported).Digest)))
                Check(roundtrip.GetSnapshot().Books.Single() == changed && roundtrip.ReadAttachment(key).SequenceEqual(Pdf(3)), "Interchange roundtrip");
            var ledger = new SalaryTrustLedger(1, [new(Guid.NewGuid().ToString(), "شخص وهمي", "", "", 2026, 11, 100, "", "", "", false, false, [], [book.Id.ToString()])], false);
            store.SaveSettings(new Dictionary<string, byte[]> { [SalaryTrust.SettingsKey] = SalaryTrust.Plist(ledger) }, Revision(), "synthetic links");
            var linked = Revision(); Reject(() => store.DeleteArchiveBook(book.Id, linked));
            Check(Revision() == linked && store.ReadAttachment(key).SequenceEqual(Pdf(3)), "Linked delete rollback");
            store.SaveSettings(new Dictionary<string, byte[]> { [SalaryTrust.SettingsKey] = SalaryTrust.Plist(ledger with { Entries = [] }) }, Revision(), "synthetic unlink");
            store.DeleteArchiveBook(book.Id, Revision());
            Check(store.GetSnapshot().Books.Count == 0 && !store.GetAttachments().Contains(key), "Delete current book and attachment");
            Check(store.GetHistory().Count >= 6, "Historical revisions retained");
            using (var sql = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            {
                sql.Open(); using var command = sql.CreateCommand(); command.CommandText = "SELECT count(*) FROM auxiliary_values WHERE id=$id"; command.Parameters.AddWithValue("$id", Convert.ToHexString(SHA256.HashData(pdf)));
                Check((long)command.ExecuteScalar()! == 1, "Original PDF remains in immutable storage after replacement and deletion");
            }
            Check(LocalPayrollStore.ValidateDatabase(store.DatabasePath).RevisionId == Revision(), "All immutable history valid");
            var native = Path.Combine(root, "native.salarybackup"); var review = BackupService.Create(store, native);
            using (var restored = new LocalPayrollStore(BackupService.RestoreAsNewWorkspace(native, Path.Combine(root, "restored"), review.Digest)))
                Check(restored.GetStatus().RevisionId == Revision() && JsonSerializer.Serialize(restored.GetHistory()) == JsonSerializer.Serialize(store.GetHistory()), "Native restore preserves deleted history");
            UnknownFields(root);
            RollbackAfterStaging(root);
            Console.WriteLine($"Local archive: {checks} assertions passed.");
        }
        finally { Directory.Delete(root, true); }
    }
    private static void RollbackAfterStaging(string root)
    {
        using var store = new LocalPayrollStore(Path.Combine(root, "bad-metadata"));
        var book = new ArchiveBook(Guid.NewGuid(), "Scan_0001", "شهر تاريخي", 1, "", 123.5);
        var snapshot = new BackupSnapshot("synthetic", new string('A', 64), 6, [], [], [], 0, [], [book], new Dictionary<string, byte[]>());
        string key = LocalPayrollStore.ArchiveAttachmentKey(book.Id);
        store.ImportDataset(snapshot, [], new Dictionary<string, byte[]> { [key] = Pdf(1), ["interop/metadata.json"] = Encoding.UTF8.GetBytes("broken metadata") }, store.GetStatus().RevisionId);
        string revision = store.GetStatus().RevisionId;
        Check(!store.EditArchiveBook(book.Id, book.Title, book.Month, book.Entity, revision).Changed, "Unchanged historical generic title preserved");
        Reject(() => store.ReplaceArchivePdf(book.Id, Pdf(2), revision));
        Check(store.GetStatus().RevisionId == revision && store.ReadAttachment(key).SequenceEqual(Pdf(1)), "Metadata failure rolls back staged PDF and metadata");
        Check(LocalPayrollStore.ValidateDatabase(store.DatabasePath).RevisionId == revision, "Rollback leaves no orphan values or chunks");
    }
    private static void UnknownFields(string root)
    {
        var source = Path.Combine(root, "unknown.zip"); var files = InteropTests.Fixture(); InteropTests.WriteZip(source, files); var original = File.ReadAllBytes(source);
        using var store = new LocalPayrollStore(IphoneInteropService.ImportNewWorkspace(source, Path.Combine(root, "import"), IphoneInteropService.Inspect(source).Digest));
        var book = store.GetSnapshot().Books.Single(); var before = JsonNode.Parse(store.ReadAttachment("interop/metadata.json"))!["extraFields"]!["books/index.json"]!.AsObject().Single().Value!.DeepClone();
        store.EditArchiveBook(book.Id, "كتاب معدل وهمي", book.Month, "جهة أخرى", store.GetStatus().RevisionId);
        store.ReplaceArchivePdf(book.Id, Pdf(2), store.GetStatus().RevisionId);
        var after = JsonNode.Parse(store.ReadAttachment("interop/metadata.json"))!["extraFields"]!["books/index.json"]!.AsObject().Single().Value;
        Check(JsonNode.DeepEquals(before, after), "Unknown book fields retained through edit and replace");
        var output = Path.Combine(root, "unknown-out.zip"); IphoneInteropService.Export(store, output);
        Check(IphoneInteropService.Inspect(output).Books == 1 && original.SequenceEqual(File.ReadAllBytes(source)), "Unknown-field export and original source preservation");
        using (var zip = ZipFile.OpenRead(output))
        using (var stream = zip.GetEntry("books/index.json")!.Open())
        using (var index = JsonDocument.Parse(stream))
        {
            var row = index.RootElement[0];
            Check(row.GetProperty("id").GetString() == book.Id.ToString().ToUpperInvariant() && row.GetProperty("createdAt").GetDouble() == book.CreatedAt && row.GetProperty("futureBook").GetString() == "محفوظ", "Uppercase UUID, original createdAt and unknown fields exported intact");
            Check(zip.GetEntry(LocalPayrollStore.ArchiveAttachmentKey(book.Id)) is not null, "Canonical uppercase PDF path");
        }
        // نسخة الاختبار لا تربط كتابها بقيد أمانات.
        store.DeleteArchiveBook(book.Id, store.GetStatus().RevisionId);
        Check(JsonNode.Parse(store.ReadAttachment("interop/metadata.json"))!["extraFields"]!["books/index.json"]!.AsObject().Count == 0, "Explicit deletion removes only current extras");
        IphoneInteropService.Export(store, Path.Combine(root, "deleted-out.zip"));
        Check(LocalPayrollStore.ValidateDatabase(store.DatabasePath).RevisionId == store.GetStatus().RevisionId, "Unknown-field deletion history valid");
    }
    private static byte[] Pdf(int pages)
    {
        var text = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int>();
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", $"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, pages).Select(p => $"{3 + p * 2} 0 R"))}] /Count {pages} >>" };
        for (int p = 0; p < pages; p++) { objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents {4 + p * 2} 0 R >>"); objects.Add("<< /Length 0 >>\nstream\n\nendstream"); }
        for (int i = 0; i < objects.Count; i++) { offsets.Add(text.Length); text.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        int xref = text.Length; text.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n"); foreach (var offset in offsets) text.Append(offset.ToString("0000000000", CultureInfo.InvariantCulture) + " 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"); return Encoding.ASCII.GetBytes(text.ToString());
    }
}
