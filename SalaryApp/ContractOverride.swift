import Foundation

/// المبلغ النهائي اليدوي يُربط بأساس اسمي العقود والتوقيفات وأعدادها عند اعتماده.
nonisolated struct ContractOverride: Codable, Equatable, Sendable {
    var total: Double
    var nominal: Double
    var retirement: Double
    var count: Int
    var missing: Int

    func matches(_ summary: BankStatementSummary) -> Bool {
        nominal == summary.contractNominal && retirement == summary.contractRetirement && count == summary.contractCount && missing == summary.missingContractNominal
    }
    var isValid: Bool {
        let amounts = [total, nominal, retirement]
        return amounts.allSatisfy { $0.isFinite && $0 >= 0 && $0 <= 9_007_199_254_740_991 && $0.rounded() == $0 } && count > 0 && missing == 0
    }
}

nonisolated enum ContractOverrides {
    static let settingsKey = "bankStatementContractOverridesV1"
    enum Failure: LocalizedError {
        case invalid
        var errorDescription: String? { tr("تعديلات العقود المحفوظة غير صالحة؛ لم يُفترض مبلغ صفري.", "Saved contract overrides are invalid; no zero value was assumed.") }
    }
    static func decode(_ data: Data?) throws -> [String: ContractOverride] {
        guard let data else { return [:] }
        guard !data.isEmpty, data.count <= 80_000,
              let entries = try? JSONDecoder().decode([String: ContractOverride].self, from: data),
              entries.allSatisfy({ !$0.key.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && $0.key.count <= 200 && $0.value.isValid }) else { throw Failure.invalid }
        return entries
    }
    static func currentData() throws -> Data? {
        guard let object = UserDefaults.standard.object(forKey: settingsKey) else { return nil }
        guard let data = object as? Data else { throw Failure.invalid }
        return data
    }
    static func read(month: String) throws -> ContractOverride? {
        try decode(currentData())[month]
    }
    /// الحقول غير المعروفة في الأشهر والقيود تبقى كما كانت عند تغيير شهر واحد.
    static func setting(_ data: Data?, month: String, value: ContractOverride?) throws -> Data {
        guard !month.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, month.count <= 200, value?.isValid != false else { throw Failure.invalid }
        _ = try decode(data)
        var root: [String: Any] = [:]
        if let data {
            guard let object = try JSONSerialization.jsonObject(with: data) as? [String: Any] else { throw Failure.invalid }
            root = object
        }
        if let value {
            var entry = root[month] as? [String: Any] ?? [:]
            entry["total"] = value.total; entry["nominal"] = value.nominal; entry["retirement"] = value.retirement
            entry["count"] = value.count; entry["missing"] = value.missing
            root[month] = entry
        } else { root.removeValue(forKey: month) }
        guard JSONSerialization.isValidJSONObject(root) else { throw Failure.invalid }
        let encoded = try JSONSerialization.data(withJSONObject: root, options: [.sortedKeys])
        _ = try decode(encoded)
        return encoded
    }
}
