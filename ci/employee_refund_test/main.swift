import Foundation
func iban(_ code: String, _ serial: Int) -> String {
    let bban = code + String(format: "%015d", serial)
    var mod = 0
    for c in (bban + "IQ00").utf8 {
        for d in (c >= 65 ? String(c - 55) : String(c - 48)).utf8 { mod = (mod * 10 + Int(d - 48)) % 97 }
    }
    return "IQ" + String(format: "%02d", 98 - mod) + bban
}
func entry(_ code: String, _ serial: Int, _ amount: String) -> EmployeeRefund.Entry {
    .init(person: .init(name: "منتسب تجريبي " + String(serial), rank: "", workplace: "", iban: iban(code, serial), bank: code), amount: amount)
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
precondition(table.rows == [["OTHR","2500","OTHRIQ00"], ["RSHD","4500","RSHDIQ00"], ["المجموع الكلي","7000",""]])
var refs = Set<String>(), csvSum: Int64 = 0, offset = 0
for group in batch.groups {
    let csv = EmployeeRefund.csv(group, batch: batch, day: "20260101", offset: offset)
    for line in String(decoding: csv, as: UTF8.self).components(separatedBy: "\r\n").dropFirst() {
        let fields = line.components(separatedBy: "|")
        precondition(fields.count == 11 && refs.insert(fields[0]).inserted && fields[6] == group.bank.bic)
        precondition(EmployeeRefund.bankCode(fields[8]) == group.code && fields[9].contains("إعادة"))
        csvSum += Int64(fields[4])!
    }
    offset += group.payments.count
}
precondition(csvSum == batch.total && offset == 4)
for (sample, count) in [(entries, 5), ([entries[0]], 2), ([entries[1],entries[3]], 2)] {
    let urls = try EmployeeRefundExport.make(try plan(sample), date: Date(), letters: .init())
    precondition(urls.count == count && Set(urls.map(\.lastPathComponent)).count == count)
    try FileManager.default.removeItem(at: urls[0].deletingLastPathComponent())
}
precondition((try? plan(entries + [entries[0]])) == nil && (try? plan([])) == nil)
var wrong = banks; wrong["RAFB"]?.bic = "RSHDIQ00"
precondition((try? plan(entries, wrong)) == nil && (try? plan(entries, [:])) == nil)
precondition((try? plan([entry("RAFB", 99, "100")])) == nil && EmployeeRefund.bankCode("IQ00RAFB1234") == nil)
for invalid in ["0","-1","1.5","123,45","9999999999999999","nan"] { precondition(EmployeeRefund.amount(invalid) == nil) }
precondition((try? plan([entry("RAFB", 1, "999999999999999"), entry("RAFB", 2, "1")])) == nil)
print("Refund bank routing, CSVs, letter table totals, single/mixed batches and rejected transfers passed.")
