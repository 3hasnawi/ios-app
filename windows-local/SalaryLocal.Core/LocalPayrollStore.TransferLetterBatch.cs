namespace SalaryLocal.Core;

public sealed partial class LocalPayrollStore
{
    public void VerifyTransferLetterBatch(LocalLetterBatchPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        using var c = Connect(); using var t = c.BeginTransaction(deferred: false);
        var current = CheckCurrent(c, t, preview.SourceRevision); var source = LetterSource(c, t, current, preview.Month);
        foreach (var letter in preview.Letters) LocalTransferLetters.VerifySource(source, letter);
        t.Commit();
    }
    public void SaveTransferLetterBatch(LocalLetterBatchPackage package, string destination)
    {
        ArgumentNullException.ThrowIfNull(package); var bytes = package.Zip();
        using var c = Connect(); using var t = c.BeginTransaction(deferred: false);
        var current = CheckCurrent(c, t, package.Preview.SourceRevision); var source = LetterSource(c, t, current, package.Preview.Month);
        foreach (var letter in package.Preview.Letters) LocalTransferLetters.VerifySource(source, letter);
        LocalTransferLetters.SaveNew(bytes, destination); t.Commit();
    }
}
