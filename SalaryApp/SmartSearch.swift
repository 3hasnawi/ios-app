import Foundation
import SwiftData

/// نتائج البحث العام: الأشخاص والجهات والحسابات والأشهر، من دون تحميل جداول البيانات في الواجهة.
struct SmartSearchResults {
    var people: [UnifiedPerson] = []
    var groups: [ReportGroup] = []
    var accounts: [SavedExecutionAccount] = []
    var months: [String] = []

    var isEmpty: Bool { people.isEmpty && groups.isEmpty && accounts.isEmpty && months.isEmpty }
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
        // رقم الإضبارة أو ملاحظة الاستقطاع يقودان إلى جهة التنفيذ المعنية.
        let digitQuery = ArabicText.latinDigits(first)
        var notes = FetchDescriptor<Deduction>(predicate: #Predicate {
            $0.note.contains(first) || $0.note.contains(digitQuery)
        })
        notes.fetchLimit = 100
        notes.propertiesToFetch = [\.month, \.title, \.note]
        for item in try modelContext.fetch(notes) {
            try Task.checkCancellation()
            let normalized = ArabicText.latinDigits(ArabicText.normalize(item.note))
            if tokens.allSatisfy({ normalized.contains(ArabicText.latinDigits($0)) }) {
                result.noteGroups.insert("\(item.month)|\(item.title)")
            }
        }
        return result
    }
}

enum SmartSearch {
    @MainActor
    static func find(_ query: String, months: [PayrollMonth], accountData: Data) async throws -> SmartSearchResults {
        let normalized = ArabicText.normalize(ArabicText.cleanQuery(query))
        let tokens = normalized.split(separator: " ").map(String.init)
        guard !tokens.isEmpty else { return SmartSearchResults() }
        let monthNames = months.map(\.name)
        let container = SharedModel.container
        let numeric = ArabicText.latinDigits(query).contains(where: \.isNumber)
        let modes: [StaffSearchMode] = numeric ? [.statNumber, .unifiedNumber] : [.name, .mother]
        var hits: [UnifiedHit] = []
        for mode in modes {
            hits += try await SearchIndex.shared.hits(query, mode: mode, scope: .all, months: monthNames, container: container)
            try Task.checkCancellation()
        }
        let extras: SmartFieldHits
        if normalized.count >= 2 {
            let task = Task.detached(priority: .userInitiated) {
                try await SmartFieldSearch(modelContainer: container).find(query, months: monthNames)
            }
            extras = try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
            hits += extras.people
        } else {
            extras = SmartFieldHits()
        }
        var seen = Set<String>()
        let people: [UnifiedPerson] = hits.filter { seen.insert($0.key).inserted }.prefix(100).map(UnifiedPerson.init)
        let accounts: [SavedExecutionAccount] = Array(try ExecutionAccountStore.decode(accountData).filter { account in
            let text = ArabicText.normalize([account.entity, account.branch, account.swift, account.iban].joined(separator: " "))
            return tokens.allSatisfy { text.contains($0) } || account.iban.contains(ArabicText.latinDigits(query))
        }.prefix(30))
        let matchedMonths: [String] = Array(months.map(\.name).filter { name in
            let key = ArabicText.normalize(name)
            return tokens.allSatisfy { key.contains($0) }
        }.prefix(12))
        var groups: [ReportGroup] = []
        if normalized.count >= 2 {
            let signature = months.map { "\($0.name)@\($0.importedAt.timeIntervalSince1970)" }.joined(separator: "|")
            let catalog = try await SmartGroupCatalog.shared.groups(months: monthNames, signature: signature, container: container)
            groups = catalog.filter { group in
                let key = ArabicText.normalize(group.title)
                return tokens.allSatisfy { key.contains($0) } || extras.noteGroups.contains("\(group.month)|\(group.title)")
            }
            for month in months {
                for (title, summary) in PayrollItems.decodeSummary(month.itemSummaryData) ?? [:] {
                    let key = ArabicText.normalize(title)
                    guard tokens.allSatisfy({ key.contains($0) }) else { continue }
                    groups.append(ReportGroup(kind: .payroll, month: month.name, title: title,
                                              count: summary.count, total: summary.total, byEntity: false))
                }
            }
            groups = Array(groups.prefix(50))
        }
        try Task.checkCancellation()
        return SmartSearchResults(people: people, groups: groups, accounts: accounts, months: matchedMonths)
    }
}
