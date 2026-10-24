import AppIntents
import SwiftData
import SwiftUI
import UIKit
import UniformTypeIdentifiers

/// قاعدة بيانات واحدة يشترك فيها التطبيق واختصارات تطبيق "الاختصارات" (Shortcuts) وSiri.
enum SharedModel {
    @MainActor static let container: ModelContainer = {
        do {
            return try ModelContainer(for: PayrollMonth.self, Employee.self, Deduction.self)
        } catch {
            fatalError("تعذّر فتح قاعدة البيانات: \(error)")
        }
    }()
}

/// ينقل اسماً من اختصار "افتح البحث" إلى الشاشة الرئيسية.
@MainActor @Observable
final class ShortcutRouter {
    static let shared = ShortcutRouter()
    var pendingSearch: String?
}

// MARK: - نطاق الأشهر

enum ShortcutMonthRange: String, AppEnum {
    case latest, lastThree, all

    static let typeDisplayRepresentation: TypeDisplayRepresentation = "الأشهر"
    static let caseDisplayRepresentations: [ShortcutMonthRange: DisplayRepresentation] = [
        .latest: "آخر شهر",
        .lastThree: "آخر 3 أشهر",
        .all: "كل الأشهر",
    ]

    var limit: Int? {
        switch self {
        case .latest: 1
        case .lastThree: 3
        case .all: nil
        }
    }
}

// MARK: - البحث عن موظف (بدون فتح التطبيق)

struct SearchEmployeeIntent: AppIntent {
    static let title: LocalizedStringResource = "البحث عن موظف"
    static let description = IntentDescription(
        "يبحث بالاسم في رواتب الأشهر المحفوظة ويعيد صافي الراتب والاستقطاعات.",
        categoryName: "البحث"
    )

    @Parameter(title: "الاسم", requestValueDialog: "ما اسم الموظف؟")
    var name: String

    @Parameter(title: "الأشهر", default: .all)
    var range: ShortcutMonthRange

    @Parameter(title: "إظهار الاستقطاعات", default: true)
    var includeDeductions: Bool

    static var parameterSummary: some ParameterSummary {
        Summary("ابحث عن \(\.$name) في \(\.$range)") {
            \.$includeDeductions
        }
    }

    @MainActor
    func perform() async throws -> some IntentResult & ReturnsValue<String> & ProvidesDialog & ShowsSnippetIntent {
        ShortcutSelection.clear()
        let results = try ShortcutSearch.run(name: name, range: range, includeDeductions: includeDeductions)

        let text = results.isEmpty
            ? tr("لم يُعثر على \"\(name)\".", "No match for \"\(name)\".")
            : results.map(\.summary).joined(separator: "\n\n")

        let dialog: IntentDialog
        if let first = results.first {
            let spoken = results.count == 1
                ? first.spokenSummary
                : tr("وجدت \(Num.format(results.count)) نتائج. أولها: \(first.spokenSummary)",
                     "Found \(Num.format(results.count)) results. First: \(first.spokenSummary)")
            dialog = IntentDialog("\(spoken)")
        } else {
            dialog = IntentDialog("\(text)")
        }

        // النافذة تفاعلية: الضغط على نتيجة يفتح تفاصيلها مع زر "نسخ كصورة".
        return .result(
            value: text,
            dialog: dialog,
            snippetIntent: SearchResultsSnippetIntent(name: name, range: range, includeDeductions: includeDeductions)
        )
    }
}

/// نافذة نتائج البحث: قائمة نتائج، والضغط على نتيجة يعرض تفاصيلها (الراتب والاستقطاعات) وزر النسخ كصورة.
struct SearchResultsSnippetIntent: SnippetIntent {
    static let title: LocalizedStringResource = "نتائج البحث"
    static let isDiscoverable = false

    @Parameter(title: "الاسم") var name: String
    @Parameter(title: "الأشهر") var range: ShortcutMonthRange
    @Parameter(title: "إظهار الاستقطاعات") var includeDeductions: Bool

    init() {}
    init(name: String, range: ShortcutMonthRange, includeDeductions: Bool) {
        self.name = name
        self.range = range
        self.includeDeductions = includeDeductions
    }

    @MainActor
    func perform() async throws -> some IntentResult & ShowsSnippetView {
        let results = try ShortcutSearch.run(name: name, range: range, includeDeductions: includeDeductions)
        let selectedID = results.count == 1 ? results[0].recordID : ShortcutSelection.current
        if let selectedID, results.contains(where: { $0.recordID == selectedID }),
           let record = try EmployeeLookup.employee(id: selectedID) {
            let data = PayslipSnippetData(employee: record, deductions: try EmployeeLookup.deductions(of: record))
            let entity = EmployeeEntity(record)
            return .result(view: AnyView(PayslipSnippetView(
                data: data, entity: entity, copied: PayslipClipboard.wasCopied(entity.id), showBack: results.count > 1
            )))
        }
        return .result(view: AnyView(ShortcutResultsView(results: Array(results.prefix(8)), total: results.count)))
    }
}

/// الضغط على نتيجة في النافذة (أو "كل النتائج" للرجوع).
struct SelectShortcutResultIntent: AppIntent {
    static let title: LocalizedStringResource = "اختيار نتيجة"
    static let isDiscoverable = false

    @Parameter(title: "النتيجة") var recordID: String

    init() {}
    init(recordID: String) { self.recordID = recordID }

    @MainActor
    func perform() async throws -> some IntentResult {
        if recordID.isEmpty { ShortcutSelection.clear() } else { ShortcutSelection.current = recordID }
        return .result()
    }
}

/// النتيجة المختارة في نافذة الاختصار.
enum ShortcutSelection {
    private static let key = "shortcutSelectedResult"
    static var current: String? {
        get { UserDefaults.standard.string(forKey: key) }
        set { UserDefaults.standard.set(newValue, forKey: key) }
    }
    static func clear() { UserDefaults.standard.removeObject(forKey: key) }
}

// MARK: - فتح التطبيق على البحث

struct OpenSearchIntent: AppIntent {
    static let title: LocalizedStringResource = "فتح البحث في الرواتب"
    static let description = IntentDescription("يفتح التطبيق ويبحث عن الاسم مباشرة.", categoryName: "البحث")
    static let openAppWhenRun = true

    @Parameter(title: "الاسم", requestValueDialog: "ما اسم الموظف؟")
    var name: String

    static var parameterSummary: some ParameterSummary {
        Summary("افتح البحث عن \(\.$name)")
    }

    @MainActor
    func perform() async throws -> some IntentResult {
        ShortcutRouter.shared.pendingSearch = name
        return .result()
    }
}

// MARK: - اختصارات جاهزة تظهر في تطبيق الاختصارات وSiri وSpotlight

struct SalaryShortcuts: AppShortcutsProvider {
    static var appShortcuts: [AppShortcut] {
        AppShortcut(
            intent: ShowPayslipIntent(),
            phrases: [
                "اعرض قسيمة راتب في \(.applicationName)",
                "قسيمة راتب من \(.applicationName)",
                "Show a payslip in \(.applicationName)",
            ],
            shortTitle: "قسيمة راتب",
            systemImageName: "doc.text.magnifyingglass"
        )
        AppShortcut(
            intent: SearchEmployeeIntent(),
            phrases: [
                "ابحث عن موظف في \(.applicationName)",
                "ابحث عن راتب في \(.applicationName)",
                "ابحث في \(.applicationName)",
                "Search \(.applicationName)",
                "Find a salary in \(.applicationName)",
            ],
            shortTitle: "البحث عن موظف",
            systemImageName: "magnifyingglass"
        )
        AppShortcut(
            intent: OpenSearchIntent(),
            phrases: [
                "افتح البحث في \(.applicationName)",
                "Open search in \(.applicationName)",
            ],
            shortTitle: "فتح البحث",
            systemImageName: "text.magnifyingglass"
        )
    }
}

// MARK: - البحث نفسه

struct ShortcutResult: Identifiable {
    let id = UUID()
    /// الشهر والاسم معاً، بنفس صيغة EmployeeEntity.id.
    var recordID: String { month + EmployeeEntity.separator + name }
    let name: String
    let month: String
    let rank: String
    let netSalary: Double?
    let deductions: [(title: String, amount: Double?)]?

    var deductionTotal: Double { deductions?.reduce(0) { $0 + ($1.amount ?? 0) } ?? 0 }

    /// نص كامل يُعاد كقيمة للاختصار (يمكن إرساله برسالة أو حفظه في ملاحظة).
    var summary: String {
        var lines = ["\(name) — \(month)"]
        if !rank.isEmpty { lines.append(rank) }
        lines.append(tr("صافي الراتب: \(Money.format(netSalary))", "Net salary: \(Money.format(netSalary))"))
        if let deductions {
            if deductions.isEmpty {
                lines.append(tr("لا توجد استقطاعات", "No deductions"))
            } else {
                for d in deductions { lines.append("• \(d.title): \(Money.format(d.amount))") }
                lines.append(tr("مجموع الاستقطاعات: \(Money.format(deductionTotal))", "Total deductions: \(Money.format(deductionTotal))"))
            }
        }
        return lines.joined(separator: "\n")
    }

    /// جملة قصيرة يقرؤها Siri.
    var spokenSummary: String {
        var text = tr("\(name)، \(month): صافي الراتب \(Money.format(netSalary))",
                      "\(name), \(month): net salary \(Money.format(netSalary))")
        if let deductions, !deductions.isEmpty {
            text += tr("، والاستقطاعات \(Money.format(deductionTotal))", ", deductions \(Money.format(deductionTotal))")
        }
        return text
    }
}

enum ShortcutSearch {
    @MainActor
    static func run(name: String, range: ShortcutMonthRange, includeDeductions: Bool) throws -> [ShortcutResult] {
        let context = SharedModel.container.mainContext
        let months = try EmployeeLookup.monthNames(limit: range.limit)
        let employees = try EmployeeLookup.find(name, in: months).prefix(30)

        return try employees.map { e in
            var lines: [(title: String, amount: Double?)]?
            if includeDeductions {
                let month = e.month
                let key = e.matchKey
                let found = try context.fetch(FetchDescriptor<Deduction>(
                    predicate: #Predicate { $0.month == month && $0.matchKey == key },
                    sortBy: [SortDescriptor(\.title)]
                ))
                lines = found.map { (title: $0.title, amount: $0.amount) }
            }
            return ShortcutResult(name: e.name, month: e.month, rank: e.rank, netSalary: e.netSalary, deductions: lines)
        }
    }
}

// MARK: - البطاقة التي تظهر في الاختصارات وSiri

struct ShortcutResultsView: View {
    let results: [ShortcutResult]
    let total: Int

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            if results.isEmpty {
                Label(tr("لا توجد نتائج", "No results"), systemImage: "magnifyingglass")
                    .foregroundStyle(.secondary)
            }
            ForEach(results) { r in
                Button(intent: SelectShortcutResultIntent(recordID: r.recordID)) {
                    HStack(spacing: 10) {
                        VStack(alignment: .leading, spacing: 3) {
                            Text(r.name).font(.headline).lineLimit(2).multilineTextAlignment(.leading)
                            Text([r.month, r.rank].filter { !$0.isEmpty }.joined(separator: " · "))
                                .font(.caption)
                                .foregroundStyle(.secondary)
                            if let deductions = r.deductions, !deductions.isEmpty {
                                Text(tr("الاستقطاعات: \(Money.format(r.deductionTotal))", "Deductions: \(Money.format(r.deductionTotal))"))
                                    .font(.caption)
                                    .foregroundStyle(.red)
                            }
                        }
                        Spacer(minLength: 8)
                        Text(Money.format(r.netSalary)).font(.headline.monospacedDigit())
                        Image(systemName: "chevron.forward")
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(.tertiary)
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                if r.id != results.last?.id { Divider() }
            }
            if total > results.count {
                Text(tr("و\(Num.format(total - results.count)) نتائج أخرى، اكتب الاسم كاملاً لتضييق البحث",
                        "and \(Num.format(total - results.count)) more — type the full name to narrow it down"))
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .padding()
        .environment(\.layoutDirection, AppSettings.isEnglish ? .leftToRight : .rightToLeft)
    }
}

// MARK: - قسيمة الراتب في نافذة منبثقة: اختيار الموظف ← التفاصيل ← نسخ كصورة

/// موظف في شهر معيّن، يظهر في قائمة البحث داخل الاختصار.
struct EmployeeEntity: AppEntity {
    static let typeDisplayRepresentation: TypeDisplayRepresentation = "موظف"
    static let defaultQuery = EmployeeQuery()

    /// الشهر والاسم معاً يحددان السجل.
    let id: String
    let name: String
    let month: String
    let netSalary: Double?

    var displayRepresentation: DisplayRepresentation {
        DisplayRepresentation(
            title: "\(name)",
            subtitle: "\(month) · \(Money.format(netSalary))",
            image: .init(systemName: "person.crop.circle.fill")
        )
    }

    static let separator = " | "

    init(_ employee: Employee) {
        id = employee.month + Self.separator + employee.name
        name = employee.name
        month = employee.month
        netSalary = employee.netSalary
    }
}

/// البحث بالاسم عند الكتابة في نافذة الاختيار (كل الأشهر، الأحدث أولاً).
struct EmployeeQuery: EntityStringQuery {
    func entities(for identifiers: [String]) async throws -> [EmployeeEntity] {
        try await EmployeeLookup.entities(ids: identifiers)
    }

    func entities(matching string: String) async throws -> [EmployeeEntity] {
        try await EmployeeLookup.search(string)
    }

    func suggestedEntities() async throws -> [EmployeeEntity] { [] }
}

enum EmployeeLookup {
    /// الأشهر المحفوظة من الأحدث إلى الأقدم، وبحد أقصى `limit` شهراً.
    @MainActor
    static func monthNames(limit: Int? = nil) throws -> [String] {
        var descriptor = FetchDescriptor<PayrollMonth>(sortBy: [
            SortDescriptor(\.sortKey, order: .reverse), SortDescriptor(\.importedAt, order: .reverse),
        ])
        if let limit { descriptor.fetchLimit = limit }
        return try SharedModel.container.mainContext.fetch(descriptor).map(\.name)
    }

    /// نفس بحث التطبيق: كل كلمة من الاسم يجب أن تكون موجودة (بعد توحيد الهمزات والتاء المربوطة).
    /// أول ثلاث كلمات تُصفّى داخل قاعدة البيانات حتى لا تضيع النتيجة بين آلاف الأسماء المتشابهة.
    @MainActor
    static func find(_ text: String, in months: [String]) throws -> [Employee] {
        let tokens = ArabicText.normalize(text).split(separator: " ").map(String.init)
        guard !tokens.isEmpty, !months.isEmpty else { return [] }
        let a = tokens[0]
        let predicate: Predicate<Employee>
        switch tokens.count {
        case 1:
            predicate = #Predicate { months.contains($0.month) && $0.nameKey.contains(a) }
        case 2:
            let b = tokens[1]
            predicate = #Predicate { months.contains($0.month) && $0.nameKey.contains(a) && $0.nameKey.contains(b) }
        default:
            let b = tokens[1]
            let c = tokens[2]
            predicate = #Predicate {
                months.contains($0.month) && $0.nameKey.contains(a) && $0.nameKey.contains(b) && $0.nameKey.contains(c)
            }
        }
        var descriptor = FetchDescriptor<Employee>(predicate: predicate, sortBy: [SortDescriptor(\.name)])
        descriptor.fetchLimit = 2000
        let order = Dictionary(months.enumerated().map { ($1, $0) }, uniquingKeysWith: { x, _ in x })
        return try SharedModel.container.mainContext.fetch(descriptor)
            .filter { e in tokens.allSatisfy { e.nameKey.contains($0) } }
            .sorted { (order[$0.month] ?? .max, $0.name) < (order[$1.month] ?? .max, $1.name) }
    }

    @MainActor
    static func search(_ text: String) throws -> [EmployeeEntity] {
        try find(text, in: monthNames()).prefix(60).map(EmployeeEntity.init)
    }

    @MainActor
    static func entities(ids: [String]) throws -> [EmployeeEntity] {
        try ids.compactMap { try employee(id: $0).map(EmployeeEntity.init) }
    }

    @MainActor
    static func employee(id: String) throws -> Employee? {
        let parts = id.components(separatedBy: EmployeeEntity.separator)
        guard parts.count == 2 else { return nil }
        let month = parts[0]
        let name = parts[1]
        var descriptor = FetchDescriptor<Employee>(predicate: #Predicate { $0.month == month && $0.name == name })
        descriptor.fetchLimit = 1
        return try SharedModel.container.mainContext.fetch(descriptor).first
    }

    @MainActor
    static func deductions(of employee: Employee) throws -> [Deduction] {
        let month = employee.month
        let key = employee.matchKey
        return try SharedModel.container.mainContext.fetch(FetchDescriptor<Deduction>(
            predicate: #Predicate { $0.month == month && $0.matchKey == key },
            sortBy: [SortDescriptor(\.title)]
        ))
    }
}

/// اختصار "قسيمة راتب": يفتح نافذة بحث فوق أي تطبيق، وبعد اختيار النتيجة تظهر
/// تفاصيل الراتب والاستقطاعات مع زر "نسخ كصورة". يعيد صورة القسيمة لاستخدامها في خطوات أخرى.
struct ShowPayslipIntent: AppIntent {
    static let title: LocalizedStringResource = "قسيمة راتب"
    static let description = IntentDescription(
        "ابحث بالاسم واختر النتيجة لعرض تفاصيل الراتب والاستقطاعات ونسخها كصورة، بدون فتح التطبيق.",
        categoryName: "البحث"
    )

    @Parameter(title: "الموظف", requestValueDialog: "اكتب الاسم واختر النتيجة")
    var employee: EmployeeEntity

    init() {}
    init(employee: EmployeeEntity) { self.employee = employee }

    static var parameterSummary: some ParameterSummary {
        Summary("اعرض قسيمة \(\.$employee)")
    }

    @MainActor
    func perform() async throws -> some IntentResult & ReturnsValue<IntentFile> & ShowsSnippetIntent {
        guard let record = try EmployeeLookup.employee(id: employee.id) else {
            throw PayslipExporter.ExportError.renderFailed
        }
        let files = try PayslipExporter.export(employee: record, deductions: try EmployeeLookup.deductions(of: record))
        let image = IntentFile(fileURL: files.imageURL, filename: files.imageURL.lastPathComponent, type: .png)
        return .result(value: image, snippetIntent: PayslipSnippetIntent(employee: employee))
    }
}

/// محتوى النافذة المنبثقة: يُعاد رسمه بعد الضغط على "نسخ كصورة" ليظهر "تم النسخ".
struct PayslipSnippetIntent: SnippetIntent {
    static let title: LocalizedStringResource = "قسيمة راتب"
    static let isDiscoverable = false

    @Parameter(title: "الموظف")
    var employee: EmployeeEntity

    init() {}
    init(employee: EmployeeEntity) { self.employee = employee }

    @MainActor
    func perform() async throws -> some IntentResult & ShowsSnippetView {
        guard let record = try EmployeeLookup.employee(id: employee.id) else {
            return .result(view: AnyView(Text(tr("لم يعد هذا السجل موجوداً.", "This record no longer exists.")).padding()))
        }
        let data = PayslipSnippetData(employee: record, deductions: try EmployeeLookup.deductions(of: record))
        let copied = PayslipClipboard.wasCopied(employee.id)
        return .result(view: AnyView(PayslipSnippetView(data: data, entity: employee, copied: copied)))
    }
}

/// زر "نسخ كصورة" داخل النافذة: يرسم القسيمة ويضعها في الحافظة.
struct CopyPayslipImageIntent: AppIntent {
    static let title: LocalizedStringResource = "نسخ قسيمة الراتب كصورة"
    static let isDiscoverable = false

    /// معرّف السجل كنص عادي (الشهر | الاسم)، حتى لا يحتاج النظام إلى البحث عنه من جديد.
    @Parameter(title: "السجل")
    var recordID: String

    init() {}
    init(recordID: String) { self.recordID = recordID }

    @MainActor
    func perform() async throws -> some IntentResult {
        guard let record = try EmployeeLookup.employee(id: recordID) else { throw CopyError.notFound }
        let files = try PayslipExporter.export(employee: record, deductions: try EmployeeLookup.deductions(of: record))
        guard let png = files.image.pngData() else { throw CopyError.renderFailed }
        // الصورة فقط في الحافظة (بدون نص)، فتُلصق كصورة في واتساب والملاحظات.
        UIPasteboard.general.setItems([[UTType.png.identifier: png]])
        PayslipClipboard.markCopied(recordID)
        return .result()
    }

    enum CopyError: LocalizedError {
        case notFound, renderFailed
        var errorDescription: String? {
            switch self {
            case .notFound: tr("لم يُعثر على السجل.", "Record not found.")
            case .renderFailed: tr("تعذّر إنشاء الصورة.", "Couldn't create the image.")
            }
        }
    }
}

/// يتذكر آخر قسيمة نُسخت (لمدة دقيقة) حتى يظهر "تم النسخ" على الزر.
enum PayslipClipboard {
    private static let idKey = "lastCopiedPayslip"
    private static let dateKey = "lastCopiedPayslipDate"

    static func markCopied(_ id: String) {
        UserDefaults.standard.set(id, forKey: idKey)
        UserDefaults.standard.set(Date(), forKey: dateKey)
    }

    static func wasCopied(_ id: String) -> Bool {
        guard UserDefaults.standard.string(forKey: idKey) == id,
              let date = UserDefaults.standard.object(forKey: dateKey) as? Date else { return false }
        return Date().timeIntervalSince(date) < 60
    }
}

/// قيم بسيطة للنافذة (بدون كائنات قاعدة البيانات).
struct PayslipSnippetData {
    struct Line: Identifiable {
        let id = UUID()
        let title: String
        let note: String
        let amount: Double?
    }

    let name: String
    let month: String
    let details: String
    let netSalary: Double?
    let deductions: [Line]
    var total: Double { deductions.reduce(0) { $0 + ($1.amount ?? 0) } }

    init(employee: Employee, deductions: [Deduction]) {
        name = employee.name
        month = employee.month
        details = [employee.rank, employee.workplace].filter { !$0.isEmpty }.joined(separator: " · ")
        netSalary = employee.netSalary
        self.deductions = deductions.map { Line(title: $0.title.orDash, note: $0.note, amount: $0.amount) }
    }
}

struct PayslipSnippetView: View {
    let data: PayslipSnippetData
    let entity: EmployeeEntity
    let copied: Bool
    /// زر "كل النتائج" عند الوصول من قائمة نتائج البحث.
    var showBack = false

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            VStack(alignment: .leading, spacing: 8) {
                Label(data.month, systemImage: "calendar")
                    .font(.caption.weight(.semibold))
                    .opacity(0.9)
                Text(data.name)
                    .font(.headline)
                if !data.details.isEmpty {
                    Text(data.details).font(.caption).opacity(0.85)
                }
                Text(tr("صافي الراتب", "Net salary"))
                    .font(.caption2.weight(.medium))
                    .opacity(0.8)
                Text(Money.format(data.netSalary))
                    .font(.system(size: 32, weight: .bold, design: .rounded))
                    .monospacedDigit()
            }
            .foregroundStyle(.white)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(16)
            .background(Theme.salaryGradient, in: RoundedRectangle(cornerRadius: 22, style: .continuous))

            VStack(alignment: .leading, spacing: 8) {
                Text(tr("الاستقطاعات", "Deductions")).font(.subheadline.weight(.semibold))
                if data.deductions.isEmpty {
                    Label(tr("لا توجد استقطاعات", "No deductions"), systemImage: "checkmark.circle")
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                } else {
                    ForEach(data.deductions) { line in
                        HStack(alignment: .firstTextBaseline) {
                            VStack(alignment: .leading, spacing: 1) {
                                Text(line.title)
                                if !line.note.isEmpty {
                                    Text(line.note).font(.caption).foregroundStyle(.secondary)
                                }
                            }
                            Spacer(minLength: 8)
                            Text(Money.format(line.amount)).monospacedDigit().foregroundStyle(.red)
                        }
                        .font(.subheadline)
                    }
                    Divider()
                    HStack {
                        Text(tr("المجموع", "Total")).fontWeight(.semibold)
                        Spacer()
                        Text(Money.format(data.total)).fontWeight(.bold).monospacedDigit().foregroundStyle(.red)
                    }
                    .font(.subheadline)
                }
            }

            Button(intent: CopyPayslipImageIntent(recordID: entity.id)) {
                Label(copied ? tr("تم نسخ الصورة", "Image copied") : tr("نسخ كصورة", "Copy as image"),
                      systemImage: copied ? "checkmark" : "doc.on.doc")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent)
            .tint(copied ? .green : .blue)

            if showBack {
                Button(intent: SelectShortcutResultIntent(recordID: "")) {
                    Label(tr("كل النتائج", "All results"), systemImage: "list.bullet")
                        .frame(maxWidth: .infinity)
                }
                .buttonStyle(.bordered)
            }
        }
        .padding()
        .environment(\.layoutDirection, AppSettings.isEnglish ? .leftToRight : .rightToLeft)
    }
}
