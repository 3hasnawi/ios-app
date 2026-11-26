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
        IReadOnlyDictionary<string, byte[]> references, bool acceptMissingContractNominal = false, BackupSnapshot? customization = null)
    {
        if (id is not ("food_company" or "food_tax" or "tax_social" or "contracts" or "cash_salary"))
            throw new InvalidDataException("نوع الكتاب غير مرتبط بمبالغ الرواتب.");
        if (summary.TransferWarnings.Count != 0) throw new InvalidDataException("مصادر الرواتب غير مكتملة؛ راجع بيانات الشهر قبل إعداد الكتاب.");
        var reference = LetterReferencePack.IndividualAccount(references, id, payerAccount) ?? throw new InvalidDataException("حساب المستفيد غير موجود في المرجع.");
        var account = customization is null ? reference : IndividualAccountCustomization.Resolve(customization, id, id, reference, reference) ?? reference;
        if (account == TransferPreparation.NormalizeAccount(payerAccount)) throw new InvalidDataException("حساب المستفيد يطابق حساب الدفع.");
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
                if (summary.ContractOverrideStale) throw new InvalidDataException("تغيّر أساس العقود بعد التعديل اليدوي؛ راجع المبلغ أولاً.");
                if (summary.MissingContractNominal > 0 && !acceptMissingContractNominal)
                    throw new InvalidDataException($"يوجد {summary.MissingContractNominal} عقد بلا راتب اسمي؛ راجع المبلغ وأكد التجاوز لإعداد الكتاب.");
                if (summary.MissingContractNominal > 0) notices.Add($"يوجد {summary.MissingContractNominal} عقد بلا راتب اسمي؛ يلزم تأكيد مراجعة المبلغ.");
                lines.Add(new("حصة التقاعد من الراتب الاسمي", Amount(summary.ContractNominalShare)));
                lines.Add(new("التوقيفات التقاعدية", Amount(summary.ContractRetirement)));
                if (summary.ManualContractOverride is not null && summary.ContractContribution != summary.CalculatedContractContribution)
                {
                    lines.Add(new("التعديل اليدوي", checked((long)(summary.ContractContribution - summary.CalculatedContractContribution))));
                    notices.Add("المبلغ النهائي معدّل يدوياً؛ يظهر فرق التعديل في الجدول.");
                }
                break;
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

public sealed record IndividualEntityPerson(string Name, string Rank, long Amount);
public sealed record IndividualEntityPlan(string Id, string Month, string Entity, string Payer, string PayerAccount,
    string? BeneficiaryAccount, string? BeneficiaryName, string? Bank, long Total, string Words, IReadOnlyList<IndividualEntityPerson> Rows, int People);

/// <summary>يطابق قيود الجهة مع المجموع والحساب قبل السماح بتجهيز كتابها.</summary>
public static class IndividualEntityPreparation
{
    public static IndividualEntityPlan Prepare(BackupSnapshot source, TransferSnapshot transfer, string id, string entryId,
        IReadOnlyDictionary<string, byte[]> references)
    {
        if (id is not ("ramadi" or "housing" or "water" or "rafidain_anbar")) throw new InvalidDataException("نوع كتاب الجهة غير معروف.");
        if (transfer.SourceWarnings.Count != 0) throw new InvalidDataException("راجع مصادر الشهر ومطابقة الاستقطاعات قبل إعداد الكتاب.");
        var found = transfer.Entries.Where(e => e.Id == entryId).ToArray();
        if (found.Length != 1) throw new InvalidDataException("جهة الكتاب غير موجودة أو مكررة.");
        var entry = found[0];
        if (entry.Month != transfer.Month || entry.Group != TransferGroup.Anbar || !entry.Id.StartsWith("deduction|", StringComparison.Ordinal) ||
            TransferPreparation.Issues(entry).Count != 0 || string.IsNullOrWhiteSpace(transfer.Payer))
            throw new InvalidDataException("راجع بيانات جهة الكتاب ومبلغها وتصنيفها.");
        var reference = LetterReferencePack.IndividualAccount(references, id, transfer.PayerAccount);
        string? account = null;
        if (id == "rafidain_anbar")
        {
            var title = string.Join(' ', PayrollAnalysis.Normalize(entry.Title).Replace('/', ' ').Replace('-', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (IndividualAccountCustomization.Read(source, id) is not null) throw new InvalidDataException("كتاب فرع الأنبار لا يقبل تخصيص حساب المستفيد.");
            if (title != PayrollAnalysis.Normalize("مصرف الرافدين فرع الانبار") || reference is not null)
                throw new InvalidDataException("اختر استقطاع مصرف الرافدين فرع الأنبار للكتاب.");
        }
        else
        {
            if (entry.Account is null) throw new InvalidDataException("اربط الجهة بحسابها أولاً.");
            account = id == "ramadi" ? TransferPreparation.NormalizeAccount(entry.Account.Iban) : TransferPreparation.LocalAccount(entry.Account);
            if (account is null || reference is null || (id == "ramadi" && (!TransferPreparation.ValidIban(account) || account == TransferPreparation.NormalizeAccount(transfer.PayerAccount))))
                throw new InvalidDataException("حساب الجهة غير صالح؛ راجع الحساب والربط قبل الإصدار.");
            var chosen = IndividualAccountCustomization.Resolve(source, id, entry.Title, account, reference);
            if (chosen is null && account != reference) throw new InvalidDataException("حساب الجهة لا يطابق مرجع الكتاب؛ راجع الحساب والربط قبل الإصدار.");
            account = chosen ?? account;
        }
        var deductions = source.Deductions.Where(d => d.Month == transfer.Month && d.Title == entry.Title && !PayrollAnalysis.IsInactive(d)).ToArray();
        if (deductions.Length == 0 || deductions.Any(d => string.IsNullOrWhiteSpace(d.Name) || d.Amount is null or < 0 || d.Amount != decimal.Truncate(d.Amount.Value) || d.Amount > 9_007_199_254_740_991m))
            throw new InvalidDataException("بيانات أسماء الجهة أو مبالغها ناقصة.");
        var people = deductions.Select(d => PayrollAnalysis.Normalize(d.Name).Replace(" ", "", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).Count();
        if (people != entry.People || deductions.Sum(d => d.Amount!.Value) != entry.Amount)
            throw new InvalidDataException("عدد الأسماء أو مجموع استقطاعات الجهة لا يطابق الكتاب.");
        if (id == "water" && (people != 1 || deductions.Any(d => string.IsNullOrWhiteSpace(d.Rank)) || deductions.Select(d => PayrollAnalysis.Normalize(d.Rank)).Distinct(StringComparer.Ordinal).Count() != 1))
            throw new InvalidDataException("كتاب الموارد المائية يتطلب منتسباً واحداً ورتبته.");
        var total = checked((long)entry.Amount);
        var rows = deductions.Select(d => new IndividualEntityPerson(d.Name, d.Rank, checked((long)d.Amount!.Value))).ToArray();
        return new(id, transfer.Month, entry.Title, transfer.Payer, TransferPreparation.NormalizeAccount(transfer.PayerAccount),
            account, entry.Account?.Entity ?? entry.Title, entry.Account?.Branch, total, ArabicAmountWords.Dinars(total), rows, people);
    }
}
