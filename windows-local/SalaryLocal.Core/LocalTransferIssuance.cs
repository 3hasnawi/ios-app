using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SalaryDesktop.Core;
namespace SalaryLocal.Core;

public sealed record LocalIssuanceRecord(string Id, string Month, string Book, decimal Total, string Note);
public sealed record LocalSingleIssuanceReview(LocalLetterPreview Preview, IReadOnlyList<string> Changes);
public static class LocalTransferIssuance
{
    private static JsonObject Ledger(BackupSnapshot source)
    {
        GroupIssuance.Validate(source);
        var root = LocalTransferLetterSettings.ReadObject(source, GroupIssuance.SettingsKey);
        if (root.Count == 0) return new JsonObject { ["version"] = 1, ["records"] = new JsonArray() };
        GroupIssuance.Validate(source); return root;
    }
    public static IReadOnlyList<LocalIssuanceRecord> Records(BackupSnapshot source) =>
        Array.AsReadOnly(((JsonArray)Ledger(source)["records"]!).OfType<JsonObject>().Select(r => new LocalIssuanceRecord(
            r["id"]!.GetValue<string>(), r["month"]!.GetValue<string>(), r["book"]!.GetValue<string>(),
            ((JsonArray)r["lines"]!).Sum(l => ExactAmount(l!["amount"])), r["note"]?.GetValue<string>() ?? "")).ToArray());
    // JsonValue المنشأ من Int64 لا يحوّل نفسه إلى Decimal، بخلاف قيمة JSON المقروءة.
    // نقرأ الأنواع الدقيقة فقط؛ لا يمر المبلغ عبر Double.
    internal static decimal ExactAmount(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<decimal>(out var amount)) return amount;
            if (value.TryGetValue<long>(out var integer)) return integer;
            if (value.TryGetValue<int>(out var smallInteger)) return smallInteger;
            if (value.TryGetValue<JsonElement>(out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out amount)) return amount;
        }
        throw new InvalidDataException("صيغة مبلغ إقرار الإصدار غير صالحة.");
    }
    private static string Hash(object data) => Convert.ToBase64String(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(data)));
    private static JsonArray Lines(BackupSnapshot source, LocalLetterPreview preview)
    {
        if (preview.Group is not null) throw new InvalidDataException("استخدم إقرار المجموعة لهذا الكتاب.");
        var result = new JsonArray();
        JsonObject Line(string id, string title, long amount, int count, string iban, string branch, string swift, string category) =>
            new() { ["id"] = id, ["title"] = title, ["amount"] = amount, ["count"] = count, ["iban"] = iban, ["branch"] = branch, ["swift"] = swift, ["category"] = category };
        if (preview.Entity is { } entity)
        {
            var entry = TransferPreparation.Prepare(source, preview.Month).Entries.Single(e => e.Title == entity.Entity && e.Group == TransferGroup.Anbar);
            var line = Line($"deductions|{preview.Month}|false|{entry.Title}|execution", entry.Title, entity.Total, entity.People,
                entry.Account?.Iban ?? "", entry.Account?.Branch ?? "", entry.Account?.Swift ?? "", "anbar");
            line["sourceIdentity"] = "deduction|" + entry.Title; line["localAccount"] = entry.Account?.LocalAccount;
            line["beneficiary"] = entry.Beneficiary;
            var details = LocalTransferLetters.EntityNameRows(source, preview.Month, entity)
                .Select(r => new { r.Name, r.Rank, r.Workplace, r.Amount }).OrderBy(r => JsonSerializer.Serialize(r), StringComparer.Ordinal).ToArray();
            line["windowsDetailHash"] = Hash(details);
            result.Add(line);
        }
        else
        {
            var payroll = preview.Payroll!;
            // صيغة الآيفون لا تسمح ببند سالب؛ لا نسقط فرق العقود أو نختلق بنداً مالياً.
            if (payroll.Lines.Any(l => l.Amount < 0) || (preview.Id == "contracts" && payroll.Lines.Count != 2))
                throw new InvalidDataException("إقرار كتاب العقود المعدّل غير مدعوم في الصيغة المشتركة؛ لم يُسجل إقرار جزئي.");
            var ids = preview.Id switch {
                "food_company" => new[] { "foodCompany" }, "food_tax" => ["foodTax"],
                "tax_social" => ["socialProtection", "incomeTax"], "contracts" => ["contractNominal", "contractRetirement"],
                "cash_salary" => ["cashSalary"], _ => throw new InvalidDataException("نوع الكتاب غير صالح.")
            };
            var titles = preview.Id switch {
                "food_company" => new[] { "شركة عراق الجوال" }, "food_tax" => ["ضريبة عقد الإطعام"],
                "tax_social" => ["الحماية الاجتماعية", "الضريبة"], "contracts" => ["حصة اسمي العقود", "توقيفات العقود التقاعدية"],
                _ => new[] { "رواتب الكاش" }
            };
            for (var i = 0; i < ids.Length; i++)
            {
                if (payroll.Lines[i].Amount == 0) continue;
                var line = Line($"payroll|{preview.Month}|{ids[i]}", titles[i], payroll.Lines[i].Amount, 0, "", "", "", "payroll");
                if (preview.Names is { } names) line["windowsDetailHash"] = Hash(names.Rows);
                result.Add(line);
            }
        }
        if (result.Count == 0 || result.Sum(l => ExactAmount(l!["amount"])) != preview.Total) throw new InvalidDataException("بنود الإقرار لا تطابق مجموع الكتاب.");
        return result;
    }
    public static LocalSingleIssuanceReview Review(BackupSnapshot source, LocalLetterPreview preview)
    {
        LocalTransferLetters.VerifySource(source, preview); var lines = Lines(source, preview); var changes = new List<string>();
        var records = ((JsonArray)Ledger(source)["records"]!).OfType<JsonObject>().Where(r => r["month"]!.GetValue<string>() == preview.Month).ToArray();
        foreach (var line in lines.OfType<JsonObject>())
        {
            var old = records.SelectMany(r => ((JsonArray)r["lines"]!).OfType<JsonObject>()).Where(l => l["id"]!.GetValue<string>() == line["id"]!.GetValue<string>()).ToArray();
            if (old.Length == 0) changes.Add(line["title"]!.GetValue<string>() + ": بلا إقرار سابق");
            else
            {
                changes.Add(line["title"]!.GetValue<string>() + ": يوجد إقرار سابق؛ الإضافة ستكرر المصدر");
                if (old.Any(l => ExactAmount(l["amount"]) != ExactAmount(line["amount"]))) changes.Add("تغيّر المبلغ عن إقرار سابق.");
                if (line["windowsDetailHash"] is not null && old.Any(l => l["windowsDetailHash"]?.GetValue<string>() != line["windowsDetailHash"]!.GetValue<string>())) changes.Add("تغيّرت قائمة الأسماء أو بصمتها السابقة غير متاحة؛ راجعها.");
            }
        }
        var matching = records.Where(r => r["windowsBookId"]?.GetValue<string>() == preview.Id).ToArray();
        if (matching.Any(r => r["windowsBookHash"]?.GetValue<string>() != BookHash(preview, lines))) changes.Add("تغيّرت تفاصيل الكتاب أو المستفيد أو الحساب أو البنود عن الإقرار السابق.");
        return new(preview, Array.AsReadOnly(changes.ToArray()));
    }
    private static string BookHash(LocalLetterPreview p, JsonArray lines) => Hash(new { p.Id, p.Month, p.Payer, p.PayerAccount, p.BeneficiaryAccount, p.Settings, Lines = lines });
    public static byte[] Append(BackupSnapshot source, LocalLetterPreview preview, string note)
    {
        _ = Review(source, preview);
        note = note.Trim(); if (note.Length > 2000 || note.Any(c => char.IsControl(c) && c != '\n')) throw new InvalidDataException("الملاحظة طويلة أو غير صالحة.");
        var root = Ledger(source); var lines = Lines(source, preview);
        ((JsonArray)root["records"]!).Add(new JsonObject {
            ["id"] = Guid.NewGuid().ToString().ToUpperInvariant(), ["month"] = preview.Month, ["book"] = preview.Title,
            ["confirmedAt"] = (DateTimeOffset.UtcNow - new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalSeconds,
            ["lines"] = lines, ["note"] = note, ["payerName"] = preview.Payer, ["payerIBAN"] = preview.PayerAccount,
            ["windowsBookId"] = preview.Id, ["windowsBookHash"] = BookHash(preview, lines)
        });
        return Encode(source, root);
    }
    public static byte[] Revoke(BackupSnapshot source, string id)
    {
        var root = Ledger(source); var records = (JsonArray)root["records"]!;
        var found = records.OfType<JsonObject>().SingleOrDefault(r => r["id"]!.GetValue<string>() == id) ?? throw new InvalidDataException("الإقرار غير موجود؛ أعد تحميل السجل.");
        records.Remove(found); return Encode(source, root);
    }
    private static byte[] Encode(BackupSnapshot source, JsonObject root)
    {
        var bytes = LocalTransferLetterSettings.DataPlist(JsonSerializer.SerializeToUtf8Bytes(root));
        GroupIssuance.Validate(source with { SettingsPlists = new Dictionary<string, byte[]>(source.SettingsPlists) { [GroupIssuance.SettingsKey] = bytes } });
        return bytes;
    }
}
