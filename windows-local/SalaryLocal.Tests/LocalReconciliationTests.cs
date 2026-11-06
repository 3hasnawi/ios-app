using System.Text.Json;
using System.Text.Json.Nodes;
using Claunia.PropertyList;
using SalaryDesktop.Core;
using SalaryLocal.Core;

internal static class LocalReconciliationTests
{
    private static int checks;
    private static void Check(bool value, string message)
    { checks++; if (!value) throw new Exception("Local reconciliation: " + message); }
    private static void Reject(Action action, string message)
    { try { action(); } catch (InvalidDataException) { checks++; return; } throw new Exception(message); }
    private static JsonObject Decode(byte[] plist) => JsonNode.Parse(((NSData)PropertyListParser.Parse(plist)).Bytes)!.AsObject();
    private static byte[] Encode(JsonObject body)
    { using var stream = new MemoryStream(); PropertyListParser.SaveAsBinary(new NSData(JsonSerializer.SerializeToUtf8Bytes(body)), stream); return stream.ToArray(); }
    private static BackupSnapshot Source(byte[] plist) => new("synthetic", "", 6, [], [], [], 0, [], [],
        new Dictionary<string, byte[]> { [BankReconciliation.SettingsKey] = plist });
    private static JsonObject Period(JsonObject body, int index = 0) => body["periods"]![index]!.AsObject();

    public static void Run()
    {
        var movement = new BankMovement(Guid.NewGuid(), new(2026, 11, 3), "حركة وهمية", 25, MovementDirection.Credit);
        var period = new BankPeriod(Guid.NewGuid(), "تشرين الثاني 2026", new(2026, 11, 1), new(2026, 11, 30), 0, "", "", null, null, [movement]);
        var other = period with { Id = Guid.NewGuid(), Month = "فترة وهمية أخرى", Movements = [] };
        var body = Decode(BankReconciliation.LedgerPlist([period, other]));
        body["futureLedger"] = JsonNode.Parse("{\"precise\":123456789012345678901234567890.123456789}");
        var raw = Period(body); var rawMovement = raw["movements"]![0]!.AsObject();
        raw["futurePeriod"] = JsonNode.Parse("{\"value\":0.123456789012345678901234567890123456789}");
        rawMovement["futureMovement"] = JsonNode.Parse("{\"value\":999999999999999999999999999999}");
        // Non-midday subsecond timestamps must survive unrelated edits verbatim.
        foreach (var key in new[] { "start", "end" }) raw[key] = JsonNode.Parse((raw[key]!.GetValue<double>() + 123).ToString(System.Globalization.CultureInfo.InvariantCulture) + ".123456789");
        rawMovement["date"] = JsonNode.Parse((rawMovement["date"]!.GetValue<double>() + 321).ToString(System.Globalization.CultureInfo.InvariantCulture) + ".987654321");
        raw.Remove("openingBalance"); raw.Remove("memberFirst"); raw["memberSecond"] = null;
        rawMovement.Remove("isRefund"); rawMovement["sourceID"] = null;
        var originalBytes = Encode(body); var source = Source(originalBytes); var read = LocalReconciliation.Read(source);
        Check(read[0].OpeningBalance == 0 && read[0].FirstMember == "" && read[0].SecondMember == "", "Swift optional defaults");
        var updated = read[0] with { Movements = [read[0].Movements[0] with { Matched = true }] };
        var patched = LocalReconciliation.Patch(source, updated); var actual = Decode(patched);
        var expected = (JsonObject)body.DeepClone(); Period(expected)["movements"]![0]!["matched"] = true;
        Check(JsonNode.DeepEquals(expected, actual), "update changed only requested movement flag, preserving unknown fields/nulls/absence");
        foreach (var key in new[] { "start", "end" }) Check(Period(actual)[key]!.ToJsonString() == raw[key]!.ToJsonString(), "exact original " + key);
        Check(Period(actual)["movements"]![0]!["date"]!.ToJsonString() == rawMovement["date"]!.ToJsonString(), "exact original movement timestamp");
        Check(source.SettingsPlists[BankReconciliation.SettingsKey].SequenceEqual(originalBytes), "source bytes were not mutated");
        Check(BankReconciliation.Same(LocalReconciliation.Read(Source(patched))[0], updated), "updated modeled values reread");

        var added = period with { Id = Guid.NewGuid(), Month = "فترة وهمية جديدة", Movements = [] };
        var appended = Decode(LocalReconciliation.Patch(source, added, add: true));
        Check(appended["periods"]!.AsArray().Count == 3 && JsonNode.DeepEquals(Period(appended), raw) && JsonNode.DeepEquals(Period(appended, 1), Period(body, 1)), "add preserves both existing periods");
        Check(JsonNode.DeepEquals(appended["futureLedger"], body["futureLedger"]), "add preserves ledger unknown fields");
        Reject(() => LocalReconciliation.Patch(source, read[0], add: true), "duplicate period add accepted");
        Reject(() => LocalReconciliation.Patch(source, added), "missing period update accepted");

        var confirmed = BankReconciliation.Confirm(updated, new DateTime(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc));
        var confirmedBytes = LocalReconciliation.Patch(Source(patched), confirmed); var locked = Source(confirmedBytes);
        Check(LocalReconciliation.Read(locked)[0].IsConfirmed, "confirmation persisted");
        Reject(() => LocalReconciliation.Patch(locked, confirmed with { FirstMember = "تغيير وهمي" }), "confirmed edit accepted");
        Reject(() => LocalReconciliation.Patch(locked, BankReconciliation.Unconfirm(confirmed) with { FirstMember = "تغيير وهمي" }), "unconfirm plus edit accepted");
        var unlockedBytes = LocalReconciliation.Patch(locked, BankReconciliation.Unconfirm(confirmed));
        Check(JsonNode.DeepEquals(Decode(unlockedBytes), actual), "separate unconfirm changes only confirmation");
        var unlocked = Source(unlockedBytes); var editable = LocalReconciliation.Read(unlocked)[0] with { FirstMember = "عضو وهمي" };
        Check(LocalReconciliation.Read(Source(LocalReconciliation.Patch(unlocked, editable)))[0].FirstMember == "عضو وهمي", "edit allowed after separate unconfirm");
        Check(locked.SettingsPlists[BankReconciliation.SettingsKey].SequenceEqual(confirmedBytes), "rejected edits leave locked source intact");
        var empty = new BackupSnapshot("synthetic", "", 6, [], [], [], 0, [], [], new Dictionary<string, byte[]>());
        Check(LocalReconciliation.Read(Source(LocalReconciliation.Patch(empty, added, add: true))).Count == 1, "add to new ledger");
        Console.WriteLine($"LocalReconciliationTests: {checks} assertions passed.");
    }
}
