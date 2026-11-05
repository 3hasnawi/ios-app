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
