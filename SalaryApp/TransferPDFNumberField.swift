import Foundation
import PDFKit

/// تحمي العدد الثابت في المرجع من أي تفريغ عرضي عند إعداد الكتاب.
nonisolated enum TransferPDFNumberField {
    enum Failure: LocalizedError {
        case invalidReference
        var errorDescription: String? {
            tr("تعذّر الحفاظ على خانة العدد الثابتة في مرجع الكتاب.", "Could not preserve the fixed document number field in the letter reference.")
        }
    }
    static func ensurePreserved(id: String, masks: [CGRect]) throws {
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
        guard !masks.contains(where: { $0.intersects(rect) }) else { throw Failure.invalidReference }
    }
}
