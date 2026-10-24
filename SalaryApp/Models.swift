import Foundation
import SwiftData

/// موظف واحد كما ورد في ملف الرواتب.
@Model
final class Employee {
    #Index<Employee>([\.name])

    var name: String
    /// الاسم بعد التطبيع، يُستخدم للبحث.
    var nameKey: String
    /// الاسم بلا مسافات، يُستخدم للربط مع الاستقطاعات ("عبد الله" = "عبدالله").
    var matchKey: String
    var rank: String
    var workplace: String
    var netSalary: Double?

    init(name: String, rank: String, workplace: String, netSalary: Double?) {
        self.name = name
        self.nameKey = ArabicText.normalize(name)
        self.matchKey = ArabicText.matchKey(name)
        self.rank = rank
        self.workplace = workplace
        self.netSalary = netSalary
    }
}

/// استقطاع مرتبط بالموظف عن طريق الاسم المطبَّع.
@Model
final class Deduction {
    #Index<Deduction>([\.matchKey])

    var employeeName: String
    var matchKey: String
    var title: String
    var amount: Double?
    var note: String

    init(employeeName: String, title: String, amount: Double?, note: String) {
        self.employeeName = employeeName
        self.matchKey = ArabicText.matchKey(employeeName)
        self.title = title
        self.amount = amount
        self.note = note
    }
}

/// يحفظ نتيجة الاستيراد في الخلفية حتى لا تتجمد الواجهة مع عشرات آلاف الصفوف.
@ModelActor
actor DataImporter {
    /// الملف الجديد يستبدل البيانات القديمة من نفس النوع (الموظفين أو الاستقطاعات).
    func replace(with imported: ImportResult) throws {
        if !imported.employees.isEmpty {
            try modelContext.delete(model: Employee.self)
            for e in imported.employees {
                modelContext.insert(Employee(name: e.name, rank: e.rank, workplace: e.workplace, netSalary: e.netSalary))
            }
        }
        if !imported.deductions.isEmpty {
            try modelContext.delete(model: Deduction.self)
            for d in imported.deductions {
                modelContext.insert(Deduction(employeeName: d.employeeName, title: d.title, amount: d.amount, note: d.note))
            }
        }
        try modelContext.save()
    }
}
