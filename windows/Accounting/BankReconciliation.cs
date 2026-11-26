using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Claunia.PropertyList;

namespace SalaryDesktop.Core;

public enum MovementDirection { Credit, Debit }
public sealed record BankMovement(Guid Id, DateOnly Date, string Description, long Amount, MovementDirection Direction,
    string? SourceId = null, IReadOnlyList<string>? SourceLineIds = null, Guid? RefundOf = null,
    bool IsRefund = false, bool IsFunding = false, bool Matched = false, string? Extra = null);
/// بيانات توقيع اللجنة كما في الآيفون (BankReconciliation.Period): الرتبة والمنصب والتاريخ لكل عضو؛ الفارغ يأخذ القيمة الافتراضية في الكشف.
public sealed record BankCommittee(string? FirstRank = null, string? FirstPosition = null, string? FirstDate = null,
    string? SecondRank = null, string? SecondPosition = null, string? SecondDate = null, bool? DefaultsInitialized = null);
/// ConfirmedAt: وقت اعتماد مطابقة الفترة بثواني الآيفون (منذ ٢٠٠١)؛ الفترة المعتمدة مقفلة حتى يُلغى اعتمادها.
/// Extra: حقول أخرى من الآيفون لا يعرفها ويندوز، تُحفظ كما هي حتى لا تضيع عند النسخ الاحتياطي.
public sealed record BankPeriod(Guid Id, string Month, DateOnly Start, DateOnly End, long OpeningBalance,
    string FirstMember, string SecondMember, int? AcceptedContractMissing, long? AcceptedContractAmount,
    IReadOnlyList<BankMovement> Movements, BankCommittee? Committee = null, double? ConfirmedAt = null, string? Extra = null)
{
    public bool IsConfirmed => ConfirmedAt is not null;
}
public sealed record BankMovementLine(BankMovement Movement, long Balance);
public sealed record OutstandingRefund(BankMovement Movement, long Remaining);
public sealed record ReconciliationTotals(long Credits, long Funding, long Debits, long Balance,
    IReadOnlyList<BankMovementLine> Lines, IReadOnlyList<OutstandingRefund> OutstandingRefunds);

public static class BankReconciliation
{
    private const long Maximum = 9_007_199_254_740_991;
    private static InvalidDataException Invalid(string message) => new(message);
    public const string SettingsKey = "bankReconciliations";
    private static readonly HashSet<string> PeriodKeys = ["id", "month", "start", "end", "openingBalance", "memberFirst", "memberSecond", "firstRank", "firstPosition", "firstDate",
        "secondRank", "secondPosition", "secondDate", "signatureDefaultsInitialized", "acceptedContractMissing", "acceptedContractAmount", "movements", "confirmedAt"];
    private static readonly HashSet<string> MovementKeys = ["id", "date", "description", "amount", "direction", "sourceID", "sourceLineIDs", "refundOf", "isRefund", "isFunding", "matched"];
    private static readonly DateTime Reference = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static TimeZoneInfo Iraq => TimeZoneInfo.TryFindSystemTimeZoneById("Asia/Baghdad", out var iraq) ? iraq :
        TimeZoneInfo.TryFindSystemTimeZoneById("Arab Standard Time", out var windows) ? windows : TimeZoneInfo.CreateCustomTimeZone("Iraq", TimeSpan.FromHours(3), "Iraq", "Iraq");

    /// اعتماد مطابقة الفترة: يُسجَّل وقت الاعتماد فتُقفل الفترة؛ وإلغاء الاعتماد يعيدها للتعديل.
    public static BankPeriod Confirm(BankPeriod period, DateTime utcNow) => period with { ConfirmedAt = (utcNow.ToUniversalTime() - Reference).TotalSeconds };
    public static BankPeriod Unconfirm(BankPeriod period) => period with { ConfirmedAt = null };
    /// وقت الاعتماد بتوقيت بغداد.
    public static DateTime? ConfirmedTime(BankPeriod period) =>
        period.ConfirmedAt is { } seconds && double.IsFinite(seconds) ? TimeZoneInfo.ConvertTimeFromUtc(Reference.AddSeconds(seconds), Iraq) : null;
    /// أسطر توقيع العضو كما في كشف الآيفون: الرتبة، الاسم، المنصب، التاريخ (الافتراضي «/ الشهر / السنة» من نهاية الفترة).
    public static IReadOnlyList<string> SignatureLines(BankPeriod period, bool first)
    {
        var c = period.Committee ?? new BankCommittee();
        var date = $" / {period.End.Month} / {period.End.Year}";
        if (first) return [c.FirstRank ?? "عقيد الشرطة", period.FirstMember, c.FirstPosition ?? "مدير قسم التدقيق والرقابة الداخلية", ReportSheet.Eastern(c.FirstDate ?? date)];
        return [c.SecondRank ?? "العميد", period.SecondMember, c.SecondPosition ?? "مدير الحسابات", ReportSheet.Eastern(c.SecondDate ?? date)];
    }
    /// يرفض تغيير فترة معتمدة أو حذفها دون إلغاء اعتمادها أولاً.
    public static void CheckLocked(IReadOnlyList<BankPeriod> before, IReadOnlyList<BankPeriod> after)
    {
        foreach (var old in before.Where(p => p.IsConfirmed))
        {
            var next = after.FirstOrDefault(p => p.Id == old.Id);
            if (next is null) throw Invalid($"فترة {old.Month} معتمدة؛ ألغِ اعتمادها قبل حذفها.");
            // إلغاء الاعتماد يُحفظ وحده: لا يُقبل معه أي تغيير آخر في الفترة في الحفظ نفسه (نفس قاعدة الآيفون).
            if (!Same(old, next) && !Same(Unconfirm(old), next)) throw Invalid($"فترة {old.Month} معتمدة ومقفلة؛ ألغِ اعتمادها أولاً ثم عدّلها.");
        }
    }
    public static bool Same(BankPeriod a, BankPeriod b) => JsonSerializer.SerializeToUtf8Bytes(a).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(b));
    private static void CheckText(string? value, int max)
    {
        if (value is not null && (value.Length > max || value.Any(char.IsControl))) throw Invalid("بيانات اللجنة طويلة جداً أو فيها رموز غير صالحة.");
    }
    private static void CheckExtra(string? extra)
    {
        if (extra is null) return;
        if (extra.Length > 100_000 || JsonNode.Parse(extra) is not JsonObject) throw Invalid("بيانات إضافية غير صالحة في دفتر المطابقة.");
    }
    public static ReconciliationTotals Calculate(BankPeriod period)
    {
        if (period.Id == Guid.Empty || string.IsNullOrWhiteSpace(period.Month) || period.Start > period.End ||
            period.OpeningBalance is < 0 or > Maximum || period.FirstMember.Length > 150 || period.SecondMember.Length > 150 ||
            period.Movements.Count > 20_000 || period.Movements.Select(m => m.Id).Distinct().Count() != period.Movements.Count ||
            !((period.AcceptedContractMissing is null && period.AcceptedContractAmount is null) ||
              (period.AcceptedContractMissing > 0 && period.AcceptedContractAmount is >= 0 and <= Maximum)))
            throw Invalid("بيانات فترة المطابقة غير صالحة.");
        if (period.Committee is { } committee)
            foreach (var text in new[] { committee.FirstRank, committee.FirstPosition, committee.FirstDate, committee.SecondRank, committee.SecondPosition, committee.SecondDate }) CheckText(text, 150);
        if (period.ConfirmedAt is { } confirmed && (!double.IsFinite(confirmed) || confirmed is < 0 or > 7_000_000_000d)) throw Invalid("تاريخ اعتماد المطابقة غير صالح.");
        CheckExtra(period.Extra);
        foreach (var movement in period.Movements) CheckExtra(movement.Extra);
        var sourceIds = period.Movements.Where(m => m.SourceId is not null).Select(m => m.SourceId!).ToArray();
        var covered = period.Movements.SelectMany(m => m.SourceLineIds ?? []).ToArray();
        if (sourceIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 1000) || sourceIds.Distinct(StringComparer.Ordinal).Count() != sourceIds.Length ||
            covered.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 1000) || covered.Distinct(StringComparer.Ordinal).Count() != covered.Length)
            throw Invalid("تكرر مصدر إحدى الحركات؛ راجع المبالغ قبل المتابعة.");
        var byId = period.Movements.ToDictionary(m => m.Id);
        var refunds = new Dictionary<Guid, long>();
        foreach (var movement in period.Movements.Where(m => m.RefundOf is not null))
        {
            var id = movement.RefundOf!.Value;
            if (movement.Direction != MovementDirection.Debit || !byId.TryGetValue(id, out var incoming) ||
                incoming.Direction != MovementDirection.Credit || !incoming.IsRefund || incoming.RefundOf is not null ||
                movement.Date < incoming.Date || movement.Amount <= 0) throw Invalid("ربط المبلغ المسترجع غير صالح.");
            var sum = checked(refunds.GetValueOrDefault(id) + movement.Amount);
            if (sum > incoming.Amount) throw Invalid("المبلغ المعاد يتجاوز المبلغ المسترجع.");
            refunds[id] = sum;
        }
        var ordered = period.Movements.Select((item, index) => (item, index)).OrderBy(pair => pair.item.Date).ThenBy(pair => pair.index).Select(pair => pair.item).ToArray();
        long credit = 0, debit = 0, funding = 0, balance = period.OpeningBalance;
        var lines = new List<BankMovementLine>();
        foreach (var movement in ordered)
        {
            if (movement.Id == Guid.Empty || movement.Date < period.Start || movement.Date > period.End || movement.Amount is <= 0 or > Maximum ||
                movement.IsRefund && movement.Direction != MovementDirection.Credit || movement.IsFunding && (movement.Direction != MovementDirection.Credit || movement.IsRefund) ||
                string.IsNullOrWhiteSpace(movement.Description) || movement.Description.Length > 200 || movement.Description.Any(char.IsControl))
                throw Invalid("تاريخ إحدى الحركات أو مبلغها أو بيانها غير صالح.");
            if (movement.Direction == MovementDirection.Credit) { credit = checked(credit + movement.Amount); balance = checked(balance + movement.Amount); }
            else if (movement.Direction == MovementDirection.Debit) { debit = checked(debit + movement.Amount); balance = checked(balance - movement.Amount); }
            else throw Invalid("نوع الحركة غير معروف.");
            if (movement.IsFunding) funding = checked(funding + movement.Amount);
            if (credit > Maximum || debit > Maximum || funding > Maximum || balance is > Maximum or < -Maximum) throw Invalid("مجموع الحركات يتجاوز الحد المدعوم.");
            lines.Add(new(movement, balance));
        }
        var outstanding = ordered.Where(m => m.Direction == MovementDirection.Credit && m.IsRefund)
            .Select(m => new OutstandingRefund(m, m.Amount - refunds.GetValueOrDefault(m.Id))).Where(item => item.Remaining > 0).ToArray();
        return new(credit, funding, debit, balance, lines, outstanding);
    }
    public static BankMovement Replace(BankMovement old, BankMovement updated)
    {
        if (old.Id != updated.Id) throw Invalid("لا يمكن تغيير هوية الحركة.");
        var unchanged = old.Date == updated.Date && old.Description == updated.Description && old.Amount == updated.Amount && old.Direction == updated.Direction &&
            old.SourceId == updated.SourceId && (old.SourceLineIds ?? []).SequenceEqual(updated.SourceLineIds ?? []) && old.RefundOf == updated.RefundOf &&
            old.IsRefund == updated.IsRefund && old.IsFunding == updated.IsFunding;
        return updated with { Matched = unchanged && old.Matched };
    }
    private static DateOnly IosDay(JsonElement value)
    {
        var seconds = value.GetDouble();
        if (!double.IsFinite(seconds) || seconds < -3_200_000_000d || seconds > 7_000_000_000d) throw Invalid("تاريخ المطابقة غير صالح.");
        var instant = DateTimeOffset.FromUnixTimeMilliseconds(checked((long)Math.Round((seconds + 978307200d) * 1000d)));
        var zone = TimeZoneInfo.TryFindSystemTimeZoneById("Asia/Baghdad", out var iraq) ? iraq :
            TimeZoneInfo.TryFindSystemTimeZoneById("Arab Standard Time", out var windows) ? windows : TimeZoneInfo.CreateCustomTimeZone("Iraq", TimeSpan.FromHours(3), "Iraq", "Iraq");
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
    }
    private static JsonElement? Optional(JsonElement element, string key) => element.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;
    private static string? Text(JsonElement element, string key) => Optional(element, key)?.GetString();
    private static BankCommittee? Committee(JsonElement item)
    {
        var committee = new BankCommittee(Text(item, "firstRank"), Text(item, "firstPosition"), Text(item, "firstDate"), Text(item, "secondRank"), Text(item, "secondPosition"),
            Text(item, "secondDate"), Optional(item, "signatureDefaultsInitialized")?.GetBoolean());
        return committee == new BankCommittee() ? null : committee;
    }
    private static string? Unknown(JsonElement element, HashSet<string> known)
    {
        var extra = new JsonObject();
        foreach (var property in element.EnumerateObject().Where(p => !known.Contains(p.Name))) extra[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        return extra.Count == 0 ? null : extra.ToJsonString();
    }
    private static double IosDate(DateOnly date)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(new TimeOnly(12, 0)), DateTimeKind.Unspecified);
        return (TimeZoneInfo.ConvertTimeToUtc(local, Iraq) - Reference).TotalSeconds;
    }
    /// الدفتر بصيغة الآيفون (BankReconciliation.Ledger داخل NSData)، مع حفظ الحقول غير المعروفة كما وردت.
    public static byte[] LedgerPlist(IReadOnlyList<BankPeriod> periods)
    {
        foreach (var period in periods) _ = Calculate(period);
        if (periods.Count > 5000 || periods.Select(p => p.Id).Distinct().Count() != periods.Count) throw Invalid("فترات المطابقة غير صالحة.");
        static JsonObject Base(string? extra) => extra is null ? new JsonObject() : JsonNode.Parse(extra)!.AsObject();
        var list = new JsonArray();
        foreach (var p in periods)
        {
            var node = Base(p.Extra);
            node["id"] = p.Id.ToString(); node["month"] = p.Month; node["start"] = IosDate(p.Start); node["end"] = IosDate(p.End); node["openingBalance"] = p.OpeningBalance;
            node["memberFirst"] = p.FirstMember; node["memberSecond"] = p.SecondMember;
            var c = p.Committee;
            void Put(string key, JsonNode? value) { if (value is null) node.Remove(key); else node[key] = value; }
            Put("firstRank", c?.FirstRank); Put("firstPosition", c?.FirstPosition); Put("firstDate", c?.FirstDate);
            Put("secondRank", c?.SecondRank); Put("secondPosition", c?.SecondPosition); Put("secondDate", c?.SecondDate);
            Put("signatureDefaultsInitialized", c?.DefaultsInitialized is { } initialized ? JsonValue.Create(initialized) : null);
            Put("acceptedContractMissing", p.AcceptedContractMissing is { } missing ? JsonValue.Create(missing) : null);
            Put("acceptedContractAmount", p.AcceptedContractAmount is { } accepted ? JsonValue.Create(accepted) : null);
            Put("confirmedAt", p.ConfirmedAt is { } confirmed ? JsonValue.Create(confirmed) : null);
            var movements = new JsonArray();
            foreach (var m in p.Movements)
            {
                var item = Base(m.Extra);
                item["id"] = m.Id.ToString(); item["date"] = IosDate(m.Date); item["description"] = m.Description; item["amount"] = m.Amount;
                item["direction"] = m.Direction == MovementDirection.Credit ? "credit" : "debit";
                if (m.SourceId is null) item.Remove("sourceID"); else item["sourceID"] = m.SourceId;
                if (m.SourceLineIds is null) item.Remove("sourceLineIDs"); else item["sourceLineIDs"] = new JsonArray(m.SourceLineIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
                if (m.RefundOf is null) item.Remove("refundOf"); else item["refundOf"] = m.RefundOf.Value.ToString();
                item["isRefund"] = m.IsRefund; item["isFunding"] = m.IsFunding; item["matched"] = m.Matched;
                movements.Add(item);
            }
            node["movements"] = movements;
            list.Add(node);
        }
        var body = new JsonObject { ["version"] = 1, ["periods"] = list };
        using var plist = new MemoryStream(); PropertyListParser.SaveAsBinary(new NSData(System.Text.Encoding.UTF8.GetBytes(body.ToJsonString())), plist); return plist.ToArray();
    }
    public static IReadOnlyList<BankPeriod> ReadFromBackup(BackupSnapshot snapshot)
    {
        if (!snapshot.SettingsPlists.TryGetValue("bankReconciliations", out var plist)) return [];
        try
        {
            if (PropertyListParser.Parse(plist) is not NSData data) throw Invalid("صيغة دفتر المطابقة غير صالحة.");
            if (data.Bytes.Length == 0) return [];
            using var document = JsonDocument.Parse(data.Bytes);
            var root = document.RootElement;
            if (root.GetProperty("version").GetInt32() != 1) throw Invalid("إصدار دفتر المطابقة غير مدعوم.");
            var periods = new List<BankPeriod>();
            foreach (var item in root.GetProperty("periods").EnumerateArray())
            {
                var movements = new List<BankMovement>();
                foreach (var m in item.GetProperty("movements").EnumerateArray())
                {
                    var direction = m.GetProperty("direction").GetString() switch { "credit" => MovementDirection.Credit, "debit" => MovementDirection.Debit, _ => throw Invalid("نوع حركة غير معروف.") };
                    movements.Add(new BankMovement(m.GetProperty("id").GetGuid(), IosDay(m.GetProperty("date")), m.GetProperty("description").GetString() ?? "", m.GetProperty("amount").GetInt64(), direction,
                        Optional(m, "sourceID")?.GetString(), Optional(m, "sourceLineIDs")?.EnumerateArray().Select(v => v.GetString() ?? "").ToArray(),
                        Optional(m, "refundOf")?.GetGuid(), Optional(m, "isRefund")?.GetBoolean() ?? false, Optional(m, "isFunding")?.GetBoolean() ?? false,
                        Optional(m, "matched")?.GetBoolean() ?? false, Unknown(m, MovementKeys)));
                }
                var period = new BankPeriod(item.GetProperty("id").GetGuid(), item.GetProperty("month").GetString() ?? "", IosDay(item.GetProperty("start")), IosDay(item.GetProperty("end")),
                    item.GetProperty("openingBalance").GetInt64(), item.GetProperty("memberFirst").GetString() ?? "", item.GetProperty("memberSecond").GetString() ?? "",
                    Optional(item, "acceptedContractMissing")?.GetInt32(), Optional(item, "acceptedContractAmount")?.GetInt64(), movements,
                    Committee(item), Optional(item, "confirmedAt")?.GetDouble(), Unknown(item, PeriodKeys));
                _ = Calculate(period); periods.Add(period);
                if (periods.Count > 5000) throw Invalid("عدد فترات المطابقة يتجاوز الحد المدعوم.");
            }
            if (periods.Select(p => p.Id).Distinct().Count() != periods.Count) throw Invalid("تتكرر فترة في دفتر المطابقة.");
            return periods;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException or KeyNotFoundException or IndexOutOfRangeException)
        { throw new InvalidDataException("تعذّر قراءة دفتر المطابقة من نسخة الآيفون.", ex); }
    }
}
