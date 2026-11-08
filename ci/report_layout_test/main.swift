import AppKit
import Foundation

let name = "اسم منتسب وهمي طويل يتضمن اسم الاب والجد واللقب للتحقق من التفاف النص"
let columns: [ReportSheet.Column] = [
    .init(title: "ت", width: 5, kind: .serial),
    .init(title: "الاسم الكامل", width: 50, kind: .text),
    .init(title: "تفاصيل", width: 60, kind: .text),
    .init(title: "المبلغ", width: 18, kind: .amount)
]
let rows = (1...70).map { [String($0), name, "بيانات وهمية", ""] }
let sheet = ReportSheet(header: "قيادة وهمية", title: "كشف تجريبي", columns: columns, rows: rows, amounts: Array(repeating: 100, count: rows.count))
let wide = try EntityDeductionPDF.prepare(sheet)
precondition(wide.page.width > wide.page.height)
precondition(wide.pages.count > 1)
precondition(wide.pages.flatMap(Array.init) == Array(0..<rows.count))
precondition(wide.rowHeights.contains { $0 > 24 })
var entity = sheet
entity.entityDeduction = true
let portrait = try EntityDeductionPDF.prepare(entity)
precondition(portrait.page.width < portrait.page.height)
precondition(portrait.pages.flatMap(Array.init) == Array(0..<rows.count))
let badTotal = ReportSheet(header: "رأس", title: "كشف", columns: [.init(title: "المبلغ", width: 12, kind: .amount)], rows: [[""]], amounts: [1])
do {
    _ = try EntityDeductionPDF.prepare(badTotal)
    fatalError("Missing total-label space was accepted")
} catch { }
var invalid = sheet
invalid.rows[0][1] = String(repeating: "حقل طويل ", count: 6000)
do {
    _ = try EntityDeductionPDF.prepare(invalid)
    fatalError("Oversized row was accepted")
} catch { }
print("Portrait and landscape report layout tests passed with fictional data")

let healthRows = (1...85).map { [String($0), "موظف", name, "3500", "1"] }
let healthHeaders = ["ت", "الرتبة", "الاسم", "المبلغ", "عدد افراد الاسرة"]
let health = try HealthCarePDFLayout.make(title: "اشتراكات الرعاية الصحية", headers: healthHeaders, rows: healthRows, ratios: [7, 13, 42, 14, 17])
precondition(health.pages.count > 1)
precondition(health.pages.flatMap(Array.init) == Array(healthRows.indices))
precondition(health.heights.allSatisfy { $0 > 24 })
let blankHealth = try HealthCarePDFLayout.make(title: "رعاية", headers: healthHeaders, rows: [], ratios: [7, 13, 42, 14, 17])
precondition(blankHealth.pages.count == 1 && blankHealth.pages[0].isEmpty)
var tooLong = healthRows
 tooLong[0][2] = String(repeating: "اسم طويل ", count: 6000)
do {
    _ = try HealthCarePDFLayout.make(title: "رعاية", headers: healthHeaders, rows: tooLong, ratios: [7, 13, 42, 14, 17])
    fatalError("Oversized health row was accepted")
} catch { }
print("Health care layout wraps names and paginates all rows")
