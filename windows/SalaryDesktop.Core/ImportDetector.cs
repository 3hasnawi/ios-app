using System.Globalization;
using System.Text;
using ExcelDataReader;

namespace SalaryDesktop.Core;

/// أنواع الملفات التي يتعرف عليها الاستيراد الموحّد (مثل ImportCenter في الآيفون).
public enum ImportKind { Backup, Payroll, Annual, Accounts, Unknown }
public sealed record ImportCandidate(string Path, ImportKind Kind, string? Month, string Note)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public string KindText => Kind switch
    {
        ImportKind.Backup => "نسخة احتياطية",
        ImportKind.Payroll => "رواتب واستقطاعات شهر",
        ImportKind.Annual => "استقطاعات سنوية",
        ImportKind.Accounts => "حسابات جهات التنفيذ",
        _ => "غير معروف",
    };
}

/// يتعرف على نوع الملف من امتداده وعناوين أعمدته في أول 20 صفاً من كل ورقة، دون قراءة البيانات أو حفظها.
public static class ImportDetector
{
    private static readonly string[] Entities = ["جهة التنفيذ", "الجهة", "المديرية"];
    private static readonly string[] Ibans = ["رقم الايبان", "الايبان", "iban"];
    private static readonly string[] MonthColumns = ["شهر", "الشهر", "month"];

    public static ImportCandidate Detect(string path, BackupSnapshot? source)
    {
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".zip") return new(path, ImportKind.Backup, null, "يُفحص فهرسها قبل فتحها؛ ملف الآيفون الأصلي لا يتغير.");
        if (extension != ".xlsx") return new(path, ImportKind.Unknown, null, "الامتداد غير مدعوم؛ الملفات المقبولة: نسخة ZIP أو ملف Excel ‏(xlsx).");
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is <= 0 or > 100 * 1024 * 1024) return new(path, ImportKind.Unknown, null, "حجم ملف Excel غير مدعوم.");
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            using var reader = ExcelReaderFactory.CreateOpenXmlReader(file);
            bool payroll = false, deductions = false, annual = false, accounts = false;
            do
            {
                int row = 0;
                while (row < 20 && reader.Read())
                {
                    row++;
                    if (reader.FieldCount > 500) break;
                    var headers = Enumerable.Range(0, reader.FieldCount)
                        .Select(i => PayrollAnalysis.Normalize(Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)?.Trim() ?? "")).ToArray();
                    var used = new HashSet<int>();
                    var name = PayrollSpreadsheet.Find(headers, PayrollSpreadsheet.Names, used, true);
                    if (name >= 0)
                    {
                        if (PayrollSpreadsheet.Find(headers, PayrollSpreadsheet.Nets, used) >= 0) { payroll = true; break; }
                        if (PayrollSpreadsheet.Find(headers, PayrollSpreadsheet.Amounts, used) >= 0)
                        {
                            if (PayrollSpreadsheet.Find(headers, MonthColumns, new HashSet<int>(), true) >= 0) annual = true; else deductions = true;
                            break;
                        }
                        continue;
                    }
                    var entityUsed = new HashSet<int>();
                    if (PayrollSpreadsheet.Find(headers, Entities, entityUsed, true) >= 0 && PayrollSpreadsheet.Find(headers, Ibans, entityUsed) >= 0) { accounts = true; break; }
                }
            } while (reader.NextResult());
            var month = SuggestedMonth(path, source);
            if (annual && !payroll && !deductions) return new(path, ImportKind.Annual, null, "يُقسّم إلى أشهر حسب عمود الشهر وسنة اسم الملف.");
            if (payroll || deductions)
            {
                var what = payroll && deductions ? "رواتب واستقطاعات" : payroll ? "رواتب" : "استقطاعات";
                return new(path, ImportKind.Payroll, month, what + (month is null ? " · حدّد الشهر في نافذة المراجعة" : " · الشهر من اسم الملف: " + month));
            }
            if (accounts) return new(path, ImportKind.Accounts, null, "يُضاف الجديد ويُحدّث الموجود دون حذف الجهات الغائبة.");
            return new(path, ImportKind.Unknown, null, "لم تُعرف أعمدة الملف (الاسم والصافي، أو الاسم والمبلغ، أو الجهة وIBAN).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ExcelDataReader.Exceptions.ExcelReaderException or ArgumentException or InvalidOperationException or FormatException)
        { return new(path, ImportKind.Unknown, null, "تعذّر فتح الملف: " + ex.Message); }
    }

    /// الشهر من اسم الملف (مثل «رواتب أيلول 2026»)، بالاسم المحفوظ إن وُجد الشهر نفسه في البيانات.
    public static string? SuggestedMonth(string path, BackupSnapshot? source)
    {
        var key = MonthNames.SortKey(System.IO.Path.GetFileNameWithoutExtension(path));
        if (key == 0) return null;
        var existing = source?.Months.Where(m => MonthNames.SortKey(m.Name) == key).Select(m => m.Name).ToArray() ?? [];
        return existing.Length == 1 ? existing[0] : existing.Length > 1 ? null : AdaptiveLetterPdf.MonthName(key % 100) + " " + (key / 100).ToString(CultureInfo.InvariantCulture);
    }

    /// ترتيب التنفيذ: النسخة أولاً، ثم الحسابات، ثم الأشهر من الأقدم، ثم الملفات السنوية.
    public static IReadOnlyList<ImportCandidate> Order(IEnumerable<ImportCandidate> items) =>
        items.OrderBy(c => c.Kind switch { ImportKind.Backup => 0, ImportKind.Accounts => 1, ImportKind.Payroll => 2, ImportKind.Annual => 3, _ => 4 })
             .ThenBy(c => c.Month is null ? int.MaxValue : MonthNames.SortKey(c.Month)).ThenBy(c => c.FileName, StringComparer.Ordinal).ToArray();
}

/// يحمي ملف النسخة المختار من الاستبدال بنسخة فقدت أشهراً (مثل حماية النسخ في الآيفون).
public static class BackupDestination
{
    public static void Guard(string path, BackupSnapshot source)
    {
        if (!File.Exists(path)) return;
        BackupSnapshot existing;
        try { existing = BackupReader.Read(path); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException or FormatException)
        { throw new InvalidDataException("الملف المختار للنسخ ليس نسخة احتياطية صالحة؛ لم يُستبدل. اختر ملف نسخة آخر من الإعدادات ← النسخ الاحتياطي.", ex); }
        var missing = existing.Months.Where(m => !source.Months.Any(n => n.Name == m.Name)).Select(m => m.Name).ToArray();
        if (missing.Length > 0 || (existing.Employees.Count > 0 && source.Employees.Count == 0) || (existing.Deductions.Count > 0 && source.Deductions.Count == 0))
            throw new InvalidDataException("النسخة السابقة تحتوي بيانات غير موجودة الآن" + (missing.Length > 0 ? " (" + string.Join("، ", missing.Take(5)) + ")" : "") + ". لم تُستبدل؛ افتح النسخة الأحدث أولاً أو احفظ النسخة الجديدة في ملف آخر.");
    }
}
