#if targetEnvironment(simulator)
import Foundation

/// يتحقق من صيغة الملف وقواعد التصنيف باستخدام حسابات وهمية فقط.
@MainActor enum TransferFilesSelfTest {
    struct Failure: LocalizedError { let errorDescription: String? }
    static func run() throws -> String {
        func check(_ value: Bool, _ message: String) throws { if !value { throw Failure(errorDescription: message) } }
        let iban = "IQ06TEST123000000000001"
        let account = SavedExecutionAccount(entity: "جهة وهمية", iban: iban, branch: "مصرف الرافدين فرع تجريبي", swift: "TESTIQBAAAA")
        let anbar = SavedExecutionAccount(entity: "جهة ثانية", iban: iban, branch: "مصرف الرافدين فرع الانبار", swift: "TESTIQBAAAA")
        let other = SavedExecutionAccount(entity: "جهة ثالثة", iban: iban, branch: "مصرف الرشيد فرع تجريبي", swift: "TESTIQBAAAA")
        try check(TransferRules.classify(account) == .rafidain && TransferRules.classify(anbar) == .anbar && TransferRules.classify(other) == .otherBanks, "classification")
        try check(TransferRules.classify(nil) == .unresolved && TransferRules.validIBAN(iban) && !TransferRules.validIBAN("IQ00"), "invalid accounts")
        let group = ReportGroup(kind: .deductions, month: "تشرين الاول 2026", title: "جهة وهمية", count: 1, total: 1200, byEntity: false)
        let item = TransferEntity(group: group, account: account, category: .rafidain, missingAmounts: 0)
        let data = try TransferRules.csv([item, item], category: .rafidain, month: group.month, payer: "دافع وهمي", payerIBAN: iban)
        let text = String(decoding: data, as: UTF8.self)
        let lines = text.components(separatedBy: "\r\n")
        try check(!data.starts(with: [0xef, 0xbb, 0xbf]) && lines.count == 3 && !text.hasSuffix("\n"), "encoding")
        let first = lines[1].components(separatedBy: "|")
        let second = lines[2].components(separatedBy: "|")
        try check(first.count == 11 && first[0] == first[1] + "300001" && second[0] == second[1] + "300002", "references")
        try check(first[4] == "1200" && first[5] == "IQD" && first[9] == "استقطاعات \(group.month)" && first[10] == "SLEV", "fields")
        let otherItem = TransferEntity(group: group, account: other, category: .otherBanks, missingAmounts: 0)
        let otherData = try TransferRules.csv([otherItem], category: .otherBanks, month: group.month, payer: "دافع وهمي", payerIBAN: iban)
        let otherFields = String(decoding: otherData, as: UTF8.self).components(separatedBy: "\r\n")[1].components(separatedBy: "|")
        try check(otherFields[0] == otherFields[1] + "200001", "other bank reference")
        let invalid = TransferEntity(group: group, account: account, category: .rafidain, missingAmounts: 1)
        do {
            _ = try TransferRules.csv([invalid], category: .rafidain, month: group.month, payer: "دافع وهمي", payerIBAN: iban)
            throw Failure(errorDescription: "invalid amount accepted")
        } catch is Failure { throw Failure(errorDescription: "invalid amount accepted") }
        catch { }
        return "transferFiles=ok encoding=ok classification=ok references=ok validation=ok"
    }
}
#endif
