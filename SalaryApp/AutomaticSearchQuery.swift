import Foundation

/// نفس القاعدة لكل المصادر؛ لا نستنتج نوع الحقل من وجود رقم داخل اسم أو عبارة.
nonisolated struct AutomaticSearchQuery: Sendable {
    struct Field: Sendable { let label: String; let value: String }
    let tokens: [String]
    let digits: String?
    init(_ input: String) {
        let latin = ArabicText.latinDigits(input)
        tokens = Self.normalize(latin).split(separator: " ").map(String.init)
        let separators = CharacterSet(charactersIn: " ,٬،").union(.whitespacesAndNewlines)
        let numeric = !latin.isEmpty && latin.unicodeScalars.allSatisfy { CharacterSet.decimalDigits.contains($0) || separators.contains($0) }
        let number = latin.filter(\.isNumber)
        digits = numeric && !number.isEmpty ? number : nil
    }
    var isEmpty: Bool { tokens.isEmpty }
    static func normalize(_ value: String) -> String {
        ArabicText.normalize(ArabicText.latinDigits(ArabicText.cleanQuery(value)))
    }
    func match(_ fields: [Field]) -> String? {
        guard !isEmpty else { return nil }
        if let digits {
            return fields.first { field in
                let value = ArabicText.latinDigits(field.value).filter { $0.isNumber || $0 == "." || $0 == "٫" }
                return value.contains(digits)
            }?.label
        }
        let normalized = fields.map { Self.normalize($0.value) }
        let combined = normalized.joined(separator: "\n")
        guard tokens.allSatisfy({ combined.contains($0) }) else { return nil }
        return fields.indices.first(where: { i in tokens.contains { normalized[i].contains($0) } }).map { fields[$0].label }
    }
    func matches(_ value: String) -> Bool { match([Field(label: "value", value: value)]) != nil }
}
