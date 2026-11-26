using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ExcelDataReader;
using System.Xml;

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
            bool uses1904=false;
            var digest = SHA256.HashData(file); file.Position = 0;
            using (var zip = new ZipArchive(file, ZipArchiveMode.Read, true))
            {
                long size = 0; var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var e in zip.Entries)
                    if (!names.Add(e.FullName) || e.Length > 128 * 1024 * 1024 || (size += e.Length) > 256 * 1024 * 1024)
                        throw new InvalidDataException("ملف Excel مكرر المحتويات أو كبير جداً.");
                using var xml=XmlReader.Create(zip.GetEntry("xl/workbook.xml")!.Open(),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,CloseInput=true});
                while(xml.Read())if(xml.NodeType==XmlNodeType.Element&&xml.LocalName=="workbookPr"){uses1904=xml.GetAttribute("date1904") is "1" or "true";break;}
            }
            file.Position = 0; Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            using var reader = ExcelReaderFactory.CreateOpenXmlReader(file);
            var entries = new List<SalaryTrustEntry>(); bool found = false; int total = 0;
            do
            {
                int row = 0, name = -1, amount = -1, month = -1, year = -1, rank = -1, workplace = -1, note = -1, note2 = -1, status = -1, date = -1;
                while (reader.Read())
                {
                    if (++row > 100_000 || ++total > 200_000 || reader.FieldCount > 500) throw new InvalidDataException("عدد صفوف الملف أو أعمدته كبير جداً.");
                    string Cell(int i) => i < 0 || i >= reader.FieldCount ? "" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)?.Trim() ?? "";
                    if (name < 0)
                    {
                        if (row > 20) break;
                        var headers = Enumerable.Range(0, reader.FieldCount).Select(i => PayrollAnalysis.Normalize(Cell(i))).ToArray();
                        int Find(params string[] aliases)=>aliases.Select(v=>Array.IndexOf(headers,PayrollAnalysis.Normalize(v))).FirstOrDefault(i=>i>=0,-1);
                        int n=Find("الاسم الكامل","الاسم"), a=Find("صافي الراتب","المبلغ"), m=Find("الشهر"), y=Find("السنة");
                        if(n<0||a<0||m<0||y<0)continue;
                        name=n;amount=a;month=m;year=y;rank=Find("الرتبة");workplace=Find("الفوج او المديرية","محل العمل");
                        note=Find("ملاحظات4","ملاحظات 4","ملاحظات");note2=Find("الملاحظات","ملاحظات إضافية");status=Find("الحالة","حالة الصرف");date=Find("تاريخ الصرف");
                        if(status<0)throw new InvalidDataException("عمود حالة الصرف مفقود.");
                        found=true;continue;
                    }
                    var employee = Cell(name); var amountText = Cell(amount); var monthText = Cell(month); var yearText = Cell(year);
                    if (employee.Length == 0 && amountText.Length == 0 && monthText.Length == 0 && yearText.Length == 0) continue;
                    var key = MonthNames.SortKey(monthText + " " + yearText); var state = Cell(status); 
                    var paid=new[]{"صرف","مصروف","مصروف بالكامل","تم الصرف"}.Any(v=>PersonProfiles.Key(v)==PersonProfiles.Key(state));
                    var review=!paid&&!new[]{"","غير مصروف","لم يصرف","لم يتم الصرف"}.Any(v=>PersonProfiles.Key(v)==PersonProfiles.Key(state));
                    if(employee.Length==0||key==0||PayrollSpreadsheet.ParseMoney(amountText) is not {} net||net<=0||net>SalaryTrust.Maximum||net!=decimal.Truncate(net))
                        throw new InvalidDataException($"صف {row}: تحقق من الاسم والشهر والمبلغ والحالة.");
                    var notes = Cell(note2);
                    string PaymentDate(){
                        object? value=date<0?null:reader.GetValue(date);
                        DateTime day;
                        if(value is DateTime formatted)day=formatted;
                        else if(value is double serial&&serial>=(uses1904?0:1)&&serial<=110000&&(uses1904||Math.Floor(serial)!=60))
                            day=(uses1904?new DateTime(1904,1,1):new DateTime(1899,12,31)).AddDays(serial-(!uses1904&&serial>=61?1:0));
                        else return Cell(date);
                        return day.ToString(day.TimeOfDay==TimeSpan.Zero?"yyyy/MM/dd":"yyyy/MM/dd HH:mm",CultureInfo.InvariantCulture);
                    }
                    entries.Add(new(Guid.NewGuid().ToString(), employee, Cell(rank), Cell(workplace), key / 100, key % 100,
                        checked((long)net), Cell(note), notes, state, paid, review, [], ImportedPaymentDate: PaymentDate()));
                }
            } while (reader.NextResult());
            if (!found || entries.Count == 0) throw new InvalidDataException("تعذّر العثور على بيانات الأمانات في الملف.");
            file.Position = 0;
            if (!digest.AsSpan().SequenceEqual(SHA256.HashData(file))) throw new InvalidDataException("تغيّر ملف Excel أثناء قراءته؛ لم تُغيّر البيانات.");
            var ledger = new SalaryTrustLedger(2, entries, true); SalaryTrust.Validate(ledger); return ledger;
        }
        catch (Exception ex) when (ex is not InvalidDataException and not IOException and not UnauthorizedAccessException)
        { throw new InvalidDataException("تعذّر قراءة ملف الأمانات؛ لم تُغيّر البيانات.", ex); }
    }
}
