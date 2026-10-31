using System.Text.Json;
using Claunia.PropertyList;

namespace SalaryDesktop.Core;

public enum MovementDirection { Credit, Debit }
public sealed record BankMovement(Guid Id, DateOnly Date, string Description, long Amount, MovementDirection Direction,
    string? SourceId = null, IReadOnlyList<string>? SourceLineIds = null, Guid? RefundOf = null,
    bool IsRefund = false, bool IsFunding = false, bool Matched = false);
public sealed record BankPeriod(Guid Id, string Month, DateOnly Start, DateOnly End, long OpeningBalance,
    string FirstMember, string SecondMember, int? AcceptedContractMissing, long? AcceptedContractAmount,
    IReadOnlyList<BankMovement> Movements);
public sealed record BankMovementLine(BankMovement Movement, long Balance);
public sealed record OutstandingRefund(BankMovement Movement, long Remaining);
public sealed record ReconciliationTotals(long Credits, long Funding, long Debits, long Balance,
    IReadOnlyList<BankMovementLine> Lines, IReadOnlyList<OutstandingRefund> OutstandingRefunds);

public static class BankReconciliation
{
    private const long Maximum = 9_007_199_254_740_991;
    private static InvalidDataException Invalid(string message) => new(message);
    public static ReconciliationTotals Calculate(BankPeriod period)
    {
        if (period.Id == Guid.Empty || string.IsNullOrWhiteSpace(period.Month) || period.Start > period.End ||
            period.OpeningBalance is < 0 or > Maximum || period.FirstMember.Length > 150 || period.SecondMember.Length > 150 ||
            period.Movements.Count > 20_000 || period.Movements.Select(m => m.Id).Distinct().Count() != period.Movements.Count ||
            !((period.AcceptedContractMissing is null && period.AcceptedContractAmount is null) ||
              (period.AcceptedContractMissing > 0 && period.AcceptedContractAmount is >= 0 and <= Maximum)))
            throw Invalid("بيانات فترة المطابقة غير صالحة.");
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
                        Optional(m, "matched")?.GetBoolean() ?? false));
                }
                var period = new BankPeriod(item.GetProperty("id").GetGuid(), item.GetProperty("month").GetString() ?? "", IosDay(item.GetProperty("start")), IosDay(item.GetProperty("end")),
                    item.GetProperty("openingBalance").GetInt64(), item.GetProperty("memberFirst").GetString() ?? "", item.GetProperty("memberSecond").GetString() ?? "",
                    Optional(item, "acceptedContractMissing")?.GetInt32(), Optional(item, "acceptedContractAmount")?.GetInt64(), movements);
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
