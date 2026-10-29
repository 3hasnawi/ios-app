import AppIntents
import SwiftData
import SwiftUI
import UniformTypeIdentifiers

// الاختصارات تعمل في الخلفية، والنظام يمنع التطبيقات في الخلفية من الكتابة في الحافظة.
// لذلك لا ينسخ التطبيق الصورة بنفسه هنا: الاختصار يُعيد صورة القسيمة كنتيجة،
// وخطوة "نسخ إلى الحافظة" في تطبيق الاختصارات هي التي تنسخها (وهي تعمل دائماً).

/// قاعدة بيانات واحدة يشترك فيها التطبيق واختصارات تطبيق "الاختصارات" (Shortcuts) وSiri.
enum SharedModel {
    @MainActor static let container: ModelContainer = {
        let started = Date()
        defer { PerfLog.record("container", since: started) }
        do {
            return try ModelContainer(for: PayrollMonth.self, Employee.self, Deduction.self, StaffMember.self, ExecutionAccount.self)
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

// MARK: - البحث عن موظف: الاسم ← اختيار النتيجة ← التفاصيل وصورة القسيمة

struct SearchEmployeeIntent: AppIntent {
    /// بيانات الرواتب لا تظهر من شاشة القفل: الاختصار يطلب فتح الآيفون أولاً.
    static let authenticationPolicy: IntentAuthenticationPolicy = .requiresLocalDeviceAuthentication
    static let title: LocalizedStringResource = "البحث عن موظف"
    static let description = IntentDescription(
        "يبحث بالاسم، وإذا وُجد أكثر من موظف تختار من القائمة. يعرض صافي الراتب والاستقطاعات ويعيد صورة القسيمة. أضف بعده \"نسخ إلى الحافظة\" لنسخ الصورة.",
        categoryName: "البحث"
    )

    @Parameter(title: "الاسم", requestValueDialog: "ما اسم الموظف؟")
    var name: String

    @Parameter(title: "الأشهر", default: .latest)
    var range: ShortcutMonthRange

    /// يُملأ عند اختيار نتيجة من القائمة.
    @Parameter(title: "الموظف")
    var employee: EmployeeEntity?

    static var parameterSummary: some ParameterSummary {
        Summary("ابحث عن \(\.$name) في \(\.$range)")
    }

    @MainActor
    func perform() async throws -> some IntentResult & ReturnsValue<IntentFile> & ShowsSnippetIntent {
        let matches = try EmployeeLookup.find(name, in: EmployeeLookup.monthNames(limit: range.limit))
            .prefix(50)
            .map(EmployeeEntity.init)
        guard let first = matches.first else { throw ShortcutError.noMatch(name) }

        var chosen = first
        if matches.count > 1 {
            // نافذة اختيار فوق التطبيقات: كل نتيجة بشهرها وصافي راتبها.
            let prompt = tr("وجدت \(Num.format(matches.count)) نتائج، اختر الموظف",
                            "Found \(Num.format(matches.count)) results. Pick one")
            chosen = try await $employee.requestDisambiguation(
                among: Array(matches),
                dialog: IntentDialog("\(prompt)")
            )
        }
        return try PayslipResult.make(for: chosen)
    }
}

// MARK: - قسيمة راتب: نافذة بحث واختيار ← التفاصيل وصورة القسيمة

struct ShowPayslipIntent: AppIntent {
    /// بيانات الرواتب لا تظهر من شاشة القفل: الاختصار يطلب فتح الآيفون أولاً.
    static let authenticationPolicy: IntentAuthenticationPolicy = .requiresLocalDeviceAuthentication
    static let title: LocalizedStringResource = "قسيمة راتب"
    static let description = IntentDescription(
        "اكتب الاسم واختر النتيجة لعرض تفاصيل الراتب والاستقطاعات. يعيد صورة القسيمة؛ أضف بعده \"نسخ إلى الحافظة\" لنسخها.",
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
        try PayslipResult.make(for: employee)
    }
}

/// يرسم القسيمة (نفس صورة "نسخ كصورة" داخل التطبيق) ويعيدها مع النافذة.
enum PayslipResult {
    @MainActor
    static func make(for entity: EmployeeEntity) throws -> some IntentResult & ReturnsValue<IntentFile> & ShowsSnippetIntent {
        guard let record = try EmployeeLookup.employee(id: entity.id) else { throw ShortcutError.recordMissing }
        let files = try PayslipExporter.export(employee: record, deductions: try EmployeeLookup.deductions(of: record))
        let image = IntentFile(fileURL: files.imageURL, filename: files.imageURL.lastPathComponent, type: .png)
        return .result(value: image, snippetIntent: PayslipSnippetIntent(employee: entity))
    }
}

/// محتوى النافذة المنبثقة: بطاقة الراتب والاستقطاعات.
struct PayslipSnippetIntent: SnippetIntent {
    static let title: LocalizedStringResource = "قسيمة راتب"
    static let isDiscoverable = false

    @Parameter(title: "الموظف")
    var employee: EmployeeEntity

    init() {}
    init(employee: EmployeeEntity) { self.employee = employee }

    @MainActor
    func perform() async throws -> some IntentResult & ShowsSnippetView {
        guard let record = try EmployeeLookup.employee(id: employee.id) else { throw ShortcutError.recordMissing }
        let data = PayslipSnippetData(employee: record, deductions: try EmployeeLookup.deductions(of: record))
        return .result(view: PayslipSnippetView(data: data))
    }
}

enum ShortcutError: LocalizedError {
    case noMatch(String)
    case recordMissing

    var errorDescription: String? {
        switch self {
        case .noMatch(let name): tr("لم يُعثر على \"\(name)\" في الأشهر المختارة.", "No match for \"\(name)\" in the selected months.")
        case .recordMissing: tr("لم يعد هذا السجل موجوداً.", "This record no longer exists.")
        }
    }
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
        AppRouter.shared.tab = .search
        return .result()
    }
}

// MARK: - اختصارات جاهزة تظهر في تطبيق الاختصارات وSiri وSpotlight

struct SalaryShortcuts: AppShortcutsProvider {
    static var appShortcuts: [AppShortcut] {
        AppShortcut(
            intent: SmartLookupIntent(),
            phrases: ["بحث ذكي في \(.applicationName)", "Smart search in \(.applicationName)"],
            shortTitle: "البحث الذكي",
            systemImageName: "magnifyingglass"
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

// MARK: - الموظف كعنصر قابل للاختيار في نوافذ الاختصارات

/// موظف في شهر معيّن.
struct EmployeeEntity: AppEntity {
    static let typeDisplayRepresentation: TypeDisplayRepresentation = "موظف"
    static let defaultQuery = EmployeeQuery()

    /// "الشهر | الاسم" يحدد السجل (فاصل عادي، لأن الأحرف الخاصة قد تضيع عند حفظ الاختصار).
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

/// البحث بالاسم عند الكتابة في نافذة الاختيار (آخر شهر).
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
        try find(text, in: months, context: SharedModel.container.mainContext)
    }

    /// نفس البحث على أي سياق (يُستدعى من الخلفية في البحث الموحّد).
    nonisolated static func find(_ text: String, in months: [String], context: ModelContext) throws -> [Employee] {
        let tokens = ArabicText.normalize(ArabicText.cleanQuery(text)).split(separator: " ").map(String.init)
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
        descriptor.propertiesToFetch = [\.month, \.name, \.nameKey, \.matchKey, \.rank, \.workplace, \.netSalary]
        let order = Dictionary(months.enumerated().map { ($1, $0) }, uniquingKeysWith: { x, _ in x })
        return try context.fetch(descriptor)
            .filter { e in tokens.allSatisfy { e.nameKey.contains($0) } }
            .sorted { (order[$0.month] ?? .max, $0.name) < (order[$1.month] ?? .max, $1.name) }
    }

    @MainActor
    static func search(_ text: String) throws -> [EmployeeEntity] {
        try find(text, in: monthNames(limit: 1)).prefix(60).map(EmployeeEntity.init)
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
        )).filter { DeductionStatus.read($0.note, amount: $0.amount) == nil }
    }
}

// MARK: - بطاقة النافذة المنبثقة

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
        }
        .padding()
        .environment(\.layoutDirection, AppSettings.isEnglish ? .leftToRight : .rightToLeft)
    }
}
