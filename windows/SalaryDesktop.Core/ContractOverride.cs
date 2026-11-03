using System.Text.Json;
using System.Text.Json.Nodes;
using Claunia.PropertyList;

namespace SalaryDesktop.Core;

/// المبلغ النهائي اليدوي مرتبط بأساس احتسابه؛ لا يُنقل إلى شهر آخر ولا يُعتمد بعد تغير الأساس.
public sealed record ContractOverride(decimal Total, decimal Nominal, decimal Retirement, int Count, int Missing)
{
    public bool Matches(BankSummary summary) => Nominal == summary.ContractNominal && Retirement == summary.ContractRetirement && Count == summary.ContractCount && Missing == summary.MissingContractNominal;
}

public static class ContractOverrides
{
    public const string SettingsKey = "bankStatementContractOverridesV1";
    private const decimal Maximum = 9_007_199_254_740_991m;
    private static bool Money(decimal value) => value >= 0 && value <= Maximum && value == decimal.Truncate(value);
    private static JsonObject Parse(byte[]? raw)
    {
        if (raw is null) return new JsonObject();
        try
        {
            if (PropertyListParser.Parse(raw) is not NSData data || data.Bytes.Length == 0 || data.Bytes.Length > 80_000) throw new InvalidDataException("صيغة تعديل العقود غير صالحة.");
            return JsonNode.Parse(data.Bytes)?.AsObject() ?? throw new InvalidDataException("تعديلات العقود غير صالحة.");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or IndexOutOfRangeException or OverflowException)
        { throw new InvalidDataException("تعذّر قراءة تعديلات العقود؛ لم تُفترض قيماً صفرية.", ex); }
    }
    private static ContractOverride Entry(JsonNode? node)
    {
        try
        {
            if (node is not JsonObject obj) throw new InvalidDataException("قيد تعديل العقود غير صالح.");
            var value = new ContractOverride(obj["total"]!.GetValue<decimal>(), obj["nominal"]!.GetValue<decimal>(), obj["retirement"]!.GetValue<decimal>(), obj["count"]!.GetValue<int>(), obj["missing"]!.GetValue<int>());
            if (!Money(value.Total) || !Money(value.Nominal) || !Money(value.Retirement) || value.Count <= 0 || value.Missing != 0) throw new InvalidDataException("قيم تعديل العقود غير صالحة.");
            return value;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or NullReferenceException or FormatException or OverflowException)
        { throw new InvalidDataException("تعذّر قراءة قيد تعديل العقود.", ex); }
    }
    public static ContractOverride? Read(BackupSnapshot source, string month)
    {
        var all = Parse(source.SettingsPlists.GetValueOrDefault(SettingsKey));
        foreach (var (key, value) in all)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > 200) throw new InvalidDataException("شهر تعديل العقود غير صالح.");
            _ = Entry(value);
        }
        return all.TryGetPropertyValue(month, out var found) ? Entry(found) : null;
    }
    public static byte[] Set(byte[]? raw, string month, ContractOverride? value)
    {
        if (string.IsNullOrWhiteSpace(month) || month.Length > 200) throw new InvalidDataException("شهر تعديل العقود غير صالح.");
        var all = Parse(raw);
        foreach (var pair in all) _ = Entry(pair.Value);
        if (value is null) all.Remove(month);
        else
        {
            if (!Money(value.Total) || !Money(value.Nominal) || !Money(value.Retirement) || value.Count <= 0 || value.Missing != 0) throw new InvalidDataException("قيم تعديل العقود غير صالحة.");
            var entry = all[month] is JsonObject existing ? (JsonObject)existing.DeepClone() : new JsonObject();
            entry["total"] = value.Total; entry["nominal"] = value.Nominal; entry["retirement"] = value.Retirement; entry["count"] = value.Count; entry["missing"] = value.Missing;
            all[month] = entry;
        }
        using var stream = new MemoryStream(); PropertyListParser.SaveAsBinary(new NSData(JsonSerializer.SerializeToUtf8Bytes(all)), stream); return stream.ToArray();
    }
}
