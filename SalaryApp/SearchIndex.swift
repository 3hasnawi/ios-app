import Foundation
import SwiftData

/// فهرس البحث في الذاكرة: أسماء الملاك والرواتب (بدون الحقول الكبيرة) تُحمَّل مرة واحدة،
/// ثم يكون البحث مطابقة نصوص في الذاكرة بدل استعلامات متكررة على قاعدة البيانات.
/// يُعاد بناؤه تلقائياً إذا تغيّرت البيانات (استيراد، حذف، استرجاع).
actor SearchIndex {
    static let shared = SearchIndex()

    struct StaffEntry: Sendable {
        let id: PersistentIdentifier
        let name: String
        let nameKey: String
        let motherKey: String
        let stat: String
        let unified: String
        let rank: String
        let workplace: String
        let status: String
        let matchKey: String
    }

    struct PayEntry: Sendable {
        let id: PersistentIdentifier
        let name: String
        let nameKey: String
        let matchKey: String
        let rank: String
        let workplace: String
        let net: Double?
    }

    private var signature = ""
    private var staff: [StaffEntry] = []
    private var staffByMatch: [String: Int] = [:]
    private var pay: [String: [PayEntry]] = [:]
    private var payByMatch: [String: [String: Int]] = [:]

    private struct Snapshot: Sendable {
        let signature: String
        let staff: [StaffEntry]
        let staffByMatch: [String: Int]
        let pay: [String: [PayEntry]]
        let payByMatch: [String: [String: Int]]
    }
    private var warming: Task<Snapshot, Error>?
    private var generation = 0

    func invalidate() {
        signature = ""
        generation += 1
        warming?.cancel()
        warming = nil
    }

    /// لا يحتجز بناء الفهرس طابور البحث؛ أول بحث يستخدم استعلاماً خفيفاً حتى يجهز.
    func warmUp(container: ModelContainer) async {
        guard warming == nil else { return }
        guard let current = try? Self.currentSignature(ModelContext(container)), current != signature else { return }
        let revision = generation
        let task = Task.detached(priority: .utility) {
            let started = Date()
            let result = try Self.buildSnapshot(ModelContext(container))
            PerfLog.record("searchIndex", since: started)
            return result
        }
        warming = task
        defer { if revision == generation { warming = nil } }
        guard let result = try? await task.value, revision == generation,
              let latest = try? Self.currentSignature(ModelContext(container)), latest == result.signature else { return }
        staff = result.staff
        staffByMatch = result.staffByMatch
        pay = result.pay
        payByMatch = result.payByMatch
        signature = result.signature
    }

    private nonisolated static func currentSignature(_ context: ModelContext) throws -> String {
        let staffCount = try context.fetchCount(FetchDescriptor<StaffMember>())
        let payCount = try context.fetchCount(FetchDescriptor<Employee>())
        let months = try context.fetch(FetchDescriptor<PayrollMonth>())
            .map { "\($0.name)@\($0.importedAt.timeIntervalSince1970)" }.sorted().joined(separator: ",")
        return "\(staffCount)|\(StaffInfo.importedAt?.timeIntervalSince1970 ?? 0)|\(payCount)|\(months)"
    }

    private nonisolated static func buildSnapshot(_ context: ModelContext) throws -> Snapshot {
        let current = try currentSignature(context)
        try Task.checkCancellation()
        var s = FetchDescriptor<StaffMember>(sortBy: [SortDescriptor(\.name)])
        s.propertiesToFetch = [\.name, \.nameKey, \.motherKey, \.statNumber, \.unifiedNumber, \.rank, \.workplace, \.status]
        let staff = try context.fetch(s).map {
            StaffEntry(id: $0.persistentModelID, name: $0.name, nameKey: $0.nameKey, motherKey: $0.motherKey,
                       stat: $0.statNumber, unified: $0.unifiedNumber, rank: $0.rank, workplace: $0.workplace,
                       status: $0.status, matchKey: $0.nameKey.replacingOccurrences(of: " ", with: ""))
        }
        var staffByMatch: [String: Int] = [:]
        for (i, e) in staff.enumerated() where staffByMatch[e.matchKey] == nil { staffByMatch[e.matchKey] = i }

        var p = FetchDescriptor<Employee>(sortBy: [SortDescriptor(\.name)])
        p.propertiesToFetch = [\.name, \.nameKey, \.matchKey, \.month, \.rank, \.workplace, \.netSalary]
        var byMonth: [String: [PayEntry]] = [:]
        for e in try context.fetch(p) {
            byMonth[e.month, default: []].append(PayEntry(id: e.persistentModelID, name: e.name, nameKey: e.nameKey, matchKey: e.matchKey,
                                                          rank: e.rank, workplace: e.workplace, net: e.netSalary))
        }
        try Task.checkCancellation()
        let payByMatch = byMonth.mapValues { list in
            var map: [String: Int] = [:]
            for (i, e) in list.enumerated() where map[e.matchKey] == nil { map[e.matchKey] = i }
            return map
        }
        return Snapshot(signature: current, staff: staff, staffByMatch: staffByMatch,
                        pay: byMonth, payByMatch: payByMatch)
    }

    /// نفس نتائج البحث الموحّد السابق (الملاك + رواتب الأشهر المختارة، مربوطة بالاسم بلا مسافات).
    func hits(_ search: String, mode: StaffSearchMode, scope: UnifiedScope, months: [String], container: ModelContainer) async throws -> [UnifiedHit] {
        let tokens = UnifiedFinder.tokens(search, mode: mode)
        guard !tokens.isEmpty else { return [] }
        try Task.checkCancellation()
        let current = try Self.currentSignature(ModelContext(container))
        if current != signature {
            if warming == nil { Task { await self.warmUp(container: container) } }
            // لا ينتظر المستخدم تحميل عشرات الآلاف من الأسماء في الذاكرة.
            return try await Task.detached(priority: .userInitiated) {
                let started = Date()
                let hits = try UnifiedFinder.hits(search, mode: mode, scope: scope, months: months, context: ModelContext(container))
                PerfLog.record("searchCold", since: started)
                return hits
            }.value
        }

        // الملاك.
        var staffHits: [Int] = []
        if !staff.isEmpty, scope != .payroll || mode != .name {
            for (i, e) in staff.enumerated() {
                let ok: Bool
                switch mode {
                case .name: ok = tokens.allSatisfy { e.nameKey.contains($0) }
                case .mother: ok = tokens.allSatisfy { e.motherKey.contains($0) }
                case .statNumber: ok = tokens.allSatisfy { e.stat.contains($0) }
                case .unifiedNumber: ok = tokens.allSatisfy { e.unified.contains($0) }
                }
                if ok {
                    staffHits.append(i)
                    if staffHits.count >= 150 { break }
                }
            }
        }

        // الرواتب: لكل شخص أول شهر حسب ترتيب الأشهر المختارة.
        var payHits: [(month: String, index: Int)] = []
        if scope != .staff, mode == .name {
            var seen = Set<String>()
            for month in months {
                guard let list = pay[month] else { continue }
                for (i, e) in list.enumerated() where tokens.allSatisfy({ e.nameKey.contains($0) }) && seen.insert(e.matchKey).inserted {
                    payHits.append((month, i))
                    if payHits.count >= 150 { break }
                }
                if payHits.count >= 150 { break }
            }
        }

        struct Pair { var pay: (month: String, index: Int)?; var staff: Int? }
        var byKey: [String: Pair] = [:]
        var keys: [String] = []
        for i in staffHits {
            let key = staff[i].matchKey
            if byKey[key] == nil { keys.append(key); byKey[key] = Pair() }
            if byKey[key]!.staff == nil { byKey[key]!.staff = i }
        }
        for hit in payHits {
            let key = pay[hit.month]![hit.index].matchKey
            if byKey[key] == nil { keys.append(key); byKey[key] = Pair() }
            if byKey[key]!.pay == nil { byKey[key]!.pay = hit }
        }
        // ربط الطرف الناقص بالاسم الكامل.
        for key in keys {
            if byKey[key]!.pay == nil {
                for month in months { if let i = payByMatch[month]?[key] { byKey[key]!.pay = (month, i); break } }
            }
            if byKey[key]!.staff == nil, let i = staffByMatch[key] { byKey[key]!.staff = i }
        }

        return keys.compactMap { key -> UnifiedHit? in
            guard let pair = byKey[key] else { return nil }
            let p = pair.pay.map { pay[$0.month]![$0.index] }
            let s = pair.staff.map { staff[$0] }
            switch scope {
            case .all: break
            case .payroll: if p == nil { return nil }
            case .staff: if s == nil { return nil }
            }
            let name = s?.name ?? p?.name ?? ""
            let subtitle = s.map { [$0.rank, $0.workplace] } ?? p.map { [$0.rank, $0.workplace] } ?? []
            return UnifiedHit(
                key: key, name: name,
                subtitle: subtitle.filter { !$0.isEmpty }.joined(separator: " · "),
                employeeID: p?.id, staffID: s?.id,
                month: pair.pay?.month, netSalary: p?.net, staffStatus: s?.status
            )
        }
        .sorted { lhs, rhs in
            let l = (lhs.employeeID != nil ? 1 : 0) + (lhs.staffID != nil ? 1 : 0)
            let r = (rhs.employeeID != nil ? 1 : 0) + (rhs.staffID != nil ? 1 : 0)
            return l != r ? l > r : lhs.name < rhs.name
        }
    }
}
