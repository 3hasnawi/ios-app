import Foundation

/// لا تسقط مطابقة الأم أو الحقول الإضافية راتباً أو أمانات عُثر عليها للشخص نفسه.
nonisolated enum UnifiedHitMerge {
    static func merge(_ hits: [UnifiedHit], months: [String]) -> [UnifiedHit] {
        let order = Dictionary(months.enumerated().map { ($1, $0) }, uniquingKeysWith: min)
        var keys: [String] = []
        var byKey: [String: UnifiedHit] = [:]
        for hit in hits {
            guard !hit.key.isEmpty else { continue }
            guard let old = byKey[hit.key] else { keys.append(hit.key); byKey[hit.key] = hit; continue }
            let useNextPayroll = hit.employeeID != nil && (old.employeeID == nil || (order[hit.month ?? ""] ?? .max) < (order[old.month ?? ""] ?? .max))
            let payroll = useNextPayroll ? hit : old
            var combined = UnifiedHit(key: old.key, name: old.name, subtitle: old.subtitle.isEmpty ? hit.subtitle : old.subtitle,
                employeeID: payroll.employeeID, staffID: old.staffID ?? hit.staffID,
                month: payroll.month, netSalary: payroll.netSalary, staffStatus: old.staffStatus ?? hit.staffStatus)
            combined.hasDeduction = old.hasDeduction || hit.hasDeduction
            combined.matchLabel = old.matchLabel ?? hit.matchLabel
            combined.trust = hit.trust ?? old.trust
            byKey[hit.key] = combined
        }
        return keys.compactMap { byKey[$0] }
    }
}
