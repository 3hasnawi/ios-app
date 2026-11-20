import Foundation
import PDFKit
import AppKit
func iban(_ code: String, _ serial: Int) -> String {
    let bban = code + String(format: "%015d", serial)
    var mod = 0
    for c in (bban + "IQ00").utf8 {
        for d in (c >= 65 ? String(c - 55) : String(c - 48)).utf8 { mod = (mod * 10 + Int(d - 48)) % 97 }
    }
    return "IQ" + String(format: "%02d", 98 - mod) + bban
}
func entry(_ code: String, _ serial: Int, _ amount: String, bic: String? = nil) -> EmployeeRefund.Entry {
    .init(person: .init(name: "منتسب تجريبي " + String(serial), rank: "", workplace: "", iban: iban(code, serial), bank: code,
        payrollBIC: .value(bic ?? code + "IQ00" + String(format: "%03d", serial))), amount: amount)
}
let payer = iban("RAFB", 99)
let entries = [entry("RAFB", 1, "١٠٬٠٠٠"), entry("RSHD", 2, "4,000"), entry("OTHR", 3, "2500"), entry("RSHD", 4, "500")]
let banks = Dictionary(uniqueKeysWithValues: ["RAFB","RSHD","OTHR"].map { ($0, RefundBank(name: $0, bic: $0 + "IQ00")) })
func plan(_ rows: [EmployeeRefund.Entry], _ directory: [String: RefundBank] = banks) throws -> EmployeeRefund.Batch {
    try EmployeeRefund.plan(entries: rows, banks: directory, payer: "جهة اختبار", account: payer, source: "مصدر تجريبي")
}
let batch = try plan(entries)
precondition(batch.total == 17000 && batch.groups.count == 3)
let other = batch.groups.filter { !$0.isRafidain }
let blocks = try EmployeeRefundExport.letter(other, batch: batch, letters: .init(), date: "1 / 1 / 2026")
guard case .table(let table) = blocks[5] else { fatalError() }
precondition(table.headers == ["ت","اسم المصرف","المبلغ","عدد المنتسبين","Receiver BIC"])
precondition(table.rows.map({ $0.map(ArabicText.latinDigits) }) == [["1","OTHR","2,500","1","OTHRIQ00003"], ["2","RSHD","4,500","2","RSHDIQ00002"], ["","المجموع","7,000","3",""]])
guard case .paragraph(let otherBody) = blocks[4] else { fatalError() }
let otherWords = try ArabicAmountWords.dinars(7000)
precondition(otherBody.text.contains(otherWords) && otherBody.text.contains("عددهم (٣)") && otherBody.text.contains("مصدر تجريبي"))
precondition(otherBody.text.contains("إلى حسابات المصارف المبينة في الجدول أدناه"))
// قد تتضمن الدفعة الرافدين أيضاً؛ صياغة الكتاب الثاني تعتمد مصارف ذلك الكتاب وحده.
let mixed = try plan([entries[0], entries[1], entries[3]], ["RSHD": RefundBank(name: "مصرف تجريبي", bic: "RSHDIQ00")])
let singleBank = mixed.groups.filter { !$0.isRafidain }
let singleBlocks = try EmployeeRefundExport.letter(singleBank, batch: mixed, letters: .init(), date: "1 / 1 / 2026")
guard case .paragraph(let singleBody) = singleBlocks[4], case .table(let singleTable) = singleBlocks[5] else { fatalError() }
precondition(singleBody.text.contains("إلى حساب مصرف تجريبي، رمز السويفت (RSHDIQ00002)"))
precondition(singleBody.text.contains("ملف CSV المرفق") && singleBody.text.contains("عددهم (٢)"))
precondition(singleTable.rows.map({ $0.map(ArabicText.latinDigits) }) == [["1","مصرف تجريبي","4,500","2","RSHDIQ00002"]])
let bankCSV = EmployeeRefund.csv(singleBank[0], batch: mixed, day: "20260101")
let bankRows = String(decoding: bankCSV, as: UTF8.self).components(separatedBy: "\r\n").dropFirst().map { $0.components(separatedBy: "|") }
precondition(bankRows.count == 2 && bankRows[0][6] == "RSHDIQ00002" && bankRows[1][6] == "RSHDIQ00004")
precondition(bankRows[0][8] == iban("RSHD", 2) && bankRows[1][8] == iban("RSHD", 4))
let raf = try plan([entries[0]], [:])
let rafBlocks = try EmployeeRefundExport.letter(raf.groups, batch: raf, letters: .init(), date: "1 / 1 / 2026")
guard case .paragraph(let rafBody) = rafBlocks[4], case .paragraph(let attachment) = rafBlocks[5] else { fatalError() }
precondition(rafBody.text.contains("مصرف الرافدين / فرع المستنصر المرقم (RAFBIQB1098)") && rafBody.text.contains("عددهم (١)"))
precondition(attachment.text.contains("قرص CD"))
precondition(EmployeeRefund.csv(raf.groups[0], batch: raf, day: "20260101").range(of: Data("|RAFBIQ00001|".utf8)) != nil)
var refs = Set<String>(), csvSum: Int64 = 0, offset = 0
var expectedAmounts = [iban("RAFB", 1): "10000", iban("RSHD", 2): "4000", iban("OTHR", 3): "2500", iban("RSHD", 4): "500"]
for group in batch.groups {
    let csv = EmployeeRefund.csv(group, batch: batch, day: "20260101")
    for line in String(decoding: csv, as: UTF8.self).components(separatedBy: "\r\n").dropFirst() {
        let fields = line.components(separatedBy: "|")
        precondition(fields.count == 11 && refs.insert(fields[0]).inserted && entries.contains { $0.person.iban == fields[8] && $0.person.payrollBIC == .value(fields[6]) })
        precondition(EmployeeRefund.bankCode(fields[8]) == group.code && fields[9].contains("إعادة"))
        precondition(expectedAmounts.removeValue(forKey: fields[8]) == fields[4])
        csvSum += Int64(fields[4])!
    }
    offset += group.payments.count
}
precondition(expectedAmounts.isEmpty && csvSum == batch.total && offset == 4)
precondition((try? plan([entries[0], entry("RSHD", 2, "")])) == nil)
for (sample, count) in [(entries, 6), ([entries[0]], 3), ([entries[1],entries[3]], 3)] {
    let urls = try EmployeeRefundExport.make(try plan(sample), date: Date(), letters: .init())
    precondition(urls.count == count && Set(urls.map(\.lastPathComponent)).count == count)
    try FileManager.default.removeItem(at: urls[0].deletingLastPathComponent())
}
precondition((try? plan(entries + [entries[0]])) == nil && (try? plan([])) == nil)
var wrong = banks; wrong["RSHD"]?.bic = "OTHRIQ00"
precondition(batch.groups.first { $0.isRafidain }?.bank.bic == "RAFBIQB1098")
let outdated = try plan(entries, wrong)
precondition(outdated.groups.first { $0.code == "RSHD" }?.bank.bic == "RSHDIQ00002")
precondition((try? plan(entries, [:])) == nil)
var preferred = banks
preferred["RSHD"]?.bic = "RSHDIQ00004"
let tied = try plan(entries.reversed(), preferred)
precondition(tied.groups.first { $0.code == "RSHD" }?.bank.bic == "RSHDIQ00004")
let majority = try plan(entries + [entry("RSHD", 5, "1", bic: "RSHDIQ00002"),
                                   entry("OTHR", 6, "1", bic: "OTHRIQ00009"),
                                   entry("OTHR", 7, "1", bic: "OTHRIQ00009")], preferred)
let majorityGroups = majority.groups.filter { !$0.isRafidain }
let majorityBlocks = try EmployeeRefundExport.letter(majorityGroups, batch: majority, letters: .init(), date: "1 / 1 / 2026")
guard case .table(let majorityTable) = majorityBlocks[5] else { fatalError() }
precondition(majorityTable.rows.map({ $0.map(ArabicText.latinDigits) }) == [["1","OTHR","2,502","3","OTHRIQ00009"], ["2","RSHD","4,501","3","RSHDIQ00002"], ["","المجموع","7,003","6",""]])
let sole = try plan([entries[1]], ["RSHD": RefundBank(name: "مصرف تجريبي")])
precondition(sole.groups.first?.bank.bic == "RSHDIQ00002")
precondition((try? plan([entry("RAFB", 99, "100")])) == nil && EmployeeRefund.bankCode("IQ00RAFB1234") == nil)
for invalid in ["0","-1","1.5","123,45","9999999999999999","nan"] { precondition(EmployeeRefund.amount(invalid) == nil) }
precondition((try? plan([entry("RAFB", 1, "999999999999999"), entry("RAFB", 2, "1")])) == nil)
print("Refund bank routing, CSVs, letter table totals, single/mixed batches and rejected transfers passed.")

let standalone = try plan([entries[3]])
let singleCSV = EmployeeRefund.csv(standalone.groups[0], batch: standalone, day: "20260101")
let singleRef = String(decoding: singleCSV, as: UTF8.self).components(separatedBy: "\r\n")[1].components(separatedBy: "|")[0]
precondition(refs.contains(singleRef) && singleRef.count <= 35)
let changed = try plan([entry("RSHD", 4, "501")])
precondition(EmployeeRefund.csv(changed.groups[0], batch: changed, day: "20260101") != singleCSV)

// تُستخدم خلايا الرواتب المحفوظة في اللقطة حتى لو سبق الاستيراد هذه الميزة.
let raw = PayrollColumnSnapshot.decode(PayrollColumnSnapshot.encode(headers: ["IBAN","رمز السويفت","Receiver BIC"], values: ["ignored"," rshdiq00002 ","RSHDIQ٠٠٠٠٢"]))
precondition(EmployeeRefund.payrollBIC(details: [:], columns: raw) == .value("RSHDIQ00002"))
precondition(EmployeeRefund.payrollBIC(details: ["Swift_Code":"rshdiq00"], columns: nil) == .value("RSHDIQ00"))
precondition(EmployeeRefund.payrollBIC(details: ["BIC":"RSHDIQ00004"], columns: raw) == .conflicting)
precondition(EmployeeRefund.payrollBIC(details: ["رمز الفرع":"002"], columns: nil) == .missingColumn)
for bad in ["", "RSHDIQ0", "OTHRIQ00002"] {
    precondition((try? plan([entry("RSHD", 2, "100", bic: bad)])) == nil)
}
let twoRaf = try plan([entries[0], entry("RAFB", 7, "100")], [:])
let rafCSV = String(decoding: EmployeeRefund.csv(twoRaf.groups[0], batch: twoRaf, day: "20260101"), as: UTF8.self)
precondition(twoRaf.groups.count == 1 && rafCSV.contains("|RAFBIQ00001|") && rafCSV.contains("|RAFBIQ00007|"))
precondition(!rafCSV.contains("|RAFBIQB1098|"))

precondition(EmployeeRefund.payrollBIC(details: ["SWIFT":"  "], columns: nil) == .empty)
precondition(EmployeeRefund.payrollBIC(details: ["SWIFT":"\u{200f}RAFBiq00\u{200e}"], columns: nil) == .value("RAFBIQ00"))

let historical = PayrollColumnSnapshot(headers: ["الرفرنس"], values: ["RAFBIQ00007"])
precondition(EmployeeRefund.payrollBIC(details: [:], columns: historical) == .value("RAFBIQ00007"))
precondition(EmployeeRefund.payrollBIC(details: ["الرفرنس":"RAFBIQ00007"], columns: nil) == .value("RAFBIQ00007"))
precondition(EmployeeRefund.payrollBIC(details: ["Receiver BIC":"RAFBIQ00008"], columns: historical) == .conflicting)
precondition(EmployeeRefund.payrollBIC(details: ["الرفرنس":"٠"], columns: nil) == .empty)
precondition(EmployeeRefund.payrollBIC(details: ["الرفرنس":"0", "Receiver BIC":"RAFBIQ00008"], columns: nil) == .value("RAFBIQ00008"))

// يوم مميز للتحقق من بقائه في CSV وترك خانته للكتابة اليدوية في الكتاب.
let refundDay = Calendar.current.date(from: DateComponents(year: 2026, month: 11, day: 27, hour: 12))!
var signers = RefundLetterSettings()
signers.first = "العضو الأول"
signers.second = "العضو الثاني"
signers.third = "العضو الثالث"
// قد تحمل الدفعة المؤرشفة فقرة نسخ مختلفة في كل كتاب، أو في القالب الموروث.
func legacySettings(_ footer: String, number: String) throws -> RefundLetterSettings {
    var settings = signers
    var template = CustomTransferTemplate()
    template.number = "٩٩"
    template.footer = "وجهة أخرى من قالب التحويلات"
    settings.template = template
    var record = try JSONSerialization.jsonObject(with: JSONEncoder().encode(settings)) as! [String: Any]
    record["number"] = number
    record["footer"] = footer
    return try JSONDecoder().decode(RefundLetterSettings.self, from: JSONSerialization.data(withJSONObject: record))
}
let letters = try RefundLetters(rafidain: legacySettings("الأرشيف.", number: ""), otherBanks: legacySettings("جهة قديمة", number: "٧٧"))
let expectedCopies = "نسخة منه إلى\nشعبة المصروفات / شعبة الموازنة / شعبة الرواتب / إجراء اللازم والمتابعة."
for (groups, settings) in [(raf.groups, letters.rafidain), (other, letters.otherBanks)] {
    let letter = try EmployeeRefundExport.letter(groups, batch: batch, letters: settings, date: " / ١١ / ٢٠٢٦")
    guard case .paragraph(let copies) = letter.last else { fatalError("Missing copies footer") }
    guard case .paragraph(let reference) = letter[1] else { fatalError("Missing reference") }
    precondition(reference.text.components(separatedBy: "\n").first == "العدد: ١\u{200F}/٢٢/ح/")
    precondition(copies.text == expectedCopies)
}
let codes = ["RSHD", "OTHR", "BANK", "TEST"]
let directory = Dictionary(uniqueKeysWithValues: codes.map { ($0, RefundBank(name: "مصرف تجريبي للاستثمار والتمويل " + $0)) })
for bankCount in [1, 4] {
    let selectedCodes = codes.prefix(bankCount)
    let sample = [entry("RAFB", 1, "500000")] + selectedCodes.enumerated().map { entry($0.element, $0.offset + 2, "1500000") }
    let urls = try EmployeeRefundExport.make(try plan(sample, directory), date: refundDay, letters: letters)
    defer { try? FileManager.default.removeItem(at: urls[0].deletingLastPathComponent()) }
    let csvURLs = urls.filter { $0.pathExtension == "csv" }
    let recovered = try RefundCSVRecovery.read(csvURLs)
    precondition(recovered.payments.count == sample.count && recovered.payer == "جهة اختبار" && recovered.source == "مصدر تجريبي")
    precondition((try? RefundCSVRecovery.read(csvURLs + [csvURLs[0]])) == nil)
    for url in urls {
        if url.pathExtension == "csv" {
            let csv = try String(contentsOf: url, encoding: .utf8)
            precondition(csv.contains("|20261127|"))
            continue
        }
        let pdf = PDFDocument(url: url)!
        precondition(pdf.pageCount == 1)
        let page = pdf.page(at: 0)!
        let text = ArabicText.latinDigits(page.string ?? "")
        if url.lastPathComponent.hasPrefix("قائمة") {
            for entry in sample { precondition(text.contains(entry.person.iban)) }
            continue
        }
        precondition(text.contains("2026") && !text.contains("27"))
        if url.lastPathComponent.contains("الرشيد") {
            for (index, code) in selectedCodes.enumerated() {
                precondition(text.contains(code + "IQ00" + String(format: "%03d", index + 2)))
            }
            if bankCount == 4, let path = ProcessInfo.processInfo.environment["REFUND_PREVIEW_DIR"] {
                let folder = URL(fileURLWithPath: path, isDirectory: true)
                try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
                try FileManager.default.copyItem(at: url, to: folder.appendingPathComponent("refund-letter.pdf"))
                let image = page.thumbnail(of: CGSize(width: 1190, height: 1684), for: .mediaBox)
                let bitmap = NSBitmapImageRep(cgImage: image.cgImage(forProposedRect: nil, context: nil, hints: nil)!)
                try bitmap.representation(using: .png, properties: [:])!.write(to: folder.appendingPathComponent("refund-letter.png"))
            }
        }
    }
}
print("Refund letters rendered and checked on one A4 page; CSV retains full value date.")
