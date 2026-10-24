import AppIntents
import SwiftData
import SwiftUI

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

    @Parameter(title: "الأشهر", default: .latest)
    var range: ShortcutMonthRange

    @Parameter(title: "إظهار الاستقطاعات", default: true)
    var includeDeductions: Bool

    static var parameterSummary: some ParameterSummary {
        Summary("ابحث عن \(\.$name) في \(\.$range)") {
            \.$includeDeductions
        }
    }

    @MainActor
    func perform() async throws -> some IntentResult & ReturnsValue<String> & ProvidesDialog & ShowsSnippetView {
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

        return .result(value: text, dialog: dialog) {
            ShortcutResultsView(results: Array(results.prefix(5)), total: results.count)
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
        return .result()
    }
}

// MARK: - اختصارات جاهزة تظهر في تطبيق الاختصارات وSiri وSpotlight

struct SalaryShortcuts: AppShortcutsProvider {
    static var appShortcuts: [AppShortcut] {
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
        let tokens = ArabicText.normalize(name).split(separator: " ").map(String.init)
        guard let first = tokens.first else { return [] }

        var monthDescriptor = FetchDescriptor<PayrollMonth>(sortBy: [
            SortDescriptor(\.sortKey, order: .reverse), SortDescriptor(\.importedAt, order: .reverse),
        ])
        if let limit = range.limit { monthDescriptor.fetchLimit = limit }
        let months = try context.fetch(monthDescriptor).map(\.name)
        guard !months.isEmpty else { return [] }

        var descriptor = FetchDescriptor<Employee>(
            predicate: #Predicate { months.contains($0.month) && $0.nameKey.contains(first) },
            sortBy: [SortDescriptor(\.name)]
        )
        descriptor.fetchLimit = 200
        let order = Dictionary(months.enumerated().map { ($1, $0) }, uniquingKeysWith: { a, _ in a })
        let employees = try context.fetch(descriptor)
            .filter { e in tokens.allSatisfy { e.nameKey.contains($0) } }
            .sorted { (order[$0.month] ?? 0, $0.name) < (order[$1.month] ?? 0, $1.name) }
            .prefix(20)

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
        VStack(alignment: .leading, spacing: 12) {
            ForEach(results) { r in
                VStack(alignment: .leading, spacing: 4) {
                    HStack(alignment: .firstTextBaseline) {
                        Text(r.name).font(.headline).lineLimit(2)
                        Spacer(minLength: 8)
                        Text(Money.format(r.netSalary)).font(.headline.monospacedDigit())
                    }
                    Text([r.month, r.rank].filter { !$0.isEmpty }.joined(separator: " · "))
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    if let deductions = r.deductions, !deductions.isEmpty {
                        HStack {
                            Text(tr("الاستقطاعات", "Deductions"))
                            Spacer()
                            Text(Money.format(r.deductionTotal)).monospacedDigit()
                        }
                        .font(.subheadline)
                        .foregroundStyle(.red)
                    }
                }
                if r.id != results.last?.id { Divider() }
            }
            if total > results.count {
                Text(tr("و\(Num.format(total - results.count)) نتائج أخرى", "and \(Num.format(total - results.count)) more"))
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .padding()
        .environment(\.layoutDirection, AppSettings.isEnglish ? .leftToRight : .rightToLeft)
    }
}
