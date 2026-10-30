import UIKit
import PDFKit

/// مواضع أسماء اللجنة في المراجع الخاصة؛ لا تحتوي أسماء أو حسابات.
nonisolated enum TransferPDFCustomization {
    enum Failure: LocalizedError {
        case unsupportedField, invalidTemplate
        var errorDescription: String? {
            switch self {
            case .unsupportedField: tr("تعديل الترويسة أو الهامش لهذا الكتاب لم يكتمل بعد.", "Header or footer customization for this letter is not ready yet.")
            case .invalidTemplate: tr("تعذّر التحقق من مواضع أسماء اللجنة في مرجع الكتاب.", "Could not verify committee name locations in the letter reference.")
            }
        }
    }
    struct Prepared { let masks: [CGRect]; let flows: [TransferPDFTextFlow] }
    private static let names: [String: [CGRect]] = [
        "food_company": [CGRect(x: 452.260, y: 638.770, width: 94.289, height: 17.668), CGRect(x: 295.610, y: 638.770, width: 98.711, height: 17.668), CGRect(x: 86.304, y: 638.666, width: 131.669, height: 17.795)],
        "tax_social": [CGRect(x: 452.260, y: 678.040, width: 94.289, height: 17.668), CGRect(x: 295.610, y: 678.040, width: 98.711, height: 17.668), CGRect(x: 60.864, y: 677.936, width: 182.501, height: 17.795)],
        "contracts": [CGRect(x: 452.260, y: 659.080, width: 94.289, height: 17.668), CGRect(x: 295.610, y: 659.080, width: 98.711, height: 17.668), CGRect(x: 60.864, y: 658.976, width: 182.501, height: 17.795)],
        "rafidain_anbar": [CGRect(x: 452.260, y: 693.280, width: 94.289, height: 17.668), CGRect(x: 295.610, y: 693.280, width: 98.711, height: 17.668), CGRect(x: 60.864, y: 693.176, width: 182.501, height: 17.795)],
        "water": [CGRect(x: 452.260, y: 643.720, width: 94.289, height: 17.668), CGRect(x: 295.610, y: 643.720, width: 98.711, height: 17.668), CGRect(x: 60.864, y: 643.616, width: 182.501, height: 17.795)],
        "housing": [CGRect(x: 452.260, y: 657.040, width: 94.289, height: 17.668), CGRect(x: 295.610, y: 657.040, width: 98.711, height: 17.668), CGRect(x: 60.864, y: 656.936, width: 182.501, height: 17.795)],
        "ramadi": [CGRect(x: 452.260, y: 626.650, width: 94.289, height: 17.668), CGRect(x: 295.610, y: 626.650, width: 98.711, height: 17.668), CGRect(x: 60.864, y: 626.546, width: 182.501, height: 17.795)],
        "rafidain_group": [CGRect(x: 452.260, y: 661.240, width: 94.289, height: 17.668), CGRect(x: 295.610, y: 661.240, width: 98.711, height: 17.668), CGRect(x: 70.464, y: 661.136, width: 163.445, height: 17.795)],
        "other_banks_group": [CGRect(x: 452.260, y: 661.240, width: 94.289, height: 17.668), CGRect(x: 295.610, y: 661.240, width: 98.711, height: 17.668), CGRect(x: 70.464, y: 661.136, width: 163.445, height: 17.795)],
        "cash_salary": [CGRect(x: 452.260, y: 629.890, width: 94.289, height: 17.668), CGRect(x: 295.610, y: 629.890, width: 98.711, height: 17.668), CGRect(x: 60.864, y: 629.786, width: 182.501, height: 17.795)],
        "food_tax": [CGRect(x: 452.260, y: 654.760, width: 94.289, height: 17.668), CGRect(x: 295.610, y: 654.760, width: 98.711, height: 17.668), CGRect(x: 60.864, y: 654.656, width: 182.501, height: 17.795)],
    ]
    static func prepare(id: String, custom: TransferLetterCustomization, page: PDFPage, height: CGFloat) throws -> Prepared {
        guard custom.header == nil, custom.footer == nil else { throw Failure.unsupportedField }
        let values = [custom.committeeFirst, custom.committeeSecond, custom.committeeThird]
        guard let originals = names[id], originals.count == 3 else { throw Failure.invalidTemplate }
        let columns: [(CGFloat, CGFloat)] = [(430, 568), (278, 412), (29, 275)]
        let fonts = ["TimesNewRomanPS-BoldMT", "TimesNewRomanPS-BoldMT", "Arial-BoldMT"]
        var masks: [CGRect] = []
        var flows: [TransferPDFTextFlow] = []
        for i in values.indices {
            guard let value = values[i] else { continue }
            let old = originals[i]
            let pdfRect = CGRect(x: old.minX - 0.2, y: height - old.maxY - 0.2, width: old.width + 0.4, height: old.height + 0.4)
            guard let found = page.selection(for: pdfRect)?.string,
                  !found.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { throw Failure.invalidTemplate }
            masks.append(old)
            if !value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                let slot = CGRect(x: columns[i].0, y: old.minY, width: columns[i].1 - columns[i].0, height: old.height)
                flows.append(TransferPDFTextFlow(value: value, slots: [TransferPDFTextSlot(rect: slot, fontName: fonts[i], fontSize: 16, alignment: .center)]))
            }
        }
        return Prepared(masks: masks, flows: flows)
    }
}
