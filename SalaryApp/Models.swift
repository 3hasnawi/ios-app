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
    /// ملخّص تقارير ورقة الرواتب: لكل بند عدد الموظفين والمجموع (JSON). فارغ للأشهر المستوردة قبل هذه الميزة.
    var itemSummaryData: Data? = nil

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
    #Index<Employee>([\.month, \.name], [\.matchKey])

    var month: String = ""
    var name: String
    /// الاسم بعد التطبيع، يُستخدم للبحث.
    var nameKey: String
    /// الاسم بلا مسافات، يُستخدم للربط مع الاستقطاعات ("عبد الله" = "عبدالله").
    var matchKey: String
    var rank: String
    var workplace: String
    var netSalary: Double?
    /// بنود ورقة الرواتب لهذا الموظف {العنوان: المبلغ} (JSON).
    var itemsData: Data? = nil
    /// كل حقول تقرير الراتب من ورقة الرواتب {العمود: القيمة} (JSON): المخصصات والاستقطاعات وبيانات الموظف.
    var detailsData: Data? = nil
    /// لقطة مستقلة لكل أعمدة صف Excel كما وردت، بما فيها الصفر والفراغ والعناوين المتكررة.
    var rawColumnsData: Data? = nil

    init(month: String, name: String, rank: String, workplace: String, netSalary: Double?, items: [String: Double] = [:], details: [String: String] = [:], rawColumnsData: Data? = nil) {
        self.month = month
        self.name = name
        let key = ArabicText.normalize(name)
        self.nameKey = key
        self.matchKey = key.replacingOccurrences(of: " ", with: "")
        self.rank = rank
        self.workplace = workplace
        self.netSalary = netSalary
        self.itemsData = PayrollItems.encode(items)
        self.detailsData = details.isEmpty ? nil : try? JSONSerialization.data(withJSONObject: details)
        self.rawColumnsData = rawColumnsData
    }

    var items: [String: Double] { PayrollItems.decode(itemsData) }

    var details: [String: String] {
        guard let detailsData, let object = try? JSONSerialization.jsonObject(with: detailsData) as? [String: String] else { return [:] }
        return object
    }

    var rawColumns: PayrollColumnSnapshot? { PayrollColumnSnapshot.decode(rawColumnsData) }
}

/// ترتيب الأعمدة وقيم الخلايا من ورقة الرواتب؛ لا تتدخل في مفاتيح التقارير المعتمدة.
nonisolated struct PayrollColumnSnapshot: Codable, Sendable {
    var headers: [String]
    var values: [String]

    static func encode(headers: [String], values: [String]) -> Data? {
        let count = max(headers.count, values.count)
        let snapshot = Self(headers: headers + Array(repeating: "", count: count - headers.count),
                            values: values + Array(repeating: "", count: count - values.count))
        return try? JSONEncoder().encode(snapshot)
    }

    static func decode(_ data: Data?) -> Self? {
        guard let data, let snapshot = try? JSONDecoder().decode(Self.self, from: data),
              snapshot.headers.count == snapshot.values.count else { return nil }
        return snapshot
    }

    func values(named title: String) -> [String] {
        let key = ArabicText.normalize(title)
        return headers.indices.filter { ArabicText.normalize(headers[$0]) == key }.map { values[$0] }
    }
}

/// ترميز سريع لبنود ورقة الرواتب، وملخّص الشهر.
nonisolated enum PayrollItems {
    static func encode(_ items: [String: Double]) -> Data? {
        items.isEmpty ? nil : try? JSONSerialization.data(withJSONObject: items)
    }

    static func decode(_ data: Data?) -> [String: Double] {
        guard let data, let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return [:] }
        return object.compactMapValues { ($0 as? NSNumber)?.doubleValue }
    }

    struct Summary: Codable, Sendable, Hashable {
        var count: Int
        var total: Double
    }

    static func summary(of employees: [ImportedEmployee]) -> Data? {
        var result: [String: Summary] = [:]
        for e in employees {
            for (title, amount) in e.items {
                result[title, default: Summary(count: 0, total: 0)].count += 1
                result[title]!.total += amount
            }
        }
        return try? JSONEncoder().encode(result)
    }

    static func decodeSummary(_ data: Data?) -> [String: Summary]? {
        guard let data else { return nil }
        return try? JSONDecoder().decode([String: Summary].self, from: data)
    }
}

/// استقطاع من ورقة الاستقطاعات، مرتبط بالموظف عن طريق الاسم المطبَّع ضمن نفس الشهر.
@Model
final class Deduction {
    #Index<Deduction>([\.month, \.matchKey], [\.matchKey])

    var month: String = ""
    var employeeName: String
    var matchKey: String
    var title: String
    var amount: Double?
    var note: String
    /// الرتبة كما في ملف الاستقطاعات (إن وُجدت).
    var rank: String = ""

    init(month: String, employeeName: String, title: String, amount: Double?, note: String, rank: String = "") {
        self.month = month
        self.employeeName = employeeName
        self.matchKey = ArabicText.matchKey(employeeName)
        self.title = title
        self.amount = amount
        self.note = note
        self.rank = rank
    }
}

/// يحفظ نتيجة الاستيراد في الخلفية حتى لا تتجمد الواجهة مع عشرات آلاف الصفوف.
@ModelActor
actor DataImporter {
    /// يستبدل بيانات الشهر نفسه فقط، وتبقى الأشهر الأخرى كما هي.
    func replace(month: String, with imported: ImportResult, progress: ProgressSink? = nil) throws {
        // لا يُحفظ الحذف منفرداً؛ أي فشل يعيد التغييرات غير المحفوظة.
        modelContext.autosaveEnabled = false
        var committed = false
        defer { if !committed { modelContext.rollback() } }
        try removeData(of: month)
        // حفظ واحد حتى تبقى بيانات الشهر السابقة عند الفشل.
        let total = Double(max(1, imported.employees.count + imported.deductions.count))
        var inserted = 0
        func advanceProgress() {
            inserted += 1
            if inserted % 500 == 0 { progress?(Double(inserted) / total) }
        }
        for e in imported.employees {
            modelContext.insert(Employee(month: month, name: e.name, rank: e.rank, workplace: e.workplace, netSalary: e.netSalary, items: e.items, details: e.details, rawColumnsData: e.rawColumnsData))
            advanceProgress()
        }
        for d in imported.deductions {
            modelContext.insert(Deduction(month: month, employeeName: d.employeeName, title: d.title, amount: d.amount, note: d.note, rank: d.rank))
            advanceProgress()
        }
        let record = PayrollMonth(name: month, employeeCount: imported.employees.count, deductionCount: imported.deductions.count)
        record.itemSummaryData = PayrollItems.summary(of: imported.employees)
        modelContext.insert(record)
        try modelContext.save()
        committed = true
        progress?(1)
    }

    /// ملف رواتب فقط: يستبدل رواتب الشهر وتبقى استقطاعاته.
    func replacePayroll(month: String, with employees: [ImportedEmployee], progress: ProgressSink? = nil) throws {
        // لا يُحفظ الحذف منفرداً؛ أي فشل يعيد التغييرات غير المحفوظة.
        modelContext.autosaveEnabled = false
        var committed = false
        defer { if !committed { modelContext.rollback() } }
        for item in try modelContext.fetch(FetchDescriptor<Employee>(predicate: #Predicate { $0.month == month })) { modelContext.delete(item) }
        let total = Double(max(1, employees.count))
        for (i, e) in employees.enumerated() {
            modelContext.insert(Employee(month: month, name: e.name, rank: e.rank, workplace: e.workplace, netSalary: e.netSalary, items: e.items, details: e.details, rawColumnsData: e.rawColumnsData))
            if i % 500 == 499 { progress?(Double(i + 1) / total) }
        }
        try upsertMonth(month, employees: employees.count, deductions: nil, summary: PayrollItems.summary(of: employees))
        try modelContext.save()
        committed = true
        progress?(1)
    }

    /// ملف استقطاعات فقط: يستبدل استقطاعات الشهر وتبقى رواتبه.
    func replaceDeductions(month: String, with deductions: [ImportedDeduction], progress: ProgressSink? = nil) throws {
        // لا يُحفظ الحذف منفرداً؛ أي فشل يعيد التغييرات غير المحفوظة.
        modelContext.autosaveEnabled = false
        var committed = false
        defer { if !committed { modelContext.rollback() } }
        for item in try modelContext.fetch(FetchDescriptor<Deduction>(predicate: #Predicate { $0.month == month })) { modelContext.delete(item) }
        let total = Double(max(1, deductions.count))
        for (i, d) in deductions.enumerated() {
            modelContext.insert(Deduction(month: month, employeeName: d.employeeName, title: d.title, amount: d.amount, note: d.note, rank: d.rank))
            if i % 500 == 499 { progress?(Double(i + 1) / total) }
        }
        try upsertMonth(month, employees: nil, deductions: deductions.count, summary: nil)
        try modelContext.save()
        committed = true
        progress?(1)
    }

    /// سجل الشهر: يُحدّث العدد الذي تغيّر فقط، أو يُنشأ إن لم يكن موجوداً.
    private func upsertMonth(_ month: String, employees: Int?, deductions: Int?, summary: Data?) throws {
        var descriptor = FetchDescriptor<PayrollMonth>(predicate: #Predicate { $0.name == month })
        descriptor.fetchLimit = 1
        if let existing = try modelContext.fetch(descriptor).first {
            if let employees { existing.employeeCount = employees; existing.itemSummaryData = summary }
            if let deductions { existing.deductionCount = deductions }
            existing.importedAt = .now
        } else {
            let record = PayrollMonth(name: month, employeeCount: employees ?? 0, deductionCount: deductions ?? 0)
            record.itemSummaryData = summary
            modelContext.insert(record)
        }
    }

    func delete(month: String) throws {
        try removeData(of: month)
        try modelContext.save()
    }

    private func removeData(of month: String) throws {
        for item in try modelContext.fetch(FetchDescriptor<Employee>(predicate: #Predicate { $0.month == month })) { modelContext.delete(item) }
        for item in try modelContext.fetch(FetchDescriptor<Deduction>(predicate: #Predicate { $0.month == month })) { modelContext.delete(item) }
        for item in try modelContext.fetch(FetchDescriptor<PayrollMonth>(predicate: #Predicate { $0.name == month })) { modelContext.delete(item) }
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

extension PayrollMonth {
    /// اسم الشهر بدون السنة ("تشرين الاول 2026" ← "تشرين الاول") لعناوين قصيرة.
    var shortName: String {
        let words = name.split(separator: " ")
        guard words.count > 1, let last = words.last, last.allSatisfy(\.isNumber) else { return name }
        return words.dropLast().joined(separator: " ")
    }
}
