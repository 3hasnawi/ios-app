import Foundation
for (nameHeader, amountHeader) in [("2اسم الاستقطاع", "2مبلغ الاستقطاع"), ("اسم الاستقطاع 2", "مبلغ الاستقطاع 2"), ("اسم الاستقطاع ٢", "٢ مبلغ الاستقطاع"), ("اسم الاستقطاع2", "2 مبلغ الاستقطاع")] {
    let parser = SheetParser()
    parser.consume(["Name_m", "صافي الراتب", "اسم الاستقطاع", "مبلغ الاستقطاع", nameHeader, amountHeader, "تضمين"])
    parser.consume(["موظف وهمي", "1000", "جهة أولى وهمية", "100", "جهة ثانية وهمية", "250", "75"])
    parser.consume(["موظف وهمي ثان", "2000", "جهة أولى وهمية", "200", "جهة ثانية وهمية", "0", "0"])
    parser.finish()
    precondition(parser.result.employees.count == 2)
    let first = parser.result.employees[0]
    precondition(first.items["جهة أولى وهمية"] == 100)
    precondition(first.items["جهة ثانية وهمية"] == 250)
    precondition(first.items["تضمين"] == 75)
    precondition(first.details["2اسم الاستقطاع"] == "جهة ثانية وهمية")
    precondition(first.details["2مبلغ الاستقطاع"] == "250")
    precondition(parser.result.employees[1].items["تضمين"] == nil)
    precondition(parser.result.employees[1].items["جهة ثانية وهمية"] == nil)
}
print("Payroll deduction pair and تضمين tests passed")

// كشف تنفيذ ومصارف مستقل أو كورقة بجانب الرواتب: ترتيب الأعمدة لا يغيّر التعرّف.
for branchHeader in ["اسم الفرع", "الفرع"] {
    let parser = SheetParser()
    parser.consume(["رقم الموظف", "الرتبة", "الاسم", "اسم المصرف", branchHeader, "المبلغ", "الملاحظات", "المديرية"])
    parser.consume(["101", "مفوض", "منتسب وهمي", "مصرف وهمي", "مصرف وهمي / فرع تجريبي", "12345", "إضبارة وهمية 123", "مديرية وهمية"])
    parser.consume(["102", "شرطي", "منتسب وهمي ثان", "مصرف وهمي", "جهة وهمية", "500", "", "مديرية وهمية"])
    parser.consume(["103", "شرطي", "منتسب صفر", "مصرف وهمي", "جهة وهمية", "0", "", "مديرية وهمية"])
    parser.consume(["104", "شرطي", "منتسب بلا مبلغ", "مصرف وهمي", "جهة وهمية", "", "", "مديرية وهمية"])
    parser.finish()
    precondition(parser.result.employees.isEmpty && parser.result.deductions.count == 2)
    let first = parser.result.deductions[0]
    precondition(first.rank == "مفوض" && first.employeeName == "منتسب وهمي" && first.amount == 12345)
    precondition(first.title == "مصرف وهمي / فرع تجريبي" && first.note == "إضبارة وهمية 123")
    precondition(parser.result.deductions[1].title == "جهة وهمية" && parser.result.deductions[1].note.isEmpty)
}
let payrollSheet = SheetParser()
payrollSheet.consume(["Name_m", "صافي الراتب"])
payrollSheet.consume(["منتسب وهمي", "1000"])
payrollSheet.finish()
let deductionsSheet = SheetParser()
deductionsSheet.consume(["الرتبة", "الاسم", "اسم المصرف", "اسم الفرع", "المبلغ", "الملاحظات"])
deductionsSheet.consume(["مفوض", "منتسب وهمي", "مصرف وهمي", "فرع وهمي", "75", "رقم وهمي"])
deductionsSheet.finish()
var combined = ImportResult()
combined.merge(payrollSheet.result)
combined.merge(deductionsSheet.result)
precondition(combined.employees.count == 1 && combined.deductions.count == 1)
print("Standalone and combined deduction sheets passed")
// لا تُغيّر القاعدة الخاصة بهذا الملف معالجة ملفات الاستقطاعات الأخرى.
let ordinary = SheetParser()
ordinary.consume(["الاسم", "المبلغ", "جهة التنفيذ"])
ordinary.consume(["اسم وهمي", "0", "جهة وهمية"])
ordinary.finish()
precondition(ordinary.result.deductions.count == 1 && ordinary.result.deductions[0].amount == 0)

let names = ["يوسف وهمي", "إياد وهمي", "باسم وهمي", "أحمد وهمي", "محمد وهمي"]
let orderedNames = names.sorted { ArabicText.alphabeticalOrder($0, $1) == .orderedAscending }
precondition(orderedNames == ["أحمد وهمي", "إياد وهمي", "باسم وهمي", "محمد وهمي", "يوسف وهمي"])
precondition(ArabicText.alphabeticalOrder("أحمد", "احمد") != .orderedSame)
precondition(ArabicText.alphabeticalOrder("أحمد", "أحمد") == .orderedSame)
print("Arabic alphabetical ordering passed")

let records = [
    ReportRow(id: 3, name: "محمد وهمي", rank: "مفوض", workplace: "", amount: 300, note: "إضبارة ٣", entity: "جهة وهمية"),
    ReportRow(id: 2, name: "أحمد وهمي", rank: "شرطي", workplace: "", amount: 200, note: "إضبارة ٢", entity: "جهة وهمية"),
    ReportRow(id: 1, name: "أحمد وهمي", rank: "ضابط", workplace: "", amount: 100, note: "إضبارة ١", entity: "جهة وهمية")
].sorted(by: ReportRow.nameOrder)
precondition(records.map(\.id) == [1, 2, 3])
precondition(records.map(\.amount) == [100, 200, 300])
precondition(records.map(\.note) == ["إضبارة ١", "إضبارة ٢", "إضبارة ٣"])
