import Foundation
#if canImport(FoundationXML)
import FoundationXML
#endif

let columns: [ReportSheet.Column] = [
    .init(title: "ت", width: 5, kind: .serial),
    .init(title: "الاسم الكامل", width: 18, kind: .text),
    .init(title: "المبلغ", width: 14, kind: .amount),
    .init(title: "التفاصيل", width: 20, kind: .text)
]
let longName = "منتسب وهمي ذو اسم طويل لا يجوز اختصاره في الكشف"
let rows = [["1", longName, "", "ملاحظة أولى"], ["2", "اسم وهمي", "", "ملاحظة ثانية"]]
let sheet = ReportSheet(header: "رأس وهمي", title: "كشف تجريبي", columns: columns, rows: rows, amounts: [1200, 2500])
try XLSXWriter.validate(sheet)
let xml = XLSXWriter.worksheet(sheet)
precondition(XMLParser(data: Data(xml.utf8)).parse())
precondition(XMLParser(data: Data(XLSXWriter.styles.utf8)).parse())
precondition(xml.contains(longName))
precondition(XLSXWriter.rowHeight(rows[0], columns: columns) > 24)
precondition(xml.contains("<c r=\"B4\" s=\"11\"") && xml.contains("<c r=\"B5\" s=\"15\""))
precondition(xml.contains("<c r=\"C5\" s=\"13\""))
precondition(xml.contains("SUM(C4:C5)") && xml.contains("<v>3700</v>"))
precondition(XLSXWriter.styles.contains("FFF3F7FB") && XLSXWriter.styles.contains("11.5"))
precondition(XLSXWriter.styles.contains("#,##0.###############"))
precondition(XLSXWriter.number(1e30) == "1e+30")
var bad = sheet
bad.amounts = [0.5, 2500]
try XLSXWriter.validate(bad)
precondition(XLSXWriter.worksheet(bad).contains("<v>0.5</v>"))
bad.amounts = [-999_999_999, 999_999_998]
precondition(bad.fitted().columns[2].width >= 19)
bad.amounts = [.infinity, 0]
do { try XLSXWriter.validate(bad); fatalError("Nonfinite amount accepted") } catch { }
bad = sheet
bad.rows[0][1] = String(repeating: "اسم ", count: 9000)
do { try XLSXWriter.validate(bad); fatalError("Overlong row accepted") } catch { }
print("Excel report wrapping, styling and numeric safety tests passed with fictional data")

let deductionColumns: [ReportSheet.Column] = [
    .init(title: "ت", width: 5, kind: .serial), .init(title: "الرتبة", width: 11, kind: .text),
    .init(title: "الاسم الكامل", width: 34, kind: .text), .init(title: "المبلغ", width: 14, kind: .amount),
    .init(title: "رقم وتاريخ الاضبارة", width: 32, kind: .text)
]
let compact = ReportSheet(header: "رأس وهمي", title: "استقطاعات تجريبية", columns: deductionColumns,
    rows: [["1", "مفوض", "أحمد سامر محمود", "", "١٢٣"], ["2", "شرطي", "علي حسن ناصر", "", "١٢٤"]],
    amounts: [125000, 130000], entityDeduction: true, deductionList: true)
precondition(compact.fitted().deductionList)
try XLSXWriter.validate(compact)
let compactXML = XLSXWriter.worksheet(compact.fitted())
precondition(compactXML.contains("<row r=\"4\" ht=\"20\"") && compactXML.contains("<row r=\"5\" ht=\"20\""))
for column in ["B", "C", "E"] {
    precondition(compactXML.contains("<c r=\"\(column)4\" s=\"11\""))
    precondition(compactXML.contains("<c r=\"\(column)5\" s=\"15\""))
}
precondition(compactXML.contains("<c r=\"D4\" s=\"16\"") && compactXML.contains("<c r=\"D5\" s=\"17\""))
precondition(compactXML.contains("SUM(D4:D5)") && compactXML.contains("<v>255000</v>"))
precondition(XLSXWriter.styles.contains("<cellXfs count=\"18\">"))
print("Deduction Excel fields share bold 11.5 styles, 20-point rows and unchanged numerical totals")

let healthLongName = String(repeating: "اسم وهمي طويل ", count: 8)
let healthRows = [ReportRow(name: healthLongName, rank: "موظف", amount: 3500), ReportRow(name: "موظف وهمي", rank: "موظف", amount: 3700)]
let healthXML = try HealthCareReport.sheet(healthRows, month: "2026", others: false)
precondition(healthXML.contains(healthLongName))
precondition(healthXML.contains("r=\"C3\" s=\"7\""))
precondition(healthXML.contains("r=\"C4\" s=\"10\""))
precondition(healthXML.contains("r=\"E4\" s=\"5\""))
precondition(!healthXML.contains("r=\"3\" ht=\"24\""))
precondition(HealthCareReport.styles.contains("wrapText=\"1\""))
do {
    _ = try HealthCareReport.sheet([ReportRow(name: "اسم", rank: "موظف", amount: .infinity)], month: "2026", others: false)
    fatalError("Invalid health amount accepted")
} catch { }
print("Health care Excel wrapping and warning cell tests passed")
