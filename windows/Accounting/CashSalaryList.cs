using System.Text.Json;

namespace SalaryDesktop.Core;

public sealed record CashSalaryPerson(string Name, string Rank, string Workplace, long Net, string? CaseStatus = null);
public sealed record CashSalaryList(string Month, IReadOnlyList<CashSalaryPerson> Rows, long Total);

public static class CashSalaryListPreparation
{
    private static string? CaseStatus(Employee employee)
    {
        if (employee.RawColumns is null) return null;
        try
        {
            using var json = JsonDocument.Parse(employee.RawColumns);
            var headers = json.RootElement.GetProperty("headers").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            var values = json.RootElement.GetProperty("values").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            if (headers.Length != values.Length) return null;
            var names = new[] { "صافي الراتب", "الراتب الصافي", "الصافي", "صافي", "net salary", "net" }.Select(PayrollAnalysis.Normalize).ToHashSet(StringComparer.Ordinal);
            var net = Enumerable.Range(0, headers.Length).Where(i => names.Contains(PayrollAnalysis.Normalize(headers[i]))).ToArray();
            if (net.Length != 1 || net[0] + 1 >= headers.Length) return null;
            var next = net[0] + 1;
            var header = PayrollAnalysis.Normalize(headers[next]);
            return header.StartsWith(PayrollAnalysis.Normalize("الملاحظات"), StringComparison.Ordinal) ||
                   header.StartsWith(PayrollAnalysis.Normalize("ملاحظات"), StringComparison.Ordinal) || header == PayrollAnalysis.Normalize("ملاحظة") ? values[next].Trim() : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { return null; }
    }
    public static CashSalaryList Prepare(BackupSnapshot source, string month)
    {
        var summary = BankStatementCalculator.Calculate(source, month);
        if (summary.MissingNet > 0 || summary.UnclassifiedCount > 0)
            throw new InvalidDataException("توجد رواتب بلا صافي أو طريقة دفع؛ راجع بيانات الشهر قبل تجهيز قائمة الكاش.");
        var rows = new List<CashSalaryPerson>();
        foreach (var employee in source.Employees.Where(e => e.Month == month))
        {
            Dictionary<string, string> details;
            try { details = employee.Details is null ? new() : JsonSerializer.Deserialize<Dictionary<string, string>>(employee.Details) ?? throw new JsonException(); }
            catch (JsonException ex) { throw new InvalidDataException("تعذّر قراءة طريقة دفع أحد الرواتب.", ex); }
            var payment = PayrollAnalysis.Normalize(details.GetValueOrDefault("طريقة الدفع", "") ?? "");
            if (!payment.Contains("كاش", StringComparison.Ordinal) && !payment.Contains("نقد", StringComparison.Ordinal)) continue;
            if (employee.Net is null or < 0 || employee.Net != decimal.Truncate(employee.Net.Value) || employee.Net > 9_007_199_254_740_991m)
                throw new InvalidDataException("صافي أحد رواتب الكاش غير صالح.");
            if (employee.Net == 0) continue;
            if (string.IsNullOrWhiteSpace(employee.Name) || string.IsNullOrWhiteSpace(employee.Rank) || string.IsNullOrWhiteSpace(employee.Workplace))
                throw new InvalidDataException("اسم أحد منتسبي الكاش أو رتبته أو محل عمله ناقص.");
            rows.Add(new(employee.Name, employee.Rank, employee.Workplace, checked((long)employee.Net.Value), CaseStatus(employee)));
        }
        var total = rows.Sum(row => (decimal)row.Net);
        if (rows.Count == 0 || total != summary.Cash || total > 9_007_199_254_740_991m)
            throw new InvalidDataException("مجموع قائمة الكاش لا يطابق رواتب الشهر أو أن القائمة فارغة.");
        return new(month, rows, checked((long)total));
    }
}
