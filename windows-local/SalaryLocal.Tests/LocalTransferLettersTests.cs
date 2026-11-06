using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Claunia.PropertyList;
using Microsoft.Data.Sqlite;
using SalaryDesktop.Core;
using SalaryLocal.Core;

internal static class LocalTransferLettersTests
{
    private const string Month = "تشرين الثاني 2026", Payer = "IQ76TEST123000000000002", Receiver = "IQ06TEST123000000000001";
    private static int checks;
    private static readonly DateOnly Date = new(2026, 11, 5);
    private static void Check(bool ok, string message) { checks++; if (!ok) throw new Exception("Transfer letters: " + message); }
    private static void Reject(Action action, string message) { try { action(); } catch (InvalidDataException) { checks++; return; } throw new Exception("Transfer letters accepted: " + message); }
    private static byte[] Plist(NSObject value) { using var output = new MemoryStream(); PropertyListParser.SaveAsBinary(value, output); return output.ToArray(); }
    private static JsonNode Json(byte[] bytes) => JsonNode.Parse(((NSData)PropertyListParser.Parse(bytes)).Bytes)!;
    private static byte[] JsonPlist(string json) => Plist(new NSData(Encoding.UTF8.GetBytes(json)));
    private static GroupLetterSettings Settings => new("وزارة وهمية\nقسم تجريبي", "123", "عضو وهمي أول", "عضو وهمي ثان", "عضو وهمي ثالث", "نسخة منه إلى ( )", null, null, 0, null, "عقد تجريبي 1 في 2026");
    internal static BackupSnapshot Fixture()
    {
        var items = new Dictionary<string, decimal> { ["صندوق الشهداء"] = 0, ["الرعاية الصحية"] = 0, ["النشاط الرياضي"] = 0, ["الضريبة"] = 30, ["الحماية الاجتماعية"] = 40, ["شركة عراق الجوال"] = 0 };
        var details = new Dictionary<string, string> { ["طريقة الدفع"] = "كاش", ["الاسمي"] = "1125", ["توقيفات تقاعدية"] = "2", ["مجموع استقطاعات المصارف"] = "7" };
        var source = new BackupSnapshot("synthetic", new string('A', 64), 6, [new(Month, 1, 1)],
            [new(Month, "منتسب وهمي", "عقود", "قسم وهمي", 1000, JsonSerializer.SerializeToUtf8Bytes(items), JsonSerializer.SerializeToUtf8Bytes(details), null)],
            [new(Month, "منتسب وهمي", "جهة وهمية", 7, "قيد وهمي", "عقود")], 0,
            [new("جهة وهمية", Receiver, "مصرف الرافدين فرع وهمي", "TESTIQBA", null)], [], new Dictionary<string, byte[]> {
                ["bankStatementAccountName"] = Plist(new NSString("دافع وهمي")), ["bankStatementIBAN"] = Plist(new NSString(Payer)),
                [LocalTransferLetterSettings.ReferencesKey] = Plist(new NSData(References())), ["unknownSetting"] = JsonPlist("{\"precise\":123456789012345678901234567890.123456789}") });
        var settings = new Dictionary<string, byte[]>(source.SettingsPlists) { [LocalTransferLetterSettings.SettingsKey] = LocalTransferLetterSettings.Patch(source, "rafidain_group", Settings, true) };
        var custom = Json(settings[LocalTransferLetterSettings.SettingsKey]);
        custom["futureRoot"] = JsonNode.Parse("{\"precise\":123456789012345678901234567890.123456789,\"nothing\":null}");
        custom["common"]!["futureCommon"] = "محفوظ"; custom["individual"]!["water"] = JsonNode.Parse("{\"futureBook\":true,\"beneficiaryAccount\":\"\"}");
        settings[LocalTransferLetterSettings.SettingsKey] = JsonPlist(custom.ToJsonString());
        return source with { SettingsPlists = settings };
    }
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "transfer-letter-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { Planning(); SettingsPreservation(); Persistence(root); AtomicRollback(root); Console.WriteLine($"LocalTransferLettersTests: {checks} assertions passed (planning and persistence only; Windows PDF rendering not exercised)."); }
        finally { Directory.Delete(root, true); }
    }
    private static void Planning()
    {
        var source = Fixture(); var plan = LocalTransferLetters.Prepare(source, Month, "rafidain_group", null, Date);
        Check(plan.Total == 7 && plan.Group!.Lines.Single().Iban == Receiver && plan.PayerAccount == Payer && plan.SourceRevision == source.SourceDigest, "group totals, accounts and global revision");
        var csv = plan.Group!.Csv; var original = csv.ToArray(); Array.Fill(csv, (byte)0);
        Check(plan.Group.Csv.SequenceEqual(original), "public CSV reads cannot mutate reviewed bytes");
        var items = source.Employees.Single().Items!; Array.Fill(items, (byte)0);
        Check(plan.Total == 7 && plan.Issuance!.Lines.Single().Amount == 7, "source byte mutation cannot mutate prepared plan");
        source = Fixture(); var cash = LocalTransferLetters.Prepare(source, Month, "cash_salary", null, Date);
        Check(cash.Total == 1000 && cash.Names!.Rows.Single().Workplace == "قسم وهمي" && cash.Names.Total == cash.Payroll!.Total, "cash letter includes matched complete name list");
        var contracts = LocalTransferLetters.Prepare(source, Month, "contracts", null, Date);
        Check(contracts.Total == 142 && contracts.Payroll!.Lines[0].Amount == 140, "contracts uses local nearest-ten engine before pension");
        var tax = LocalTransferLetters.Prepare(source, Month, "tax_social", null, Date);
        Check(tax.Total == 70 && tax.Payroll!.Lines.Count == 2, "tax and social remain separate lines");
        var details = JsonSerializer.Deserialize<Dictionary<string, string>>(source.Employees[0].Details!)!; details.Remove("الاسمي");
        Reject(() => LocalTransferLetters.Prepare(source with { Employees = [source.Employees[0] with { Details = JsonSerializer.SerializeToUtf8Bytes(details) }] }, Month, "contracts", null, Date), "missing nominal cannot be bypassed");
        Reject(() => LocalTransferLetters.Prepare(source with { Months = [new(Month, 2, 1)] }, Month, "rafidain_group", null, Date), "incomplete source");
        Reject(() => LocalTransferLetters.Prepare(source with { Months = [new(Month, 1, 1), new("2026-11", 0, 0)] }, Month, "rafidain_group", null, Date), "ambiguous canonical month");
        Reject(() => LocalTransferLetters.Prepare(source with { SourceDigest = "BAD" }, Month, "rafidain_group", null, Date), "invalid revision");
        Reject(() => LocalTransferLetters.Prepare(source with { Accounts = [] }, Month, "rafidain_group", null, Date), "unresolved beneficiary blocks group");
        var otherBank = source with { Accounts = [source.Accounts[0] with { Branch = "مصرف الرشيد فرع وهمي" }] };
        var other = LocalTransferLetters.Prepare(otherBank, Month, "other_banks_group", null, Date);
        Check(other.Group!.Group == TransferGroup.OtherBanks && Encoding.UTF8.GetString(other.Group.Csv).Contains("20261105200001", StringComparison.Ordinal), "other-bank category and reference sequence");
        var entitySource = source with { Accounts = [source.Accounts[0] with { Branch = "مصرف الرافدين فرع الأنبار" }] };
        var entityId = TransferPreparation.Prepare(entitySource, Month).Entries.Single().Id;
        foreach (var id in new[] { "ramadi", "housing", "water" })
        {
            var entityPlan = LocalTransferLetters.Prepare(entitySource, Month, id, entityId, Date);
            Check(entityPlan.Total == 7 && entityPlan.BeneficiaryAccount == (id == "ramadi" ? Receiver : Receiver[^15..]) && (id == "water" ? entityPlan.Names is null : entityPlan.Names?.Rows.Count == 1), "entity reference and attachment rules: " + id);
        }
        var settings = new Dictionary<string, byte[]>(source.SettingsPlists) { ["bankStatementIBAN"] = Plist(new NSString(Receiver)) };
        Reject(() => LocalTransferLetters.Prepare(source with { SettingsPlists = settings }, Month, "rafidain_group", null, Date), "payer reference mismatch");
        var anbar = source with { Accounts = [], Deductions = [source.Deductions[0] with { Title = "مصرف الرافدين/فرع الانبار" }] };
        var entry = TransferPreparation.Prepare(anbar, Month).Entries.Single();
        var individual = LocalTransferLetters.Prepare(anbar, Month, "rafidain_anbar", entry.Id, Date);
        Check(individual.Total == 7 && individual.BeneficiaryAccount is null && individual.Names!.Rows.Count == 1, "direct Anbar has no invented beneficiary and includes list");
        Reject(() => LocalTransferLetters.Prepare(anbar, Month, "rafidain_anbar", null, Date), "entity choice required");
        Check(individual.Names!.Rows[0].Workplace == "قسم وهمي", "entity attachment preserves payroll workplace");
        var noRank = anbar with { Deductions = [anbar.Deductions[0] with { Rank = "" }] };
        Check(LocalTransferLetters.Prepare(noRank, Month, "rafidain_anbar", entry.Id, Date).Names!.Rows[0].Rank == "عقود", "entity attachment fills missing rank from unique payroll match");
        var ambiguous = anbar with { Months = [new(Month, 2, 1)], Employees = [anbar.Employees[0], anbar.Employees[0] with { Workplace = "قسم ثان وهمي" }] };
        Reject(() => LocalTransferLetters.Prepare(ambiguous, Month, "rafidain_anbar", entry.Id, Date), "ambiguous payroll name cannot populate names attachment");
        var foodItems = JsonSerializer.Deserialize<Dictionary<string, decimal>>(source.Employees[0].Items!)!; foodItems["شركة عراق الجوال"] = 100000;
        var foodSource = source with { Employees = [source.Employees[0] with { Items = JsonSerializer.SerializeToUtf8Bytes(foodItems) }] };
        Check(LocalTransferLetters.Prepare(foodSource, Month, "food_company", null, Date).Total == 95254 && LocalTransferLetters.Prepare(foodSource, Month, "food_tax", null, Date).Total == 1746, "food deductions preserve original split");
    }
    private static void SettingsPreservation()
    {
        var source = Fixture(); var before = Json(source.SettingsPlists[LocalTransferLetterSettings.SettingsKey]);
        var changed = LocalTransferLetterSettings.Patch(source, "rafidain_group", Settings with { First = "عضو جديد وهمي" }, true); var after = Json(changed);
        Check(JsonNode.DeepEquals(before["futureRoot"], after["futureRoot"]) && JsonNode.DeepEquals(before["individual"]!["water"], after["individual"]!["water"]) && after["common"]!["futureCommon"]!.GetValue<string>() == "محفوظ", "unknown root/common/book values and precise numbers survive");
        Check(after["common"]!["committeeFirst"]!.GetValue<string>() == "عضو جديد وهمي", "selected common field updated");
        Reject(() => LocalTransferLetterSettings.Patch(source, "rafidain_group", Settings with { BodyTemplate = "متن ناقص" }, false), "accounting token removal");
        var broken = source with { SettingsPlists = new Dictionary<string, byte[]>(source.SettingsPlists) { [LocalTransferLetterSettings.SettingsKey] = JsonPlist("{\"common\":{},\"individual\":{},\"duplicate\":1,\"duplicate\":2}") } };
        Reject(() => LocalTransferLetterSettings.Patch(broken, "rafidain_group", Settings, true), "duplicate unknown properties are not discarded");
    }
    private static void Persistence(string root)
    {
        using var store = new LocalPayrollStore(Path.Combine(root, "store")); var seed = Fixture(); store.ImportDataset(seed, [], new Dictionary<string, byte[]>(), store.GetStatus().RevisionId);
        var source = store.GetSnapshot(Month); var review = LocalTransferLetters.Prepare(source, Month, "rafidain_group", null, Date);
        // الحزمة الاصطناعية تختبر الحفظ فقط؛ ليست ناتج رسم PDF ولا إثباتاً لشكل الكتاب.
        var fakeFiles = new Dictionary<string, byte[]> { ["synthetic.pdf"] = Pdf("rafidain_group", 1), ["transfer.csv"] = review.Group!.Csv };
        var package = new LocalLetterPackage(review, fakeFiles); var saved = Path.Combine(root, "draft.zip");
        Array.Fill(fakeFiles["transfer.csv"], (byte)0); store.SaveTransferLetterPackage(package, saved);
        using (var zip = ZipFile.OpenRead(saved)) { using var input = zip.GetEntry("transfer.csv")!.Open(); using var output = new MemoryStream(); input.CopyTo(output); Check(output.ToArray().SequenceEqual(review.Group!.Csv), "package owns saved bytes"); Check(zip.GetEntry("manifest.json") is not null, "package includes source and digest index"); }
        var savedHash = SHA256.HashData(File.ReadAllBytes(saved)); Reject(() => store.SaveTransferLetterPackage(package, saved), "existing export not overwritten"); Check(savedHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(saved))), "existing file unchanged");
        var editedItems = source.Employees[0].Items!.ToArray(); var forged = source with { Employees = [source.Employees[0] with { Net = 1100, Items = editedItems }] };
        var forgedReview = LocalTransferLetters.Prepare(forged, Month, "rafidain_group", null, Date);
        Reject(() => store.SaveTransferLetterPackage(new(forgedReview, fakeFiles), Path.Combine(root, "forged.zip")), "same-revision forged snapshot rejected before save");
        Reject(() => store.ConfirmTransferLetterGroup(forgedReview, ""), "same-revision forged snapshot rejected before issuance");
        Check(!File.Exists(Path.Combine(root, "forged.zip")), "forged package creates no destination");
        var unknown = source.SettingsPlists["unknownSetting"].ToArray(); var initialRevision = source.SourceDigest;
        store.SaveTransferLetterSettings("rafidain_group", Settings with { First = "اسم معدل وهمي" }, false, initialRevision);
        Check(store.GetSnapshot().SettingsPlists["unknownSetting"].SequenceEqual(unknown), "unrelated settings remain byte-identical");
        Reject(() => store.SaveTransferLetterPackage(package, Path.Combine(root, "stale.zip")), "any settings revision invalidates old preview");
        Reject(() => store.ConfirmTransferLetterGroup(review, ""), "old issuance rejected");
        Reject(() => store.SaveTransferLetterSettings("rafidain_group", Settings, false, initialRevision), "stale settings rejected");
        source = store.GetSnapshot(Month); review = LocalTransferLetters.Prepare(source, Month, "rafidain_group", null, Date);
        store.ConfirmTransferLetterGroup(review, "إقرار وهمي للاختبار");
        var ledger = Json(store.GetSnapshot().SettingsPlists[GroupIssuance.SettingsKey]); ledger["futureLedger"] = JsonNode.Parse("{\"large\":123456789012345678901234567890.123456789}"); ledger["records"]![0]!["futureRecord"] = "محفوظ";
        store.SaveSettings(new Dictionary<string, byte[]> { [GroupIssuance.SettingsKey] = JsonPlist(ledger.ToJsonString()) }, store.GetStatus().RevisionId, "اختبار حقل مستقبلي");
        source = store.GetSnapshot(Month); review = LocalTransferLetters.Prepare(source, Month, "rafidain_group", null, Date); Check(review.Issuance!.Changes.Count == 0, "unchanged prior issuance yields no invented changes");
        store.ConfirmTransferLetterGroup(review, "إقرار وهمي ثان"); var final = Json(store.GetSnapshot().SettingsPlists[GroupIssuance.SettingsKey]);
        Check(final["records"]!.AsArray().Count == 2 && JsonNode.DeepEquals(ledger["futureLedger"], final["futureLedger"]) && final["records"]![0]!["futureRecord"]!.GetValue<string>() == "محفوظ", "issuance append preserves unknown ledger and prior record fields");
        var beforeBad = store.GetStatus().RevisionId; Reject(() => store.ImportTransferLetterReferences([1, 2, 3], beforeBad), "invalid archive rejected"); Check(store.GetStatus().RevisionId == beforeBad, "failed reference import is atomic");
        Check(LocalPayrollStore.ValidateDatabase(store.DatabasePath).RevisionId == beforeBad, "all letter history and chunks validate");
    }
    private static byte[] References()
    {
        var entries = LocalTransferLetterSettings.Choices.Select(c => (c.Id, Pages: LocalTransferLetterSettings.IsGroup(c.Id) ? 2 : 1)).Select(c => (c.Id, c.Pages, Bytes: Pdf(c.Id, c.Pages))).ToArray();
        using var output = new MemoryStream(); using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            using (var stream = zip.CreateEntry("manifest.json").Open()) JsonSerializer.Serialize(stream, new { version = 1, templates = entries.Select(e => new { id = e.Id, path = "templates/" + e.Id + ".pdf", pages = e.Pages, sha256 = Convert.ToHexString(SHA256.HashData(e.Bytes)) }) });
            foreach (var e in entries) { using var stream = zip.CreateEntry("templates/" + e.Id + ".pdf").Open(); stream.Write(e.Bytes); }
        }
        return output.ToArray();
    }
    private static void AtomicRollback(string root)
    {
        using var store = new LocalPayrollStore(Path.Combine(root, "rollback")); var source = Fixture();
        store.ImportDataset(source, [], new Dictionary<string, byte[]>(), store.GetStatus().RevisionId);
        var revision = store.GetStatus().RevisionId; var before = store.GetSnapshot().SettingsPlists[LocalTransferLetterSettings.SettingsKey].ToArray();
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString()))
        {
            c.Open(); using var command = c.CreateCommand(); command.CommandText = "CREATE TRIGGER fail_letter_revision BEFORE INSERT ON revisions BEGIN SELECT RAISE(ABORT, 'synthetic failure'); END"; command.ExecuteNonQuery();
        }
        bool failed = false;
        try { store.SaveTransferLetterSettings("rafidain_group", Settings with { First = "تغيير لن يُحفظ" }, false, revision); } catch (SqliteException) { failed = true; }
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString()))
        { c.Open(); using var command = c.CreateCommand(); command.CommandText = "DROP TRIGGER fail_letter_revision"; command.ExecuteNonQuery(); }
        Check(failed && store.GetStatus().RevisionId == revision && before.SequenceEqual(store.GetSnapshot().SettingsPlists[LocalTransferLetterSettings.SettingsKey]), "late settings failure rolls back bytes and revision");
        Check(LocalPayrollStore.ValidateDatabase(store.DatabasePath).RevisionId == revision, "rollback leaves no orphan auxiliary data");
    }
    private static byte[] Pdf(string id, int pages)
    {
        var (x, top) = id switch { "housing" => (212, 312), "water" => (387, 346), "ramadi" => (387, 362), "rafidain_anbar" => (162, 312), "contracts" => (127, 347), "tax_social" => (99, 347), "rafidain_group" or "other_banks_group" => (387, 350), _ => (387, 363) };
        // Courier ثابت العرض. النص كله وهمي، بنفس مستطيلات قراءة المراجع الأصلية، دون خطوط خاصة.
        string Draw(int px, int py, string value) => $"BT /F1 10 Tf {px} {842 - py} Td ({value}) Tj ET\n";
        var content = Draw(x, top, Payer);
        if (!LocalTransferLetterSettings.IsGroup(id) && id != "rafidain_anbar") content += id switch {
            "housing" => Draw(141, 328, Receiver[^15..]), "water" => Draw(41, 362, Receiver[^15..]), "ramadi" => Draw(335, 378, Receiver),
            _ => Draw(80, 405, "(" + Receiver + ")") };
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", $"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, pages).Select(p => $"{4 + p * 2} 0 R"))}] /Count {pages} >>", "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>" };
        for (int i = 0; i < pages; i++) { var commands = i == 0 ? content : ""; objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + i * 2} 0 R >>"); objects.Add($"<< /Length {commands.Length} >>\nstream\n{commands}endstream"); }
        var text = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int>(); for (int i = 0; i < objects.Count; i++) { offsets.Add(text.Length); text.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        int start = text.Length; text.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n"); foreach (var offset in offsets) text.Append(offset.ToString("0000000000", CultureInfo.InvariantCulture) + " 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{start}\n%%EOF\n"); return Encoding.ASCII.GetBytes(text.ToString());
    }
}
