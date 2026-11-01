using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ExcelDataReader;

namespace SalaryDesktop.Core;

public sealed record AnnualDeductionPreview(IReadOnlyList<PayrollImportPreview> Months, IReadOnlyList<PayrollImportProblem> Problems);

/// يفصل ملف الاستقطاعات السنوي دون حفظ أي بيانات أثناء القراءة.
public static class AnnualDeductionSpreadsheet
{
    private static readonly string[] Months = ["كانون الثاني", "شباط", "آذار", "نيسان", "أيار", "حزيران", "تموز", "آب", "أيلول", "تشرين الأول", "تشرين الثاني", "كانون الأول"];
    private static HashSet<int> Years(string text)
    {
        var latin = string.Concat(text.Select(c => c switch { >= '٠' and <= '٩' => (char)('0' + c - '٠'), >= '۰' and <= '۹' => (char)('0' + c - '۰'), _ => c }));
        return Regex.Matches(latin, @"(?<![0-9])(?:19[0-9]{2}|20[0-9]{2}|21[0-9]{2}|2200)(?![0-9])", RegexOptions.CultureInvariant).Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture)).ToHashSet();
    }
    public static AnnualDeductionPreview Read(string path, BackupSnapshot source)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > 100 * 1024 * 1024) throw new InvalidDataException("حجم ملف Excel غير مدعوم.");
        try
        {
            var digest = SHA256.HashData(file); file.Position = 0;
            using (var zip = new ZipArchive(file, ZipArchiveMode.Read, true))
            {
                long size = 0; var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in zip.Entries) if (!names.Add(entry.FullName) || entry.Length > 512 * 1024 * 1024 || (size += entry.Length) > 1_000_000_000) throw new InvalidDataException("ملف Excel مكرر المحتوى أو يتجاوز الحجم المدعوم.");
            }
            file.Position = 0; Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            using var reader = ExcelReaderFactory.CreateOpenXmlReader(file);
            var problems = new List<PayrollImportProblem>(); var months = new Dictionary<int, List<Deduction>>(); var owners = new Dictionary<int, int>();
            var fileYears = Years(Path.GetFileNameWithoutExtension(path)); int sheetIndex = 0, totalRows = 0; bool recognized = false;
            var ignoredNames = PayrollSpreadsheet.Names.Concat(PayrollSpreadsheet.TotalNames).Select(PayrollAnalysis.Normalize).ToHashSet(StringComparer.Ordinal);
            var knownMonths = source.Months.GroupBy(m => MonthNames.SortKey(m.Name)).ToDictionary(g => g.Key, g => g.Select(m => m.Name).ToArray());
            void Problem(string sheet, int row, string message) { if (problems.Count < 1000) problems.Add(new(sheet, row, message)); }
            do
            {
                sheetIndex++; var sheet = reader.Name ?? "ورقة"; var years = Years(sheet); years.UnionWith(fileYears); var year = years.Count == 1 ? years.Single() : 0;
                int row = 0, name = -1, amount = -1, title = -1, month = -1, rank = -1, note = -1; bool nonempty = false;
                while (reader.Read())
                {
                    if (++row > 200_000 || ++totalRows > 500_000 || reader.FieldCount > 500) throw new InvalidDataException("عدد صفوف الملف أو أعمدته يتجاوز الحد المدعوم.");
                    string Cell(int index) => index < 0 || index >= reader.FieldCount ? "" : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture)?.Trim() ?? "";
                    if (name < 0)
                    {
                        if (row > 20) break;
                        var raw = Enumerable.Range(0, reader.FieldCount).Select(Cell).ToArray(); nonempty |= raw.Any(x => x.Length > 0); var headers = raw.Select(PayrollAnalysis.Normalize).ToArray();
                        var used = new HashSet<int>(); var candidate = PayrollSpreadsheet.Find(headers, PayrollSpreadsheet.Names, used, true); if (candidate < 0) continue;
                        name = candidate; amount = PayrollSpreadsheet.Find(headers, PayrollSpreadsheet.Amounts, used); title = PayrollSpreadsheet.Find(headers, PayrollSpreadsheet.Titles, used);
                        month = PayrollSpreadsheet.Find(headers, ["شهر", "الشهر", "month"], used, true); rank = PayrollSpreadsheet.Find(headers, PayrollSpreadsheet.Ranks, used);
                        note = PayrollSpreadsheet.Find(headers, ["رقم وتاريخ الاضبارة", "رقم الاضبارة", "الاضبارة", "الملاحظات", "ملاحظات", "ملاحظة", "note"], used);
                        if (amount < 0 || title < 0 || month < 0) { Problem(sheet, row, "يجب أن يحتوي الملف السنوي أعمدة الاسم والمبلغ والجهة والشهر."); break; }
                        if (year == 0) { Problem(sheet, row, "تعذّر تحديد سنة واحدة من اسم الملف والورقة."); break; }
                        recognized = true; continue;
                    }
                    if (amount < 0 || title < 0 || month < 0 || year == 0) break;
                    var person = Cell(name); var text = Cell(amount); var entity = Cell(title); var monthText = Cell(month);
                    if (person.Length == 0 && Enumerable.Range(0, reader.FieldCount).All(i => Cell(i).Length == 0)) continue;
                    if (ignoredNames.Contains(PayrollAnalysis.Normalize(person))) continue;
                    if (person.Length == 0 || entity.Length == 0) { Problem(sheet, row, "اسم المنتسب أو جهة التنفيذ فارغ."); continue; }
                    var explicitYears = Years(monthText); var monthKey = MonthNames.SortKey(monthText + " " + year.ToString(CultureInfo.InvariantCulture));
                    if (explicitYears.Any(y => y != year) || monthKey / 100 != year || monthKey % 100 is < 1 or > 12) { Problem(sheet, row, "الشهر غير صالح أو لا يطابق سنة الملف."); continue; }
                    var value = PayrollSpreadsheet.Number(text); var status = PayrollAnalysis.Normalize(text) switch { "موقوف" => "موقوف", "هارب" => "هارب", "محكوم" => "محكوم", _ => null };
                    if (value is null && status is null) { Problem(sheet, row, "مبلغ الاستقطاع أو حالته غير صالح."); continue; }
                    if (owners.TryGetValue(monthKey, out var owner) && owner != sheetIndex) { Problem(sheet, row, "يتكرر الشهر نفسه في أكثر من ورقة؛ راجع الملف."); continue; }
                    owners[monthKey] = sheetIndex;
                    var existing = knownMonths.GetValueOrDefault(monthKey) ?? [];
                    if (existing.Length > 1) { Problem(sheet, row, "يوجد أكثر من شهر محفوظ لنفس الفترة؛ راجع البيانات."); continue; }
                    var label = existing.Length == 1 ? existing[0] : Months[monthKey % 100 - 1] + " " + year.ToString(CultureInfo.InvariantCulture);
                    var remark = Cell(note); if (status is not null) remark = $"⟦حالة الاستقطاع:{status}⟧" + (remark.Length == 0 ? "" : "\n" + remark);
                    if (!months.TryGetValue(monthKey, out var entries)) months[monthKey] = entries = [];
                    entries.Add(new(label, person, entity, value, remark, Cell(rank)));
                }
                if (name < 0 && nonempty) Problem(sheet, 0, "ورقة غير فارغة لم تُتعرف أعمدتها.");
            } while (reader.NextResult());
            if (!recognized && problems.Count == 0) throw new InvalidDataException("لم يُعثر على استقطاعات سنوية في الملف.");
            if (months.Count == 0 && problems.Count == 0) throw new InvalidDataException("لم يُعثر على قيود استقطاع في الملف.");
            file.Position = 0; if (!digest.AsSpan().SequenceEqual(SHA256.HashData(file))) throw new InvalidDataException("تغيّر الملف أثناء قراءته؛ لم تُغيّر البيانات.");
            return new(months.OrderByDescending(p => p.Key).Select(p => new PayrollImportPreview(p.Value[0].Month, [], p.Value, problems)).ToArray(), problems);
        }
        catch (Exception ex) when (ex is not IOException and not UnauthorizedAccessException and not OverflowException) { throw new InvalidDataException("تعذّر قراءة ملف الاستقطاعات السنوي.", ex); }
    }
}
