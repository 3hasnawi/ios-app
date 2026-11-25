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

// An account can change while its last four digits and the bank name stay the same.
let oldAccount = SalaryAuditAccount(bank: "مصرف تجريبي", iban: "IQ00TEST000000000001234")
let newAccount = SalaryAuditAccount(bank: oldAccount.bank, iban: "IQ00TEST000000000991234")
let emptyAccount = SalaryAuditAccount(bank: "", iban: "")
let renamedBank = SalaryAuditAccount(bank: "مصرف تجريبي آخر", iban: oldAccount.iban)
precondition(SalaryBankChange(before: oldAccount, after: oldAccount) == nil)
precondition(SalaryBankChange(before: oldAccount, after: renamedBank) != nil)
precondition(SalaryBankChange(before: oldAccount, after: .init(bank: "", iban: oldAccount.iban)) != nil)
let comparison = SalaryBankChange(before: oldAccount, after: newAccount)!
let missing = SalaryBankChange(before: newAccount, after: emptyAccount)!
let bankRows = [comparison, missing].enumerated().map { i, change in
    SalaryChange(id: i, key: "test", name: "اسم تجريبي", rank: "", workplace: "", before: 100, after: 100, reasons: [], bankChange: change)
}
let bankSheet = AuditExport.salarySheet(.bank, bankRows, from: "أيلول", to: "تشرين الأول")
precondition(bankSheet.title.contains("السابق أيلول") && bankSheet.title.contains("الجديد تشرين الأول"))
precondition(Array(bankSheet.rows[0].suffix(4)) == [oldAccount.bank, oldAccount.iban, newAccount.bank, newAccount.iban])
precondition(Array(bankSheet.rows[1].suffix(2)) == ["غير مسجل", "غير مسجل"])
try XLSXWriter.validate(bankSheet)
let bankXML = XLSXWriter.worksheet(bankSheet)
precondition(bankXML.contains(oldAccount.iban) && bankXML.contains(newAccount.iban))

// Previous imports stored the count only in the original columns. Its allowance is a different field.
var profileColumns = SalaryReviewProfile.Columns()
let priorProfile = SalaryReviewProfile(details: [:], raw: .init(
    headers: ["الأطفال", "عدد الأطفال", "الحالة الاجتماعية", "الزوجية", "اسم الشهدة", "الشهادة"],
    values: ["30,000", "٠", "أعزب", "0", "دبلوم", "20000"]), columns: &profileColumns)
let currentProfile = SalaryReviewProfile(details: ["عدد الاطفال": "٢", "الحالة الزوجية": "متزوج", "الزوجية": "٥٠٬٠٠٠",
                                                   "اسم الشهادة": "بكالوريوس", "الشهادة": "٢٠٬٠٠٠"],
                                         raw: nil, columns: &profileColumns)
let childrenChanges = currentProfile.changes(from: priorProfile, kind: .children)
precondition(childrenChanges == [.init(label: "عدد الأطفال", before: Money.format(0), after: Money.format(2))])
let marriageChanges = currentProfile.changes(from: priorProfile, kind: .marital)
precondition(marriageChanges.count == 2 && marriageChanges[0].before == "أعزب" && marriageChanges[1].after == Money.format(50000))
precondition(currentProfile.changes(from: priorProfile, kind: .education) == [.init(label: "اسم الشهادة", before: "دبلوم", after: "بكالوريوس")])
let equivalent = SalaryReviewProfile(details: ["اسم الشهادة": "بكالوريُوس", "عدد الاطفال": "2.0", "الزوجية": "50,000"],
                                    raw: nil, columns: &profileColumns)
precondition(SalaryChangeKind.profileKinds.allSatisfy { equivalent.changes(from: currentProfile, kind: $0).isEmpty })
let noCount = SalaryReviewProfile(details: [:], raw: .init(headers: ["الشهادة", "الاطفال", "الزوجية"],
                                                          values: ["30000", "15000", ""]), columns: &profileColumns)
precondition(noCount.changes(from: priorProfile, kind: .children).isEmpty)
precondition(noCount.changes(from: priorProfile, kind: .marital).isEmpty)
precondition(noCount.changes(from: priorProfile, kind: .education).first?.after == Money.format(30000))
let profileRow = SalaryChange(id: 1, key: "test", name: "منتسب تجريبي", rank: "", workplace: "",
                              before: 100, after: 100, reasons: [], fieldChanges: marriageChanges)
let profileSheet = AuditExport.salarySheet(.marital, [profileRow], from: "أيلول", to: "تشرين الأول")
precondition(profileSheet.rows.count == 2 && !profileSheet.showTotal && profileSheet.columns.suffix(2).map(\.title) == ["أيلول", "تشرين الأول"])
precondition(Array(profileSheet.rows[0].suffix(2)) == ["أعزب", "متزوج"])
try XLSXWriter.validate(profileSheet)
print("Payroll family and qualification comparison and report tests passed")
