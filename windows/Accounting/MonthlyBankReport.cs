namespace SalaryDesktop.Core;

public static class MonthlyBankReport
{
    public static ReportSheet Sheet(BackupSnapshot source, BankSummary summary)
    {
        var rows = new List<string[]>();
        var amounts = new List<decimal>();
        void Add(string section, string title, decimal amount)
        {
            rows.Add([section, title, ""]);
            amounts.Add(amount);
        }

        const string pay = "صافي الرواتب", additions = "مبالغ تضاف للرواتب",
            deductions = "الاستقطاعات المحتسبة", contracts = "الضمان الاجتماعي للعقود", execution = "مطابقة الاستقطاعات التنفيذية";
        Add(pay, "رواتب بطاقة", summary.Card);
        Add(pay, "رواتب كاش", summary.Cash);
        if (summary.UnclassifiedCount > 0) Add(pay, "رواتب غير مصنفة", summary.Unclassified);
        Add(pay, "مجموع صافي الرواتب", summary.Net);
        Add(additions, "الإجازات المتراكمة", summary.Manual.AccumulatedLeave);
        Add(additions, "مكافآت المنتسبين", summary.Manual.EmployeeRewards);
        Add(additions, "قائمة مفردة", summary.Manual.SingleList);
        Add(deductions, "مجموع استقطاعات المصارف", summary.BankDeductions);
        foreach (var (label, amount) in summary.Breakdown) Add(deductions, label, amount);
        Add(contracts, "مجموع الاسمي للعقود", summary.ContractNominal);
        Add(contracts, "١٢٪ من اسمي العقود", summary.ContractNominalShare);
        Add(contracts, "التوقيفات التقاعدية", summary.ContractRetirement);
        Add(contracts, "المبلغ المحسوب تلقائياً", summary.CalculatedContractContribution);
        Add(contracts, "المبلغ النهائي المستخدم", summary.ContractContribution);
        Add(execution, "مجموع ملف جهات التنفيذ", summary.ExecutionDeductions);
        Add(execution, "الفرق مع استقطاعات المصارف", summary.ExecutionDifference);
        Add("الإجمالي", "مجموع كشف حساب البنك", summary.BankTotal);

        var title = $"كشف حساب البنك · {summary.Month}";
        if (!summary.ExecutionMatched || summary.ContractOverrideStale || summary.MissingNet > 0)
            title += " · للمراجعة";
        return new(ReportSheet.HeaderFor(source), title,
            [new("القسم", 28, ReportColumnKind.Text), new("البيان", 38, ReportColumnKind.Text), new("المبلغ (دينار)", 22, ReportColumnKind.Amount)],
            rows, amounts, ShowTotal: false, ForcePortrait: true);
    }
}
