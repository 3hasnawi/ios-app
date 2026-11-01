using System.Text.Json;
using Claunia.PropertyList;

namespace SalaryDesktop.Core;

public sealed record GroupLetterSettings(string Header, string Number, string First, string Second, string Third, string Footer, string? BodyMonth, int? OutgoingMonth, int SignatureOffset, string? BodyTemplate);
public static class GroupLetterSettingsReader
{
    public static GroupLetterSettings Read(BackupSnapshot snapshot, TransferGroup group)
    {
        var id = group switch { TransferGroup.Rafidain => "rafidain_group", TransferGroup.OtherBanks => "other_banks_group", _ => throw new InvalidDataException("المجموعة غير صالحة للكتاب.") };
        return Read(snapshot, id);
    }
    public static GroupLetterSettings Read(BackupSnapshot snapshot, string id)
    {
        if (id is not ("rafidain_group" or "other_banks_group" or "food_company" or "food_tax" or "tax_social" or "contracts" or "cash_salary" or "ramadi" or "housing" or "water" or "rafidain_anbar")) throw new InvalidDataException("نوع الكتاب غير صالح.");
        if (!snapshot.SettingsPlists.TryGetValue("transferLetterCustomizations", out var plist)) return new("", "", "", "", "", "", null, null, 0, null);
        try
        {
            if (PropertyListParser.Parse(plist) is not NSData data) throw new InvalidDataException("صيغة تخصيص الكتب غير صالحة.");
            if (data.Bytes.Length == 0) return new("", "", "", "", "", "", null, null, 0, null);
            using var doc = JsonDocument.Parse(data.Bytes); var root = doc.RootElement;
            var common = root.GetProperty("common");
            JsonElement? item = root.TryGetProperty("individual", out var all) && all.TryGetProperty(id, out var found) ? found : null;
            string? Value(JsonElement element, string key) => element.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;
            string? Effective(string key) => (item is { } x ? Value(x, key) : null) ?? Value(common, key);
            int? offset = item is { } o && o.TryGetProperty("signatureOffset", out var ov) && ov.ValueKind != JsonValueKind.Null ? ov.GetInt32() : common.TryGetProperty("signatureOffset", out var cv) && cv.ValueKind != JsonValueKind.Null ? cv.GetInt32() : null;
            var outgoing = Effective("outgoingMonth"); int? parsed = null;
            if (!string.IsNullOrWhiteSpace(outgoing))
            {
                var digits = TransferPreparation.NormalizeAccount(outgoing);
                if (!int.TryParse(digits, out var n) || n is < 1 or > 12) throw new InvalidDataException("شهر الصادر المحفوظ غير صالح.");
                parsed = n;
            }
            return new(Effective("header") ?? "", Value(common, "fixedNumber") ?? "", Effective("committeeFirst") ?? "", Effective("committeeSecond") ?? "", Effective("committeeThird") ?? "", Effective("footer") ?? "", Effective("bodyMonth"), parsed, offset ?? 0, Effective("bodyTemplate"));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new InvalidDataException("تعذّر قراءة تخصيصات الكتب من النسخة.", ex); }
    }
    public static string? FoodContract(BackupSnapshot snapshot)
    {
        if (!snapshot.SettingsPlists.TryGetValue("transferLetterCustomizations", out var plist)) return null;
        try
        {
            if (PropertyListParser.Parse(plist) is not NSData data) throw new InvalidDataException("صيغة تخصيص الكتب غير صالحة.");
            if (data.Bytes.Length == 0) return null;
            using var doc = JsonDocument.Parse(data.Bytes);
            var common = doc.RootElement.GetProperty("common");
            return common.TryGetProperty("foodContractReference", out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        { throw new InvalidDataException("تعذّر قراءة رقم عقد الإطعام.", ex); }
    }
    public static void EnsureAccountNotCustomized(BackupSnapshot snapshot, string id)
    {
        if (!snapshot.SettingsPlists.TryGetValue("transferLetterCustomizations", out var plist)) return;
        try
        {
            if (PropertyListParser.Parse(plist) is not NSData data) throw new InvalidDataException("صيغة تخصيص الكتب غير صالحة.");
            if (data.Bytes.Length == 0) return;
            using var doc = JsonDocument.Parse(data.Bytes);
            var root = doc.RootElement;
            if (root.TryGetProperty("individual", out var all) && all.TryGetProperty(id, out var item) && item.TryGetProperty("beneficiaryAccount", out var value) && value.ValueKind != JsonValueKind.Null && !string.IsNullOrWhiteSpace(value.GetString()))
                throw new InvalidDataException("هذا الكتاب يحتوي حساب مستفيد مخصصاً من الآيفون؛ دعمه لم يكتمل على ويندوز، لذا لا يمكن إصدار المسودة بأمان.");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        { throw new InvalidDataException("تعذّر التحقق من تخصيص حساب المستفيد.", ex); }
    }
}
