using System.Text.Json;
using Claunia.PropertyList;

namespace SalaryDesktop.Core;

public sealed record IndividualAccountOverride(string Value, string Source, string Original, string Reference);

public static class IndividualAccountCustomization
{
    public static IndividualAccountOverride? Read(BackupSnapshot snapshot, string id)
    {
        if (!snapshot.SettingsPlists.TryGetValue("transferLetterCustomizations", out var plist)) return null;
        try
        {
            if (PropertyListParser.Parse(plist) is not NSData data) throw new InvalidDataException("صيغة تخصيص الكتب غير صالحة.");
            if (data.Bytes.Length == 0) return null;
            using var doc = JsonDocument.Parse(data.Bytes); var root = doc.RootElement;
            JsonElement? item = root.TryGetProperty("individual", out var all) && all.TryGetProperty(id, out var found) ? found : null;
            JsonElement? common = root.TryGetProperty("common", out var shared) ? shared : null;
            string? Value(JsonElement? element, string key) => element is { } x && x.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;
            string? Effective(string key) => Value(item, key) ?? Value(common, key);
            var account = Effective("beneficiaryAccount"); if (string.IsNullOrWhiteSpace(account)) return null;
            var source = Effective("beneficiarySource"); var original = Effective("beneficiaryOriginal"); var reference = Effective("beneficiaryReference");
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(reference)) throw new InvalidDataException("تخصيص حساب المستفيد ناقص؛ راجع بيانات الكتاب.");
            return new(account, source, original, reference);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        { throw new InvalidDataException("تعذّر قراءة تخصيص حساب المستفيد.", ex); }
    }
    public static string? Resolve(BackupSnapshot snapshot, string id, string source, string original, string reference, bool requireAccountListMatch = true)
    {
        var custom = Read(snapshot, id); if (custom is null) return null;
        var chosen = TransferPreparation.NormalizeAccount(custom.Value); var current = TransferPreparation.NormalizeAccount(original);
        var prior = TransferPreparation.NormalizeAccount(custom.Original); var pinned = TransferPreparation.NormalizeAccount(custom.Reference); var checkedReference = TransferPreparation.NormalizeAccount(reference);
        if (custom.Source != source || pinned != checkedReference || (prior != current && chosen != current)) throw new InvalidDataException("تغيّرت الجهة أو حسابها الأصلي؛ راجع تخصيص حساب الكتاب.");
        if (id is "housing" or "water")
        {
            if (new[] { chosen, prior, pinned }.Any(v => v.Length != 15 || v.Any(c => !char.IsAsciiDigit(c)))) throw new InvalidDataException("الحساب المحلي المخصص غير صالح.");
        }
        else if (id is "ramadi" or "food_company" or "food_tax" or "tax_social" or "contracts" or "cash_salary")
        {
            if (!TransferPreparation.ValidIban(chosen) || !TransferPreparation.ValidIban(pinned) || !TransferPreparation.ValidIban(prior)) throw new InvalidDataException("IBAN المستفيد المخصص غير صالح.");
            if (chosen.Substring(4, 4) != prior.Substring(4, 4)) throw new InvalidDataException("الحساب المخصص يتبع مصرفاً مختلفاً؛ راجع بيانات المستفيد.");
        }
        else throw new InvalidDataException("هذا الكتاب لا يدعم حساب مستفيد مخصصاً.");
        if (requireAccountListMatch && id is ("ramadi" or "housing" or "water") && chosen != current) throw new InvalidDataException("حدّث حساب الجهة في قائمة الحسابات ليطابق الحساب المخصص للكتاب.");
        return chosen;
    }
}
