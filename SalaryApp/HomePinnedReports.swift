enum WorkReport: String, CaseIterable, Identifiable {
    case entities, food, fund, health
    case reports, transferFiles, deductionAudit, salaryAudit
    static let statements: [Self] = [.entities, .food, .fund, .health]
    static let sections: [Self] = [.reports, .transferFiles, .deductionAudit, .salaryAudit]
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

    var id: Self { self }
}
