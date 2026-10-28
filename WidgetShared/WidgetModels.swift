import Foundation

/// ملخصات فقط؛ لا تُنقل سجلات الأشخاص أو رموز السيرفر إلى الويدجت.
nonisolated struct SalaryWidgetMonth: Codable, Sendable {
    struct Entity: Codable, Sendable {
        var title: String
        var amount: Double
        var employees: Int
        var rows: Int
    }
    var name: String
    var sourceKey: String
    var net: Double
    var employees: Int
    var deductionAmount: Double
    var deductionCount: Int
    var bankTotal: Double?
    var bankMatched: Bool
    var cash: Double
    var card: Double
    var unclassified: Double
    var bankDeductions: Double
    var executionDifference: Double
    var leave: Double
    var rewards: Double
    var singleList: Double
    var healthcare: Double
    var martyrs: Double
    var sport: Double
    var tax: Double
    var protection: Double
    var mobile: Double
    var pension: Double
    var bookCount: Int
    var officers: Int
    var civil: Int
    var ranks: Int
    var contracts: Int
    var changePercent: Double?
    var auditCount: Int?
    var newEmployees: Int?
    var removedEmployees: Int?
    var changedSalaries: Int?
    var stoppedDeductions: Int?
    var startedDeductions: Int?
    var changedDeductions: Int?
    var changedBanks: Int?
    var changedRanks: Int?
    var changedWorkplaces: Int?
    var entities: [Entity]
}

nonisolated struct SalaryWidgetSnapshot: Codable, Sendable {
    var updatedAt: Date
    var months: [SalaryWidgetMonth]
    var backupStatus: String
    var googleDriveStatus: String?
    var arabicDigits: Bool
    var isEnglish: Bool
}

nonisolated enum SalaryWidgetStore {
    static let group = "group.com.example.SalaryApp.widgets"
    static let key = "salaryWidgetSnapshotV1"
    static var available: Bool { FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: group) != nil }
    static func clear() { UserDefaults(suiteName: group)?.removeObject(forKey: key) }
    static func read() -> SalaryWidgetSnapshot? {
        guard let data = UserDefaults(suiteName: group)?.data(forKey: key) else { return nil }
        return try? JSONDecoder().decode(SalaryWidgetSnapshot.self, from: data)
    }
    @discardableResult static func write(_ value: SalaryWidgetSnapshot) -> Bool {
        guard available, let defaults = UserDefaults(suiteName: group), let data = try? JSONEncoder().encode(value) else { return false }
        defaults.set(data, forKey: key)
        return true
    }
}
