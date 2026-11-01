using System.Text.Json;
using Claunia.PropertyList;

namespace SalaryDesktop.Core;

public sealed record GroupLetterSettings(string Header, string Number, string First, string Second, string Third, string Footer, string? BodyMonth, int? OutgoingMonth, int SignatureOffset, string? BodyTemplate);
public static class GroupLetterSettingsReader
{
    public static GroupLetterSettings Read(BackupSnapshot snapshot, TransferGroup group)
    {
        var id = group switch { TransferGroup.Rafidain => "rafidain_group", TransferGroup.OtherBanks => "other_banks_group", _ => throw new InvalidDataException("المجموعة غير صالحة للكتاب.") };
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
}
