namespace SalaryDesktop.Core;

public sealed record IndividualLetterLine(string Title, long Amount);
public sealed record IndividualLetterPlan(string Id, string Month, string Payer, string PayerAccount,
    string? BeneficiaryAccount, long Total, string Words, IReadOnlyList<IndividualLetterLine> Lines,
    string? Entity, int? People, IReadOnlyList<string> Notices);

/// <summary>Prepares amounts only; generating and exporting the individual document requires a separate renderer.</summary>
public static class IndividualLetterPreparation
{
    private const decimal Maximum = 9_007_199_254_740_991m;
    private static long Amount(decimal value)
    {
        if (value < 0 || value > Maximum || value != decimal.Truncate(value))
            throw new InvalidDataException("المبلغ غير صالح للكتاب؛ راجع بيانات الشهر.");
        return (long)value;
    }
    public static IndividualLetterPlan Payroll(string id, BankSummary summary, string payer, string payerAccount,
        IReadOnlyDictionary<string, byte[]> references, bool acceptMissingContractNominal = false)
    {
        if (id is not ("food_company" or "food_tax" or "tax_social" or "contracts" or "cash_salary"))
            throw new InvalidDataException("نوع الكتاب غير مرتبط بمبالغ الرواتب.");
        if (summary.TransferWarnings.Count != 0) throw new InvalidDataException("مصادر الرواتب غير مكتملة؛ راجع بيانات الشهر قبل إعداد الكتاب.");
        var account = LetterReferencePack.IndividualAccount(references, id, payerAccount);
        var lines = new List<IndividualLetterLine>(); var notices = new List<string>();
        switch (id)
        {
            case "food_company": case "food_tax":
                var food = summary.Breakdown["شركة عراق الجوال"];
                var fund = decimal.Round(food * .03m, 0, MidpointRounding.AwayFromZero);
                var tax = decimal.Round((food - fund) * .018m, 0, MidpointRounding.AwayFromZero);
                lines.Add(new(id == "food_company" ? "حصة شركة عراق الجوال" : "ضريبة عقد الإطعام", Amount(id == "food_company" ? food - fund - tax : tax))); break;
            case "tax_social":
                lines.Add(new("الحماية الاجتماعية", Amount(summary.Breakdown["الحماية الاجتماعية"])));
                lines.Add(new("ضريبة الدخل", Amount(summary.Breakdown["الضريبة"]))); break;
            case "contracts":
                if (summary.MissingContractNominal > 0 && !acceptMissingContractNominal)
                    throw new InvalidDataException($"يوجد {summary.MissingContractNominal} عقد بلا راتب اسمي؛ راجع المبلغ وأكد التجاوز لإعداد الكتاب.");
                if (summary.MissingContractNominal > 0) notices.Add($"تم تجاوز تنبيه {summary.MissingContractNominal} عقد بلا راتب اسمي بعد مراجعة المستخدم.");
                lines.Add(new("حصة التقاعد من الراتب الاسمي", Amount(summary.ContractNominalShare)));
                lines.Add(new("التوقيفات التقاعدية", Amount(summary.ContractRetirement))); break;
            case "cash_salary":
                if (summary.MissingNet > 0 || summary.UnclassifiedCount > 0) throw new InvalidDataException("توجد رواتب بلا صافي أو طريقة دفع؛ راجع الرواتب قبل إعداد كتاب الكاش.");
                lines.Add(new("رواتب الكاش", Amount(summary.Cash))); break;
        }
        var total = Amount(lines.Sum(line => (decimal)line.Amount));
        if (total == 0) throw new InvalidDataException("مبلغ الكتاب صفر؛ راجع بيانات الشهر.");
        if (string.IsNullOrWhiteSpace(payer)) throw new InvalidDataException("اسم الجهة الدافعة غير موجود.");
        return new(id, summary.Month, payer, TransferPreparation.NormalizeAccount(payerAccount), account, total,
            ArabicAmountWords.Dinars(total), lines, null, null, notices);
    }
}
