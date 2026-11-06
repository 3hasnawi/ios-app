using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Claunia.PropertyList;
using SalaryDesktop.Core;
using SalaryLocal.Core;

internal static class LocalTransferRecipientsTests
{
    private const string Month = "تشرين الثاني 2026";
    private static int checks;
    private static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception("Recipient/issuance: " + why); }
    private static void Reject(Action action, string why) { try { action(); } catch (InvalidDataException) { checks++; return; } throw new Exception("Accepted: " + why); }
    // نفس الحزمة الوهمية للمراجع، دون نسخ نماذج حقيقية أو تعديل اختبار عامل آخر.
    private static BackupSnapshot Fixture() => (BackupSnapshot)typeof(LocalTransferLettersTests).GetMethod("Fixture", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
    private static JsonObject Read(byte[] plist) => JsonNode.Parse(((NSData)PropertyListParser.Parse(plist)).Bytes)!.AsObject();
    private static byte[] Write(JsonObject value) { using var ms = new MemoryStream(); PropertyListParser.SaveAsBinary(new NSData(JsonSerializer.SerializeToUtf8Bytes(value)), ms); return ms.ToArray(); }
    public static void Run()
    {
        Check(LocalTransferIssuance.ExactAmount(JsonValue.Create(9_007_199_254_740_991L)) == 9_007_199_254_740_991m, "Int64 amount is exact");
        Check(LocalTransferIssuance.ExactAmount(JsonValue.Create(0.1234567890123456789012345678m)) == 0.1234567890123456789012345678m, "decimal amount is exact");
        Check(LocalTransferIssuance.ExactAmount(JsonNode.Parse("9007199254740990.5")) == 9007199254740990.5m, "parsed JSON amount is exact");
        Reject(() => LocalTransferIssuance.ExactAmount(JsonValue.Create("1000")), "text is not a numeric amount");
        Reject(() => LocalTransferIssuance.ExactAmount(JsonValue.Create(1000d)), "binary floating point not accepted");
        var root = Path.Combine(Path.GetTempPath(), "recipient-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new LocalPayrollStore(root); store.ImportDataset(Fixture(), [], new Dictionary<string, byte[]>(), store.GetStatus().RevisionId);
            var source = store.GetSnapshot(Month);
            var review = LocalTransferRecipients.Review(source, Month, "cash_salary", null);
            Reject(() => LocalTransferRecipients.Patch(source, review, "guess"), "invalid IBAN");
            Reject(() => LocalTransferRecipients.Patch(source, review, "IQ76TEST123000000000002"), "payer cannot be beneficiary");
            Reject(() => LocalTransferRecipients.Review(source, Month, "rafidain_group", null), "group override");
            var old = Read(source.SettingsPlists[LocalTransferLetterSettings.SettingsKey]);
            store.SaveTransferRecipient(review, review.Reference);
            var after = store.GetSnapshot(Month);
            Check(IndividualAccountCustomization.Read(after, "cash_salary")!.Value == review.Reference, "saved verified account");
            Check(JsonNode.DeepEquals(old["futureRoot"], Read(after.SettingsPlists[LocalTransferLetterSettings.SettingsKey])["futureRoot"]), "precise unknown fields preserved");
            Reject(() => store.SaveTransferRecipient(review, null), "stale settings");
            review = LocalTransferRecipients.Review(after, Month, "cash_salary", null); store.SaveTransferRecipient(review, null);
            source = store.GetSnapshot(Month);
            Check(IndividualAccountCustomization.Read(source, "cash_salary") is null, "reset does not inherit common account");
            var preview = LocalTransferLetters.Prepare(source, Month, "cash_salary", null, new(2026, 11, 5));
            store.ConfirmTransferLetterSingle(preview, "إقرار تجريبي فقط");
            Reject(() => store.ConfirmTransferLetterSingle(preview, ""), "stale duplicate click");
            source = store.GetSnapshot(Month); var records = LocalTransferIssuance.Records(source); Check(records.Count == 1 && records[0].Total == 1000, "single saved");
            var json = Read(source.SettingsPlists[GroupIssuance.SettingsKey]); var line = json["records"]![0]!["lines"]![0]!;
            Check(line["id"]!.GetValue<string>() == "payroll|" + Month + "|cashSalary" && line["windowsDetailHash"] is not null, "iPhone identity and local names hash");
            json["future"] = JsonNode.Parse("{\"precise\":123456789012345678901234567890.123456789}");
            json["records"]![0]!["futureRecord"] = "preserved";
            store.SaveSettings(new Dictionary<string, byte[]> { [GroupIssuance.SettingsKey] = Write(json) }, source.SourceDigest, "synthetic");
            source = store.GetSnapshot(Month); preview = LocalTransferLetters.Prepare(source, Month, "tax_social", null, new(2026, 11, 5));
            store.ConfirmTransferLetterSingle(preview, "");
            source = store.GetSnapshot(Month); var final = Read(source.SettingsPlists[GroupIssuance.SettingsKey]);
            Check(JsonNode.DeepEquals(json["future"], final["future"]) && final["records"]![0]!["futureRecord"]!.GetValue<string>() == "preserved", "append preserves old fields");
            Check(final["records"]![1]!["lines"]!.AsArray().Count == 2, "tax social split preserved");
            preview = LocalTransferLetters.Prepare(source, Month, "cash_salary", null, new(2026, 11, 5));
            Check(LocalTransferIssuance.Review(source, preview).Changes.Any(c => c.Contains("تكرر")), "duplicate explicitly warned");
            var renamed = source with { Employees = [source.Employees[0] with { Workplace = "مكان وهمي آخر" }] };
            var changedPreview = LocalTransferLetters.Prepare(renamed, Month, "cash_salary", null, new(2026, 11, 5));
            Check(LocalTransferIssuance.Review(renamed, changedPreview).Changes.Any(c => c.Contains("قائمة الأسماء")), "same-total name details change detected");
            var before = source.SourceDigest; store.RevokeTransferIssuance(records[0].Id, before);
            Reject(() => store.RevokeTransferIssuance(records[0].Id, before), "stale revoke");
            source = store.GetSnapshot(Month); Check(LocalTransferIssuance.Records(source).Count == 1 && source.Employees.Count == 1, "revoke only removes chosen confirmation");
            Check(JsonNode.DeepEquals(json["future"], Read(source.SettingsPlists[GroupIssuance.SettingsKey])["future"]), "revoke preserves unknown decimal");
            var malformed = source with { SettingsPlists = new Dictionary<string, byte[]>(source.SettingsPlists) { [GroupIssuance.SettingsKey] = Write(new JsonObject()) } };
            Reject(() => LocalTransferIssuance.Records(malformed), "malformed ledger never reset");
            var current = source.SourceDigest;
            preview = LocalTransferLetters.Prepare(source with { Employees = [source.Employees[0] with { Net = 1100 }] }, Month, "cash_salary", null, new(2026, 11, 5));
            Reject(() => store.ConfirmTransferLetterSingle(preview, ""), "same-revision forged data rejected");
            Check(store.GetStatus().RevisionId == current, "failed confirmation atomic");
            Console.WriteLine($"LocalTransferRecipientsTests: {checks} assertions passed.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
