using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using SalaryLocal.Core;
using SalaryDesktop.Core;

internal static class LocalTrustSpreadsheetTests
{
    private static int checks;
    private static readonly string[] Headers = ["الاسم الكامل", "صافي الراتب", "الشهر", "السنة", "الحالة", "ملاحظات4", "الملاحظات", "الرتبة", "الفوج او المديرية"];
    private static string[] Row(string name = "موظف وهمي", string status = "") => [name, "100", "أيلول", "2026", status, "سبب وهمي", "ملاحظة وهمية", "رتبة وهمية", "جهة وهمية"];
    private static void Check(bool value, string reason) { checks++; if (!value) throw new Exception("Trust XLSX: " + reason); }
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "trust-xlsx-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        int serial = 0;
        string Write(params string[][][] sheets) { var path = Path.Combine(root, (++serial) + ".xlsx"); Workbook(path, sheets); return path; }
        void Reject(params string[][][] sheets) {
            var path = Write(sheets); var before = File.ReadAllBytes(path);
            try { _ = LocalSalaryTrustSpreadsheet.Read(path); throw new Exception("Invalid synthetic workbook accepted"); }
            catch (InvalidDataException) { checks++; }
            Check(before.SequenceEqual(File.ReadAllBytes(path)), "rejected workbook unchanged");
        }
        try {
            var valid = Write([Headers, Row(), Row("مصروف وهمي", "صرف"), Row("مراجعة وهمية", "قطع علاقة تقاعد")]);
            var before = File.ReadAllBytes(valid); var ledger = LocalSalaryTrustSpreadsheet.Read(valid);
            Check(ledger.OpeningImported && ledger.Entries.Count == 3, "valid rows imported");
            Check(!ledger.Entries[0].LegacyPaid && !ledger.Entries[0].ReviewPending && SalaryTrust.Remaining(ledger.Entries[0]) == 100, "present empty status means unpaid");
            Check(ledger.Entries[1].LegacyPaid && SalaryTrust.Remaining(ledger.Entries[1]) == 0, "paid status preserved");
            Check(ledger.Entries[2].ReviewPending && SalaryTrust.Outstanding(ledger.Entries) == 100, "review excluded from confirmed balance");
            Check(ledger.Entries.All(e => e.CaseStatus == "سبب وهمي" && e.Note == "ملاحظة وهمية" && e.Payments.Count == 0), "case and notes preserved");
            Check(before.SequenceEqual(File.ReadAllBytes(valid)), "successful source unchanged");
            foreach (var missing in new[] { 0, 1, 2, 3, 4, 5 })
                Reject([Headers.Where((_,i) => i != missing).ToArray(), Row().Where((_,i) => i != missing).ToArray()]);
            Reject([[.. Headers, "الحالة"], [.. Row(), "صرف"]]);
            Reject([[.. Headers, " الاسم الكامل "], [.. Row(), "اسم آخر وهمي"]]);
            Reject([[.. Headers, "الرتبة"], [.. Row(), "رتبة أخرى"]]);
            Reject([Headers, Row()], [Headers.Where((_,i) => i != 4).ToArray(), Row("ثان وهمي").Where((_,i) => i != 4).ToArray()]);
            Reject([Headers, Row()], [["ورقة غير مفهومة"], ["بيانات غير معروفة"]]);
            foreach (var change in new (int Column,string Value)[] { (1,"100.5"),(1,"-1"),(1,"0"),(1,"غير مبلغ"),(1,"9007199254740992"),(2,"تشرين الأول"),(2,"13"),(2,"9 كلام"),(3,"2026 كلام"),(3,"1899"),(4,"حالة مجهولة"),(0,"") }) {
                var row = Row(); row[change.Column] = change.Value; Reject([Headers,row]);
            }
            Reject([Headers,Row(),Row()]);
            var blankCase = Row(); blankCase[5] = "";
            var numericMonth = Row("شهر رقمي وهمي"); numericMonth[2] = "٩"; numericMonth[3] = "٢٠٢٦";
            Check(LocalSalaryTrustSpreadsheet.Read(Write([Headers,blankCase,numericMonth])).Entries.Count == 2, "empty case value and exact Arabic numeric dates accepted");
            var two = LocalSalaryTrustSpreadsheet.Read(Write([Headers,Row()], [Headers,Row("ورقة ثانية وهمية", "صرف")]));
            Check(two.Entries.Count == 2 && two.Entries[1].LegacyPaid, "all valid sheets included");
            Console.WriteLine($"LocalTrustSpreadsheetTests: {checks} assertions passed.");
        } finally { Directory.Delete(root,true); }
    }
    // Real, minimal OpenXML workbooks, with exclusively synthetic inline-string data.
    private static void Workbook(string path, string[][][] sheets)
    {
        XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace package = "http://schemas.openxmlformats.org/package/2006/relationships";
        XNamespace types = "http://schemas.openxmlformats.org/package/2006/content-types";
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Save(string name, XElement root) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); new XDocument(root).Save(writer); }
        Save("[Content_Types].xml", new XElement(types + "Types",
            new XElement(types + "Default", new XAttribute("Extension","rels"),new XAttribute("ContentType","application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(types + "Default", new XAttribute("Extension","xml"),new XAttribute("ContentType","application/xml")),
            new XElement(types + "Override", new XAttribute("PartName","/xl/workbook.xml"),new XAttribute("ContentType","application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
            sheets.Select((_,i) => new XElement(types + "Override", new XAttribute("PartName",$"/xl/worksheets/sheet{i+1}.xml"),new XAttribute("ContentType","application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")))));
        Save("_rels/.rels",new XElement(package + "Relationships",new XElement(package + "Relationship",new XAttribute("Id","rId1"),new XAttribute("Type",rel.NamespaceName + "/officeDocument"),new XAttribute("Target","xl/workbook.xml"))));
        Save("xl/workbook.xml",new XElement(main + "workbook",new XAttribute(XNamespace.Xmlns + "r",rel),new XElement(main + "sheets",sheets.Select((_,i) => new XElement(main + "sheet",new XAttribute("name",$"وهمي {i+1}"),new XAttribute("sheetId",i+1),new XAttribute(rel + "id",$"rId{i+1}"))))));
        Save("xl/_rels/workbook.xml.rels",new XElement(package + "Relationships",sheets.Select((_,i) => new XElement(package + "Relationship",new XAttribute("Id",$"rId{i+1}"),new XAttribute("Type",rel.NamespaceName + "/worksheet"),new XAttribute("Target",$"worksheets/sheet{i+1}.xml")))));
        for (int i = 0; i < sheets.Length; i++) Save($"xl/worksheets/sheet{i+1}.xml",new XElement(main + "worksheet",new XElement(main + "sheetData",sheets[i].Select((row,r) => new XElement(main + "row",new XAttribute("r",r+1),row.Select((value,c) => new XElement(main + "c",new XAttribute("r",$"{(char)('A'+c)}{r+1}"),new XAttribute("t","inlineStr"),new XElement(main + "is",new XElement(main + "t",value)))))))));
    }
}
