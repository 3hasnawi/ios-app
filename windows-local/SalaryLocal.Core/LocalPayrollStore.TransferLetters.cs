using Microsoft.Data.Sqlite;
using SalaryDesktop.Core;

namespace SalaryLocal.Core;

public sealed partial class LocalPayrollStore
{
    private BackupSnapshot LetterSource(SqliteConnection c, SqliteTransaction t, string revision, string? month)
    {
        var months = new List<PayrollMonth>(); var employees = new List<Employee>(); var deductions = new List<Deduction>();
        foreach (var pointer in MonthPointers(c, t))
        {
            int Count(string? section) => section is null ? 0 : checked((int)(long)Scalar(c, t, "SELECT count FROM sections WHERE id=$0", section));
            months.Add(new(pointer.Name, Count(pointer.Payroll), Count(pointer.Deductions)));
            if (pointer.Name != month) continue;
            employees.AddRange(ReadRows(c, t, pointer.Payroll).Select(r => r.Employee! with { Month = pointer.Name, RawColumns = r.Raw }));
            deductions.AddRange(ReadRows(c, t, pointer.Deductions).Select(r => r.Deduction! with { Month = pointer.Name }));
        }
        var aux = ReadAuxiliarySnapshot(c, t);
        return new(DatabasePath, revision, aux.Version, months, employees, deductions, aux.StaffCount, aux.Accounts, aux.Books, aux.Settings) { CreatedAt = aux.CreatedAt };
    }
    private static MutationResult SaveLetterSetting(SqliteConnection c, SqliteTransaction t, string key, byte[] bytes, string current, string description)
    {
        if (bytes.Length > MaximumRowBytes) throw new InvalidDataException("حجم إعداد الكتب يتجاوز الحد المحلي؛ لم يُحفظ.");
        var state = CurrentAuxiliaryState(c, t); var entries = ReadSet(c, t, state.GetValueOrDefault("settings"));
        entries[key] = SaveValue(c, t, bytes); SetArea(c, t, state, "settings", entries);
        return SaveAuxiliaryRevision(c, t, state, description, Hash(bytes), current);
    }
    public MutationResult SaveTransferLetterSettings(string id, GroupLetterSettings value, bool commonFields, string expectedRevision)
    {
        using var c = Connect(); using var t = c.BeginTransaction(deferred: false); var current = CheckCurrent(c, t, expectedRevision);
        var source = LetterSource(c, t, current, null);
        var patched = LocalTransferLetterSettings.Patch(source, id, value, commonFields);
        var result = SaveLetterSetting(c, t, LocalTransferLetterSettings.SettingsKey, patched, current, "تخصيص كتاب تحويل: " + LocalTransferLetterSettings.Title(id));
        if (result.Changed) t.Commit(); return result;
    }
    public MutationResult ImportTransferLetterReferences(byte[] archive, string expectedRevision)
    {
        var owned = archive.ToArray(); var data = LocalTransferLetterSettings.ReferencePlist(owned);
        return SaveSettings(new Dictionary<string, byte[]> { [LocalTransferLetterSettings.ReferencesKey] = data }, expectedRevision, "استيراد مراجع كتب التحويل بعد المراجعة");
    }
    public MutationResult ImportTransferLetterFont(byte[] font, string expectedRevision)
    {
        var owned = font.ToArray(); var data = LocalTransferLetterSettings.FontPlist(owned);
        return SaveSettings(new Dictionary<string, byte[]> { [LocalTransferLetterSettings.FontKey] = data }, expectedRevision, "استيراد خط ساكال العريض للكتب");
    }
    public void SaveTransferLetterPackage(LocalLetterPackage package, string destination)
    {
        ArgumentNullException.ThrowIfNull(package); var bytes = package.Zip();
        // قفل الكتابة يغطي فحص المراجعة والحفظ؛ لا يمكن لحساب أو شهر جديد المرور بينهما.
        using var c = Connect(); using var t = c.BeginTransaction(deferred: false); var current = CheckCurrent(c, t, package.Preview.SourceRevision);
        LocalTransferLetters.VerifySource(LetterSource(c, t, current, package.Preview.Month), package.Preview);
        LocalTransferLetters.SaveNew(bytes, destination); t.Commit();
    }
    public MutationResult ConfirmTransferLetterGroup(LocalLetterPreview preview, string note)
    {
        if (preview.Issuance is not { } reviewed || preview.Group is null) throw new InvalidDataException("إقرار الإصدار متاح للمجموعتين بعد المراجعة فقط.");
        using var c = Connect(); using var t = c.BeginTransaction(deferred: false); var current = CheckCurrent(c, t, preview.SourceRevision);
        var source = LetterSource(c, t, current, preview.Month);
        LocalTransferLetters.VerifySource(source, preview);
        _ = LocalTransferLetterSettings.ReadObject(source, GroupIssuance.SettingsKey);
        var fresh = LocalTransferLetters.Prepare(source, preview.Month, preview.Id, null, preview.Date).Issuance!;
        if (fresh.Month != reviewed.Month || fresh.Group != reviewed.Group || fresh.Payer != reviewed.Payer || fresh.PayerIban != reviewed.PayerIban ||
            fresh.BaselineMonth != reviewed.BaselineMonth || !fresh.Lines.SequenceEqual(reviewed.Lines) || !fresh.Changes.SequenceEqual(reviewed.Changes))
            throw new InvalidDataException("تغيّرت تفاصيل المجموعة؛ أعد فتح مراجعتها.");
        // GroupIssuance.Append يضيف سجلاً إلى JSON الأصلي دون إعادة بناء السجلات السابقة أو حذف حقولها.
        var bytes = GroupIssuance.Append(source, fresh, note);
        var settings = new Dictionary<string, byte[]>(source.SettingsPlists) { [GroupIssuance.SettingsKey] = bytes };
        GroupIssuance.Validate(source with { SettingsPlists = settings });
        var result = SaveLetterSetting(c, t, GroupIssuance.SettingsKey, bytes, current, "إقرار يدوي بإصدار مجموعة: " + preview.Month);
        if (result.Changed) t.Commit(); return result;
    }
}
