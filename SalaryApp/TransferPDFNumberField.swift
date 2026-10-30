import Foundation
import PDFKit

/// خانة العدد تُترك فارغة للكتابة اليدوية، مع إبقاء عنوانها الثابت.
nonisolated enum TransferPDFNumberField {
    enum Failure: LocalizedError {
        case invalidReference
        var errorDescription: String? {
            tr("تعذّر التحقق من موضع العدد القديم في مرجع الكتاب.", "Could not verify the old document number field in the letter reference.")
        }
    }
    static func mask(id: String, page: PDFPage, height: CGFloat) throws -> CGRect {
        let rect: CGRect
        switch id {
        case "contracts", "ramadi": rect = CGRect(x: 121.2, y: 151.6, width: 51.5, height: 22.2)
        case "rafidain_anbar": rect = CGRect(x: 87.6, y: 151.6, width: 53.4, height: 22.2)
        case "housing": rect = CGRect(x: 95.3, y: 151.6, width: 51.5, height: 22.2)
        case "rafidain_group", "other_banks_group": rect = CGRect(x: 64.8, y: 160.0, width: 58.4, height: 20.4)
        case "food_company", "tax_social", "water", "cash_salary", "food_tax":
            rect = CGRect(x: 92.5, y: 151.6, width: 51.6, height: 22.2)
        default: throw Failure.invalidReference
        }
        let pdfRect = CGRect(x: rect.minX, y: height - rect.maxY, width: rect.width, height: rect.height)
        let source = page.selection(for: pdfRect)?.string ?? ""
        let digits = ArabicText.latinDigits(source).filter { $0.isASCII && $0.isNumber }
        guard digits.count == 3 else { throw Failure.invalidReference }
        return rect
    }
}
