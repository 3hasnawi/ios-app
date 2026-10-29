using System.Globalization;
using System.IO.Compression;
using System.Xml;
namespace SalaryWindows;
internal static class OfficialExcel
{
    private sealed record Line(string Rank, string Name, double? Amount, string Entity, string Note);
    public static void Entity(string path, BackupData data, string month, string title)
    {
        var rankByName = data.Employees.Where(x => x.Month == month).GroupBy(x => PayrollMath.Key(x.Name)).ToDictionary(g => g.Key, g => g.First().Rank);
        var rows = data.Deductions.Where(x => x.Month == month && x.Title == title)
            .Select(x => new Line(string.IsNullOrWhiteSpace(x.Rank) ? rankByName.GetValueOrDefault(PayrollMath.Key(x.Name)) ?? "" : x.Rank, x.Name, x.Amount, x.Title, x.Note ?? ""))
            .OrderBy(x => x.Name).ToList();
        Write(path, BackupSettings.ReportHeader(data), $"قائمة الاستقطاعات لـ شهر {Eastern(month)} - {title}", rows, true);
    }
    public static void Payroll(string path, BackupData data, string month, string title)
    {
        var rows = data.Employees.Where(x => x.Month == month).Select(x => (Employee: x, Items: PayrollMath.Decode<double>(x.Items)))
            .Where(x => x.Items.ContainsKey(title)).Select(x => new Line(x.Employee.Rank, x.Employee.Name, x.Items[title], "", x.Employee.Workplace))
            .OrderBy(x => x.Name).ToList();
        Write(path, BackupSettings.ReportHeader(data), $"قائمة {title} لـ شهر {Eastern(month)}", rows, false);
    }
    private static string Eastern(string s) => string.Concat(s.Select(c => c is >= '0' and <= '9' ? (char)('٠' + c - '0') : c));
    private static string Col(int i) => ((char)('A' + i)).ToString();
    private static void Raw(ZipArchive zip, string name, string text)
    { using var stream = new StreamWriter(zip.CreateEntry(name).Open(), new System.Text.UTF8Encoding(false)); stream.Write(text); }
    private static void Write(string path, string header, string title, List<Line> rows, bool entity)
    {
        var file = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
            {
                Raw(zip,"[Content_Types].xml","<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>");
                Raw(zip,"_rels/.rels","<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
                Raw(zip,"xl/_rels/workbook.xml.rels","<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
                Raw(zip,"xl/workbook.xml","<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><bookViews><workbookView/></bookViews><sheets><sheet name=\"الكشف\" sheetId=\"1\" r:id=\"rId1\"/></sheets><definedNames><definedName name=\"_xlnm.Print_Titles\" localSheetId=\"0\">'الكشف'!$1:$3</definedName></definedNames></workbook>");
                Raw(zip,"xl/styles.xml",Styles);
                using var stream = zip.CreateEntry("xl/worksheets/sheet1.xml").Open(); using var xml = XmlWriter.Create(stream,new XmlWriterSettings { Encoding = new System.Text.UTF8Encoding(false) });
                const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                void Start(string name) => xml.WriteStartElement(name,ns);
                void Attr(string k,string v) => xml.WriteAttributeString(k,v);
                void TextCell(string pos,string text,int style)
                { Start("c"); Attr("r",pos); Attr("s",style.ToString()); Attr("t","inlineStr"); Start("is"); Start("t"); xml.WriteString(text ?? ""); xml.WriteEndElement(); xml.WriteEndElement(); xml.WriteEndElement(); }
                void NumberCell(string pos,double value,int style,string? formula = null)
                { if (!double.IsFinite(value)) throw new InvalidDataException("مبلغ غير صالح؛ لم يُصدَّر الكشف."); Start("c"); Attr("r",pos); Attr("s",style.ToString()); if (formula != null) { Start("f"); xml.WriteString(formula); xml.WriteEndElement(); } Start("v"); xml.WriteString(value.ToString("R",CultureInfo.InvariantCulture)); xml.WriteEndElement(); xml.WriteEndElement(); }
                void RowStart(int r,int h) { Start("row"); Attr("r",r.ToString()); Attr("ht",h.ToString()); Attr("customHeight","1"); }
                var heads = entity ? new[] { "ت", "الرتبة", "الاسم الكامل", "المبلغ", "جهة التنفيذ", "رقم وتاريخ الاضبارة" } : new[] { "ت", "الرتبة", "الاسم الكامل", "المبلغ", "محل العمل" };
                var widths = entity ? new[] { 6, 13, 34, 18, 28, 32 } : new[] { 6, 13, 34, 18, 36 };
                Start("worksheet"); Start("sheetPr"); Start("pageSetUpPr"); Attr("fitToPage","1"); xml.WriteEndElement(); xml.WriteEndElement();
                Start("sheetViews"); Start("sheetView"); Attr("workbookViewId","0"); Attr("rightToLeft","1"); Start("pane"); Attr("ySplit","3"); Attr("topLeftCell","A4"); Attr("state","frozen"); xml.WriteEndElement(); xml.WriteEndElement(); xml.WriteEndElement();
                Start("cols"); for (int i=0;i<widths.Length;i++) { Start("col"); Attr("min",(i+1).ToString()); Attr("max",(i+1).ToString()); Attr("width",widths[i].ToString()); Attr("customWidth","1"); xml.WriteEndElement(); } xml.WriteEndElement();
                Start("sheetData"); RowStart(1,24); TextCell("A1",header,1); xml.WriteEndElement(); RowStart(2,30); TextCell("A2",title,2); xml.WriteEndElement();
                RowStart(3,22); for(int i=0;i<heads.Length;i++) TextCell(Col(i)+"3",heads[i],3); xml.WriteEndElement();
                for(int i=0;i<rows.Count;i++)
                {
                    int n=i+4; var r=rows[i]; RowStart(n,20);
                    NumberCell("A"+n,i+1,5); TextCell("B"+n,r.Rank,4); TextCell("C"+n,r.Name,4);
                    if(r.Amount.HasValue) NumberCell("D"+n,r.Amount.Value,6); else TextCell("D"+n,"غير متوفر",4);
                    if(entity) { TextCell("E"+n,r.Entity,4); TextCell("F"+n,r.Note,4); } else TextCell("E"+n,r.Note,4);
                    xml.WriteEndElement();
                }
                var last=rows.Count+4; RowStart(last,22);
                if(rows.All(x=>x.Amount.HasValue))
                { TextCell("A"+last,"المجموع",7); NumberCell("D"+last,rows.Sum(x=>x.Amount!.Value),8,rows.Count==0 ? "0" : $"SUM(D4:D{last-1})"); }
                else TextCell("A"+last,"المجموع غير متاح لوجود مبالغ فارغة",7);
                xml.WriteEndElement(); xml.WriteEndElement();
                Start("mergeCells"); Attr("count","3"); foreach(var reference in new[] { $"A1:{Col(heads.Length-1)}1", $"A2:{Col(heads.Length-1)}2", $"A{last}:C{last}" }) { Start("mergeCell"); Attr("ref",reference); xml.WriteEndElement(); } xml.WriteEndElement();
                Start("printOptions"); Attr("horizontalCentered","1"); xml.WriteEndElement();
                Start("pageMargins"); foreach(var (k,v) in new[] {("left","0.4"),("right","0.4"),("top","0.5"),("bottom","0.5"),("header","0.3"),("footer","0.3")}) Attr(k,v); xml.WriteEndElement();
                Start("pageSetup"); Attr("paperSize","9"); Attr("orientation","landscape"); Attr("fitToWidth","1"); Attr("fitToHeight","0"); xml.WriteEndElement();
                xml.WriteEndElement();
            }
            File.Move(file,path,true);
        }
        finally { try { File.Delete(file); } catch { } }
    }
    private const string Styles = """
<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><numFmts count="1"><numFmt numFmtId="164" formatCode="#,##0.##"/></numFmts><fonts count="5"><font><sz val="11"/><name val="Arial"/></font><font><b/><sz val="14"/><name val="Arial"/></font><font><b/><sz val="15"/><color rgb="FFFFFFFF"/><name val="Arial"/></font><font><b/><sz val="11"/><name val="Arial"/></font><font><b/><sz val="12"/><name val="Arial"/></font></fonts><fills count="4"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF1F4E79"/><bgColor indexed="64"/></patternFill></fill><fill><patternFill patternType="solid"><fgColor rgb="FFDDEBF7"/><bgColor indexed="64"/></patternFill></fill></fills><borders count="2"><border><left/><right/><top/><bottom/><diagonal/></border><border><left style="thin"><color rgb="FF9BB7D4"/></left><right style="thin"><color rgb="FF9BB7D4"/></right><top style="thin"><color rgb="FF9BB7D4"/></top><bottom style="thin"><color rgb="FF9BB7D4"/></bottom><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="10"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1" applyAlignment="1"><alignment horizontal="center" vertical="center" readingOrder="2"/></xf><xf fontId="2" fillId="2" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center" readingOrder="2"/></xf><xf fontId="3" fillId="3" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center" readingOrder="2"/></xf><xf fontId="0" fillId="0" borderId="1" xfId="0" applyBorder="1" applyAlignment="1"><alignment horizontal="right" vertical="center" readingOrder="2"/></xf><xf fontId="3" fillId="0" borderId="1" xfId="0" applyFont="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf><xf numFmtId="164" fontId="4" fillId="0" borderId="1" xfId="0" applyNumberFormat="1" applyFont="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf><xf fontId="3" fillId="3" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center" readingOrder="2"/></xf><xf numFmtId="164" fontId="4" fillId="3" borderId="1" xfId="0" applyNumberFormat="1" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf><xf fontId="0" fillId="3" borderId="1" xfId="0" applyFill="1" applyBorder="1"/></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>
""";
}
