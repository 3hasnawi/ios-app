import SwiftUI

enum WorkReport: String, CaseIterable, Identifiable {
    case entities, food, fund, health
    var id: Self { self }
    var title: String {
        switch self {
        case .entities: tr("جهات التنفيذ", "Execution entities")
        case .food: tr("شركة عراق الجوال", "Iraq Mobile")
        case .fund: tr("صندوق الشهداء", "Martyrs fund")
        case .health: tr("الرعاية الصحية", "Health care")
        }
    }
    var icon: String {
        switch self {
        case .entities: "building.columns"
        case .food: "iphone"
        case .fund: "star"
        case .health: "cross.case"
        }
    }
    func group(month: String, summary: [String: PayrollItems.Summary]) -> ReportGroup? {
        let matches = summary.keys.sorted().filter { title in
            switch self {
            case .entities: false
            case .food: FoodContractReport.applies(to: title)
            case .fund: FundReport.applies(to: title)
            case .health: HealthCareReport.applies(to: title)
            }
        }
        guard let name = matches.max(by: { summary[$0]!.total < summary[$1]!.total }), let item = summary[name] else { return nil }
        return ReportGroup(kind: .payroll, month: month, title: name, count: item.count, total: item.total, byEntity: false)
    }
}

enum PinnedEntities {
    static let key = EntityRenamePlan.pinnedKey
    static func read(_ data: Data) -> [String] { (try? JSONDecoder().decode([String].self, from: data)) ?? [] }
    static func toggle(_ title: String, in data: Data) -> Data {
        var names = read(data)
        if names.contains(title) { names.removeAll { $0 == title } } else { names.append(title) }
        return (try? JSONEncoder().encode(names)) ?? data
    }
}
