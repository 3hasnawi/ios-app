using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Claunia.PropertyList;
using Microsoft.Data.Sqlite;
using SalaryDesktop.Core;
using SalaryLocal.Core;

internal static class LocalMonthlyCashTrustTests
{
    private const string Month = "تشرين الثاني 2026";
    private static int checks;
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception("Monthly cash trust: " + message); }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (InvalidDataException ex) { Check(!string.IsNullOrWhiteSpace(ex.Message), message + " gives a reason"); return; }
        throw new Exception("Monthly cash trust accepted: " + message);
    }
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    private static JsonNode Decode(byte[] bytes) => JsonNode.Parse(((NSData)PropertyListParser.Parse(bytes)).Bytes)!;
    private static byte[] Plist(JsonNode node)
    { using var stream = new MemoryStream(); PropertyListParser.SaveAsBinary(new NSData(JsonSerializer.SerializeToUtf8Bytes(node)), stream); return stream.ToArray(); }
    private static Employee Person(string name, decimal amount, string payment = "كاش", string status = "") => new(Month, name,
        "شرطي", "جهة وهمية", amount, null, JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["طريقة الدفع"] = payment }),
        JsonSerializer.SerializeToUtf8Bytes(new { headers = new[] { "اسم", "صافي الراتب", "ملاحظات4", "حقل وهمي غير معروف" }, values = new[] { name, amount.ToString(CultureInfo.InvariantCulture), status, "" }, future = new { keep = true } }));
    private static BackupSnapshot Fixture(bool existingCash = true)
    {
        var book = new ArchiveBook(Guid.Parse("A0A0A0A0-B0B0-C0C0-D0D0-E0E0E0E0E0E0"), "كتاب وهمي", Month, 1, null, 100);
        var old = new SalaryTrustEntry(Guid.Empty.ToString(), "منتسب افتتاحي وهمي", "", "", 2026, 9, 300, "", "ملاحظة سابقة", "صرف", true, false, []);
        var existing = new SalaryTrustEntry(Guid.NewGuid().ToString(), "منتسب نقدي وهمي أول", "رتبة سابقة", "جهة سابقة", 2026, 11, 100,
            "موقوف", "ملاحظة محفوظة", "", false, false, [new(Guid.NewGuid().ToString(), 25, "", "", "دفعة سابقة")], [book.Id.ToString()]);
        var ledger = new SalaryTrustLedger(1, existingCash ? [old, existing] : [old], true);
        var raw = Decode(SalaryTrust.Plist(ledger));
        raw["future"] = JsonNode.Parse("{\"precise\":123456789012345678901234567890.123456789,\"nothing\":null}");
        raw["entries"]![0]!["future"] = "حقل افتتاحي محفوظ"; raw["entries"]![0]!.AsObject().Remove("bookIds");
        if (existingCash)
        {
            raw["entries"]![1]!["future"] = JsonNode.Parse("{\"array\":[null,1.0000000000000000000000001]}");
            raw["entries"]![1]!["payments"]![0]!["futurePayment"] = "يُحفظ";
        }
        Employee[] employees = [Person("منتسب نقدي وهمي أول", 100, "كاش", "موقوف"), Person("منتسب نقدي وهمي ثان", 200, "نقدي", "هارب"),
            Person("منتسب بطاقة وهمي", 500, "بطاقة"), Person("منتسب صفري وهمي", 0)];
        return new("synthetic", new string('A', 64), 6, [new(Month, employees.Length, 0)], employees, [], 0, [], [book],
            new Dictionary<string, byte[]> { [SalaryTrust.SettingsKey] = Plist(raw), ["unknownSetting"] = Plist(JsonNode.Parse("{\"unchanged\":true}")!) });
    }
    private static BackupSnapshot Employees(BackupSnapshot source, params Employee[] employees) => source with
    { Employees = employees, Months = [new(Month, employees.Length, source.Deductions.Count)] };
    private static BackupSnapshot Ledger(BackupSnapshot source, SalaryTrustLedger ledger) => source with
    { SettingsPlists = new Dictionary<string, byte[]>(source.SettingsPlists) { [SalaryTrust.SettingsKey] = SalaryTrust.Plist(ledger) } };
    private static void Populate(LocalPayrollStore store, BackupSnapshot source) => store.ImportDataset(source, [],
        source.Books.ToDictionary(b => LocalPayrollStore.ArchiveAttachmentKey(b.Id), _ => Pdf()), store.GetStatus().RevisionId);

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "monthly-cash-trust-synthetic-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            Preparation(); StoreAndRoundtrips(root); MultipleMonths(root); StaleAndChangedSources(root); AtomicRollback(root); OversizedLedger(root); ConcurrentSave(root);
            Console.WriteLine($"LocalMonthlyCashTrustTests: {checks} assertions passed.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Preparation()
    {
        var source = Fixture(); var bytes = source.SettingsPlists[SalaryTrust.SettingsKey].ToArray();
        var review = LocalMonthlyCashTrust.Prepare(source, Month);
        Check(review.Month == Month && review.SourceRevision == source.SourceDigest, "review preserves exact month and global revision");
        Check(review.Count == 1 && review.Total == 200 && review.ExistingCount == 1 && review.CashCount == 2 && review.CashTotal == 300 && !review.IsNoOp, "review separates additions and prior matching cash");
        var entry = review.Additions.Single();
        Check(entry.Name == source.Employees[1].Name && entry.Amount == 200 && entry.CaseStatus == "هارب" && entry.Year == 2026 && entry.Month == 11, "addition comes from typed cash source and adjacent status");
        Check(!entry.LegacyPaid && !entry.ReviewPending && entry.Payments.Count == 0 && entry.BookIds is null && entry.Note == "" && entry.ImportedStatus == "", "review creates no payment or book action");
        Check(bytes.SequenceEqual(source.SettingsPlists[SalaryTrust.SettingsKey]), "preparation never writes original ledger");
        Check(((IList<SalaryTrustEntry>)review.Additions).IsReadOnly && ((IList<SalaryTrustPayment>)entry.Payments).IsReadOnly, "review lists cannot be edited");
        Reject(() => LocalMonthlyCashTrust.Prepare(source, "2026-11"), "exact source month name required");
        Reject(() => LocalMonthlyCashTrust.Prepare(source with { Months = [source.Months[0], new("2026-11", 0, 0)] }, Month), "canonical period aliases are ambiguous");
        Reject(() => LocalMonthlyCashTrust.Prepare(source with { Months = [source.Months[0], source.Months[0]] }, Month), "duplicate month metadata");
        Reject(() => LocalMonthlyCashTrust.Prepare(source with { SourceDigest = "BAD" }, Month), "invalid revision");
        Reject(() => LocalMonthlyCashTrust.Prepare(source, "غير معروف"), "unrecognized period");
        Reject(() => LocalMonthlyCashTrust.Prepare(source, "أيلول 2026"), "opening cutoff inclusive");
        Reject(() => LocalMonthlyCashTrust.Prepare(source, "آب 2026"), "pre-opening period");
        Reject(() => LocalMonthlyCashTrust.Prepare(source with { Months = [] }, Month), "missing month");
        Reject(() => LocalMonthlyCashTrust.Prepare(source with { Months = [new(Month, 999, 0)] }, Month), "incomplete payroll count");
        var ledger = SalaryTrust.ReadFromBackup(source);
        Reject(() => LocalMonthlyCashTrust.Prepare(Ledger(source, ledger with { OpeningImported = false }), Month), "opening review required");
        Reject(() => LocalMonthlyCashTrust.Prepare(source with { SettingsPlists = new Dictionary<string, byte[]>() }, Month), "missing opening ledger");
        Reject(() => LocalMonthlyCashTrust.Prepare(source with { Books = [] }, Month), "dangling prior book prevents intake");
        Reject(() => LocalMonthlyCashTrust.Prepare(Employees(source), Month), "empty payroll");
        Reject(() => LocalMonthlyCashTrust.Prepare(Employees(Fixture(false), Person("بطاقة وهمية", 100, "بطاقة")), Month), "card-only month cannot create cash trust");
        Reject(() => LocalMonthlyCashTrust.Prepare(Employees(Fixture(false), Person("صفري وهمي", 0)), Month), "zero-only month cannot create cash trust");

        var first = source.Employees[0]; var second = source.Employees[1];
        var invalidPeople = new[] {
            first with { Net = null }, first with { Net = -1 }, first with { Net = 100.5m }, first with { Net = SalaryTrust.Maximum + 1m },
            first with { Name = "" }, first with { Rank = "" }, first with { Workplace = "" }, first with { Name = null! }, first with { Rank = null! }, first with { Workplace = null! },
            first with { Details = null }, first with { Details = Bytes("null") }, first with { Details = Bytes("[]") }, first with { Details = Bytes("{") },
            first with { Details = Bytes("{\"طريقة الدفع\":\"بطاقة\",\"طريقة الدفع\":\"كاش\"}") },
            first with { Details = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string,string?> { ["طريقة الدفع"] = null }) },
            Person(first.Name, 100, "غير مصنف", "موقوف"), Person(first.Name, 100, "كاش وبطاقة", "موقوف"),
            first with { RawColumns = null }, first with { RawColumns = Bytes("null") }, first with { RawColumns = Bytes("[]") }, first with { RawColumns = Bytes("{") },
            first with { RawColumns = Bytes("{\"headers\":[\"صافي الراتب\"],\"values\":[\"100\"]}") },
            first with { RawColumns = JsonSerializer.SerializeToUtf8Bytes(new { headers = new[] { "صافي الراتب", "الحالة" }, values = new[] { "100", "موقوف" } }) },
            first with { RawColumns = JsonSerializer.SerializeToUtf8Bytes(new { headers = new[] { "صافي الراتب", "ملاحظات", "صافي" }, values = new[] { "100", "موقوف", "100" } }) },
            first with { RawColumns = JsonSerializer.SerializeToUtf8Bytes(new { headers = new[] { "صافي الراتب", "ملاحظات" }, values = new[] { "100" } }) },
            first with { RawColumns = JsonSerializer.SerializeToUtf8Bytes(new { headers = new string?[] { "صافي الراتب", "ملاحظات" }, values = new string?[] { "100", null } }) }
        };
        foreach (var person in invalidPeople) Reject(() => LocalMonthlyCashTrust.Prepare(Employees(source, person, second), Month), "malformed or unsafe payroll member");
        Reject(() => LocalMonthlyCashTrust.Prepare(Employees(source, first, first with { Name = "منتسب نقدي وهمي أوّل" }, second), Month), "normalized duplicate cash person");
        Reject(() => LocalMonthlyCashTrust.Prepare(Employees(source, first with { Net = 101 }, second), Month), "existing amount changed");
        Reject(() => LocalMonthlyCashTrust.Prepare(Employees(source, Person(first.Name, 100, "كاش", "محكوم"), second), Month), "existing case status changed");
        Reject(() => LocalMonthlyCashTrust.Prepare(Employees(source, second), Month), "saved person disappeared");
        Reject(() => LocalMonthlyCashTrust.Prepare(Employees(source, Person(first.Name, 100, "بطاقة", "موقوف"), second), Month), "saved person changed to card");
        Reject(() => LocalMonthlyCashTrust.Prepare(Employees(source, first with { Net = 0 }, second), Month), "saved person changed to zero cash");
        var blankStatus = LocalMonthlyCashTrust.Prepare(Employees(Fixture(false), Person("حالة فارغة وهمية", 70)), Month);
        Check(blankStatus.Additions.Single().CaseStatus == "", "available but blank adjacent status is valid");
        var longStatus = Person("حالة طويلة وهمية", 70, "كاش", new string('x', 2001));
        Reject(() => LocalMonthlyCashTrust.Prepare(Employees(Fixture(false), longStatus), Month), "ledger status length validation");
        var maximum = Employees(Ledger(Fixture(false), new(1, [], true)), Person("حد مبلغ وهمي", SalaryTrust.Maximum));
        Check(LocalMonthlyCashTrust.Prepare(maximum, Month).Total == SalaryTrust.Maximum, "largest exact integer retained without rounding");
        Reject(() => LocalMonthlyCashTrust.Prepare(Employees(maximum, maximum.Employees[0], Person("مبلغ زائد وهمي", 1)), Month), "cash sum overflow of supported maximum");
        Reject(() => LocalMonthlyCashTrust.Prepare(Employees(Fixture(false), Person("رصيد زائد وهمي", SalaryTrust.Maximum)), Month), "whole ledger maximum includes old entries");
    }

    private static void StoreAndRoundtrips(string root)
    {
        using var store = new LocalPayrollStore(Path.Combine(root, "main")); Populate(store, Fixture());
        var source = store.GetSnapshot(); var before = Decode(source.SettingsPlists[SalaryTrust.SettingsKey]); var historyCount = store.GetHistory().Count;
        var preview = LocalMonthlyCashTrust.Prepare(source, Month); string reviewedId = preview.Additions.Single().Id;
        var result = store.SaveMonthlyCashTrust(preview); var after = store.GetSnapshot(); var raw = Decode(after.SettingsPlists[SalaryTrust.SettingsKey]);
        Check(result.Changed && after.SourceDigest == result.RevisionId && result.RevisionId != source.SourceDigest && store.GetHistory().Count == historyCount + 1, "intake saves exactly one global revision");
        Check(SalaryTrust.ReadFromBackup(after).Entries.Last().Id == reviewedId, "reviewed entry identity survives save-time recomputation");
        Check(JsonNode.DeepEquals(before["future"], raw["future"]), "unknown precise root fields survive");
        for (int i = 0; i < before["entries"]!.AsArray().Count; i++) Check(JsonNode.DeepEquals(before["entries"]![i], raw["entries"]![i]), "previous entry and payment stay lossless");
        Check(raw["entries"]![0]!["bookIds"] is null && !raw["entries"]![0]!.AsObject().ContainsKey("bookIds"), "absent optional prior fields remain absent");
        Check(SalaryTrust.ReadFromBackup(after).Entries[1].Payments.Single().Amount == 25 && SalaryTrust.ReadFromBackup(after).Entries[1].BookIds!.Single() == after.Books.Single().Id.ToString(), "existing payment and book association untouched");
        Check(JsonSerializer.Serialize(source.Employees) == JsonSerializer.Serialize(after.Employees), "payroll source is unchanged");
        Check(after.SettingsPlists["unknownSetting"].SequenceEqual(source.SettingsPlists["unknownSetting"]), "unrelated setting unchanged");
        Check(store.ReadAttachment(LocalPayrollStore.ArchiveAttachmentKey(after.Books.Single().Id)).SequenceEqual(Pdf()), "archive attachment unchanged");
        Reject(() => store.SaveMonthlyCashTrust(preview), "double-click uses stale review and cannot duplicate");
        var repeat = LocalMonthlyCashTrust.Prepare(after, Month); var exact = after.SettingsPlists[SalaryTrust.SettingsKey].ToArray(); var history = JsonSerializer.Serialize(store.GetHistory());
        Check(repeat.IsNoOp && repeat.Count == 0 && repeat.Total == 0 && repeat.ExistingCount == 2 && repeat.CashTotal == 300 && repeat.Message.Length > 0, "repeat review explains no-op");
        Check(!store.SaveMonthlyCashTrust(repeat).Changed && store.GetStatus().RevisionId == after.SourceDigest && history == JsonSerializer.Serialize(store.GetHistory()), "repeat no-op makes no revision");
        Check(exact.SequenceEqual(store.GetSnapshot().SettingsPlists[SalaryTrust.SettingsKey]), "repeat no-op keeps exact original plist");

        var native = Path.Combine(root, "cash.salarybackup"); var inspection = BackupService.Create(store, native);
        using var restored = new LocalPayrollStore(BackupService.RestoreAsNewWorkspace(native, Path.Combine(root, "native-restored"), inspection.Digest));
        Check(exact.SequenceEqual(restored.GetSnapshot().SettingsPlists[SalaryTrust.SettingsKey]) && history == JsonSerializer.Serialize(restored.GetHistory()), "native roundtrip retains exact ledger and full history");
        var export = Path.Combine(root, "cash.zip"); IphoneInteropService.Export(restored, export);
        Check(BackupReader.Read(export).SettingsPlists[SalaryTrust.SettingsKey].SequenceEqual(exact), "iPhone-compatible export retains exact ledger");
        var exported = IphoneInteropService.Inspect(export);
        using var reimported = new LocalPayrollStore(IphoneInteropService.ImportNewWorkspace(export, Path.Combine(root, "iphone-reimported"), exported.Digest));
        Check(reimported.GetSnapshot().SettingsPlists[SalaryTrust.SettingsKey].SequenceEqual(exact), "iPhone-format reimport retains lossless ledger");
        var noOp = LocalMonthlyCashTrust.Prepare(reimported.GetSnapshot(), Month);
        Check(noOp.IsNoOp && !reimported.SaveMonthlyCashTrust(noOp).Changed, "reimported source does not duplicate monthly cash");

        using var fresh = new LocalPayrollStore(Path.Combine(root, "all-new")); Populate(fresh, Fixture(false));
        var all = LocalMonthlyCashTrust.Prepare(fresh.GetSnapshot(), Month);
        Check(all.Count == 2 && all.Total == 300 && all.ExistingCount == 0, "all cash additions reviewed together");
        Check(fresh.SaveMonthlyCashTrust(all).Changed && SalaryTrust.ReadFromBackup(fresh.GetSnapshot()).Entries.Count == 3, "all cash additions committed as one batch");
    }

    private static void MultipleMonths(string root)
    {
        using var store = new LocalPayrollStore(Path.Combine(root, "multiple-months")); Populate(store, Fixture(false));
        var deduction = new Deduction(Month, "منتسب نقدي وهمي أول", "جهة تنفيذ وهمية", 15, "", "شرطي");
        store.Import(new(Month, [], [deduction], []), "synthetic-deductions.xlsx", new string('B', 64), store.GetStatus().RevisionId, false);
        foreach (string month in new[] { "2026-10", "2026-12", "2025-12" })
            store.Import(new(month, [Person("منتسب شهر آخر وهمي", 10) with { Month = month }], [deduction with { Month = month }], []),
                "synthetic-month.xlsx", new string('C', 64), store.GetStatus().RevisionId, false);
        var source = store.GetSnapshot(); var original = JsonSerializer.Serialize(new { source.Months, source.Employees, source.Deductions });
        Check(source.Months.Count == 4 && source.Deductions.Count == 4, "multi-month source includes independent deduction sections");
        var preview = LocalMonthlyCashTrust.Prepare(source, Month);
        Check(preview.Count == 2 && store.SaveMonthlyCashTrust(preview).Changed, "transaction reconstruction matches full multi-month snapshot review");
        var after = store.GetSnapshot();
        Check(original == JsonSerializer.Serialize(new { after.Months, after.Employees, after.Deductions }), "intake does not alter any month or deduction section");
        Check(LocalMonthlyCashTrust.Prepare(after, Month).IsNoOp, "repeat with all months and deductions is a no-op");
        var selected = LocalMonthlyCashTrust.Prepare(store.GetSnapshot(Month), Month);
        Check(selected.IsNoOp && !store.SaveMonthlyCashTrust(selected).Changed, "filtered snapshot agrees with transaction snapshot when unique");
        var october = LocalMonthlyCashTrust.Prepare(after, "2026-10");
        Check(october.Count == 1 && october.Additions.Single().Month == 10 && store.SaveMonthlyCashTrust(october).Changed, "earliest eligible October intake independent from November");
    }

    private static void StaleAndChangedSources(string root)
    {
        using var store = new LocalPayrollStore(Path.Combine(root, "changed")); Populate(store, Fixture());
        var snapshot = store.GetSnapshot(); var before = JsonSerializer.Serialize(store.GetHistory()); var ledgerBytes = snapshot.SettingsPlists[SalaryTrust.SettingsKey].ToArray();
        var original = snapshot.Employees.ToArray();
        var changed = original.Select((e, i) => i == 1 ? e with { Net = 201 } : e).ToArray();
        var forged = LocalMonthlyCashTrust.Prepare(Employees(snapshot, changed), Month);
        Reject(() => store.SaveMonthlyCashTrust(forged), "forged snapshot sharing current revision but changed amount");
        var changedRaw = original.Select((e, i) => i == 1 ? e with { RawColumns = Bytes(Encoding.UTF8.GetString(e.RawColumns!).Replace("true", "false", StringComparison.Ordinal)) } : e).ToArray();
        Reject(() => store.SaveMonthlyCashTrust(LocalMonthlyCashTrust.Prepare(Employees(snapshot, changedRaw), Month)), "same-revision source raw-field change");
        var changedCard = original.Select((e, i) => i == 2 ? e with { Net = 501 } : e).ToArray();
        Reject(() => store.SaveMonthlyCashTrust(LocalMonthlyCashTrust.Prepare(Employees(snapshot, changedCard), Month)), "same-revision card payroll change");
        var oldLedger = SalaryTrust.ReadFromBackup(snapshot);
        var changedLedger = oldLedger with { Entries = oldLedger.Entries.Select(e => e with { Note = "ملاحظة وهمية معدلة" }).ToArray() };
        Reject(() => store.SaveMonthlyCashTrust(LocalMonthlyCashTrust.Prepare(Ledger(snapshot, changedLedger), Month)), "same-revision ledger change");
        Check(before == JsonSerializer.Serialize(store.GetHistory()) && ledgerBytes.SequenceEqual(store.GetSnapshot().SettingsPlists[SalaryTrust.SettingsKey]), "forged reviews fail without writes");

        var preview = LocalMonthlyCashTrust.Prepare(snapshot, Month);
        store.Import(new(Month, changed, [], []), "changed-synthetic.xlsx", new string('B', 64), snapshot.SourceDigest, true);
        string changedRevision = store.GetStatus().RevisionId;
        Reject(() => store.SaveMonthlyCashTrust(preview), "payroll changed after review");
        Check(store.GetStatus().RevisionId == changedRevision && ledgerBytes.SequenceEqual(store.GetSnapshot().SettingsPlists[SalaryTrust.SettingsKey]), "stale payroll leaves ledger untouched");
        var current = LocalMonthlyCashTrust.Prepare(store.GetSnapshot(), Month);
        store.SaveSettings(new Dictionary<string, byte[]> { ["anotherSetting"] = Plist(JsonNode.Parse("[1]")!) }, changedRevision, "synthetic auxiliary change");
        var auxiliaryRevision = store.GetStatus().RevisionId;
        Reject(() => store.SaveMonthlyCashTrust(current), "unrelated change still invalidates global review");
        Check(store.GetStatus().RevisionId == auxiliaryRevision, "stale auxiliary review creates no revision");
        var review = LocalMonthlyCashTrust.Prepare(store.GetSnapshot(), Month); store.SaveMonthlyCashTrust(review);
        var noOp = LocalMonthlyCashTrust.Prepare(store.GetSnapshot(), Month);
        store.SaveSalaryTrustEntry(SalaryTrust.ReadFromBackup(store.GetSnapshot()).Entries[0] with { Note = "اختبار المراجعة القديمة" }, noOp.SourceRevision);
        Reject(() => store.SaveMonthlyCashTrust(noOp), "even no-op review checks global revision");
        var actual = store.GetSnapshot();
        var mutateSource = LocalMonthlyCashTrust.Prepare(actual, Month);
        actual.Employees[0].RawColumns![0] = (byte)'!';
        Check(!store.SaveMonthlyCashTrust(mutateSource).Changed, "mutating caller snapshot after preparation does not mutate captured review");
    }

    private static void AtomicRollback(string root)
    {
        using var store = new LocalPayrollStore(Path.Combine(root, "rollback")); Populate(store, Fixture(false));
        var snapshot = store.GetSnapshot(); var preview = LocalMonthlyCashTrust.Prepare(snapshot, Month);
        var counts = DatabaseCounts(store.DatabasePath); var history = JsonSerializer.Serialize(store.GetHistory());
        Sql(store.DatabasePath, "CREATE TRIGGER synthetic_abort BEFORE UPDATE ON metadata BEGIN SELECT RAISE(ABORT,'synthetic forced failure'); END;");
        try
        {
            try { store.SaveMonthlyCashTrust(preview); throw new Exception("Forced transaction failure was ignored"); }
            catch (SqliteException ex) { Check(ex.SqliteErrorCode == 19, "forced failure occurs during final revision pointer write"); }
        }
        finally { Sql(store.DatabasePath, "DROP TRIGGER synthetic_abort"); }
        Check(DatabaseCounts(store.DatabasePath) == counts, "failed save rolls back chunks, values, sets, entries and revisions");
        Check(history == JsonSerializer.Serialize(store.GetHistory()) && snapshot.SourceDigest == store.GetStatus().RevisionId, "failed save leaves history and global pointer untouched");
        Check(snapshot.SettingsPlists[SalaryTrust.SettingsKey].SequenceEqual(store.GetSnapshot().SettingsPlists[SalaryTrust.SettingsKey]), "failed batch keeps original ledger bytes");
        Check(LocalPayrollStore.ValidateDatabase(store.DatabasePath).RevisionId == snapshot.SourceDigest, "rollback leaves validated store");
        Check(store.SaveMonthlyCashTrust(preview).Changed, "same reviewed identities can be retried after definite rollback");
        Check(SalaryTrust.ReadFromBackup(store.GetSnapshot()).Entries.Skip(1).Select(e => e.Id).SequenceEqual(preview.Additions.Select(e => e.Id)), "rollback retry keeps both approved IDs");
    }

    private static void ConcurrentSave(string root)
    {
        using var store = new LocalPayrollStore(Path.Combine(root, "concurrent")); Populate(store, Fixture(false));
        var source = store.GetSnapshot(); var first = LocalMonthlyCashTrust.Prepare(source, Month); var second = LocalMonthlyCashTrust.Prepare(source, Month);
        using var ready = new Barrier(2); var outcomes = new ConcurrentBag<bool>();
        Task Save(MonthlyCashTrustPreview review) => Task.Run(() =>
        {
            ready.SignalAndWait();
            try { outcomes.Add(store.SaveMonthlyCashTrust(review).Changed); }
            catch (InvalidDataException) { outcomes.Add(false); }
        });
        Task.WaitAll(Save(first), Save(second));
        Check(outcomes.Count == 2 && outcomes.Count(x => x) == 1, "two reviewers cannot add the same person-month twice");
        var ledger = SalaryTrust.ReadFromBackup(store.GetSnapshot());
        Check(ledger.Entries.Count == 3 && ledger.Entries.Where(e => e.Month == 11).Sum(e => e.Amount) == 300, "concurrent batch is wholly committed once");
        Check(LocalPayrollStore.ValidateDatabase(store.DatabasePath).RevisionId == store.GetStatus().RevisionId, "concurrent save leaves valid database");
    }

    private static void OversizedLedger(string root)
    {
        const int maximum = 16 * 1024 * 1024;
        var fixture = Fixture(); var body = Decode(fixture.SettingsPlists[SalaryTrust.SettingsKey]); body["syntheticPadding"] = "";
        int baseSize = Plist(body).Length;
        body["syntheticPadding"] = new string('x', maximum - baseSize - 256);
        var original = Plist(body);
        Check(original.Length <= maximum && original.Length >= maximum - 512, "large synthetic source ledger is inside the existing setting limit");
        var employees = fixture.Employees.Select((e, i) => i == 1 ? Person(e.Name, 200, "كاش", new string('s', 2000)) : e).ToArray();
        var source = Employees(fixture, employees) with
        { SettingsPlists = new Dictionary<string, byte[]>(fixture.SettingsPlists) { [SalaryTrust.SettingsKey] = original } };
        using var store = new LocalPayrollStore(Path.Combine(root, "oversized")); Populate(store, source);
        var preview = LocalMonthlyCashTrust.Prepare(store.GetSnapshot(), Month); var counts = DatabaseCounts(store.DatabasePath); var history = JsonSerializer.Serialize(store.GetHistory());
        Check(preview.Count == 1 && preview.Additions[0].CaseStatus.Length == 2000, "otherwise valid reviewed addition would exceed setting limit");
        Reject(() => store.SaveMonthlyCashTrust(preview), "oversized patched ledger");
        Check(store.GetStatus().RevisionId == preview.SourceRevision && history == JsonSerializer.Serialize(store.GetHistory()) && DatabaseCounts(store.DatabasePath) == counts, "size rejection rolls back without new storage or revisions");
        Check(original.SequenceEqual(store.GetSnapshot().SettingsPlists[SalaryTrust.SettingsKey]), "size rejection preserves exact original large ledger");
    }

    private static void Sql(string path, string sql)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); c.Open();
        using var command = c.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    private static string DatabaseCounts(string path)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); c.Open();
        return string.Join("|", new[] { "auxiliary_chunks", "auxiliary_values", "auxiliary_sets", "auxiliary_entries", "auxiliary_revisions", "revisions" }.Select(table =>
        { using var command = c.CreateCommand(); command.CommandText = "SELECT count(*) FROM " + table; return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture); }));
    }
    private static byte[] Pdf()
    {
        var text = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int>();
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R >>", "<< /Length 0 >>\nstream\n\nendstream"];
        for (int i = 0; i < objects.Length; i++) { offsets.Add(text.Length); text.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        int xref = text.Length; text.Append("xref\n0 5\n0000000000 65535 f \n");
        foreach (int offset in offsets) text.Append(offset.ToString("0000000000", CultureInfo.InvariantCulture) + " 00000 n \n");
        text.Append($"trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"); return Encoding.ASCII.GetBytes(text.ToString());
    }
}
