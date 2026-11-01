using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Claunia.PropertyList;

namespace SalaryDesktop.Core;

public sealed record GroupIssuanceLine(string Id, string SourceIdentity, string Title, long Amount, int Count, string Iban, string Branch, string Swift, string Category, string? LocalAccount, string? Beneficiary, string? WindowsDetailHash);
public sealed record GroupIssuanceReview(string Month, TransferGroup Group, string Payer, string PayerIban, IReadOnlyList<GroupIssuanceLine> Lines, IReadOnlyList<string> Changes, string? BaselineMonth);

/// إقرار مستخدم محلي؛ لا يثبت التحويل المصرفي أو مطابقة النسخة المطبوعة.
public static class GroupIssuance
{
    public const string SettingsKey = "transferIssuedConfirmations";
    private static string Key(string value) => PayrollAnalysis.Normalize(value).Replace(" ", "", StringComparison.Ordinal);
    private static string Mask(string value) { var v = TransferPreparation.NormalizeAccount(value); return v.Length <= 4 ? v : "••••" + v[^4..]; }
    private static string Category(TransferGroup group) => group switch { TransferGroup.Rafidain => "rafidain", TransferGroup.OtherBanks => "otherBanks", _ => throw new InvalidDataException("المجموعة غير صالحة للتأكيد.") };
    private static JsonObject ReadLedger(BackupSnapshot source)
    {
        if (!source.SettingsPlists.TryGetValue(SettingsKey, out var plist)) return new JsonObject { ["version"] = 1, ["records"] = new JsonArray() };
        try
        {
            if (PropertyListParser.Parse(plist) is not NSData data || data.Bytes.Length > 30_000_000) throw new InvalidDataException("سجل إصدارات الكتب غير صالح.");
            if (data.Bytes.Length == 0) return new JsonObject { ["version"] = 1, ["records"] = new JsonArray() };
            var result = JsonNode.Parse(data.Bytes)?.AsObject() ?? throw new JsonException();
            if (result["version"]?.GetValue<int>() != 1 || result["records"] is not JsonArray records || records.Count > 100_000) throw new InvalidDataException("إصدار سجل الكتب غير مدعوم.");
            var ids = new HashSet<Guid>();
            foreach (var node in records)
            {
                if (node is not JsonObject record || !Guid.TryParse(record["id"]?.GetValue<string>(), out var id) || id == Guid.Empty || !ids.Add(id) || string.IsNullOrWhiteSpace(record["month"]?.GetValue<string>()) || string.IsNullOrWhiteSpace(record["book"]?.GetValue<string>()) || record["confirmedAt"]?.GetValue<double>() is not { } time || !double.IsFinite(time) || record["lines"] is not JsonArray lines || lines.Count == 0 || lines.Count > 100_000) throw new InvalidDataException("يوجد إقرار كتاب غير صالح.");
                var lineIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in lines)
                {
                    if (item is not JsonObject line || string.IsNullOrWhiteSpace(line["id"]?.GetValue<string>()) || !lineIds.Add(line["id"]!.GetValue<string>()) || line["amount"]?.GetValue<decimal>() is not { } amount || amount <= 0 || amount > 9_007_199_254_740_991m) throw new InvalidDataException("يوجد بند إصدار غير صالح.");
                }
                var category = record["groupCategory"]?.GetValue<string>();
                if (category is not null && category is not ("rafidain" or "otherBanks")) throw new InvalidDataException("نوع إقرار المجموعة غير صالح.");
                if (category is not null && (lines.Select(x => x?["sourceIdentity"]?.GetValue<string>()).Any(string.IsNullOrWhiteSpace) || lines.Select(x => x?["sourceIdentity"]?.GetValue<string>()).Distinct(StringComparer.Ordinal).Count() != lines.Count)) throw new InvalidDataException("هويات جهات إقرار المجموعة غير صالحة.");
            }
            return result;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
        { throw new InvalidDataException("تعذّر قراءة سجل إصدارات الكتب؛ لم يُغيّر السجل.", ex); }
    }
    public static void Validate(BackupSnapshot source) => _ = ReadLedger(source);
    private static string Fingerprint(TransferEntry entry, IReadOnlyList<Deduction> deductions, IReadOnlyDictionary<string, Employee[]> payroll)
    {
        if (deductions.Count == 0 || deductions.Any(d => d.Amount is null or < 0 || d.Amount != decimal.Truncate(d.Amount.Value) || string.IsNullOrWhiteSpace(d.Name)) || deductions.Sum(d => d.Amount ?? 0) != entry.Amount || deductions.Select(d => Key(d.Name)).Distinct(StringComparer.Ordinal).Count() != entry.People) throw new InvalidDataException("قائمة أسماء إحدى الجهات لا تطابق مبلغها أو عددها.");
        var rows = new List<string[]>();
        foreach (var d in deductions)
        {
            var matches = payroll.GetValueOrDefault(Key(d.Name)) ?? [];
            if (matches.Length > 1) throw new InvalidDataException("يوجد اسم مكرر في الرواتب؛ راجع هوية المنتسب قبل تأكيد المجموعة.");
            var employee = matches.SingleOrDefault(); rows.Add([d.Name, d.Rank.Length > 0 ? d.Rank : employee?.Rank ?? "", employee?.Workplace ?? "", d.Amount!.Value.ToString("0", CultureInfo.InvariantCulture)]);
        }
        var sorted = rows.Select(r => JsonSerializer.Serialize(r)).Order(StringComparer.Ordinal).ToArray();
        return Convert.ToBase64String(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(sorted)));
    }
    private static IReadOnlyList<GroupIssuanceLine> Lines(BackupSnapshot source, TransferSnapshot transfer, TransferGroup group)
    {
        _ = GroupLetterPreparation.Prepare(transfer, group, DateOnly.FromDateTime(DateTime.Now));
        var category = Category(group); var result = new List<GroupIssuanceLine>();
        var payrollLookup = source.Employees.Where(e => e.Month == transfer.Month).GroupBy(e => Key(e.Name)).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var deductions = source.Deductions.Where(d => d.Month == transfer.Month && !PayrollAnalysis.IsInactive(d)).GroupBy(d => d.Title).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        foreach (var e in transfer.Entries.Where(e => e.Group == group))
        {
            var payroll = e.Id.StartsWith("payroll|", StringComparison.Ordinal); var suffix = payroll ? e.Id[8..] : "execution";
            var sourceId = payroll ? "payroll|" + suffix : "deduction|" + e.Title;
            var id = $"deductions|{transfer.Month}|false|{e.Title}|{suffix}";
            result.Add(new(id, sourceId, e.Title, checked((long)e.Amount), e.People, e.Account?.Iban ?? "", e.Account?.Branch ?? "", e.Account?.Swift ?? "", category, e.Account?.LocalAccount, e.Beneficiary, payroll ? null : Fingerprint(e, deductions.GetValueOrDefault(e.Title) ?? [], payrollLookup)));
        }
        if (result.Select(l => l.SourceIdentity).Distinct(StringComparer.Ordinal).Count() != result.Count) throw new InvalidDataException("توجد جهات مكررة في المجموعة.");
        return result;
    }
    public static GroupIssuanceReview Review(BackupSnapshot source, TransferSnapshot transfer, TransferGroup group)
    {
        var lines = Lines(source, transfer, group); var ledger = ReadLedger(source); var category = Category(group); var records = (JsonArray)ledger["records"]!; var monthKey = MonthNames.SortKey(transfer.Month);
        var eligible = records.OfType<JsonObject>().Where(r => r["groupCategory"]?.GetValue<string>() == category && MonthNames.SortKey(r["month"]!.GetValue<string>()) <= monthKey && MonthNames.SortKey(r["month"]!.GetValue<string>()) > 0)
            .OrderByDescending(r => MonthNames.SortKey(r["month"]!.GetValue<string>())).ThenByDescending(r => r["confirmedAt"]!.GetValue<double>()).ToArray();
        var baseline = eligible.FirstOrDefault(); var changes = new List<string>();
        if (baseline is not null)
        {
            var old = ((JsonArray)baseline["lines"]!).OfType<JsonObject>().ToDictionary(x => x["sourceIdentity"]!.GetValue<string>(), StringComparer.Ordinal);
            var current = lines.ToDictionary(x => x.SourceIdentity, StringComparer.Ordinal);
            foreach (var line in lines)
            {
                if (!old.TryGetValue(line.SourceIdentity, out var previous)) { changes.Add(line.Title + ": جهة جديدة بمبلغ " + line.Amount.ToString("N0", CultureInfo.GetCultureInfo("en-US"))); continue; }
                var parts = new List<string>();
                var previousAmount = previous["amount"]!.GetValue<decimal>(); var previousCount = previous["count"]?.GetValue<int>() ?? 0;
                if (previousAmount != line.Amount) parts.Add("المبلغ: " + previousAmount.ToString("N0", CultureInfo.GetCultureInfo("en-US")) + " ← " + line.Amount.ToString("N0", CultureInfo.GetCultureInfo("en-US")));
                if (previousCount != line.Count) parts.Add("عدد المنتسبين: " + previousCount + " ← " + line.Count);
                if (TransferPreparation.NormalizeAccount(previous["iban"]?.GetValue<string>() ?? "") != TransferPreparation.NormalizeAccount(line.Iban) || TransferPreparation.NormalizeAccount(previous["localAccount"]?.GetValue<string>() ?? "") != TransferPreparation.NormalizeAccount(line.LocalAccount ?? "")) parts.Add("تغيّر الحساب: " + Mask(previous["iban"]?.GetValue<string>() ?? previous["localAccount"]?.GetValue<string>() ?? "") + " ← " + Mask(line.Iban.Length > 0 ? line.Iban : line.LocalAccount ?? ""));
                if (PayrollAnalysis.Normalize(previous["branch"]?.GetValue<string>() ?? "") != PayrollAnalysis.Normalize(line.Branch)) parts.Add("المصرف أو الفرع: " + (previous["branch"]?.GetValue<string>() ?? "") + " ← " + line.Branch);
                if (TransferPreparation.NormalizeAccount(previous["swift"]?.GetValue<string>() ?? "") != TransferPreparation.NormalizeAccount(line.Swift)) parts.Add("تغيّر SWIFT");
                if (PayrollAnalysis.Normalize(previous["beneficiary"]?.GetValue<string>() ?? previous["title"]?.GetValue<string>() ?? "") != PayrollAnalysis.Normalize(line.Beneficiary ?? line.Title)) parts.Add("اسم المستفيد: " + (previous["beneficiary"]?.GetValue<string>() ?? previous["title"]?.GetValue<string>() ?? "") + " ← " + (line.Beneficiary ?? line.Title));
                if (line.WindowsDetailHash is not null)
                {
                    var oldHash = previous["windowsDetailHash"]?.GetValue<string>();
                    if (oldHash is null) parts.Add("بصمة الأسماء السابقة غير متاحة على ويندوز؛ راجع القائمة");
                    else if (oldHash != line.WindowsDetailHash) parts.Add("تغيّرت تفاصيل الأسماء أو الرتب أو المبالغ");
                }
                if (parts.Count > 0) changes.Add(line.Title + ": " + string.Join("؛ ", parts));
            }
            foreach (var (identity, previous) in old) if (!current.ContainsKey(identity)) changes.Add((previous["title"]?.GetValue<string>() ?? "جهة") + ": جهة غير موجودة في المجموعة الحالية");
            if ((baseline["payerName"]?.GetValue<string>() ?? "") != transfer.Payer || TransferPreparation.NormalizeAccount(baseline["payerIBAN"]?.GetValue<string>() ?? "") != TransferPreparation.NormalizeAccount(transfer.PayerAccount)) changes.Add("تغيّرت بيانات الجهة الدافعة أو لم تكن محفوظة في الإقرار السابق.");
        }
        return new(transfer.Month, group, transfer.Payer, TransferPreparation.NormalizeAccount(transfer.PayerAccount), lines, changes, baseline?["month"]?.GetValue<string>());
    }
    public static byte[] Append(BackupSnapshot source, GroupIssuanceReview review, string note)
    {
        note = note.Trim(); if (note.Length > 2000 || note.Any(c => char.IsControl(c) && c != '\n')) throw new InvalidDataException("ملاحظة الإصدار غير صالحة.");
        if (review.Changes.Count > 10_000 || review.Changes.Any(c => c.Length > 2000) || review.Lines.Count == 0 || review.Lines.Count > 100_000 || review.Lines.Any(l => l.Amount <= 0 || l.Count < 0 || string.IsNullOrWhiteSpace(l.Id) || string.IsNullOrWhiteSpace(l.SourceIdentity)) || review.Lines.Select(l => l.Id).Distinct(StringComparer.Ordinal).Count() != review.Lines.Count || review.Lines.Select(l => l.SourceIdentity).Distinct(StringComparer.Ordinal).Count() != review.Lines.Count) throw new InvalidDataException("بيانات إقرار المجموعة غير صالحة أو تتجاوز الحد المدعوم.");
        var ledger = ReadLedger(source); var records = (JsonArray)ledger["records"]!; var category = Category(review.Group);
        var lines = new JsonArray();
        foreach (var line in review.Lines) lines.Add(new JsonObject { ["id"] = line.Id, ["title"] = line.Title, ["amount"] = line.Amount, ["count"] = line.Count, ["iban"] = line.Iban, ["branch"] = line.Branch, ["swift"] = line.Swift, ["category"] = line.Category, ["localAccount"] = line.LocalAccount, ["beneficiary"] = line.Beneficiary, ["sourceIdentity"] = line.SourceIdentity, ["windowsDetailHash"] = line.WindowsDetailHash });
        var changes = new JsonArray(); foreach (var change in review.Changes) changes.Add(change);
        var timestamp = (DateTime.UtcNow - new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        records.Add(new JsonObject { ["id"] = Guid.NewGuid().ToString().ToUpperInvariant(), ["month"] = review.Month, ["book"] = review.Group == TransferGroup.Rafidain ? "الرافدين · عدا فرع الأنبار" : "مجموعة رشيد ومصارف أخرى", ["confirmedAt"] = timestamp, ["lines"] = lines, ["groupCategory"] = category, ["note"] = note.Length == 0 ? null : note, ["payerName"] = review.Payer, ["payerIBAN"] = review.PayerIban, ["groupChanges"] = changes });
        using var output = new MemoryStream(); PropertyListParser.SaveAsBinary(new NSData(JsonSerializer.SerializeToUtf8Bytes(ledger)), output); return output.ToArray();
    }
}
