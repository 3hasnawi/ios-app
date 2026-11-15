import Foundation
import SwiftData

/// نتائج البحث العام: الأشخاص والجهات والحسابات والأشهر، من دون تحميل جداول البيانات في الواجهة.
struct SmartSearchResults {
    var people: [UnifiedPerson] = []
    var groups: [ReportGroup] = []
    var accounts: [SavedExecutionAccount] = []
    var months: [String] = []
    var books: [OfficialBook] = []
    var trustError: String? = nil

    var isEmpty: Bool { people.isEmpty && groups.isEmpty && accounts.isEmpty && months.isEmpty && books.isEmpty && trustError == nil }
}

/// يُحسب فهرس الجهات مرة لكل نسخة بيانات، بدلاً من جمع كل الاستقطاعات عند كل حرف.
actor SmartGroupCatalog {
    static let shared = SmartGroupCatalog()
    private var signature = ""
    private var cached: [ReportGroup] = []

    func invalidate() {
        signature = ""
        cached = []
    }

    func groups(months: [String], signature next: String, container: ModelContainer) async throws -> [ReportGroup] {
        if signature == next { return cached }
        let work = Task.detached(priority: .utility) {
            let engine = ReportEngine(modelContainer: container)
            var result: [ReportGroup] = []
            for month in months {
                try Task.checkCancellation()
                result += try await engine.deductionGroups(month: month, byEntity: false)
            }
            return result
        }
        let result = try await withTaskCancellationHandler {
            try await work.value
        } onCancel: {
            work.cancel()
        }
        try Task.checkCancellation()
        signature = next
        cached = result
        return result
    }
}

nonisolated struct SmartFieldHits: Sendable {
    var people: [UnifiedHit] = []
    var noteGroups: Set<String> = []
}

@ModelActor
actor SmartFieldSearch {
    func find(_ query: String, months: [String]) throws -> SmartFieldHits {
        let tokens = ArabicText.normalize(ArabicText.cleanQuery(query)).split(separator: " ").map(String.init)
        guard let first = tokens.first, !first.isEmpty else { return SmartFieldHits() }
        var result = SmartFieldHits()
        let numeric = ArabicText.latinDigits(query).contains(where: \.isNumber)
        // حقول العمل والرتبة والحالة: حد صغير يحافظ على سرعة الواجهة.
        if !numeric {
            var staffQuery = FetchDescriptor<StaffMember>(predicate: #Predicate {
                $0.workplace.contains(first) || $0.rank.contains(first) || $0.status.contains(first)
            })
            staffQuery.fetchLimit = 60
            staffQuery.propertiesToFetch = [\.name, \.nameKey, \.rank, \.workplace, \.status]
            for member in try modelContext.fetch(staffQuery) {
                try Task.checkCancellation()
                let haystack = ArabicText.normalize([member.workplace, member.rank, member.status].joined(separator: " "))
                guard tokens.allSatisfy({ haystack.contains($0) }) else { continue }
                result.people.append(UnifiedHit(key: ArabicText.matchKey(member.name), name: member.name,
                    subtitle: [member.rank, member.workplace].filter { !$0.isEmpty }.joined(separator: " · "),
                    employeeID: nil, staffID: member.persistentModelID, month: nil, netSalary: nil, staffStatus: member.status))
            }
            var payrollQuery = FetchDescriptor<Employee>(predicate: #Predicate {
                $0.workplace.contains(first) || $0.rank.contains(first)
            })
            payrollQuery.fetchLimit = 80
            payrollQuery.propertiesToFetch = [\.name, \.matchKey, \.month, \.rank, \.workplace, \.netSalary]
            let order = Dictionary(months.enumerated().map { ($1, $0) }, uniquingKeysWith: { a, _ in a })
            for employee in try modelContext.fetch(payrollQuery).sorted(by: { (order[$0.month] ?? .max) < (order[$1.month] ?? .max) }) {
                try Task.checkCancellation()
                let haystack = ArabicText.normalize([employee.workplace, employee.rank].joined(separator: " "))
                guard tokens.allSatisfy({ haystack.contains($0) }) else { continue }
                result.people.append(UnifiedHit(key: employee.matchKey, name: employee.name,
                    subtitle: [employee.rank, employee.workplace].filter { !$0.isEmpty }.joined(separator: " · "),
                    employeeID: employee.persistentModelID, staffID: nil, month: employee.month,
                    netSalary: employee.netSalary, staffStatus: nil))
            }
        }
        // الإضبارة والملاحظة والمبلغ تعيد الجهة، حتى مع اختلاف شكل الأرقام أو تأخر المطابقة في الدفعات.
        let digitQuery = ArabicText.latinDigits(first)
        let easternQuery = String(digitQuery.map { character -> Character in
            if let value = character.wholeNumberValue, (0...9).contains(value) { return Character(String(UnicodeScalar(0x0660 + value)!)) }
            return character
        })
        let wantedAmount = ArabicText.number(from: query) ?? -Double.greatestFiniteMagnitude
        let matcher = AutomaticSearchQuery(query)
        var notes = FetchDescriptor<Deduction>(predicate: #Predicate {
            $0.note.contains(first) || $0.note.contains(digitQuery) || $0.note.contains(easternQuery) ||
            $0.title.contains(first) || $0.employeeName.contains(first) || $0.amount == wantedAmount
        })
        notes.fetchLimit = 128
        notes.propertiesToFetch = [\.month, \.title, \.note, \.employeeName, \.rank, \.amount]
        while result.noteGroups.count < 50 {
            try Task.checkCancellation()
            let count = try autoreleasepool {
                let page = try modelContext.fetch(notes)
                for item in page {
                    try Task.checkCancellation()
                    let fields = [AutomaticSearchQuery.Field(label: "entity", value: item.title),
                        .init(label: "name", value: item.employeeName), .init(label: "note", value: item.note),
                        .init(label: "rank", value: item.rank), .init(label: "amount", value: item.amount.map(String.init(describing:)) ?? "")]
                    if matcher.match(fields) != nil { result.noteGroups.insert("\(item.month)|\(item.title)") }
                    if result.noteGroups.count >= 50 { break }
                }
                return page.count
            }
            if count < 128 { break }
            notes.fetchOffset = (notes.fetchOffset ?? 0) + count
        }
        return result
    }
}

/// يكمل البحث في كل الحقول حتى مع وجود نتائج سريعة؛ الدفعات قصيرة وتُلغى عند استمرار الكتابة.
@ModelActor actor SmartDetailSearch {
    func find(_ input: String, months: [String]) throws -> [UnifiedHit] {
        let query = AutomaticSearchQuery(input)
        guard !query.isEmpty else { return [] }
        var hits: [UnifiedHit] = []
        var staffQuery = FetchDescriptor<StaffMember>(sortBy: [SortDescriptor(\.name)])
        staffQuery.fetchLimit = 128
        staffQuery.propertiesToFetch = [\.name, \.motherKey, \.statNumber, \.unifiedNumber, \.rank, \.workplace, \.status, \.detailsData]
        while hits.count < 60 {
            try Task.checkCancellation()
            let count = try autoreleasepool {
                let page = try modelContext.fetch(staffQuery)
                for member in page {
                    try Task.checkCancellation()
                    var fields = [AutomaticSearchQuery.Field(label: tr("الاسم", "Name"), value: member.name),
                        .init(label: tr("اسم الأم", "Mother's name"), value: member.motherKey),
                        .init(label: tr("الرقم الإحصائي", "Statistical number"), value: member.statNumber),
                        .init(label: tr("الرقم الموحد", "Unified number"), value: member.unifiedNumber),
                        .init(label: tr("محل العمل", "Workplace"), value: member.workplace),
                        .init(label: tr("الرتبة", "Rank"), value: member.rank),
                        .init(label: tr("الحالة", "Status"), value: member.status)]
                    fields += member.sections.flatMap(\.fields).map { .init(label: $0.label, value: $0.value) }
                    guard let label = query.match(fields) else { continue }
                    var hit = UnifiedHit(key: ArabicText.matchKey(member.name), name: member.name,
                        subtitle: [member.rank, member.workplace].filter { !$0.isEmpty }.joined(separator: " · "),
                        employeeID: nil, staffID: member.persistentModelID, month: nil, netSalary: nil, staffStatus: member.status)
                    hit.matchLabel = tr("مطابقة في \(label)", "Match in \(label)")
                    hits.append(hit)
                    if hits.count >= 60 { break }
                }
                return page.count
            }
            if count < 128 { break }
            staffQuery.fetchOffset = (staffQuery.fetchOffset ?? 0) + count
        }
        let staffCount = hits.count
        var payrollQuery = FetchDescriptor<Employee>(predicate: #Predicate { months.contains($0.month) }, sortBy: [SortDescriptor(\.name)])
        payrollQuery.fetchLimit = 64
        payrollQuery.propertiesToFetch = [\.name, \.matchKey, \.rank, \.workplace, \.month,
                                          \.netSalary, \.detailsData, \.itemsData, \.rawColumnsData]
        while hits.count - staffCount < 60 {
            try Task.checkCancellation()
            let count = try autoreleasepool {
                let page = try modelContext.fetch(payrollQuery)
                for employee in page {
                    try Task.checkCancellation()
                    var fields = [AutomaticSearchQuery.Field(label: tr("الاسم", "Name"), value: employee.name),
                        .init(label: tr("الرتبة", "Rank"), value: employee.rank),
                        .init(label: tr("محل العمل", "Workplace"), value: employee.workplace),
                        .init(label: tr("الشهر", "Month"), value: employee.month),
                        .init(label: tr("صافي الراتب", "Net salary"), value: employee.netSalary.map(String.init(describing:)) ?? "")]
                    fields += employee.details.sorted { $0.key < $1.key }.map { .init(label: $0.key, value: $0.value) }
                    fields += employee.items.filter { $0.value != 0 }.sorted { $0.key < $1.key }.map { .init(label: $0.key, value: "\($0.key) \($0.value)") }
                    if let raw = employee.rawColumns {
                        fields += zip(raw.headers, raw.values).map { .init(label: $0.0, value: $0.1) }
                    }
                    guard let label = query.match(fields) else { continue }
                    var hit = UnifiedHit(key: employee.matchKey, name: employee.name,
                        subtitle: [employee.rank, employee.workplace].filter { !$0.isEmpty }.joined(separator: " · "),
                        employeeID: employee.persistentModelID, staffID: nil, month: employee.month,
                        netSalary: employee.netSalary, staffStatus: nil)
                    hit.matchLabel = tr("مطابقة في \(label)", "Match in \(label)")
                    hits.append(hit)
                    if hits.count - staffCount >= 60 { break }
                }
                return page.count
            }
            if count < 64 { break }
            payrollQuery.fetchOffset = (payrollQuery.fetchOffset ?? 0) + count
        }
        return hits
    }
}

enum SmartSearch {
    @MainActor
    static func find(_ query: String, months: [PayrollMonth], accountData: Data, trustData: Data? = nil,
                     publish: ((SmartSearchResults) -> Void)? = nil) async throws -> SmartSearchResults {
        let text = AutomaticSearchQuery(query)
        guard !text.isEmpty else { return SmartSearchResults() }
        let normalized = AutomaticSearchQuery.normalize(query)
        let monthNames = months.map(\.name)
        let container = SharedModel.container
        let trustSnapshot = trustData ?? UserDefaults.standard.data(forKey: SalaryTrust.settingsKey) ?? Data()
        var trustError: String?
        var hits: [UnifiedHit] = []
        var modes: [StaffSearchMode] = [.name, .mother]
        if text.digits != nil { modes += [.statNumber, .unifiedNumber] }
        for mode in modes {
            var found = try await SearchIndex.shared.hits(query, mode: mode, scope: .all, months: monthNames, container: container)
            if mode != .name {
                for index in found.indices { found[index].matchLabel = tr("مطابقة في \(mode.title)", "Match in \(mode.title)") }
            }
            hits += found
            try Task.checkCancellation()
        }
        do {
            let trustHits = try await SalaryTrustSearch.shared.find(query, data: trustSnapshot)
            hits += trustHits.map { match in
                var hit = UnifiedHit(key: match.summary.key, name: match.summary.name, subtitle: tr("أمانات رواتب", "Salary trust"),
                    employeeID: nil, staffID: nil, month: nil, netSalary: nil, staffStatus: nil)
                hit.trust = match.summary
                if match.field != tr("الاسم", "Name") { hit.matchLabel = tr("مطابقة في الأمانات: \(match.field)", "Trust match: \(match.field)") }
                return hit
            }
        } catch is CancellationError { throw CancellationError() }
        catch { trustError = tr("تعذّرت قراءة دفتر الأمانات؛ بقية نتائج البحث متاحة.", "The trust ledger could not be read; other search results remain available.") }

        func resolve(_ input: [UnifiedHit]) async throws -> [UnifiedPerson] {
            let merged = Array(UnifiedHitMerge.merge(input, months: monthNames).prefix(150))
            let work = Task.detached(priority: .userInitiated) {
                try await SmartPersonResolver(modelContainer: container).enrich(merged, months: monthNames)
            }
            var resolved = try await withTaskCancellationHandler { try await work.value } onCancel: { work.cancel() }
            if trustError == nil {
                do {
                    let summaries = try await SalaryTrustSearch.shared.summaries(keys: resolved.map(\.key), data: trustSnapshot)
                    for index in resolved.indices { resolved[index].trust = summaries[resolved[index].key] }
                } catch is CancellationError { throw CancellationError() }
                catch { trustError = tr("تعذّرت قراءة دفتر الأمانات؛ بقية نتائج البحث متاحة.", "The trust ledger could not be read; other search results remain available.") }
            }
            return Array(resolved.prefix(100)).map(UnifiedPerson.init)
        }

        var people = try await resolve(hits)
        let accounts = Array(try ExecutionAccountStore.decode(accountData).filter { account in
            text.matches([account.entity, account.branch, account.swift, account.iban, account.localAccount ?? ""].joined(separator: " "))
        }.prefix(30))
        let matchedMonths = Array(monthNames.filter(text.matches).prefix(12))
        let books = Array(OfficialBookStore.shared.books.filter { book in
            text.matches([book.title, book.month, book.entity ?? ""].joined(separator: " "))
        }.prefix(30))
        var groups: [ReportGroup] = []
        func result() -> SmartSearchResults {
            SmartSearchResults(people: people, groups: groups, accounts: accounts, months: matchedMonths, books: books, trustError: trustError)
        }
        try Task.checkCancellation()
        publish?(result())
        if normalized.count >= 2 {
            let task = Task.detached(priority: .userInitiated) {
                try await SmartFieldSearch(modelContainer: container).find(query, months: monthNames)
            }
            let extras = try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
            hits += extras.people
            people = try await resolve(hits)
            let signature = months.map { "\($0.name)@\($0.importedAt.timeIntervalSince1970)" }.joined(separator: "|")
            let catalog = try await SmartGroupCatalog.shared.groups(months: monthNames, signature: signature, container: container)
            groups = catalog.filter { group in text.matches(group.title) || extras.noteGroups.contains("\(group.month)|\(group.title)") }
            for month in months {
                for (title, summary) in PayrollItems.decodeSummary(month.itemSummaryData) ?? [:] where text.matches(title) {
                    groups.append(ReportGroup(kind: .payroll, month: month.name, title: title, count: summary.count, total: summary.total, byEntity: false))
                }
            }
            groups = Array(groups.prefix(50))
            try Task.checkCancellation()
            publish?(result())
        }
        if normalized.count >= 3 {
            // لا نحجب النتائج السريعة أثناء فحص الحقول الإضافية؛ تتوقف الدفعات مع تغير الاستعلام.
            try await Task.sleep(for: .milliseconds(350))
            let work = Task.detached(priority: .utility) {
                try await SmartDetailSearch(modelContainer: container).find(query, months: monthNames)
            }
            hits += try await withTaskCancellationHandler { try await work.value } onCancel: { work.cancel() }
            people = try await resolve(hits)
        }
        try Task.checkCancellation()
        return result()
    }
}
