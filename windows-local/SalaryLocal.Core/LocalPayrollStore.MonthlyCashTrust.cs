using SalaryDesktop.Core;

namespace SalaryLocal.Core;

public sealed partial class LocalPayrollStore
{
    public MutationResult SaveMonthlyCashTrust(MonthlyCashTrustPreview preview)
    {
        if (preview is null) throw new InvalidDataException("راجع كاش الشهر قبل حفظ الأمانات.");
        // إعادة الحساب والكتابة ضمن القفل نفسه؛ لا يمكن لاستيراد آخر المرور بينهما.
        using var c = Connect(); using var t = c.BeginTransaction(deferred: false);
        string current = CheckCurrent(c, t, preview.SourceRevision);
        var summaries = new List<PayrollMonth>(); var employees = new List<Employee>(); var deductions = new List<Deduction>();
        foreach (var pointer in MonthPointers(c, t))
        {
            int Count(string? section) => section is null ? 0 : checked((int)(long)Scalar(c, t, "SELECT count FROM sections WHERE id=$0", section));
            summaries.Add(new(pointer.Name, Count(pointer.Payroll), Count(pointer.Deductions)));
            if (pointer.Name != preview.Month) continue;
            employees.AddRange(ReadRows(c, t, pointer.Payroll).Select(r => r.Employee! with { Month = pointer.Name, RawColumns = r.Raw }));
            deductions.AddRange(ReadRows(c, t, pointer.Deductions).Select(r => r.Deduction! with { Month = pointer.Name }));
        }
        var aux = ReadAuxiliarySnapshot(c, t);
        var source = new BackupSnapshot(DatabasePath, current, aux.Version, summaries, employees, deductions,
            aux.StaffCount, aux.Accounts, aux.Books, aux.Settings) { CreatedAt = aux.CreatedAt };
        var patched = LocalMonthlyCashTrust.RevalidateAndPatch(source, preview);
        if (patched.Length > MaximumRowBytes) throw new InvalidDataException("حجم دفتر الأمانات يتجاوز الحد المحلي المدعوم؛ لم تُحفظ الإضافات.");
        if (preview.IsNoOp) return new(false, current);
        var state = CurrentAuxiliaryState(c, t); var settings = ReadSet(c, t, state.GetValueOrDefault("settings"));
        settings[SalaryTrust.SettingsKey] = SaveValue(c, t, patched); SetArea(c, t, state, "settings", settings);
        var result = SaveAuxiliaryRevision(c, t, state, "إضافة كاش الشهر إلى الأمانات بعد المراجعة", Hash(patched), current);
        if (result.Changed) t.Commit(); return result;
    }
}
