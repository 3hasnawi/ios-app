import Foundation
import PDFKit

/// قراءة حساب المستفيد الثابت من مرجع خاص، مع مطابقة حساب الدفع وعدم التخمين عند الغموض.
nonisolated enum TransferWordReferenceAccounts {
    enum Failure: LocalizedError {
        case mismatch
        var errorDescription: String? { tr("تعذّر مطابقة حساب الدفع وحساب المستفيد مع مرجع الكتاب؛ راجع بيانات الحساب.", "The payer and beneficiary accounts could not be verified against the letter reference.") }
    }
    private static func normalized(_ value: String) -> String {
        ArabicText.latinDigits(value).uppercased().filter { !$0.isWhitespace && $0 != "-" }
    }
    static func beneficiary(in text: String, payerIBAN: String) throws -> String {
        let payer = normalized(payerIBAN)
        guard TransferRules.validIBAN(payer), text.utf8.count <= 2_000_000 else { throw Failure.mismatch }
        let regex = try NSRegularExpression(pattern: "(?i)IQ(?:[\\s]*[A-Z0-9]){21}(?![A-Z0-9])")
        let matches = regex.matches(in: text, range: NSRange(text.startIndex..., in: text))
        let values = matches.compactMap { Range($0.range, in: text).map { normalized(String(text[$0])) } }
        let unique = Set(values)
        guard values.count == 2, unique.count == 2, values.allSatisfy(TransferRules.validIBAN),
              unique.contains(payer), let beneficiary = unique.first(where: { $0 != payer }) else { throw Failure.mismatch }
        return beneficiary
    }
    static func beneficiary(bookID: String, references: Data, payerIBAN: String) throws -> String {
        guard ["food_company", "food_tax", "tax_social", "contracts", "cash_salary"].contains(bookID) else { throw Failure.mismatch }
        let source = try TransferReferencePack.load([bookID], from: references)
        guard let data = source[bookID], let document = PDFDocument(data: data), !document.isLocked,
              let text = document.page(at: 0)?.string else { throw Failure.mismatch }
        return try beneficiary(in: text, payerIBAN: payerIBAN)
    }
}
