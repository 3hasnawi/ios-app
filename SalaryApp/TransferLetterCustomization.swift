import Foundation

/// القيم nil تعني استخدام نص القالب أو القيمة العامة، لا استبدال النص بنص فارغ.
nonisolated struct TransferLetterCustomization: Codable, Equatable, Sendable {
    var header: String?
    var committeeFirst: String?
    var committeeSecond: String?
    var committeeThird: String?
    var footer: String?

    func applying(over base: Self) -> Self {
        Self(header: header ?? base.header,
             committeeFirst: committeeFirst ?? base.committeeFirst,
             committeeSecond: committeeSecond ?? base.committeeSecond,
             committeeThird: committeeThird ?? base.committeeThird,
             footer: footer ?? base.footer)
    }
}

/// إعدادات مستقلة عن بيانات الرواتب. الشهر لا يُحفظ هنا: يُختار من الأشهر المستوردة
/// ويحدد بيانات الكتاب وملف التحويل معاً.
nonisolated struct TransferLetterCustomizationStore: Codable, Equatable, Sendable {
    static let settingsKey = "transferLetterCustomizations"
    var common = TransferLetterCustomization()
    var individual: [String: TransferLetterCustomization] = [:]

    func effective(for id: String) -> TransferLetterCustomization {
        (individual[id] ?? TransferLetterCustomization()).applying(over: common)
    }

    /// التطبيق على الكل يستبدل التخصيصات السابقة للحقول نفسها فقط.
    mutating func applyToAll(_ changes: TransferLetterCustomization) {
        common = changes.applying(over: common)
        for key in Array(individual.keys) {
            guard var value = individual[key] else { continue }
            if changes.header != nil { value.header = nil }
            if changes.committeeFirst != nil { value.committeeFirst = nil }
            if changes.committeeSecond != nil { value.committeeSecond = nil }
            if changes.committeeThird != nil { value.committeeThird = nil }
            if changes.footer != nil { value.footer = nil }
            individual[key] = value
        }
    }

    mutating func apply(_ changes: TransferLetterCustomization, to id: String) {
        individual[id] = changes.applying(over: individual[id] ?? TransferLetterCustomization())
    }
}
