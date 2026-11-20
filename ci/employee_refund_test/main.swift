import Foundation
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
        payrollBIC: bic ?? code + "IQ00" + String(format: "%03d", serial)), amount: amount)
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
precondition(table.rows == [["1","OTHR","2500","1","OTHRIQ00"], ["2","RSHD","4500","2","RSHDIQ00"], ["","المجموع","7000","3",""]])
guard case .paragraph(let otherBody) = blocks[4] else { fatalError() }
let otherWords = try ArabicAmountWords.dinars(7000)
precondition(otherBody.text.contains(otherWords) && otherBody.text.contains("عددهم (3)") && otherBody.text.contains("مصدر تجريبي"))
precondition(otherBody.text.contains("إلى حسابات المصارف المبينة في الجدول أدناه"))
// قد تتضمن الدفعة الرافدين أيضاً؛ صياغة الكتاب الثاني تعتمد مصارف ذلك الكتاب وحده.
let mixed = try plan([entries[0], entries[1], entries[3]], ["RSHD": RefundBank(name: "مصرف تجريبي", bic: "RSHDIQ00")])
let singleBank = mixed.groups.filter { !$0.isRafidain }
let singleBlocks = try EmployeeRefundExport.letter(singleBank, batch: mixed, letters: .init(), date: "1 / 1 / 2026")
guard case .paragraph(let singleBody) = singleBlocks[4], case .table(let singleTable) = singleBlocks[5] else { fatalError() }
precondition(singleBody.text.contains("إلى حساب مصرف تجريبي، رمز السويفت (RSHDIQ00)"))
precondition(singleBody.text.contains("ملف CSV المرفق") && singleBody.text.contains("عددهم (2)"))
precondition(singleTable.rows == [["1","مصرف تجريبي","4500","2","RSHDIQ00"]])
let bankCSV = EmployeeRefund.csv(singleBank[0], batch: mixed, day: "20260101")
let bankRows = String(decoding: bankCSV, as: UTF8.self).components(separatedBy: "\r\n").dropFirst().map { $0.components(separatedBy: "|") }
precondition(bankRows.count == 2 && bankRows[0][6] == "RSHDIQ00002" && bankRows[1][6] == "RSHDIQ00004")
precondition(bankRows[0][8] == iban("RSHD", 2) && bankRows[1][8] == iban("RSHD", 4))
let raf = try plan([entries[0]], [:])
let rafBlocks = try EmployeeRefundExport.letter(raf.groups, batch: raf, letters: .init(), date: "1 / 1 / 2026")
guard case .paragraph(let rafBody) = rafBlocks[4], case .paragraph(let attachment) = rafBlocks[5] else { fatalError() }
precondition(rafBody.text.contains("مصرف الرافدين / فرع المستنصر المرقم (RAFBIQB1098)") && rafBody.text.contains("عددهم (1)"))
precondition(attachment.text.contains("قرص CD"))
precondition(EmployeeRefund.csv(raf.groups[0], batch: raf, day: "20260101").range(of: Data("|RAFBIQ00001|".utf8)) != nil)
var refs = Set<String>(), csvSum: Int64 = 0, offset = 0
var expectedAmounts = [iban("RAFB", 1): "10000", iban("RSHD", 2): "4000", iban("OTHR", 3): "2500", iban("RSHD", 4): "500"]
for group in batch.groups {
    let csv = EmployeeRefund.csv(group, batch: batch, day: "20260101")
    for line in String(decoding: csv, as: UTF8.self).components(separatedBy: "\r\n").dropFirst() {
        let fields = line.components(separatedBy: "|")
        precondition(fields.count == 11 && refs.insert(fields[0]).inserted && entries.contains { $0.person.iban == fields[8] && $0.person.payrollBIC == fields[6] })
        precondition(EmployeeRefund.bankCode(fields[8]) == group.code && fields[9].contains("إعادة"))
        precondition(expectedAmounts.removeValue(forKey: fields[8]) == fields[4])
        csvSum += Int64(fields[4])!
    }
    offset += group.payments.count
}
precondition(expectedAmounts.isEmpty && csvSum == batch.total && offset == 4)
precondition((try? plan([entries[0], entry("RSHD", 2, "")])) == nil)
for (sample, count) in [(entries, 5), ([entries[0]], 2), ([entries[1],entries[3]], 2)] {
    let urls = try EmployeeRefundExport.make(try plan(sample), date: Date(), letters: .init())
    precondition(urls.count == count && Set(urls.map(\.lastPathComponent)).count == count)
    try FileManager.default.removeItem(at: urls[0].deletingLastPathComponent())
}
precondition((try? plan(entries + [entries[0]])) == nil && (try? plan([])) == nil)
var wrong = banks; wrong["RSHD"]?.bic = "OTHRIQ00"
precondition(batch.groups.first { $0.isRafidain }?.bank.bic == "RAFBIQB1098")
precondition((try? plan(entries, wrong)) == nil && (try? plan(entries, [:])) == nil)
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
precondition(EmployeeRefund.payrollBIC(details: [:], columns: raw) == "RSHDIQ00002")
precondition(EmployeeRefund.payrollBIC(details: ["Swift_Code":"rshdiq00"], columns: nil) == "RSHDIQ00")
precondition(EmployeeRefund.payrollBIC(details: ["BIC":"RSHDIQ00004"], columns: raw).isEmpty)
precondition(EmployeeRefund.payrollBIC(details: ["رمز الفرع":"002"], columns: nil).isEmpty)
for bad in ["", "RSHDIQ0", "OTHRIQ00002"] {
    precondition((try? plan([entry("RSHD", 2, "100", bic: bad)])) == nil)
}
let twoRaf = try plan([entries[0], entry("RAFB", 7, "100")], [:])
let rafCSV = String(decoding: EmployeeRefund.csv(twoRaf.groups[0], batch: twoRaf, day: "20260101"), as: UTF8.self)
precondition(twoRaf.groups.count == 1 && rafCSV.contains("|RAFBIQ00001|") && rafCSV.contains("|RAFBIQ00007|"))
precondition(!rafCSV.contains("|RAFBIQB1098|"))
