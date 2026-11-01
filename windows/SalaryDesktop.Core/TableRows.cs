using System.Globalization;

namespace SalaryDesktop.Core;

/// صف جدول الرواتب: بيانات الراتب مع حقول التفاصيل التي تحتاجها التصفية والأعمدة الإضافية.
public sealed record PayrollRow(Employee Employee, string Payment, string Unified, string Grade, string Bank, string Iban, decimal? Gross, decimal? TotalDeductions)
{
    public string Month => Employee.Month;
    public string Name => Employee.Name;
    public string Rank => Employee.Rank;
    public string Workplace => Employee.Workplace;
    public decimal? Net => Employee.Net;
}

public static class PayrollRows
{
    /// تفاصيل الراتب التالفة لا توقف الجدول: تبقى حقولها فارغة ويظهر الراتب ببياناته الأساسية.
    public static PayrollRow Row(Employee employee)
    {
        Dictionary<string, string> details;
        try { details = PersonProfiles.Details(employee); }
        catch (InvalidDataException) { details = new(StringComparer.Ordinal); }
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in details) normalized.TryAdd(PayrollAnalysis.Normalize(key), value?.Trim() ?? "");
        string Get(string key) => normalized.GetValueOrDefault(PayrollAnalysis.Normalize(key), "");
        decimal? Money(string key) => Get(key) is { Length: > 0 } value ? PersonProfiles.Amount(value) : null;
        return new PayrollRow(employee, Get("طريقة الدفع"), Get("الرقم الموحد"), Get("الدرجة الوظيفية"), Get("توطين المصرف"), Get("IBAN"), Money("الاستحقاق"), Money("مجموع الاستقطاعات"));
    }

    public static PayrollRow[] Build(IEnumerable<Employee> employees) => employees.Select(Row).ToArray();
}

/// كشف بالشكل المعتمد من جدول معروض: تسلسل ثم الأعمدة الظاهرة بترتيبها، وعمود مبلغ واحد اختياري له مجموع.
/// المبلغ الناقص يظهر «—» ولا يدخل المجموع.
public static class TableReport
{
    public static ReportSheet Build(string header, string title, IReadOnlyList<string> columns, int amountColumn, IReadOnlyList<string[]> rows, IReadOnlyList<decimal?> amounts)
    {
        if (columns.Count == 0) throw new InvalidDataException("لا توجد أعمدة ظاهرة للتصدير.");
        if (rows.Count == 0) throw new InvalidDataException("لا توجد صفوف معروضة للتصدير.");
        if (amountColumn >= columns.Count || (amountColumn >= 0 && amounts.Count != rows.Count)) throw new InvalidDataException("مبالغ الجدول لا تطابق صفوفه.");
        var reportColumns = new List<ReportColumn> { new("ت", 5, ReportColumnKind.Serial) };
        reportColumns.AddRange(columns.Select((title, c) => new ReportColumn(title, 12, c == amountColumn ? ReportColumnKind.Amount : ReportColumnKind.Text)));
        var data = rows.Select((row, i) =>
        {
            if (row.Length != columns.Count) throw new InvalidDataException("صفوف الجدول لا تطابق أعمدته.");
            var values = new string[columns.Count + 1];
            values[0] = (i + 1).ToString(CultureInfo.InvariantCulture);
            for (int c = 0; c < row.Length; c++) values[c + 1] = c == amountColumn ? (amounts[i] is null ? "—" : "") : row[c] ?? "";
            return values;
        }).ToArray();
        var totals = amountColumn >= 0 ? amounts.Select(a => a ?? 0m).ToArray() : Array.Empty<decimal>();
        return new ReportSheet(header, title, reportColumns, data, totals, amountColumn >= 0).Fitted();
    }
}
