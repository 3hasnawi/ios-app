using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
namespace SalaryWindows;
internal static class SpecialExcel
{
    internal sealed record Cell(string? Text = null, double? Number = null, string? Formula = null, int Style = 4);
    internal sealed record Sheet(string Name, string Title, string[] Headers, double[] Widths, List<List<Cell>> Rows, List<string> Merges, bool Filter = false, string? Tab = null);
    private static Cell T(string value, int style = 4) => new(value, Style: style);
    private static Cell N(double value, int style = 3, string? formula = null)
    {
        if (!double.IsFinite(value)) throw new InvalidDataException("يوجد مبلغ غير صالح؛ لم يُصدر الكشف.");
        return new(Number: value, Formula: formula, Style: style);
    }
    private static string Eastern(string s) => string.Concat(s.Select(c => c is >= '0' and <= '9' ? (char)('٠' + c - '0') : c));
    private static string C(int col) => ((char)('A' + col)).ToString();
    public static void Write(string path, BackupData data, string month, string title) => Package(path, Build(data, month, title));
    internal static List<Sheet> Build(BackupData data, string month, string title)
    {
        var key = PayrollMath.Normalize(title);
        var people = data.Employees.Where(e => e.Month == month).Select(e => (Employee: e, Items: PayrollMath.Decode<double>(e.Items))).ToList();
        if (!people.Any(x => x.Items.ContainsKey(title))) throw new InvalidDataException("لا يوجد هذا البند في بيانات الشهر.");
        var header = BackupSettings.ReportHeader(data); var date = Eastern(month);
        var sheets = new List<Sheet>();
        if (key.Contains(PayrollMath.Normalize("الرعاية")))
        {
            var excluded = new[] { "عقد", "عقود", "رجل امن", "رجل الامن", "رجال الامن" }.Select(PayrollMath.Normalize).ToArray();
            var subscribers = people.Where(x => x.Items.TryGetValue(title, out var a) && a != 0).ToList();
            var others = people.Where(x => (!x.Items.TryGetValue(title, out var a) || a == 0) && (x.Employee.Net ?? 0) > 0 && !excluded.Any(word => PayrollMath.Normalize(x.Employee.Rank ?? "").Contains(word))).ToList();
            var subscribersRows = subscribers.OrderBy(x => Rank(x.Employee.Rank)).Select((x,i) => {
                var amount = x.Items[title]; var exact = amount % 3500 == 0;
                var family = exact ? Math.Max(1, Math.Round(amount / 3500, MidpointRounding.AwayFromZero)) : Math.Round(amount / 3500, 2);
                return new List<Cell> { N(i+1), T(x.Employee.Rank), T(x.Employee.Name), N(amount), N(family, exact ? 3 : 5) };
            }).ToList();
            var othersRows = others.OrderBy(x => Rank(x.Employee.Rank)).Select((x,i) => new List<Cell> { N(i+1), T(x.Employee.Rank), T(x.Employee.Name), N(0) }).ToList();
            sheets.Add(new("المشتركين", $"اشتراكات الرعاية الصحية ل{header} لشهر {date}", ["ت", "الرتبة", "الاسم", "المبلغ", "عدد افراد الاسرة"], [7,13,42,14,17], subscribersRows, [], true, "FF70AD47"));
            sheets.Add(new("الغير مشتركين", $"غير المشتركين في الرعاية الصحية ل{header} لشهر {date}", ["ت", "الرتبة", "الاسم", "المبلغ"], [7,13,42,14], othersRows, [], true, "FFFF0000"));
        }
        else if (key.Contains(PayrollMath.Normalize("شهداء")))
        {
            string[] names = ["ضباط", "موظفين", "مراتب", "عقود"]; int[] rates = [5000,4000,4000,2000]; var counts = new int[4]; var sums = new double[4];
            foreach (var (e,items) in people) if (items.TryGetValue(title,out var amount) && amount != 0) { int i=PayrollMath.Category(e.Rank); counts[i]++; sums[i]+=amount; }
            var lines = Enumerable.Range(0,4).Select(i => new List<Cell> { N(i+1),T(names[i]),N(counts[i]),N(rates[i]),N(sums[i]),T("") }).ToList();
            lines.Add([T("المجموع",2),T("",2),N(counts.Sum(),2,"SUM(C3:C6)"),T("",2),N(sums.Sum(),6,"SUM(E3:E6)"),T("")]);
            var parts = month.Split(' ', StringSplitOptions.RemoveEmptyEntries); var year = parts.LastOrDefault();
            var hasYear = year != null && year.All(char.IsDigit); var name = hasYear ? string.Join(" ",parts.SkipLast(1)) : month;
            var reportTitle = $"{header} / تبرعات صندوق شهداء الشرطة لشهر( {name} )" + (hasYear ? $" لسنة ({Eastern(year!)})" : "");
            sheets.Add(new("صندوق الشهداء", reportTitle,["ت","الرتبة","العدد","دعم النشاط الاجتماعي","المجموع","الملاحظات"],[5,16,14,24,22,24],lines,["A7:B7"]));
        }
        else if (key.Contains(PayrollMath.Normalize("الجوال")))
        {
            var amounts = people.Where(x=>x.Items.TryGetValue(title,out var v)&&v!=0).Select(x=>x.Items[title]).ToList();
            var groups = amounts.GroupBy(x=>x).OrderBy(x=>x.Key).ToList(); var lines = new List<List<Cell>>();
            for(int i=0;i<groups.Count;i++) { var g=groups[i]; int row=i+3; lines.Add([N(i+1),N(g.Count()),N(g.Key),N(g.Sum(),3,$"B{row}*C{row}")]); }
            int last = groups.Count+2, totalRow=last+1; var total=amounts.Sum(); var fund=Math.Round(total*.03,MidpointRounding.AwayFromZero); var rest=total-fund;
            var tax=Math.Round(rest*.018,MidpointRounding.AwayFromZero); var company=rest-tax;
            lines.Add([T("الإجمالي",2),N(amounts.Count,2,groups.Count==0?"0":$"SUM(B3:B{last})"),T(""),N(total,2,groups.Count==0?"0":$"SUM(D3:D{last})")]);
            lines.Add([]); lines.Add([T(""),T("تفاصيل النسبة",2),T("",2)]); lines.Add([T(""),T("البيان",2),T("المبلغ",2)]);
            var first=totalRow+4; var values = new (string Label,double Value,string Formula)[] {
                ("مجموع الاستقطاع",total,$"D{totalRow}"),("حصة صندوق شهداء ٣٪",fund,$"ROUND(C{first}*0.03,0)"),
                ("المبلغ بعد حسم حصة الصندوق",rest,$"C{first}-C{first+1}"),("ضريبة ١٫٨٪",tax,$"ROUND(C{first+2}*0.018,0)"),
                ("شركة عراق الجوال",company,$"C{first+2}-C{first+3}") };
            foreach(var row in values) lines.Add([T(""),T(row.Label),N(row.Value,3,row.Formula)]);
            sheets.Add(new("عقد الإطعام",$"تفاصيل نسبة عدد المشتركين في عقد الاطعام ( {header} ) لشهر {date}",["ت","عدد المشتركين","مبلغ الاستقطاع","المجموع"],[7,28,22,22],lines,[$"B{totalRow+2}:C{totalRow+2}"]));
        }
        else throw new InvalidDataException("لا يوجد كشف خاص لهذا البند.");
        return sheets;
    }
    private static int Rank(string rank)
    {
        string[] ordered = ["فريق اول","فريق","لواء","عميد","عقيد","مقدم","رائد","نقيب","ملازم اول","م اول","ملازم","موظف د1","موظف د2","موظف د3","موظف د4","موظف د5","موظف د6","موظف د7","موظف د8","موظف د9","موظف د10","مفوض1","مفوض2","مفوض3","مفوض4","مفوض5","مفوض6","مفوض7","مفوض8","ر ع","ع م","ن ع","ش1 م","ش م","شرطي","عقود","عقد"];
        static string Key(string s) => string.Join(" ", PayrollMath.Normalize(s ?? "").Replace('.', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var i = Array.FindIndex(ordered,x => Key(x)==Key(rank)); return i < 0 ? ordered.Length : i;
    }
    private static void Raw(ZipArchive z,string name,string content) { using var w=new StreamWriter(z.CreateEntry(name).Open(),new UTF8Encoding(false)); w.Write(content); }
    private static void Package(string path,List<Sheet> sheets)
    {
        var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using (var zip=ZipFile.Open(temp,ZipArchiveMode.Create))
            {
                var sheetOverrides=string.Concat(sheets.Select((_,i)=>$"<Override PartName=\"/xl/worksheets/sheet{i+1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"));
                Raw(zip,"[Content_Types].xml",$"<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>{sheetOverrides}<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>");
                Raw(zip,"_rels/.rels","<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
                var rels=string.Concat(sheets.Select((_,i)=>$"<Relationship Id=\"rId{i+1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i+1}.xml\"/>"));
                Raw(zip,"xl/_rels/workbook.xml.rels",$"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">{rels}<Relationship Id=\"rId{sheets.Count+1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
                var sheetNames=string.Concat(sheets.Select((s,i)=>$"<sheet name=\"{s.Name}\" sheetId=\"{i+1}\" r:id=\"rId{i+1}\"/>"));
                Raw(zip,"xl/workbook.xml",$"<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><bookViews><workbookView/></bookViews><sheets>{sheetNames}</sheets></workbook>");
                Raw(zip,"xl/styles.xml",Styles);
                for(int i=0;i<sheets.Count;i++) WriteSheet(zip,$"xl/worksheets/sheet{i+1}.xml",sheets[i]);
            }
            File.Move(temp,path,true);
        }
        finally { try { File.Delete(temp); } catch { } }
    }
    private static void WriteSheet(ZipArchive zip,string name,Sheet sheet)
    {
        using var stream=zip.CreateEntry(name).Open(); using var xml=XmlWriter.Create(stream,new XmlWriterSettings { Encoding=new UTF8Encoding(false) });
        const string ns="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        void S(string tag)=>xml.WriteStartElement(tag,ns); void A(string key,string value)=>xml.WriteAttributeString(key,value);
        void WriteCell(int col,int row,Cell cell)
        {
            S("c"); A("r",$"{C(col)}{row}"); A("s",cell.Style.ToString());
            if(cell.Number.HasValue)
            {
                if(!double.IsFinite(cell.Number.Value)) throw new InvalidDataException("يوجد رقم غير صالح؛ لم يُصدر الكشف.");
                if(cell.Formula!=null) { S("f"); xml.WriteString(cell.Formula); xml.WriteEndElement(); }
                S("v"); xml.WriteString(cell.Number.Value.ToString("R",CultureInfo.InvariantCulture)); xml.WriteEndElement();
            }
            else { if ((cell.Text?.Length ?? 0) > 32767) throw new InvalidDataException("أحد الحقول أطول من حد Excel؛ لم يُصدر كشف ناقص."); A("t","inlineStr"); S("is"); S("t"); xml.WriteString(cell.Text ?? ""); xml.WriteEndElement(); xml.WriteEndElement(); }
            xml.WriteEndElement();
        }
        void StartRow(int n,int h) { S("row"); A("r",n.ToString()); A("ht",h.ToString()); A("customHeight","1"); }
        S("worksheet"); S("sheetPr"); if(sheet.Tab!=null) { S("tabColor"); A("rgb",sheet.Tab); xml.WriteEndElement(); } S("pageSetUpPr"); A("fitToPage","1"); xml.WriteEndElement(); xml.WriteEndElement();
        S("sheetViews"); S("sheetView"); A("rightToLeft","1"); A("workbookViewId","0"); if(sheet.Filter) { S("pane"); A("ySplit","2"); A("topLeftCell","A3"); A("state","frozen"); xml.WriteEndElement(); } xml.WriteEndElement(); xml.WriteEndElement();
        S("cols"); for(int i=0;i<sheet.Widths.Length;i++) { S("col"); A("min",(i+1).ToString()); A("max",(i+1).ToString()); A("width",sheet.Widths[i].ToString(CultureInfo.InvariantCulture)); A("customWidth","1"); xml.WriteEndElement(); } xml.WriteEndElement();
        S("sheetData"); StartRow(1,34); WriteCell(0,1,T(sheet.Title,sheet.Tab != null ? 1 : 7)); xml.WriteEndElement();
        StartRow(2,24); for(int i=0;i<sheet.Headers.Length;i++) WriteCell(i,2,T(sheet.Headers[i],2)); xml.WriteEndElement();
        for(int i=0;i<sheet.Rows.Count;i++) { int row=i+3; StartRow(row,22); for(int c=0;c<sheet.Rows[i].Count;c++) WriteCell(c,row,sheet.Rows[i][c]); xml.WriteEndElement(); }
        xml.WriteEndElement();
        var last=C(sheet.Headers.Length-1); if(sheet.Filter) { S("autoFilter"); A("ref",$"A2:{last}{Math.Max(2,sheet.Rows.Count+2)}"); xml.WriteEndElement(); }
        S("mergeCells"); A("count",(sheet.Merges.Count+1).ToString()); foreach(var merge in new[] { $"A1:{last}1" }.Concat(sheet.Merges)) { S("mergeCell"); A("ref",merge); xml.WriteEndElement(); } xml.WriteEndElement();
        S("printOptions"); A("horizontalCentered","1"); xml.WriteEndElement();
        S("pageMargins"); foreach(var (k,v) in new[] {("left","0.5"),("right","0.5"),("top","0.6"),("bottom","0.6"),("header","0.3"),("footer","0.3")}) A(k,v); xml.WriteEndElement();
        S("pageSetup"); A("paperSize","9"); A("orientation","portrait"); A("fitToWidth","1"); A("fitToHeight","0"); xml.WriteEndElement(); xml.WriteEndElement();
    }
    private const string Styles = """
<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><numFmts count="1"><numFmt numFmtId="164" formatCode="#,##0.##"/></numFmts><fonts count="3"><font><sz val="12"/><name val="Arial"/></font><font><b/><sz val="16"/><name val="Arial"/></font><font><b/><sz val="12"/><name val="Arial"/></font></fonts><fills count="5"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FFFFFF00"/><bgColor indexed="64"/></patternFill></fill><fill><patternFill patternType="solid"><fgColor rgb="FFD9D9D9"/><bgColor indexed="64"/></patternFill></fill><fill><patternFill patternType="solid"><fgColor rgb="FFFF7C80"/><bgColor indexed="64"/></patternFill></fill></fills><borders count="2"><border><left/><right/><top/><bottom/><diagonal/></border><border><left style="thin"><color rgb="FF000000"/></left><right style="thin"><color rgb="FF000000"/></right><top style="thin"><color rgb="FF000000"/></top><bottom style="thin"><color rgb="FF000000"/></bottom><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="8"><xf fontId="0" fillId="0" borderId="0" xfId="0"/><xf fontId="1" fillId="2" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center" readingOrder="2"/></xf><xf fontId="2" fillId="3" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center" readingOrder="2"/></xf><xf numFmtId="164" fontId="2" fillId="0" borderId="1" xfId="0" applyNumberFormat="1" applyFont="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf><xf fontId="2" fillId="0" borderId="1" xfId="0" applyFont="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center" readingOrder="2"/></xf><xf numFmtId="164" fontId="2" fillId="4" borderId="1" xfId="0" applyNumberFormat="1" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf><xf numFmtId="164" fontId="2" fillId="3" borderId="1" xfId="0" applyNumberFormat="1" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf><xf fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1" applyAlignment="1"><alignment horizontal="right" vertical="center" readingOrder="2"/></xf></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>
""";
}
