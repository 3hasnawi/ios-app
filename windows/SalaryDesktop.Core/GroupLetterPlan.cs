namespace SalaryDesktop.Core;

public sealed record GroupLetterLine(string Title, string Iban, string Branch, long Amount);
public sealed record GroupLetterPlan(string Month, TransferGroup Group, string Payer, string PayerAccount, long Total, string Words,
    IReadOnlyList<GroupLetterLine> Lines, byte[] Csv);
public static class GroupLetterPreparation
{
    public static void Verify(GroupLetterPlan plan)
    {
        if (plan.Group is not (TransferGroup.Rafidain or TransferGroup.OtherBanks) || plan.Lines.Count == 0 || plan.Total != plan.Lines.Sum(l => l.Amount) || plan.Words != ArabicAmountWords.Dinars(plan.Total)) throw new InvalidDataException("بيانات كتاب المجموعة غير متطابقة.");
        string text;
        try { text = new System.Text.UTF8Encoding(false, true).GetString(plan.Csv); }
        catch (System.Text.DecoderFallbackException ex) { throw new InvalidDataException("ترميز ملف التحويل غير صالح.", ex); }
        var rows = text.Split("\r\n", StringSplitOptions.None);
        const string header = "Reference|Value Date|Payer Name|Payer Account|Amount|Currency|Receiver BIC|Beneficiary Name|Beneficiary Account|Remittance Information|Details of Charges";
        if (rows.Length != plan.Lines.Count + 1 || rows[0] != header) throw new InvalidDataException("ملف التحويل لا يطابق جدول الكتاب.");
        string? day = null;
        for (int i = 0; i < plan.Lines.Count; i++)
        {
            var fields = rows[i + 1].Split('|'); var line = plan.Lines[i];
            if (fields.Length != 11 || !DateOnly.TryParseExact(fields[1], "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _) ||
                (day is not null && day != fields[1])) throw new InvalidDataException("تاريخ التحويل غير مطابق.");
            day ??= fields[1];
            if (fields[0] != day + (plan.Group == TransferGroup.Rafidain ? "30000" : "20000") + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) ||
                fields[2] != plan.Payer || fields[3] != plan.PayerAccount || fields[4] != line.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
                fields[5] != "IQD" || !TransferPreparation.ValidSwift(fields[6]) || fields[7] != line.Title || fields[8] != line.Iban ||
                fields[9] != "استقطاعات " + plan.Month || fields[10] != "SLEV") throw new InvalidDataException("أحد صفوف ملف التحويل لا يطابق جدول الكتاب.");
        }
    }
    public static GroupLetterPlan Prepare(TransferSnapshot snapshot, TransferGroup group, DateOnly date)
    {
        var csv = TransferPreparation.MakeCsv(snapshot, group, date);
        TransferPreparation.VerifyCsv(csv, snapshot, group, date);
        var entries = snapshot.Entries.Where(e => e.Group == group).ToArray();
        if (entries.Any(e => e.Month != snapshot.Month) || entries.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != entries.Length)
            throw new InvalidDataException("مصادر كتاب المجموعة مكررة أو تنتمي لشهر آخر.");
        var sum = entries.Sum(e => e.Amount);
        if (sum <= 0 || sum >= 1_000_000_000_000_000m || sum != decimal.Truncate(sum)) throw new InvalidDataException("مجموع كتاب المجموعة خارج النطاق المدعوم.");
        var lines = entries.Select(e => new GroupLetterLine(e.Beneficiary ?? e.Title, TransferPreparation.NormalizeAccount(e.Account!.Iban), e.Account.Branch, (long)e.Amount)).ToArray();
        if (lines.Sum(e => e.Amount) != (long)sum) throw new InvalidDataException("جدول الكتاب لا يطابق مجموع التحويل.");
        var result = new GroupLetterPlan(snapshot.Month, group, snapshot.Payer, TransferPreparation.NormalizeAccount(snapshot.PayerAccount), (long)sum, ArabicAmountWords.Dinars((long)sum), lines, csv);
        Verify(result); return result;
    }
}
