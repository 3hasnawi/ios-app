using SalaryDesktop.Core;
namespace SalaryLocal.Core;
public sealed partial class LocalPayrollStore
{
    public MutationResult SaveTrustLedger(BackupSnapshot source, SalaryTrustLedger merged, string description)
    {
        var bytes = LocalSalaryTrust.PatchOpeningImport(source, merged);
        return SaveSettings(new Dictionary<string, byte[]> { [SalaryTrust.SettingsKey] = bytes }, source.SourceDigest, description);
    }
    public MutationResult SaveSalaryTrustEntry(SalaryTrustEntry updated, string expectedRevision)
    {
        // Snapshot validation and the subsequent write both require the same revision.
        // Any concurrent change to settings/books/payroll invalidates the whole operation.
        var source = GetSnapshot();
        if (source.SourceDigest != expectedRevision) throw new InvalidDataException("تغيّر الدفتر؛ أعد فتح القيد قبل الحفظ.");
        var bytes = LocalSalaryTrust.Patch(source, updated);
        return SaveSettings(new Dictionary<string, byte[]> { [SalaryTrust.SettingsKey] = bytes }, expectedRevision, "تعديل قيد أمانة محلي");
    }
}
