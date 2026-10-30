import Foundation

/// يفحص محتوى CSV الناتج مقابل لقطة الجهات نفسها قبل اعتماد حزمة المجموعة.
nonisolated enum TransferGroupCSVCheck {
    enum Failure: LocalizedError {
        case mismatch
        var errorDescription: String? { tr("ملف التحويل لا يطابق جهات المجموعة أو مبالغها؛ أُوقفت العملية.", "The transfer file does not match the group entities or amounts; the operation was stopped.") }
    }
    static func validate(_ data: Data, entities: [TransferEntity], category: TransferCategory,
                         month: String, payer: String, payerIBAN: String) throws {
        guard category == .rafidain || category == .otherBanks, !entities.isEmpty,
              Set(entities.map(\.id)).count == entities.count, TransferRules.safeField(payer),
              TransferRules.validIBAN(payerIBAN), TransferRules.safeField(month),
              !data.starts(with: [0xef, 0xbb, 0xbf]), let text = String(data: data, encoding: .utf8),
              !text.hasSuffix("\n"), !text.replacingOccurrences(of: "\r\n", with: "").contains("\n") else { throw Failure.mismatch }
        let lines = text.components(separatedBy: "\r\n")
        let header = "Reference|Value Date|Payer Name|Payer Account|Amount|Currency|Receiver BIC|Beneficiary Name|Beneficiary Account|Remittance Information|Details of Charges"
        guard lines.count == entities.count + 1, lines.first == header else { throw Failure.mismatch }
        func account(_ value: String) -> String { ArabicText.latinDigits(value).uppercased().filter { !$0.isWhitespace && $0 != "-" } }
        let dateFormatter = DateFormatter()
        dateFormatter.calendar = Calendar(identifier: .gregorian)
        dateFormatter.locale = Locale(identifier: "en_US_POSIX")
        dateFormatter.timeZone = TimeZone(secondsFromGMT: 0)
        dateFormatter.dateFormat = "yyyyMMdd"
        dateFormatter.isLenient = false
        var firstDate: String?
        for (index, entity) in entities.enumerated() {
            guard entity.category == category, entity.group.month == month, TransferRules.issues(entity).isEmpty,
                  let beneficiary = entity.account else { throw Failure.mismatch }
            let fields = lines[index + 1].components(separatedBy: "|")
            guard fields.count == 11 else { throw Failure.mismatch }
            let day = fields[1]
            guard day.utf8.count == 8, day.utf8.allSatisfy({ (48...57).contains($0) }),
                  let parsed = dateFormatter.date(from: day), dateFormatter.string(from: parsed) == day else { throw Failure.mismatch }
            if let firstDate, firstDate != day { throw Failure.mismatch }
            firstDate = day
            let amount = String(format: "%.0f", locale: Locale(identifier: "en_US_POSIX"), entity.group.total)
            guard fields[0] == day + category.prefix + String(index + 1), fields[2] == payer,
                  fields[3] == account(payerIBAN), fields[4] == amount, fields[5] == "IQD",
                  fields[6] == beneficiary.swift.trimmingCharacters(in: .whitespacesAndNewlines).uppercased(),
                  fields[7] == (entity.beneficiaryName ?? entity.group.title), fields[8] == account(beneficiary.iban),
                  fields[9] == "استقطاعات \(month)", fields[10] == "SLEV" else { throw Failure.mismatch }
        }
    }
}
