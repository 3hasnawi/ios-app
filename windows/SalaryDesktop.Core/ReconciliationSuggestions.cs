namespace SalaryDesktop.Core;

public sealed record ReconciliationSuggestion(string Id, string Title, long Amount, IReadOnlyList<string> SourceLineIds);
public sealed record ReconciliationSuggestionResult(IReadOnlyList<ReconciliationSuggestion> Items, IReadOnlyList<string> Warnings);
public static class ReconciliationSuggestions
{
    private const decimal Maximum = 9_007_199_254_740_991m;
    public static ReconciliationSuggestionResult Prepare(BackupSnapshot source, string month)
    {
        var summary = BankStatementCalculator.Calculate(source, month);
        var transfer = TransferPreparation.Prepare(source, month);
        var values = new List<ReconciliationSuggestion>();
        var warnings = new List<string>();
        void Add(string id, string title, decimal amount, params string[] lines)
        {
            if (amount == 0) return;
            if (amount < 0 || amount > Maximum || amount != decimal.Truncate(amount)) throw new InvalidDataException($"مبلغ {title} غير صالح للمطابقة.");
            values.Add(new(month + "|" + id, title, (long)amount, lines));
        }
        if (summary.MissingNet == 0)
        {
            Add("card", "رواتب البطاقة", summary.Card);
            Add("cash", "رواتب الكاش", summary.Cash, $"payroll|{month}|cashSalary");
            Add("unclassified", "رواتب غير مصنفة", summary.Unclassified);
        }
        else warnings.Add("توجد رواتب بلا صافي؛ لم تُقترح مبالغ الرواتب.");
        Add("leave", "الإجازات المتراكمة", summary.Manual.AccumulatedLeave);
        Add("rewards", "مكافآت المنتسبين", summary.Manual.EmployeeRewards);
        Add("single", "قائمة مفردة", summary.Manual.SingleList);
        if (summary.TransferWarnings.Count == 0)
        {
            var food = summary.Breakdown["شركة عراق الجوال"];
            var fund = decimal.Round(food * 0.03m, 0, MidpointRounding.AwayFromZero);
            var tax = decimal.Round((food - fund) * 0.018m, 0, MidpointRounding.AwayFromZero);
            Add("foodCompany", "شركة عراق الجوال", food - fund - tax, $"payroll|{month}|foodCompany");
            Add("foodTax", "ضريبة عقد الإطعام", tax, $"payroll|{month}|foodTax");
            Add("treasury", "الضريبة والحماية الاجتماعية", summary.Breakdown["الضريبة"] + summary.Breakdown["الحماية الاجتماعية"], $"payroll|{month}|incomeTax", $"payroll|{month}|socialProtection");
            if (summary.MissingContractNominal == 0)
                Add("contracts", "ضمان العقود", summary.ContractContribution, $"payroll|{month}|contractNominal", $"payroll|{month}|contractRetirement");
            else warnings.Add("يوجد عقد بلا اسمي؛ لم يُقترح مبلغ ضمان العقود.");
        }
        else warnings.Add("بعض مصادر الرواتب غير مكتملة؛ لم تُقترح مبالغ الكتب المعتمدة عليها.");
        string LineId(TransferEntry entry)
        {
            var recipient = entry.Id.StartsWith("payroll|", StringComparison.Ordinal) ? entry.Id[8..] : "execution";
            return $"deductions|{month}|false|{entry.Title}|{recipient}";
        }
        if (transfer.SourceWarnings.Count == 0 && transfer.Entries.All(e => e.Group != TransferGroup.Unresolved))
        {
            foreach (var (group, id, title) in new[] { (TransferGroup.Rafidain, "rafidain", "مجموعة الرافدين"), (TransferGroup.OtherBanks, "otherBanks", "مجموعة رشيد ومصارف أخرى") })
            {
                var entries = transfer.Entries.Where(e => e.Group == group).ToArray();
                if (entries.Any(e => TransferPreparation.Issues(e).Count != 0)) { warnings.Add($"تحتاج {title} مراجعة قبل اقتراح مبلغها."); continue; }
                if (entries.Length > 0) Add("group|" + id, title, entries.Sum(e => e.Amount), entries.Select(LineId).ToArray());
            }
            foreach (var entry in transfer.Entries.Where(e => e.Group == TransferGroup.Anbar && TransferPreparation.Issues(e).Count == 0))
            {
                var line = LineId(entry); Add("individual|" + line, entry.Title, entry.Amount, line);
            }
        }
        else warnings.Add("الجهات أو مصادر التحويل غير مكتملة؛ لم تُقترح مبالغ المجموعات والجهات المنفردة.");
        return new(values, warnings);
    }
    public static IReadOnlyList<BankMovement> Changed(BankPeriod period, ReconciliationSuggestionResult current)
    {
        var lookup = current.Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
        return period.Movements.Where(m => m.SourceId is not null && (!lookup.TryGetValue(m.SourceId, out var suggestion) ||
            suggestion.Amount != m.Amount || m.Direction != MovementDirection.Debit || !(m.SourceLineIds ?? []).ToHashSet(StringComparer.Ordinal).SetEquals(suggestion.SourceLineIds))).ToArray();
    }
    public static BankPeriod Add(BankPeriod period, ReconciliationSuggestion suggestion, DateOnly date)
    {
        var movement = new BankMovement(Guid.NewGuid(), date, suggestion.Title, suggestion.Amount, MovementDirection.Debit,
            suggestion.Id, suggestion.SourceLineIds.ToArray());
        var result = period with { Movements = period.Movements.Concat([movement]).ToArray() };
        _ = BankReconciliation.Calculate(result); return result;
    }
}
