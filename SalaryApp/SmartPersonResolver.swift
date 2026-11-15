import Foundation
import SwiftData

@ModelActor actor SmartPersonResolver {
    /// استعلامان محدودان يربطان نتائج الحقول والأمانات بالرواتب والملاك، دون تحميل كل السجلات.
    func enrich(_ source: [UnifiedHit], months: [String]) throws -> [UnifiedHit] {
        let hits = Array(UnifiedHitMerge.merge(source, months: months).prefix(150))
        let keys = hits.map(\.key)
        guard !keys.isEmpty else { return [] }
        var additions: [UnifiedHit] = []
        var payroll = FetchDescriptor<Employee>(predicate: #Predicate { months.contains($0.month) && keys.contains($0.matchKey) })
        payroll.propertiesToFetch = [\.name, \.matchKey, \.rank, \.workplace, \.month, \.netSalary]
        for employee in try modelContext.fetch(payroll) {
            try Task.checkCancellation()
            additions.append(UnifiedHit(key: employee.matchKey, name: employee.name,
                subtitle: [employee.rank, employee.workplace].filter { !$0.isEmpty }.joined(separator: " · "),
                employeeID: employee.persistentModelID, staffID: nil, month: employee.month,
                netSalary: employee.netSalary, staffStatus: nil))
        }
        let names = hits.map { ArabicText.normalize($0.name) }
        var staff = FetchDescriptor<StaffMember>(predicate: #Predicate { names.contains($0.nameKey) })
        staff.propertiesToFetch = [\.name, \.rank, \.workplace, \.status]
        for member in try modelContext.fetch(staff) {
            try Task.checkCancellation()
            additions.append(UnifiedHit(key: ArabicText.matchKey(member.name), name: member.name,
                subtitle: [member.rank, member.workplace].filter { !$0.isEmpty }.joined(separator: " · "),
                employeeID: nil, staffID: member.persistentModelID, month: nil, netSalary: nil, staffStatus: member.status))
        }
        return UnifiedHitMerge.merge(hits + additions, months: months)
    }
}
