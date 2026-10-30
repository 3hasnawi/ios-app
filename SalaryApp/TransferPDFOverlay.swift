import UIKit
import CoreText
import PDFKit

/// مواضع مُفرغة مسبقاً في مرجع موثوق؛ الإحداثيات من أعلى الصفحة مثل UIKit.
nonisolated struct TransferPDFTextSlot: Sendable {
    let rect: CGRect
    let fontName: String
    let fontSize: CGFloat
    let alignment: NSTextAlignment
}

nonisolated struct TransferPDFTextFlow: Sendable {
    let value: String
    /// عند تعدد الأسطر تحدد المساحة المتاحة لكل سطر دون الكتابة فوق نص ثابت.
    let slots: [TransferPDFTextSlot]
}

nonisolated enum TransferPDFOverlay {
    enum Failure: LocalizedError {
        case invalidTemplate, missingFont, unsafeSlot, doesNotFit
        var errorDescription: String? {
            switch self {
            case .invalidTemplate: tr("مرجع الكتاب غير صالح.", "The letter reference is invalid.")
            case .missingFont: tr("خط الكتاب المطلوب غير متوفر على الجهاز.", "The required letter font is unavailable on this device.")
            case .unsafeSlot: tr("تعذّر التأكد من خلو موضع البيانات القديمة في القالب.", "Could not verify that old data was removed from the template.")
            case .doesNotFit: tr("النص الجديد لا يتسع في موضعه دون تغيير تنسيق الكتاب.", "The new text does not fit without changing the letter layout.")
            }
        }
    }
    private struct Line {
        let text: String
        let slot: TransferPDFTextSlot
        let font: UIFont
    }
    private static let rtl: CTParagraphStyle = {
        let direction = CTWritingDirection.rightToLeft
        return withUnsafeBytes(of: direction) { raw in
            var setting = CTParagraphStyleSetting(spec: .baseWritingDirection,
                valueSize: MemoryLayout<CTWritingDirection>.size, value: raw.baseAddress!)
            return CTParagraphStyleCreate(&setting, 1)
        }
    }()
    /// ترفض القالب غير المفرغ، وخطاً بديلاً، وأي قيمة لا تدخل كاملة في المواضع المحددة.
    static func make(blankReference: Data, flows: [TransferPDFTextFlow]) async throws -> URL {
        try await PDFWork.run {
            guard let source = PDFDocument(data: blankReference), !source.isLocked, source.pageCount == 1,
                  let page = source.page(at: 0) else { throw Failure.invalidTemplate }
            let box = page.bounds(for: .mediaBox)
            guard abs(box.width - 595.3) < 2, abs(box.height - 841.9) < 2, !flows.isEmpty else { throw Failure.invalidTemplate }
            var occupied: [CGRect] = []
            var planned: [Line] = []
            for flow in flows {
                guard !flow.value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, !flow.slots.isEmpty,
                      !flow.value.unicodeScalars.contains(where: { $0.value < 32 }) else { throw Failure.invalidTemplate }
                let value = flow.value as NSString
                var offset = 0
                for slot in flow.slots {
                    let rect = slot.rect
                    guard rect.width > 4, rect.height > 4, rect.minX >= 0, rect.minY >= 0,
                          rect.maxX <= box.width, rect.maxY <= box.height,
                          !occupied.contains(where: { $0.intersects(rect) }) else { throw Failure.unsafeSlot }
                    occupied.append(rect)
                    let pdfRect = CGRect(x: rect.minX, y: box.height - rect.maxY, width: rect.width, height: rect.height)
                    if let existing = page.selection(for: pdfRect)?.string,
                       existing.unicodeScalars.contains(where: { CharacterSet.alphanumerics.contains($0) }) { throw Failure.unsafeSlot }
                    guard let font = UIFont(name: slot.fontName, size: slot.fontSize) else { throw Failure.missingFont }
                    guard font.lineHeight <= rect.height else { throw Failure.doesNotFit }
                    if offset == value.length { continue }
                    let rest = value.substring(from: offset)
                    let attributes: [NSAttributedString.Key: Any] = [
                        .font: font, NSAttributedString.Key(kCTParagraphStyleAttributeName as String): rtl
                    ]
                    let attributed = NSAttributedString(string: rest, attributes: attributes)
                    let typesetter = CTTypesetterCreateWithAttributedString(attributed)
                    let count = CTTypesetterSuggestLineBreak(typesetter, 0, Double(rect.width))
                    guard count > 0 else { throw Failure.doesNotFit }
                    let line = value.substring(with: NSRange(location: offset, length: count))
                    let measuredLine = CTLineCreateWithAttributedString(NSAttributedString(string: line, attributes: attributes))
                    let width = CTLineGetTypographicBounds(measuredLine, nil, nil, nil) - CTLineGetTrailingWhitespaceWidth(measuredLine)
                    guard width <= Double(rect.width) else { throw Failure.doesNotFit }
                    planned.append(Line(text: line, slot: slot, font: font))
                    offset += count
                }
                guard offset == value.length else { throw Failure.doesNotFit }
            }
            let folder = FileManager.default.temporaryDirectory.appendingPathComponent("letter-pdf-\(UUID().uuidString)", isDirectory: true)
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            let url = folder.appendingPathComponent("كتاب تحويل.pdf")
            var finished = false
            defer { if !finished { try? FileManager.default.removeItem(at: folder) } }
            let renderer = UIGraphicsPDFRenderer(bounds: CGRect(origin: .zero, size: box.size))
            try renderer.writePDF(to: url) { context in
                context.beginPage()
                let cg = context.cgContext
                cg.saveGState()
                cg.translateBy(x: 0, y: box.height)
                cg.scaleBy(x: 1, y: -1)
                page.draw(with: .mediaBox, to: cg)
                cg.restoreGState()
                for line in planned {
                    PDFText.draw(line.text, in: line.slot.rect, font: line.font, align: line.slot.alignment)
                }
            }
            finished = true
            return url
        }
    }
}
