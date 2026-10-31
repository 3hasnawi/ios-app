import Foundation

/// القيم nil تعني استخدام نص القالب أو القيمة العامة، لا استبدال النص بنص فارغ.
nonisolated struct TransferLetterCustomization: Codable, Equatable, Sendable {
    var header: String?
    var committeeFirst: String?
    var committeeSecond: String?
    var committeeThird: String?
    var footer: String?
    var fixedNumber: String? = nil
    var foodContractReference: String? = nil
    var bodyTemplate: String? = nil
    var signatureOffset: Int? = nil
    var bodyMonth: String? = nil
    var committeeDateFirst: String? = nil
    var committeeDateSecond: String? = nil
    var committeeDateThird: String? = nil
    var beneficiaryAccount: String? = nil
    var beneficiarySource: String? = nil
    var beneficiaryOriginal: String? = nil
    var beneficiaryReference: String? = nil

    func applying(over base: Self) -> Self {
        var result = base
        result.header = header ?? base.header
        result.committeeFirst = committeeFirst ?? base.committeeFirst
        result.committeeSecond = committeeSecond ?? base.committeeSecond
        result.committeeThird = committeeThird ?? base.committeeThird
        result.footer = footer ?? base.footer
        result.fixedNumber = fixedNumber ?? base.fixedNumber
        result.foodContractReference = foodContractReference ?? base.foodContractReference
        result.bodyTemplate = bodyTemplate ?? base.bodyTemplate
        result.signatureOffset = signatureOffset ?? base.signatureOffset
        result.bodyMonth = bodyMonth ?? base.bodyMonth
        result.committeeDateFirst = committeeDateFirst ?? base.committeeDateFirst
        result.committeeDateSecond = committeeDateSecond ?? base.committeeDateSecond
        result.committeeDateThird = committeeDateThird ?? base.committeeDateThird
        result.beneficiaryAccount = beneficiaryAccount ?? base.beneficiaryAccount
        result.beneficiarySource = beneficiarySource ?? base.beneficiarySource
        result.beneficiaryOriginal = beneficiaryOriginal ?? base.beneficiaryOriginal
        result.beneficiaryReference = beneficiaryReference ?? base.beneficiaryReference
        return result
    }
}

/// إعدادات مستقلة عن بيانات الرواتب. الشهر لا يُحفظ هنا: يُختار من الأشهر المستوردة
/// ويحدد بيانات الكتاب وملف التحويل معاً.
nonisolated struct TransferLetterCustomizationStore: Codable, Equatable, Sendable {
    static let settingsKey = "transferLetterCustomizations"
    var common = TransferLetterCustomization()
    var individual: [String: TransferLetterCustomization] = [:]
    /// بصمات المراجع التي استُخرجت منها الإعدادات؛ تحفظ مع النسخة الاحتياطية.
    var wordReferenceHashes: [String: String]? = nil

    func effective(for id: String) -> TransferLetterCustomization {
        var result = (individual[id] ?? TransferLetterCustomization()).applying(over: common)
        // العدد ومرجع عقد الإطعام موحّدان لجميع الكتب.
        result.fixedNumber = common.fixedNumber
        result.foodContractReference = common.foodContractReference
        return result
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
            if changes.fixedNumber != nil { value.fixedNumber = nil }
            if changes.foodContractReference != nil { value.foodContractReference = nil }
            if changes.bodyTemplate != nil { value.bodyTemplate = nil }
            if changes.signatureOffset != nil { value.signatureOffset = nil }
            if changes.bodyMonth != nil { value.bodyMonth = nil }
            if changes.committeeDateFirst != nil { value.committeeDateFirst = nil }
            if changes.committeeDateSecond != nil { value.committeeDateSecond = nil }
            if changes.committeeDateThird != nil { value.committeeDateThird = nil }
            if changes.beneficiaryAccount != nil { value.beneficiaryAccount = nil; value.beneficiarySource = nil; value.beneficiaryOriginal = nil; value.beneficiaryReference = nil }
            individual[key] = value
        }
    }

    mutating func apply(_ changes: TransferLetterCustomization, to id: String) {
        individual[id] = changes.applying(over: individual[id] ?? TransferLetterCustomization())
    }
}
