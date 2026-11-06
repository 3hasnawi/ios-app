using SalaryDesktop.Core;
namespace SalaryLocal.Core;
public sealed partial class LocalPayrollStore
{
    public MutationResult ConfirmTransferLetterSingle(LocalLetterPreview preview, string note)
    {
        using var c = Connect(); using var t = c.BeginTransaction(deferred: false); var current = CheckCurrent(c, t, preview.SourceRevision);
        var bytes = LocalTransferIssuance.Append(LetterSource(c, t, current, preview.Month), preview, note);
        var result = SaveLetterSetting(c, t, GroupIssuance.SettingsKey, bytes, current, "إقرار يدوي بإصدار كتاب منفرد خارج التطبيق");
        if (result.Changed) t.Commit(); return result;
    }
    public MutationResult RevokeTransferIssuance(string id, string expectedRevision)
    {
        using var c = Connect(); using var t = c.BeginTransaction(deferred: false); var current = CheckCurrent(c, t, expectedRevision);
        var bytes = LocalTransferIssuance.Revoke(LetterSource(c, t, current, null), id);
        var result = SaveLetterSetting(c, t, GroupIssuance.SettingsKey, bytes, current, "إلغاء إقرار إصدار فقط دون حذف الكتاب");
        if (result.Changed) t.Commit(); return result;
    }
}
