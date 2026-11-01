namespace SalaryDesktop.Core;

public sealed record NameListRow(int Number, string Name, string Rank, string Workplace, long Amount);
public sealed record NameList(string Id, string Title, string Month, IReadOnlyList<NameListRow> Rows, long Total, string? Problem);

public static class NameLists
{
    public static IReadOnlyList<NameList> Prepare(BackupSnapshot source, TransferSnapshot transfer)
    {
        var lists = new List<NameList>();
        try
        {
            var cash = CashSalaryListPreparation.Prepare(source, transfer.Month);
            lists.Add(new("cash", "رواتب الكاش", transfer.Month, cash.Rows.Select((r, i) => new NameListRow(i + 1, r.Name, r.Rank, r.Workplace, r.Net)).ToArray(), cash.Total, null));
        }
        catch (InvalidDataException ex) { lists.Add(new("cash", "رواتب الكاش", transfer.Month, [], 0, ex.Message)); }
        foreach (var entry in transfer.Entries.Where(e => e.Group == TransferGroup.Anbar && e.Id.StartsWith("deduction|", StringComparison.Ordinal)))
        {
            var deductions = source.Deductions.Where(d => d.Month == transfer.Month && d.Title == entry.Title && !PayrollAnalysis.IsInactive(d)).ToArray();
            var valid = deductions.Length > 0 && entry.Month == transfer.Month && entry.MissingAmounts == 0 &&
                deductions.All(d => !string.IsNullOrWhiteSpace(d.Name) && d.Amount is >= 0 and <= 9_007_199_254_740_991m && d.Amount == decimal.Truncate(d.Amount.Value)) &&
                deductions.Sum(d => d.Amount ?? 0) == entry.Amount && deductions.Select(d => PayrollAnalysis.Normalize(d.Name).Replace(" ", "", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).Count() == entry.People;
            if (!valid) { lists.Add(new(entry.Id, entry.Title, transfer.Month, [], 0, "الأسماء أو المبالغ لا تطابق استقطاعات الجهة؛ راجع بيانات الشهر.")); continue; }
            var rows = deductions.Select((d, i) => new NameListRow(i + 1, d.Name, d.Rank, "", checked((long)d.Amount!.Value))).ToArray();
            lists.Add(new(entry.Id, entry.Title, transfer.Month, rows, checked((long)entry.Amount), null));
        }
        return lists;
    }
}
