import UIKit
import CoreText
import PDFKit

/// مواضع مُفرغة مسبقاً في مرجع موثوق؛ الإحداثيات من أعلى الصفحة مثل UIKit.
nonisolated struct TransferPDFTextSlot: Sendable {
    let rect: CGRect
    let fontName: String
    let fontSize: CGFloat
    let alignment: NSTextAlignment
    /// خط خاص يقرأ من بيانات المالك؛ nil يعني استخدام خط النظام المذكور أعلاه.
    var privateFontData: Data? = nil
    var privateFontStyle: TransferHeaderFont.Style? = nil
}

nonisolated struct TransferPDFTextFlow: Sendable {
    let value: String
    /// عند تعدد الأسطر تحدد المساحة المتاحة لكل سطر دون الكتابة فوق نص ثابت.
    let slots: [TransferPDFTextSlot]
    /// يحافظ على كلمة أخيرة بمحاذاة القوس الثابت في السطر الأخير من النموذج.
    var keepLastWordOnLastLine = false
}

nonisolated struct TransferPDFMonthSlot: Sendable {
    let rect: CGRect
    let month: Int
    let glyphs: [TransferMonthDigitGlyph]
    /// عند توفر الخط الأصلي تُرسم الأرقام منه؛ وإلا تستخدم صور أرقام المرجع.
    var fontData: Data? = nil
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
        let font: UIFont?
        let coreLine: CTLine?
        let x: CGFloat
        let baseline: CGFloat
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
    static func make(blankReference: Data, flows: [TransferPDFTextFlow], months: [TransferPDFMonthSlot] = []) async throws -> URL {
        try await PDFWork.run {
            guard let source = PDFDocument(data: blankReference), !source.isLocked, source.pageCount == 1,
                  let page = source.page(at: 0) else { throw Failure.invalidTemplate }
            let box = page.bounds(for: .mediaBox)
            guard abs(box.width - 595.3) < 2, abs(box.height - 841.9) < 2,
                  !flows.isEmpty || !months.isEmpty else { throw Failure.invalidTemplate }
            var occupied: [CGRect] = []
            var planned: [Line] = []
            var plannedMonths: [(rect: CGRect, images: [(UIImage, CGFloat, CGFloat)])] = []
            var resolvedFlows = flows
            var imageMonths: [TransferPDFMonthSlot] = []
            for month in months {
                guard (1...12).contains(month.month) else { throw Failure.invalidTemplate }
                if let fontData = month.fontData, !fontData.isEmpty {
                    let font = try TransferHeaderFont.make(data: fontData, style: .bold, size: 18)
                    let value = String(month.month)
                    try TransferHeaderFont.requireSupported(value, font: font)
                    let line = CTLineCreateWithAttributedString(NSAttributedString(string: value, attributes: [NSAttributedString.Key(kCTFontAttributeName as String): font]))
                    // قد تكون مساحة الرقم أضيق من عرض الخط الاسمي؛ أرقام المرجع بديل مطابق.
                    if CTLineGetTypographicBounds(line, nil, nil, nil) > Double(month.rect.width) {
                        imageMonths.append(month)
                        continue
                    }
                    var slot = TransferPDFTextSlot(rect: month.rect, fontName: "SakkalMajalla-Bold", fontSize: 18, alignment: .right)
                    slot.privateFontData = fontData
                    slot.privateFontStyle = .bold
                    resolvedFlows.append(TransferPDFTextFlow(value: value, slots: [slot]))
                } else { imageMonths.append(month) }
            }
            func checkSlot(_ rect: CGRect) throws {
                guard rect.width > 4, rect.height > 4, rect.minX >= 0, rect.minY >= 0,
                      rect.maxX <= box.width, rect.maxY <= box.height,
                      !occupied.contains(where: { $0.intersects(rect) }) else { throw Failure.unsafeSlot }
                occupied.append(rect)
                let pdfRect = CGRect(x: rect.minX, y: box.height - rect.maxY, width: rect.width, height: rect.height)
                if let existing = page.selection(for: pdfRect)?.string,
                   existing.unicodeScalars.contains(where: { !CharacterSet.whitespacesAndNewlines.contains($0) && !CharacterSet.controlCharacters.contains($0) }) { throw Failure.unsafeSlot }
            }
            for flow in resolvedFlows {
                guard !flow.value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, !flow.slots.isEmpty,
                      !flow.value.unicodeScalars.contains(where: { $0.value < 32 }) else { throw Failure.invalidTemplate }
                let value = flow.value as NSString
                var offset = 0
                let lastSpace = value.rangeOfCharacter(from: .whitespacesAndNewlines, options: .backwards)
                let lastWordStart = lastSpace.location == NSNotFound ? 0 : lastSpace.location + lastSpace.length
                for (slotIndex, slot) in flow.slots.enumerated() {
                    let rect = slot.rect
                    try checkSlot(rect)
                    let font: UIFont?
                    let privateFont: CTFont?
                    if let data = slot.privateFontData {
                        guard let style = slot.privateFontStyle else { throw Failure.missingFont }
                        privateFont = try TransferHeaderFont.make(data: data, style: style, size: slot.fontSize)
                        font = nil
                    } else {
                        guard slot.privateFontStyle == nil, let systemFont = UIFont(name: slot.fontName, size: slot.fontSize) else { throw Failure.missingFont }
                        font = systemFont
                        privateFont = nil
                    }
                    if offset == value.length { continue }
                    if flow.keepLastWordOnLastLine && slotIndex < flow.slots.count - 1 && offset >= lastWordStart { continue }
                    let rest = value.substring(from: offset)
                    var attributes: [NSAttributedString.Key: Any] = [NSAttributedString.Key(kCTParagraphStyleAttributeName as String): rtl]
                    if let font { attributes[.font] = font }
                    if let privateFont { attributes[NSAttributedString.Key(kCTFontAttributeName as String)] = privateFont }
                    let attributed = NSAttributedString(string: rest, attributes: attributes)
                    let typesetter = CTTypesetterCreateWithAttributedString(attributed)
                    var count = CTTypesetterSuggestLineBreak(typesetter, 0, Double(rect.width))
                    if flow.keepLastWordOnLastLine && slotIndex < flow.slots.count - 1 {
                        count = min(count, lastWordStart - offset)
                    }
                    guard count > 0 else { throw Failure.doesNotFit }
                    let line = value.substring(with: NSRange(location: offset, length: count))
                    if let privateFont { try TransferHeaderFont.requireSupported(line, font: privateFont) }
                    let measuredLine = CTLineCreateWithAttributedString(NSAttributedString(string: line, attributes: attributes))
                    let width = CTLineGetTypographicBounds(measuredLine, nil, nil, nil) - CTLineGetTrailingWhitespaceWidth(measuredLine)
                    guard width <= Double(rect.width) else { throw Failure.doesNotFit }
                    // نتحقق من حدود الحروف الفعلية بدلاً من ارتفاع الخط الاسمي الذي يشمل فراغاً إضافياً.
                    let ink = CTLineGetBoundsWithOptions(measuredLine, [.useGlyphPathBounds])
                    let flush: CGFloat = slot.alignment == .left ? 0 : slot.alignment == .center ? 0.5 : 1
                    let x = rect.minX + CGFloat(CTLineGetPenOffsetForFlush(measuredLine, flush, Double(rect.width)))
                    let lineHeight = font?.lineHeight ?? (CTFontGetAscent(privateFont!) + CTFontGetDescent(privateFont!) + CTFontGetLeading(privateFont!))
                    let ascent = font?.ascender ?? CTFontGetAscent(privateFont!)
                    let baseline = rect.midY - lineHeight / 2 + ascent
                    guard x + ink.minX >= rect.minX - 0.5, x + ink.maxX <= rect.maxX + 0.5,
                          baseline - ink.maxY >= rect.minY - 0.5, baseline - ink.minY <= rect.maxY + 0.5 else { throw Failure.doesNotFit }
                    planned.append(Line(text: line, slot: slot, font: font, coreLine: privateFont == nil ? nil : measuredLine, x: x, baseline: baseline))
                    offset += count
                }
                guard offset == value.length else { throw Failure.doesNotFit }
            }
            for month in imageMonths {
                try checkSlot(month.rect)
                guard (1...12).contains(month.month), month.glyphs.count == 10 else { throw Failure.invalidTemplate }
                let indexes = String(month.month).compactMap(\.wholeNumberValue)
                let glyphs = indexes.map { month.glyphs[$0] }
                guard glyphs.allSatisfy({ !$0.png.isEmpty && $0.width > 0 && $0.height > 0 && $0.height <= month.rect.height + 1 }),
                      glyphs.reduce(CGFloat.zero, { $0 + $1.width }) <= month.rect.width + 0.5 else { throw Failure.doesNotFit }
                var images: [(UIImage, CGFloat, CGFloat)] = []
                for glyph in glyphs {
                    guard let image = UIImage(data: glyph.png), image.cgImage != nil else { throw Failure.invalidTemplate }
                    images.append((image, glyph.width, glyph.height))
                }
                plannedMonths.append((month.rect, images))
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
                    if let font = line.font {
                        PDFText.draw(line.text, in: line.slot.rect, font: font, align: line.slot.alignment)
                    } else if let coreLine = line.coreLine {
                        cg.saveGState()
                        cg.textMatrix = CGAffineTransform(scaleX: 1, y: -1)
                        cg.textPosition = CGPoint(x: line.x, y: line.baseline)
                        CTLineDraw(coreLine, cg)
                        cg.restoreGState()
                        cg.textMatrix = .identity
                    }
                }
                for month in plannedMonths {
                    var x = month.rect.maxX - month.images.reduce(CGFloat.zero) { $0 + $1.1 }
                    for (image, width, height) in month.images {
                        image.draw(in: CGRect(x: x, y: month.rect.minY, width: width, height: height))
                        x += width
                    }
                }
            }
            try FileManager.default.setAttributes([.protectionKey: FileProtectionType.complete], ofItemAtPath: url.path)
            finished = true
            return url
        }
    }
}
