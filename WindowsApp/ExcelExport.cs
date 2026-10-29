using System.IO.Compression;
using System.Globalization;
using System.Xml;
namespace SalaryWindows;
internal static class ExcelExport
{
    // يُنشأ الملف كاملاً في مكان مؤقت قبل استبدال الملف الذي اختاره المستخدم.
    public static void Entity(string path, BackupData data, string month, string title)
    {
        OfficialExcel.Entity(path, data, month, title);
    }
    public static void Payroll(string path, BackupData data, string month, string title)
    {
        OfficialExcel.Payroll(path, data, month, title);
    }
    public static void Audit(string path, string months, string title, IReadOnlyList<string> lines)
    {
        var rows = lines.Select(x => new ReportLine(x, "", null, "")).ToList();
        WriteReport(path, months, title, rows, audit: true);
    }
    private sealed record ReportLine(string Name, string Rank, double? Amount, string Note);
    private static void WriteReport(string path, string month, string title, List<ReportLine> rows, bool audit = false)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                Write(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>");
                Write(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
                Write(zip, "xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
                Write(zip, "xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><bookViews><workbookView/></bookViews><sheets><sheet name=\"كشف الجهة\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
                Write(zip, "xl/styles.xml", "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Arial\"/></font></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills><borders count=\"1\"><border/></borders><cellStyleXfs count=\"1\"><xf/></cellStyleXfs><cellXfs count=\"1\"><xf fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/></cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>");
                var entry = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Fastest);
                using var stream = entry.Open(); using var xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new System.Text.UTF8Encoding(false) });
                const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                xml.WriteStartElement("worksheet", ns); xml.WriteStartElement("sheetViews", ns); xml.WriteStartElement("sheetView", ns); xml.WriteAttributeString("workbookViewId", "0"); xml.WriteAttributeString("rightToLeft", "1"); xml.WriteEndElement(); xml.WriteEndElement();
                xml.WriteStartElement("cols", ns); xml.WriteStartElement("col", ns); xml.WriteAttributeString("min", "1"); xml.WriteAttributeString("max", "4"); xml.WriteAttributeString("width", "28"); xml.WriteAttributeString("customWidth", "1"); xml.WriteEndElement(); xml.WriteEndElement();
                xml.WriteStartElement("sheetData", ns);
                void Row(params (string Value, bool Number)[] cells)
                {
                    xml.WriteStartElement("row", ns);
                    foreach (var cell in cells)
                    {
                        xml.WriteStartElement("c", ns);
                        if (cell.Number) { xml.WriteStartElement("v", ns); xml.WriteString(cell.Value); xml.WriteEndElement(); }
                        else { xml.WriteAttributeString("t", "inlineStr"); xml.WriteStartElement("is", ns); xml.WriteStartElement("t", ns); xml.WriteString(cell.Value); xml.WriteEndElement(); xml.WriteEndElement(); }
                        xml.WriteEndElement();
                    }
                    xml.WriteEndElement();
                }
                Row(("الكشف", false), (title, false), ("الشهر", false), (month, false));
                if (audit)
                {
                    Row(("ت", false), ("تفاصيل الحالة", false));
                    for (var i = 0; i < rows.Count; i++) Row(((i + 1).ToString(CultureInfo.InvariantCulture), true), (rows[i].Name ?? "", false));
                    Row(("عدد الحالات", false), (rows.Count.ToString(CultureInfo.InvariantCulture), true));
                }
                else
                {
                    Row(("الاسم", false), ("الرتبة", false), ("المبلغ", false), ("ملاحظات", false));
                    foreach (var d in rows) Row((d.Name ?? "", false), (d.Rank ?? "", false), (d.Amount?.ToString("R", CultureInfo.InvariantCulture) ?? "", d.Amount.HasValue), (d.Note ?? "", false));
                    // لا يُعرض مجموع مضلل عندما يكون أحد المبالغ غير متاح.
                    if (rows.All(x => x.Amount.HasValue)) Row(("المجموع", false), ("", false), (rows.Sum(x => x.Amount!.Value).ToString("R", CultureInfo.InvariantCulture), true));
                    else Row(("المجموع غير متاح لوجود مبالغ فارغة", false));
                }
                xml.WriteEndElement(); xml.WriteEndElement();
            }
            File.Move(temp, path, true);
        }
        finally { try { File.Delete(temp); } catch { } }
    }
    private static void Write(ZipArchive zip, string name, string content)
    {
        using var stream = new StreamWriter(zip.CreateEntry(name).Open(), new System.Text.UTF8Encoding(false)); stream.Write(content);
    }
}
