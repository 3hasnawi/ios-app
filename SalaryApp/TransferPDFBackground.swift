import UIKit
import PDFKit
import CryptoKit

/// يجهز صفحة مرجعية دون تضمين نص الشهر السابق مخفياً في ملف PDF الجديد.
nonisolated enum TransferPDFBackground {
    enum Failure: LocalizedError {
        case invalidReference, damagedReference, invalidMask
        var errorDescription: String? {
            switch self {
            case .invalidReference: tr("مرجع الكتاب غير صالح.", "The letter reference is invalid.")
            case .damagedReference: tr("بصمة مرجع الكتاب لا تطابق الحزمة.", "The letter reference hash does not match the package.")
            case .invalidMask: tr("مواضع الحقول المتغيرة في الكتاب غير صالحة.", "The letter's variable field positions are invalid.")
            }
        }
    }
    /// تُعتمد المواضع فقط مع بصمة المرجع نفسه؛ الصورة الناتجة لا تحتفظ بطبقة النص القديمة.
    static func make(reference: Data, sha256: String, pageIndex: Int, masks: [CGRect]) async throws -> Data {
        try await PDFWork.run {
            let digest = SHA256.hash(data: reference).map { String(format: "%02x", $0) }.joined()
            guard digest == sha256.lowercased() else { throw Failure.damagedReference }
            guard let document = PDFDocument(data: reference), !document.isLocked,
                  pageIndex >= 0, pageIndex < document.pageCount, let page = document.page(at: pageIndex) else { throw Failure.invalidReference }
            let box = page.bounds(for: .mediaBox)
            guard abs(box.width - 595.3) < 2, abs(box.height - 841.9) < 2,
                  !masks.isEmpty, masks.count <= 200 else { throw Failure.invalidMask }
            for rect in masks {
                guard rect.minX.isFinite, rect.minY.isFinite, rect.width.isFinite, rect.height.isFinite,
                      rect.width > 0, rect.height > 0, rect.minX >= 0, rect.minY >= 0,
                      rect.maxX <= box.width, rect.maxY <= box.height else { throw Failure.invalidMask }
            }
            // عمل المستخدم فقط، صفحة واحدة في الذاكرة، بدقة طباعة 288 نقطة/بوصة.
            let format = UIGraphicsImageRendererFormat()
            format.scale = 4
            format.opaque = true
            let size = box.size
            let image = UIGraphicsImageRenderer(size: size, format: format).image { context in
                let cg = context.cgContext
                UIColor.white.setFill(); cg.fill(CGRect(origin: .zero, size: size))
                cg.saveGState()
                cg.translateBy(x: 0, y: size.height)
                cg.scaleBy(x: 1, y: -1)
                page.draw(with: .mediaBox, to: cg)
                cg.restoreGState()
                UIColor.white.setFill()
                for rect in masks { cg.fill(rect) }
            }
            let renderer = UIGraphicsPDFRenderer(bounds: CGRect(origin: .zero, size: size))
            return renderer.pdfData { context in
                context.beginPage()
                image.draw(in: CGRect(origin: .zero, size: size))
            }
        }
    }
}
