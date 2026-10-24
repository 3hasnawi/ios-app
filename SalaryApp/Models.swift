import Foundation
import SwiftData

/// شهر رواتب واحد = ملف Excel واحد، واسمه هو اسم الملف (مثل "تشرين الاول 2026").
@Model
final class PayrollMonth {
    var name: String
    /// سنة×100 + رقم الشهر للترتيب (0 إذا لم يُفهم الشهر من الاسم).
    var sortKey: Int
    var importedAt: Date
    var employeeCount: Int
    var deductionCount: Int

    init(name: String, employeeCount: Int, deductionCount: Int) {
        self.name = name
        self.sortKey = MonthName.sortKey(name)
        self.importedAt = .now
        self.employeeCount = employeeCount
        self.deductionCount = deductionCount
    }
}

/// موظف واحد كما ورد في ملف الرواتب.
@Model
final class Employee {
    #Index<Employee>([\.month, \.name])

    var month: String = ""
    var name: String
    /// الاسم بعد التطبيع، يُستخدم للبحث.
    var nameKey: String
    /// الاسم بلا مسافات، يُستخدم للربط مع الاستقطاعات ("عبد الله" = "عبدالله").
    var matchKey: String
    var rank: String
    var workplace: String
    var netSalary: Double?

    init(month: String, name: String, rank: String, workplace: String, netSalary: Double?) {
        self.month = month
        self.name = name
        let key = ArabicText.normalize(name)
        self.nameKey = key
        self.matchKey = key.replacingOccurrences(of: " ", with: "")
        self.rank = rank
        self.workplace = workplace
        self.netSalary = netSalary
    }
}

/// استقطاع من ورقة الاستقطاعات، مرتبط بالموظف عن طريق الاسم المطبَّع ضمن نفس الشهر.
@Model
final class Deduction {
    #Index<Deduction>([\.month, \.matchKey])

    var month: String = ""
    var employeeName: String
    var matchKey: String
    var title: String
    var amount: Double?
    var note: String

    init(month: String, employeeName: String, title: String, amount: Double?, note: String) {
        self.month = month
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
    /// يستبدل بيانات الشهر نفسه فقط، وتبقى الأشهر الأخرى كما هي.
    func replace(month: String, with imported: ImportResult) throws {
        try removeData(of: month)
        // الحفظ على دفعات حتى لا تتراكم عشرات آلاف السجلات غير المحفوظة في الذاكرة.
        var pending = 0
        func flush() throws {
            pending += 1
            if pending >= 5000 { try modelContext.save(); pending = 0 }
        }
        for e in imported.employees {
            modelContext.insert(Employee(month: month, name: e.name, rank: e.rank, workplace: e.workplace, netSalary: e.netSalary))
            try flush()
        }
        for d in imported.deductions {
            modelContext.insert(Deduction(month: month, employeeName: d.employeeName, title: d.title, amount: d.amount, note: d.note))
            try flush()
        }
        modelContext.insert(PayrollMonth(name: month, employeeCount: imported.employees.count, deductionCount: imported.deductions.count))
        try modelContext.save()
    }

    func delete(month: String) throws {
        try removeData(of: month)
        try modelContext.save()
    }

    private func removeData(of month: String) throws {
        try modelContext.delete(model: Employee.self, where: #Predicate { $0.month == month })
        try modelContext.delete(model: Deduction.self, where: #Predicate { $0.month == month })
        try modelContext.delete(model: PayrollMonth.self, where: #Predicate { $0.name == month })
    }
}

/// اسم الشهر من اسم الملف، وترتيبه الزمني.
nonisolated enum MonthName {
    static func label(for url: URL) -> String {
        url.deletingPathExtension().lastPathComponent.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// يفهم الأسماء العراقية والشامية (تشرين الاول، كانون الثاني...) والمصرية (أكتوبر، يناير...) والأرقام.
    static func sortKey(_ label: String) -> Int {
        let words = ArabicText.normalize(label)
            .replacingOccurrences(of: "_", with: " ")
            .replacingOccurrences(of: "-", with: " ")
            .split(separator: " ").map(String.init)

        var year: Int?
        var month: Int?
        var numbers: [Int] = []
        for word in words {
            if let n = ArabicText.number(from: word).map({ Int($0) }), word.allSatisfy({ $0.isNumber }) {
                if n >= 1900 && n <= 2200 { year = n } else { numbers.append(n) }
            }
        }
        for (i, word) in words.enumerated() {
            let pair = i + 1 < words.count ? word + " " + words[i + 1] : ""
            if let m = names[pair] ?? names[word] {
                month = m
                break
            }
        }
        if month == nil, let n = numbers.first(where: { (1...12).contains($0) }) {
            month = n
        }
        guard let year, let month else { return 0 }
        return year * 100 + month
    }

    private static let names: [String: Int] = {
        let table: [(String, Int)] = [
            ("كانون الثاني", 1), ("شباط", 2), ("آذار", 3), ("نيسان", 4), ("أيار", 5), ("حزيران", 6),
            ("تموز", 7), ("آب", 8), ("أيلول", 9), ("تشرين الأول", 10), ("تشرين الثاني", 11), ("كانون الأول", 12),
            ("يناير", 1), ("فبراير", 2), ("مارس", 3), ("أبريل", 4), ("ابريل", 4), ("مايو", 5), ("يونيو", 6),
            ("يوليو", 7), ("أغسطس", 8), ("سبتمبر", 9), ("أكتوبر", 10), ("نوفمبر", 11), ("ديسمبر", 12),
            ("كانون2", 1), ("تشرين1", 10), ("تشرين2", 11), ("كانون1", 12),
        ]
        return Dictionary(table.map { (ArabicText.normalize($0.0), $0.1) }, uniquingKeysWith: { a, _ in a })
    }()
}
