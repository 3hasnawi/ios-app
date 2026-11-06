using System.Text.Json;
using System.Text.Json.Nodes;
using Claunia.PropertyList;
using SalaryDesktop.Core;
namespace SalaryLocal.Core;

/// قراءة توافقية وترقيع موضعي؛ لا يُعاد تطبيع تواريخ الفترات والحركات غير المعدلة.
public static class LocalReconciliation
{
    private static JsonObject Body(BackupSnapshot source)
    {
        if (!source.SettingsPlists.TryGetValue(BankReconciliation.SettingsKey, out var raw)) return new JsonObject { ["version"] = 1, ["periods"] = new JsonArray() };
        if (PropertyListParser.Parse(raw) is not NSData data) throw new InvalidDataException("صيغة دفتر المطابقة غير صالحة.");
        if (data.Bytes.Length == 0) return new JsonObject { ["version"] = 1, ["periods"] = new JsonArray() };
        if (data.Bytes.Length > 64_000_000) throw new InvalidDataException("دفتر المطابقة أكبر من الحد المدعوم.");
        return JsonNode.Parse(data.Bytes)?.AsObject() ?? throw new InvalidDataException("دفتر المطابقة غير صالح.");
    }
    private static byte[] Encode(JsonObject body)
    { using var stream = new MemoryStream(); PropertyListParser.SaveAsBinary(new NSData(JsonSerializer.SerializeToUtf8Bytes(body)), stream); return stream.ToArray(); }
    public static IReadOnlyList<BankPeriod> Read(BackupSnapshot source)
    {
        var copy = Body(source);
        if (copy["periods"] is not JsonArray periods) throw new InvalidDataException("فترات المطابقة غير صالحة.");
        foreach (var node in periods)
        {
            var period = node?.AsObject() ?? throw new InvalidDataException("فترة المطابقة غير صالحة.");
            period["openingBalance"] ??= JsonValue.Create(0L); period["memberFirst"] ??= JsonValue.Create(""); period["memberSecond"] ??= JsonValue.Create(""); period["movements"] ??= new JsonArray();
        }
        var settings = source.SettingsPlists.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        settings[BankReconciliation.SettingsKey] = Encode(copy);
        return BankReconciliation.ReadFromBackup(source with { SettingsPlists = settings });
    }
    private static JsonObject Modeled(BankPeriod period)
    {
        var raw = BankReconciliation.LedgerPlist([period]);
        if (PropertyListParser.Parse(raw) is not NSData data) throw new InvalidDataException("تعذّر ترميز الفترة.");
        return JsonNode.Parse(data.Bytes)!["periods"]![0]!.AsObject();
    }
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
        if (before is JsonArray oldArray && after is JsonArray newArray && oldArray.All(n => Id(n) != Guid.Empty) && newArray.All(n => Id(n) != Guid.Empty))
        {
            var actual = original as JsonArray; var result = new JsonArray();
            var oldById = oldArray.ToDictionary(Id); var actualById = actual?.Where(n => Id(n) != Guid.Empty).ToDictionary(Id) ?? new Dictionary<Guid, JsonNode?>();
            foreach (var node in newArray)
            {
                var id = Id(node); oldById.TryGetValue(id, out var old); actualById.TryGetValue(id, out var raw);
                result.Add(old is null ? node?.DeepClone() : ApplyChanges(raw, old, node));
            }
            return result;
        }
        return after?.DeepClone();
    }
    public static byte[] Patch(BackupSnapshot source, BankPeriod updated, bool add = false)
    {
        _ = BankReconciliation.Calculate(updated); var before = Read(source); var existing = before.SingleOrDefault(p => p.Id == updated.Id);
        if (add ? existing is not null : existing is null) throw new InvalidDataException("تغيّرت هوية الفترة؛ أعد فتح دفتر المطابقة.");
        var after = add ? before.Concat([updated]).ToArray() : before.Select(p => p.Id == updated.Id ? updated : p).ToArray();
        BankReconciliation.CheckLocked(before, after);
        var root = Body(source); var periods = root["periods"]!.AsArray();
        if (add) periods.Add(Modeled(updated).DeepClone());
        else
        {
            var index = Enumerable.Range(0, periods.Count).Single(i => Id(periods[i]) == updated.Id);
            periods[index] = ApplyChanges(periods[index], Modeled(existing!), Modeled(updated));
        }
        var encoded = Encode(root); var settings = source.SettingsPlists.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal); settings[BankReconciliation.SettingsKey] = encoded;
        var verified = Read(source with { SettingsPlists = settings });
        if (verified.Count != after.Length || verified.Any(p => !after.Any(a => a.Id == p.Id && BankReconciliation.Same(a, p)))) throw new InvalidDataException("تعذّر التحقق من تعديل دفتر المطابقة؛ لم تُحفظ البيانات.");
        return encoded;
    }
}
