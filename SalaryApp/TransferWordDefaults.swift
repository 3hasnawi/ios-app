import Foundation

/// استيراد إعدادات مستخرجة محلياً من نماذج المالك، دون استبدال تخصيصاته السابقة.
nonisolated enum TransferWordDefaults {
    struct Package: Codable, Sendable {
        let version: Int
        let defaults: TransferLetterCustomizationStore
    }
    enum Failure: LocalizedError {
        case invalid
        var errorDescription: String? { tr("ملف إعدادات الكتب غير صالح أو لا يحتوي جميع الكتب المطلوبة.", "The letter settings file is invalid or incomplete.") }
    }
    static func decode(_ data: Data) throws -> TransferLetterCustomizationStore {
        guard !data.isEmpty, data.count <= 1_000_000,
              let package = try? JSONDecoder().decode(Package.self, from: data), package.version == 1,
              Set(package.defaults.individual.keys) == Set(TransferReferencePack.expected.keys) else { throw Failure.invalid }
        func valid(_ value: String?, max: Int, allowEmpty: Bool = false) -> Bool {
            guard let value, value.count <= max, allowEmpty || !value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return false }
            return !value.unicodeScalars.contains {
                let c = $0.value
                return (c < 32 && c != 10) || c == 0xfffe || c == 0xffff
            }
        }
        let common = package.defaults.common
        guard valid(common.header, max: 2000), valid(common.fixedNumber, max: 200),
              valid(common.foodContractReference, max: 200) else { throw Failure.invalid }
        for id in TransferReferencePack.expected.keys {
            let raw = package.defaults.individual[id]
            guard raw?.fixedNumber == nil, raw?.foodContractReference == nil else { throw Failure.invalid }
            let value = package.defaults.effective(for: id)
            guard valid(value.header, max: 2000), valid(value.committeeFirst, max: 200),
                  valid(value.committeeSecond, max: 200), valid(value.committeeThird, max: 200),
                  valid(value.footer, max: 4000, allowEmpty: true) else { throw Failure.invalid }
        }
        return package.defaults
    }
    static func merging(_ imported: TransferLetterCustomizationStore,
                        preserving existing: TransferLetterCustomizationStore) -> TransferLetterCustomizationStore {
        var result = imported
        result.applyToAll(existing.common)
        for (id, custom) in existing.individual { result.apply(custom, to: id) }
        return result
    }
}
