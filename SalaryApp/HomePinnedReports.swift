enum WorkReport: String, CaseIterable, Identifiable {
    case entities, food, fund, health, commandDeductions
    case reports, transferFiles, deductionAudit, salaryAudit
    case salaryTrust, fundingExpenses, employeeDeductions, payrollItems, refunds
    static let statements: [Self] = [.entities, .food, .fund, .health, .commandDeductions]
    static let sections: [Self] = [
        .reports, .transferFiles, .deductionAudit, .salaryAudit, .salaryTrust,
        .fundingExpenses, .employeeDeductions, .payrollItems, .refunds
    ]
    static let pinsKey = "homePinnedWorkItems"

    // تستمر قراءة خيارات الإصدارات السابقة حتى يعدّل المستخدم القائمة الموحّدة.
    static func pins(saved: String?, legacyHidden: String) -> [Self] {
        func entries(_ raw: String) -> [Self] {
            raw.split(separator: "|").compactMap { Self(rawValue: String($0)) }
        }
        var seen = Set<Self>()
        if let saved { return entries(saved).filter { seen.insert($0).inserted } }
        let hidden = Set(entries(legacyHidden))
        return statements.filter { !hidden.contains($0) }
    }

    static func saving(_ reports: [Self], previous: String?) -> String {
        var selected = reports.map(\.rawValue).makeIterator()
        var saved: [String] = []
        for token in (previous ?? "").split(separator: "|") {
            let id = String(token)
            if Self(rawValue: id) == nil { saved.append(id) }
            else if let next = selected.next() { saved.append(next) }
        }
        while let next = selected.next() { saved.append(next) }
        return saved.joined(separator: "|")
    }

    var id: Self { self }
}
