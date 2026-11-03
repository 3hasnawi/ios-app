using System.Text.Json;

namespace SalaryDesktop.Core;

public enum IbanAuditStatus { Valid, Missing, Invalid, Duplicate }
public sealed record IbanAuditRow(string Name, string Rank, string Workplace, decimal? Net, string Iban, IbanAuditStatus Status, string Detail);
public sealed record IbanAuditResult(string Month, IReadOnlyList<IbanAuditRow> Rows);

/// تدقيق قراءة فقط لرواتب الشهر، ولا يثبت ملكية الحساب أو صلاحيته لدى المصرف.
public static class IbanAudit
{
    public static IbanAuditResult Prepare(BackupSnapshot snapshot, string month)
    {
        if (MonthNames.SortKey(month) == 0 || !snapshot.Months.Any(m => m.Name == month)) throw new InvalidDataException("اختر شهراً محفوظاً صالحاً.");
        var data = snapshot.Employees.Where(e => e.Month == month).Select(e => (Employee: e, Iban: ReadIban(e))).ToArray();
        var duplicates = data.Where(x => x.Iban.Length > 0)
            .GroupBy(x => x.Iban, StringComparer.Ordinal)
            .Where(g => g.Select(x => PayrollAnalysis.Normalize(x.Employee.Name).Replace(" ", "", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        return new(month, data.Select(x =>
        {
            var status = x.Iban.Length == 0 ? IbanAuditStatus.Missing : duplicates.Contains(x.Iban) ? IbanAuditStatus.Duplicate : Valid(x.Iban) ? IbanAuditStatus.Valid : IbanAuditStatus.Invalid;
            var detail = status switch { IbanAuditStatus.Missing => "لم يُسجّل IBAN في بيانات الراتب", IbanAuditStatus.Duplicate => "الحساب مستخدم لأكثر من اسم؛ راجع الهوية", IbanAuditStatus.Invalid => "رقم الحساب يحتاج مراجعة", _ => "اجتاز فحص الصيغة ورقم التحقق" };
            return new IbanAuditRow(x.Employee.Name, x.Employee.Rank, x.Employee.Workplace, x.Employee.Net, x.Iban, status, detail);
        }).ToArray());
    }
    public static bool Valid(string iban) => iban.Length == 23 && iban.StartsWith("IQ", StringComparison.Ordinal)
        && iban.Skip(2).Take(2).All(char.IsAsciiDigit) && iban.Skip(4).Take(4).All(c => c is >= 'A' and <= 'Z')
        && iban.Skip(8).All(char.IsAsciiDigit) && TransferPreparation.ValidIban(iban);
    private static string ReadIban(Employee employee)
    {
        try
        {
            var details = employee.Details is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(employee.Details);
            return TransferPreparation.NormalizeAccount(details?.FirstOrDefault(p => p.Key.Equals("IBAN", StringComparison.OrdinalIgnoreCase)).Value ?? "");
        }
        catch (JsonException ex) { throw new InvalidDataException("تعذّر قراءة بيانات أحد الرواتب لتدقيق الحسابات.", ex); }
    }
}
