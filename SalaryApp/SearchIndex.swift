import Foundation
import SwiftData

/// بحث محدود في الخلفية مع ذاكرة لآخر النتائج، دون تحميل قاعدة الملاك والرواتب عند الفتح.
actor SearchIndex {
    static let shared = SearchIndex()
    private var signature = ""
    private var generation = 0
    private var recent: [String: [UnifiedHit]] = [:]

    func invalidate() {
        generation += 1
        signature = ""
        recent.removeAll()
    }

    private func currentSignature(_ context: ModelContext) throws -> String {
        let staffCount = try context.fetchCount(FetchDescriptor<StaffMember>())
        let payCount = try context.fetchCount(FetchDescriptor<Employee>())
        let months = try context.fetch(FetchDescriptor<PayrollMonth>())
            .map { "\($0.name)@\($0.importedAt.timeIntervalSince1970)" }.sorted().joined(separator: ",")
        return "\(staffCount)|\(StaffInfo.importedAt?.timeIntervalSince1970 ?? 0)|\(payCount)|\(months)"
    }

    func hits(_ search: String, mode: StaffSearchMode, scope: UnifiedScope, months: [String], container: ModelContainer) async throws -> [UnifiedHit] {
        try Task.checkCancellation()
        let tokens = UnifiedFinder.tokens(search, mode: mode)
        guard !tokens.isEmpty else { return [] }
        let current = try currentSignature(ModelContext(container))
        if current != signature {
            signature = current
            recent.removeAll()
        }
        let key = "\(tokens.joined(separator: "|"))|\(mode.rawValue)|\(scope.rawValue)|\(months.joined(separator: "|"))"
        if let cached = recent[key] { return cached }
        let revision = generation
        let work = Task.detached(priority: .userInitiated) {
            let started = Date()
            try Task.checkCancellation()
            let result = try UnifiedFinder.hits(search, mode: mode, scope: scope, months: months, context: ModelContext(container))
            PerfLog.record("searchQuery", since: started)
            return result
        }
        let result = try await withTaskCancellationHandler {
            try await work.value
        } onCancel: {
            work.cancel()
        }
        try Task.checkCancellation()
        guard revision == generation, signature == current else { throw CancellationError() }
        if recent.count >= 12 { recent.removeAll() }
        recent[key] = result
        return result
    }
}
