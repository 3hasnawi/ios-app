namespace SalaryLocal.Core;
public sealed partial class LocalPayrollStore
{
    public MutationResult SaveTransferRecipient(LocalRecipientReview review, string? chosen)
    {
        using var c = Connect(); using var t = c.BeginTransaction(deferred: false); var current = CheckCurrent(c, t, review.Revision);
        var bytes = LocalTransferRecipients.Patch(LetterSource(c, t, current, review.Month), review, chosen);
        var result = SaveLetterSetting(c, t, LocalTransferLetterSettings.SettingsKey, bytes, current, "مراجعة حساب مستفيد كتاب تحويل");
        if (result.Changed) t.Commit(); return result;
    }
}
