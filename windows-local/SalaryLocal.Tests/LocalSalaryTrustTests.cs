using System.Text;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Claunia.PropertyList;
using SalaryDesktop.Core;
using SalaryLocal.Core;
internal static class LocalSalaryTrustTests
{
    private static int checks;
    private static void Check(bool b, string text) { checks++; if (!b) throw new Exception("Trust: " + text); }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { checks++; return; } throw new Exception("Trust accepted invalid operation"); }
    private static JsonNode Decode(byte[] bytes) => JsonNode.Parse(((NSData)PropertyListParser.Parse(bytes)).Bytes)!;
    private static byte[] Encode(JsonNode node) { using var s = new MemoryStream(); PropertyListParser.SaveAsBinary(new NSData(JsonSerializer.SerializeToUtf8Bytes(node)), s); return s.ToArray(); }
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "trust-synthetic-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            var book = new ArchiveBook(Guid.NewGuid(), "كتاب وهمي", "2026-11", 1, null, 0);
            var p = new SalaryTrustPayment(Guid.NewGuid().ToString(), 25, "", "2026-11-05", "");
            var e = new SalaryTrustEntry(Guid.NewGuid().ToString(), "موظف وهمي", "", "", 2026, 11, 100, "", "", "", false, false, [p]);
            var second = e with { Id = Guid.NewGuid().ToString(), Name = "موظف وهمي آخر", Payments = [], ReviewPending = true };
            var ledger = new SalaryTrustLedger(1, [e, second], false);
            var body = Decode(SalaryTrust.Plist(ledger)); body["unknown"] = JsonNode.Parse("{\"number\":123456789012345678901234567890.123456789}");
            body["entries"]![0]!["unknown"] = "retain"; body["entries"]![0]!["payments"]![0]!["unknown"] = JsonNode.Parse("[null,1.00000000000000000000001]");
            body["entries"]![0]!.AsObject().Remove("bookIds");
            var bytes = Encode(body);
            var source = new BackupSnapshot("synthetic", new string('A',64), 6, [], [], [], 0, [], [book], new Dictionary<string, byte[]> { [SalaryTrust.SettingsKey] = bytes });
            Check(LocalSalaryTrust.Patch(source, e).SequenceEqual(bytes), "no-op exact plist");
            var changed = e with { Note = "ملاحظة وهمية", Payments = [p with { Amount = 30 }] };
            var patch = LocalSalaryTrust.Patch(source, changed); var expected = body.DeepClone(); expected["entries"]![0]!["note"] = changed.Note; expected["entries"]![0]!["payments"]![0]!["amount"] = 30;
            Check(JsonNode.DeepEquals(Decode(patch), expected), "unknown root/entry/payment fields and absent fields survive");
            Check(Decode(patch)["unknown"]!.ToJsonString() == body["unknown"]!.ToJsonString(), "precise unknown number");
            Check(bytes.SequenceEqual(source.SettingsPlists[SalaryTrust.SettingsKey]), "source immutable");
            var partial = p with { Id = Guid.NewGuid().ToString(), Amount = 20, BookDate = "" };
            var added = Decode(LocalSalaryTrust.Patch(source, e with { Payments = [p,partial] }));
            Check(added["entries"]![0]!["payments"]!.AsArray().Count == 2 && JsonNode.DeepEquals(added["entries"]![0]!["payments"]![0],body["entries"]![0]!["payments"]![0]), "add partial preserves original payment");
            Reject(() => LocalSalaryTrust.Patch(source, e with { Payments = [p,partial with { Amount = 76 }] }));
            Check(Decode(LocalSalaryTrust.Patch(source, second with { ReviewPending = false }))["entries"]![1]!["reviewPending"]!.GetValue<bool>() == false, "explicit review clearance");
            Reject(() => LocalSalaryTrust.Patch(source, e with { LegacyPaid = true, Payments = [] }));
            Reject(() => LocalSalaryTrust.Patch(source, e with { Amount = 101 }));
            Reject(() => LocalSalaryTrust.Patch(source, e with { Payments = [p with { Amount = 101 }] }));
            Reject(() => LocalSalaryTrust.Patch(source, e with { Payments = [p with { Amount = 0 }] }));
            Reject(() => LocalSalaryTrust.Patch(source, e with { Payments = [p with { BookDate = "2026-02-30" }] }));
            Reject(() => LocalSalaryTrust.Patch(source, e with { Payments = [p,p] }));
            Reject(() => LocalSalaryTrust.Patch(source, e with { BookIds = [Guid.NewGuid().ToString()] }));
            Reject(() => LocalSalaryTrust.Patch(source, e with { BookIds = [book.Id.ToString(),book.Id.ToString()] }));
            Reject(() => LocalSalaryTrust.Patch(source, second with { ReviewPending = false, Payments = [p with { Id = Guid.NewGuid().ToString() }] }));
            Check(Decode(LocalSalaryTrust.Patch(source, e with { BookIds = [book.Id.ToString()] }))["entries"]![0]!["amount"]!.GetValue<long>() == 100, "link keeps amount");
            Check(Decode(LocalSalaryTrust.Patch(source, e with { Payments = [] }))["entries"]![0]!["payments"]!.AsArray().Count == 0, "remove payment");
            // The legacy decoder accepts a zero UUID; do not mistake it for a missing identity.
            var zeroEntry = e with { Id = Guid.Empty.ToString(), Payments = [p with { Id = Guid.Empty.ToString() }] };
            var zeroBody = Decode(SalaryTrust.Plist(new(1, [zeroEntry], false)));
            zeroBody["entries"]![0]!["futureEntry"] = "keep zero identity";
            zeroBody["entries"]![0]!["payments"]![0]!["futurePayment"] = JsonNode.Parse("{\"precise\":1.123456789012345678901234567890123}");
            var zeroSource = source with { SettingsPlists = new Dictionary<string, byte[]> { [SalaryTrust.SettingsKey] = Encode(zeroBody) } };
            var zeroUpdated = zeroEntry with { Note = "zero test", Payments = [zeroEntry.Payments[0] with { Amount = 30 }] };
            var zeroExpected = zeroBody.DeepClone(); zeroExpected["entries"]![0]!["note"] = "zero test"; zeroExpected["entries"]![0]!["payments"]![0]!["amount"] = 30;
            Check(JsonNode.DeepEquals(Decode(LocalSalaryTrust.Patch(zeroSource, zeroUpdated)), zeroExpected), "zero UUID entry/payment retain unknown fields on edit");
            var zeroAppend = second with { Year = 2026, Month = 9 };
            var zeroOpened = Decode(LocalSalaryTrust.PatchOpeningImport(zeroSource, new(1, [zeroEntry,zeroAppend], true)));
            Check(JsonNode.DeepEquals(zeroOpened["entries"]![0], zeroBody["entries"]![0]), "opening import retains zero UUID fields");
            using var store = new LocalPayrollStore(Path.Combine(root,"store"));
            store.ImportDataset(source, [], new Dictionary<string, byte[]> { [$"books/{book.Id.ToString().ToUpperInvariant()}.pdf"] = Pdf(1) }, store.GetStatus().RevisionId);
            var revision = store.GetStatus().RevisionId;
            Check(!store.SaveSalaryTrustEntry(e,revision).Changed, "no-op creates no revision");
            var result = store.SaveSalaryTrustEntry(changed,revision); Check(result.Changed, "partial corrected payment persisted");
            Reject(() => store.SaveSalaryTrustEntry(e,revision));
            Check(store.GetStatus().RevisionId == result.RevisionId, "stale write atomic");
            var snap = store.GetSnapshot(); Check(snap.SettingsPlists[SalaryTrust.SettingsKey].SequenceEqual(patch), "saved exact patch");
            var linked = changed with { BookIds = [book.Id.ToString()] }; store.SaveSalaryTrustEntry(linked,snap.SourceDigest);
            snap = store.GetSnapshot(); store.SaveSalaryTrustEntry(linked with { BookIds = [] },snap.SourceDigest);
            Check(SalaryTrust.Remaining(SalaryTrust.ReadFromBackup(store.GetSnapshot()).Entries[0]) == 70, "link/unlink preserves balance");
            var backup = Path.Combine(root,"trust.salarybackup"); var inspection = BackupService.Create(store,backup);
            var restored = BackupService.RestoreAsNewWorkspace(backup,Path.Combine(root,"restore"),inspection.Digest);
            using var reopened = new LocalPayrollStore(restored);
            Check(reopened.GetSnapshot().SettingsPlists[SalaryTrust.SettingsKey].SequenceEqual(store.GetSnapshot().SettingsPlists[SalaryTrust.SettingsKey]), "backup exact ledger roundtrip");
            var export = Path.Combine(root,"trust-export.zip"); IphoneInteropService.Export(reopened,export);
            Check(BackupReader.Read(export).SettingsPlists[SalaryTrust.SettingsKey].SequenceEqual(reopened.GetSnapshot().SettingsPlists[SalaryTrust.SettingsKey]), "interchange export exact ledger roundtrip");
            try { IphoneInteropService.Export(reopened,export); throw new Exception("Overwrite accepted"); } catch (IOException) { checks++; }
            var append = e with { Id = Guid.NewGuid().ToString(), Name = "افتتاحي وهمي", Month = 9, Payments = [] };
            var imported = LocalSalaryTrust.PatchOpeningImport(source, ledger with { OpeningImported = true, Entries = [e,second,append] });
            Check(JsonNode.DeepEquals(Decode(imported)["entries"]![0],body["entries"]![0]), "opening import preserves old entry");
            Reject(() => LocalSalaryTrust.PatchOpeningImport(source, ledger with { OpeningImported = true, Entries = [changed,second,append] }));
            Reject(() => LocalSalaryTrust.PatchOpeningImport(source, ledger with { OpeningImported = true, Entries = [e,second,append with { Month = 10 }] }));
            var importedSource = source with { SettingsPlists = new Dictionary<string, byte[]> { [SalaryTrust.SettingsKey] = imported } };
            Reject(() => LocalSalaryTrust.PatchOpeningImport(importedSource, SalaryTrust.ReadFromBackup(importedSource)));
            Console.WriteLine($"LocalSalaryTrustTests: {checks} assertions passed.");
        } finally { Directory.Delete(root,true); }
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
