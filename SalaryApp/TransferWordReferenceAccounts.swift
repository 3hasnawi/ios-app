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
        guard ["food_company", "food_tax", "tax_social", "contracts", "cash_salary", "ramadi"].contains(bookID) else { throw Failure.mismatch }
        let source = try TransferReferencePack.load([bookID], from: references)
        guard let data = source[bookID], let document = PDFDocument(data: data), !document.isLocked,
              let text = document.page(at: 0)?.string else { throw Failure.mismatch }
        return try beneficiary(in: text, payerIBAN: payerIBAN)
    }
    /// تُقرأ حسابات الكتابين المحليين من مواضع محددة في المرجع المعتمد وتُطابق الحساب المحفوظ.
    static func verifyIndividual(bookID: String, references: Data, payerIBAN: String,
                                 beneficiary: String?) throws {
        let payerRect: CGRect
        let beneficiaryRect: CGRect?
        switch bookID {
        case "housing":
            payerRect = CGRect(x: 211.37, y: 299.06, width: 170.586, height: 15.543)
            beneficiaryRect = CGRect(x: 140.42, y: 315.14, width: 105.20, height: 15.54)
        case "water":
            payerRect = CGRect(x: 385.99, y: 333.62, width: 170.586, height: 15.543)
            beneficiaryRect = CGRect(x: 40.20, y: 349.70, width: 104.91, height: 15.54)
        case "ramadi":
            payerRect = CGRect(x: 385.99, y: 349.7, width: 170.586, height: 15.543)
            beneficiaryRect = CGRect(x: 334.39, y: 365.78, width: 170.66, height: 15.54)
        case "rafidain_anbar":
            payerRect = CGRect(x: 161.06, y: 299.06, width: 170.6, height: 15.543)
            beneficiaryRect = nil
        default: throw Failure.mismatch
        }
        guard TransferRules.validIBAN(payerIBAN) else { throw Failure.mismatch }
        let source = try TransferReferencePack.load([bookID], from: references)
        guard let data = source[bookID], let doc = PDFDocument(data: data), !doc.isLocked,
              let page = doc.page(at: 0) else { throw Failure.mismatch }
        let height = page.bounds(for: .mediaBox).height
        func selected(_ rect: CGRect) -> String {
            let box = CGRect(x: rect.minX - 0.2, y: height - rect.maxY - 0.2, width: rect.width + 0.4, height: rect.height + 0.4)
            return page.selection(for: box)?.string ?? ""
        }
        func alphanumeric(_ value: String) -> String {
            ArabicText.latinDigits(value).uppercased().filter { $0.isASCII && ($0.isLetter || $0.isNumber) }
        }
        guard alphanumeric(selected(payerRect)) == alphanumeric(payerIBAN) else { throw Failure.mismatch }
        if let beneficiaryRect {
            guard let beneficiary, !beneficiary.isEmpty,
                  alphanumeric(selected(beneficiaryRect)) == alphanumeric(beneficiary) else { throw Failure.mismatch }
        }
    }
}
