enum WorkReport: String, CaseIterable, Identifiable {
    case entities, food, fund, health
    case reports, transferFiles, deductionAudit, salaryAudit, changes, special
    static let statements: [Self] = [.entities, .food, .fund, .health]
    static let sections: [Self] = [.reports, .transferFiles, .deductionAudit, .salaryAudit, .changes, .special]
    static let pinsKey = "homePinnedWorkItems"

    // تستمر قراءة خيارات الإصدارات السابقة حتى يعدّل المستخدم القائمة الموحّدة.
    static func pins(saved: String?, legacyHidden: String, legacyCards: String) -> [Self] {
        func entries(_ raw: String) -> [Self] {
            raw.split(separator: "|").compactMap { Self(rawValue: String($0)) }
        }
        var seen = Set<Self>()
        if let saved { return entries(saved).filter { seen.insert($0).inserted } }
        let hidden = Set(entries(legacyHidden))
        let legacyChoices: [Self] = [.reports, .salaryAudit, .deductionAudit, .entities, .changes, .special]
        let cards = entries(legacyCards).filter { legacyChoices.contains($0) }
        let previous: [Self] = cards.count == 2 && cards[0] != cards[1] ? cards : [.reports, .salaryAudit]
        return (statements.filter { !hidden.contains($0) } + previous).filter { seen.insert($0).inserted }
    }

    var id: Self { self }
}
