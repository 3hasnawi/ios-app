using System.Text.Json;
using System.Text.Json.Nodes;
using Claunia.PropertyList;
using SalaryDesktop.Core;
namespace SalaryLocal.Core;

/// Local edits never recreate the typed ledger: unknown fields and unchanged values survive.
public static class LocalSalaryTrust
{
    private static bool HasId(JsonNode? node) => node is JsonObject obj && obj["id"] is JsonValue value && value.TryGetValue<string>(out var text) && Guid.TryParse(text, out _);
    private static Guid Id(JsonNode? node) => node is JsonObject obj && obj["id"] is JsonValue value && value.TryGetValue<string>(out var text) && Guid.TryParse(text, out var id) ? id : Guid.Empty;
    private static JsonNode? ApplyChanges(JsonNode? original, JsonNode? before, JsonNode? after)
    {
        if (JsonNode.DeepEquals(before, after)) return original?.DeepClone();
        if (before is JsonObject oldObject && after is JsonObject newObject)
        {
            var result = original is JsonObject actual ? (JsonObject)actual.DeepClone() : new JsonObject();
            foreach (var key in oldObject.Select(p => p.Key).Concat(newObject.Select(p => p.Key)).Distinct(StringComparer.Ordinal))
            {
                var had = oldObject.TryGetPropertyValue(key, out var oldValue); var has = newObject.TryGetPropertyValue(key, out var newValue);
                // إبقاء الغياب وnull والتمثيل العددي الأصلي عندما لم تتغير القيمة النموذجية.
                if (had == has && JsonNode.DeepEquals(oldValue, newValue)) continue;
                if (!has) result.Remove(key);
                else result[key] = had ? ApplyChanges(result[key], oldValue, newValue) : newValue?.DeepClone();
            }
            return result;
        }
        if (before is JsonArray oldArray && after is JsonArray newArray && oldArray.All(HasId) && newArray.All(HasId))
        {
            var actual = original as JsonArray; var result = new JsonArray();
            var oldById = oldArray.ToDictionary(Id); var actualById = actual?.Where(HasId).ToDictionary(Id) ?? new Dictionary<Guid, JsonNode?>();
            foreach (var node in newArray)
            {
                var id = Id(node); oldById.TryGetValue(id, out var old); actualById.TryGetValue(id, out var raw);
                result.Add(old is null ? node?.DeepClone() : ApplyChanges(raw, old, node));
            }
            return result;
        }
        return after?.DeepClone();
    }
    public static byte[] PatchOpeningImport(BackupSnapshot source, SalaryTrustLedger updated)
    {
        var before = SalaryTrust.ReadFromBackup(source);
        SalaryTrust.Validate(updated);
        if (before.OpeningImported || !updated.OpeningImported || updated.Version != before.Version || updated.Entries.Count <= before.Entries.Count)
            throw new InvalidDataException("لا يمكن استيراد الرصيد الافتتاحي بهذه الحالة.");
        if (updated.Entries.Take(before.Entries.Count).Where((e, i) => !SalaryTrust.Encode(new(1, [e], false)).SequenceEqual(SalaryTrust.Encode(new(1, [before.Entries[i]], false)))).Any())
            throw new InvalidDataException("الاستيراد لا يغيّر القيود الموجودة.");
        if (updated.Entries.Skip(before.Entries.Count).Any(e => e.Year * 100 + e.Month > SalaryTrust.OpeningCutoff || e.Payments.Count != 0 || e.BookIds is { Count: > 0 }))
            throw new InvalidDataException("قيود الرصيد الافتتاحي يجب أن تكون حتى أيلول 2026 دون دفعات أو روابط كتب جديدة.");
        SalaryTrust.Validate(updated); SalaryTrust.ValidateBooks(updated, source.Books);
        return PatchLedger(source, before, updated);
    }
    internal static byte[] PatchLedger(BackupSnapshot source, SalaryTrustLedger before, SalaryTrustLedger after)
    {
        var beforeModel = JsonNode.Parse(SalaryTrust.Encode(before))!;
        var afterModel = JsonNode.Parse(SalaryTrust.Encode(after))!;
        source.SettingsPlists.TryGetValue(SalaryTrust.SettingsKey, out var original);
        if (original is not null && JsonNode.DeepEquals(beforeModel, afterModel)) return original.ToArray();
        var rawJson = original is null ? null : ((NSData)PropertyListParser.Parse(original)).Bytes;
        var body = rawJson is null || rawJson.Length == 0 ? beforeModel : JsonNode.Parse(rawJson)!;
        var patched = ApplyChanges(body, beforeModel, afterModel)!;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(patched);
        var verified = SalaryTrust.Decode(bytes); SalaryTrust.ValidateBooks(verified, source.Books);
        if (!SalaryTrust.Encode(verified).SequenceEqual(SalaryTrust.Encode(after))) throw new InvalidDataException("تعذّر التحقق من دفتر الأمانات.");
        using var stream = new MemoryStream(); PropertyListParser.SaveAsBinary(new NSData(bytes), stream); return stream.ToArray();
    }
    public static byte[] Patch(BackupSnapshot source, SalaryTrustEntry updated)
    {
        var ledger = SalaryTrust.ReadFromBackup(source);
        var existing = ledger.Entries.SingleOrDefault(e => e.Id == updated.Id)
            ?? throw new InvalidDataException("القيد غير موجود؛ أعد فتح دفتر الأمانات.");
        // This workflow cannot create entries or change original financial identity/status.
        if (existing.Name != updated.Name || existing.Rank != updated.Rank || existing.Workplace != updated.Workplace ||
            existing.Year != updated.Year || existing.Month != updated.Month || existing.Amount != updated.Amount ||
            existing.CaseStatus != updated.CaseStatus || existing.ImportedStatus != updated.ImportedStatus || existing.LegacyPaid != updated.LegacyPaid)
            throw new InvalidDataException("لا يُسمح بتغيير أصل الأمانة أو مبلغها أو حالة الصرف القديم هنا.");
        if (existing.ReviewPending != updated.ReviewPending && !existing.Payments.SequenceEqual(updated.Payments))
            throw new InvalidDataException("احفظ قرار المراجعة أولاً، ثم أعد فتح القيد لتسجيل الدفعة.");
        var after = ledger with { Entries = ledger.Entries.Select(e => e.Id == updated.Id ? updated : e).ToArray() };
        SalaryTrust.Validate(after); SalaryTrust.ValidateBooks(after, source.Books);
        return PatchLedger(source, ledger, after);
    }
}
