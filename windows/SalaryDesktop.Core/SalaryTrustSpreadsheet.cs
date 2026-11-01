using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ExcelDataReader;

namespace SalaryDesktop.Core;

/// لا يحفظ أثناء القراءة؛ يمنع الاستيراد عند غموض صف أو تكرار شخص وشهر.
public static class SalaryTrustSpreadsheet
{
    public static SalaryTrustLedger Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > 32 * 1024 * 1024) throw new InvalidDataException("حجم ملف الأمانات غير مدعوم.");
        try
        {
            var digest = SHA256.HashData(file); file.Position = 0;
            using (var zip = new ZipArchive(file, ZipArchiveMode.Read, true))
            {
                long size = 0; var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var e in zip.Entries)
                    if (!names.Add(e.FullName) || e.Length > 128 * 1024 * 1024 || (size += e.Length) > 256 * 1024 * 1024)
                        throw new InvalidDataException("ملف Excel مكرر المحتويات أو كبير جداً.");
            }
            file.Position = 0; Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            using var reader = ExcelReaderFactory.CreateOpenXmlReader(file);
            var entries = new List<SalaryTrustEntry>(); bool found = false; int total = 0;
            do
            {
                int row = 0, name = -1, amount = -1, month = -1, year = -1, rank = -1, workplace = -1, note = -1, note2 = -1, status = -1;
                while (reader.Read())
                {
                    if (++row > 100_000 || ++total > 200_000 || reader.FieldCount > 500) throw new InvalidDataException("عدد صفوف الملف أو أعمدته كبير جداً.");
                    string Cell(int i) => i < 0 || i >= reader.FieldCount ? "" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)?.Trim() ?? "";
                    if (name < 0)
                    {
                        if (row > 20) break;
                        var headers = Enumerable.Range(0, reader.FieldCount).Select(i => PayrollAnalysis.Normalize(Cell(i))).ToArray();
                        int Find(string value) => Array.IndexOf(headers, PayrollAnalysis.Normalize(value));
                        int n = Find("الاسم الكامل"), a = Find("صافي الراتب"), m = Find("الشهر"), y = Find("السنة");
                        if (n < 0 || a < 0 || m < 0 || y < 0) continue;
                        name = n; amount = a; month = m; year = y; rank = Find("الرتبة"); workplace = Find("الفوج او المديرية");
                        note = Find("ملاحظات4"); note2 = Find("الملاحظات"); status = Find("الحالة"); found = true; continue;
                    }
                    var employee = Cell(name); var amountText = Cell(amount); var monthText = Cell(month); var yearText = Cell(year);
                    if (employee.Length == 0 && amountText.Length == 0 && monthText.Length == 0 && yearText.Length == 0) continue;
                    var key = MonthNames.SortKey(monthText + " " + yearText); var state = Cell(status); var stateKey = PayrollAnalysis.Normalize(state);
                    var paid = stateKey == PayrollAnalysis.Normalize("صرف"); var review = stateKey == PayrollAnalysis.Normalize("قطع علاقة تقاعد");
                    if (employee.Length == 0 || key == 0 || key > SalaryTrust.OpeningCutoff ||
                        PersonProfiles.Amount(amountText) is not { } net ||
                        net <= 0 || net > SalaryTrust.Maximum || net != decimal.Truncate(net) || !(state.Length == 0 || paid || review))
                        throw new InvalidDataException($"صف {row}: تحقق من الاسم والشهر والمبلغ والحالة.");
                    var notes = Cell(note2);
                    entries.Add(new(Guid.NewGuid().ToString(), employee, Cell(rank), Cell(workplace), key / 100, key % 100,
                        checked((long)net), Cell(note), notes, state, paid, review, []));
                }
            } while (reader.NextResult());
            if (!found || entries.Count == 0) throw new InvalidDataException("تعذّر العثور على بيانات الأمانات في الملف.");
            file.Position = 0;
            if (!digest.AsSpan().SequenceEqual(SHA256.HashData(file))) throw new InvalidDataException("تغيّر ملف Excel أثناء قراءته؛ لم تُغيّر البيانات.");
            var ledger = new SalaryTrustLedger(1, entries, true); SalaryTrust.Validate(ledger); return ledger;
        }
        catch (Exception ex) when (ex is not InvalidDataException and not IOException and not UnauthorizedAccessException)
        { throw new InvalidDataException("تعذّر قراءة ملف الأمانات؛ لم تُغيّر البيانات.", ex); }
    }
}
