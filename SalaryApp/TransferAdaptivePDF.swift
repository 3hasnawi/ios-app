import UIKit
import CoreText
import PDFKit

/// مسودة كتاب A4 مرنة؛ تقيس السطور والخلايا قبل الرسم وترفض قصّ المحتوى.
/// لا تعني اجتياز التحقق على الجهاز أو السماح بالمشاركة.
nonisolated enum TransferAdaptivePDF {
    enum Failure: LocalizedError {
        case invalid, noFont, doesNotFit, damaged
        var errorDescription: String? {
            switch self {
            case .invalid: tr("بيانات الكتاب غير مكتملة.", "The letter data is incomplete.")
            case .noFont: tr("استورد خط ساكال العريض والشعار قبل إعداد الكتاب.", "Import the bold Sakkal font and logo before preparing the letter.")
            case .doesNotFit: tr("لا يتسع أحد الحقول أو جدول المجموعة بوضوح؛ راجع البيانات.", "A field or the group table does not fit legibly; review the data.")
            case .damaged: tr("تعذّر التحقق من صفحات مسودة الكتاب.", "The letter draft pages could not be verified.")
            }
        }
    }
    private static let page = CGRect(x: 0, y: 0, width: 595.3, height: 841.9)
    private static let margin: CGFloat = 38
    private static let bottom: CGFloat = 803
    private static let contentWidth: CGFloat = 519.3
    private static let rtl: CTParagraphStyle = {
        let direction = CTWritingDirection.rightToLeft
        return withUnsafeBytes(of: direction) { raw in
            var setting = CTParagraphStyleSetting(spec: .baseWritingDirection,
                valueSize: MemoryLayout<CTWritingDirection>.size, value: raw.baseAddress!)
            return CTParagraphStyleCreate(&setting, 1)
        }
    }()
    private struct TextLine {
        let text: String
        let font: CTFont
        let height: CGFloat
        let ascent: CGFloat
    }
    private enum Draw {
        case line(TextLine, CGRect, CGFloat)
        case box(CGRect, Bool)
        case logo(CGRect)
    }
    private static func font(_ paragraph: TransferWordDocument.Paragraph, sakkal: Data?) throws -> CTFont {
        let size = CGFloat(paragraph.halfPoints) / 2
        guard size >= 6, size <= 48 else { throw Failure.invalid }
        if paragraph.font == "Sakkal Majalla" {
            guard let sakkal else { throw Failure.noFont }
            let face = try TransferHeaderFont.make(data: sakkal, style: .bold, size: size)
            try TransferHeaderFont.requireSupported(paragraph.text.filter { !$0.isNewline }, font: face)
            return face
        }
        let name: String
        if paragraph.font == "Arial" { name = paragraph.bold ? "Arial-BoldMT" : "ArialMT" }
        else { name = paragraph.bold ? "TimesNewRomanPS-BoldMT" : "TimesNewRomanPSMT" }
        guard let face = UIFont(name: name, size: size) else { throw Failure.noFont }
        return CTFontCreateWithName(face.fontName as CFString, size, nil)
    }
    private static func lines(_ text: String, font: CTFont, width: CGFloat) throws -> [TextLine] {
        guard text.utf8.count <= 100_000, width > 8 else { throw Failure.invalid }
        if text.isEmpty { return [] }
        let attr = NSAttributedString(string: text, attributes: [
            NSAttributedString.Key(kCTFontAttributeName as String): font,
            NSAttributedString.Key(kCTParagraphStyleAttributeName as String): rtl
        ])
        let typesetter = CTTypesetterCreateWithAttributedString(attr)
        let height = ceil(CTFontGetAscent(font) + CTFontGetDescent(font) + CTFontGetLeading(font)) + 3
        var result: [TextLine] = [], offset = 0
        while offset < attr.length {
            if result.count.isMultiple(of: 128) { try Task.checkCancellation() }
            let count = CTTypesetterSuggestLineBreak(typesetter, offset, Double(width))
            guard count > 0, count <= attr.length - offset else { throw Failure.doesNotFit }
            let line = CTTypesetterCreateLine(typesetter, CFRange(location: offset, length: count))
            let measured = CTLineGetTypographicBounds(line, nil, nil, nil) - CTLineGetTrailingWhitespaceWidth(line)
            guard measured <= Double(width) + 0.5 else { throw Failure.doesNotFit }
            let value = (text as NSString).substring(with: NSRange(location: offset, length: count))
            result.append(TextLine(text: value, font: font, height: height, ascent: CTFontGetAscent(font)))
            guard result.count <= 20_000 else { throw Failure.doesNotFit }
            offset += count
        }
        return result
    }
    static func write(blocks: [TransferWordDocument.Block], destination: URL,
                      sakkalBold: Data?, group: Bool) throws {
        guard blocks.count >= 7, !FileManager.default.fileExists(atPath: destination.path),
              case let .paragraph(header) = blocks[0], case let .paragraph(reference) = blocks[1],
              let logo = UIImage(named: "HomeLogo")?.cgImage else { throw Failure.invalid }
        let headerFont = try font(header, sakkal: sakkalBold)
        let refFont = try font(reference, sakkal: sakkalBold)
        let headerLines = try lines(header.text, font: headerFont, width: contentWidth - 86)
        let refLines = try lines(reference.text, font: refFont, width: contentWidth - 86)
        let fixedHeight = (headerLines + refLines).reduce(CGFloat.zero) { $0 + $1.height }
        guard !headerLines.isEmpty, !refLines.isEmpty, fixedHeight <= 205 else { throw Failure.doesNotFit }
        var pages: [[Draw]] = [[]]
        var y: CGFloat = margin
        // يترك المتن مساحة ثابتة للتواقيع والنسخ أسفل صفحة الكتاب.
        var limit: CGFloat = 640
        var expectingFooter = false
        func add(_ operation: Draw) { pages[pages.count - 1].append(operation) }
        func nextPage() throws {
            guard pages.count < 4000 else { throw Failure.doesNotFit }
            pages.append([]); y = margin
        }
        func addLines(_ items: [TextLine], x: CGFloat, width: CGFloat, flush: CGFloat) throws {
            for line in items {
                if y + line.height > limit { try nextPage() }
                add(.line(line, CGRect(x: x, y: y, width: width, height: line.height), flush))
                y += line.height
            }
        }
        add(.logo(CGRect(x: margin, y: margin, width: 66, height: 66)))
        try addLines(headerLines, x: margin + 86, width: contentWidth - 86, flush: 1)
        try addLines(refLines, x: margin + 86, width: contentWidth - 86, flush: 1)
        y = max(y, margin + 72) + 10
        var afterBreak = false
        for block in blocks.dropFirst(2) {
            try Task.checkCancellation()
            switch block {
            case .pageBreak:
                guard !expectingFooter else { throw Failure.invalid }
                try nextPage(); limit = bottom; afterBreak = true
            case .paragraph(let p):
                let face = try font(p, sakkal: sakkalBold)
                let ls = try lines(p.text, font: face, width: contentWidth)
                if expectingFooter {
                    y = 741; limit = bottom
                    guard y + ls.reduce(CGFloat.zero, { $0 + $1.height }) <= bottom else { throw Failure.doesNotFit }
                    expectingFooter = false
                }
                try addLines(ls, x: margin, width: contentWidth, flush: p.centered ? 0.5 : 1)
                if !ls.isEmpty { y += 6 }
            case .table(let table):
                let count = table.headers.isEmpty ? (table.rows.first?.count ?? 0) : table.headers.count
                let weights = table.columnWeights ?? Array(repeating: 1, count: count)
                guard count > 0, count <= 12, weights.count == count, weights.allSatisfy({ $0 > 0 }),
                      table.rows.allSatisfy({ $0.count == count }), table.rows.count < 50_000 else { throw Failure.invalid }
                let sum = weights.reduce(0, +)
                let widths = weights.map { contentWidth * CGFloat($0) / CGFloat(sum) }
                guard widths.allSatisfy({ $0 > 10 }) else { throw Failure.doesNotFit }
                let face = try font(.init(text: "", font: table.font, halfPoints: table.halfPoints), sakkal: sakkalBold)
                let boldFace = try font(.init(text: "", font: table.font, halfPoints: table.halfPoints, bold: true), sakkal: sakkalBold)
                func layout(_ values: [String], bold: Bool) throws -> ([[TextLine]], CGFloat) {
                    guard values.count == count else { throw Failure.invalid }
                    let cells = try zip(values, widths).map { try lines($0.0, font: bold ? boldFace : face, width: $0.1 - 8) }
                    let height = max(ceil(CGFloat(table.halfPoints) / 2 * 1.3) + 6,
                                     (cells.map { $0.reduce(CGFloat.zero) { $0 + $1.height } }.max() ?? 0) + 6)
                    guard height <= bottom - margin else { throw Failure.doesNotFit }
                    return (cells, height)
                }
                func drawRow(_ values: [String], bold: Bool) throws {
                    let (cells, height) = try layout(values, bold: bold)
                    guard y + height <= limit else { throw Failure.doesNotFit }
                    var right = page.width - margin
                    for index in 0..<count {
                        right -= widths[index]
                        let rect = CGRect(x: right, y: y, width: widths[index], height: height)
                        if table.showBorders { add(.box(rect, bold)) }
                        var baseline = y + (height - cells[index].reduce(CGFloat.zero) { $0 + $1.height }) / 2
                        for line in cells[index] {
                            add(.line(line, CGRect(x: right + 4, y: baseline, width: widths[index] - 8, height: line.height), 0.5))
                            baseline += line.height
                        }
                    }
                    y += height
                }
                if !table.showBorders {
                    guard table.rows.count == 1 else { throw Failure.invalid }
                    let height = try layout(table.rows[0], bold: false).1
                    guard height <= 72 else { throw Failure.doesNotFit }
                    y = 662; limit = 735
                    try drawRow(table.rows[0], bold: false)
                    expectingFooter = true
                    continue
                }
                let groupTable = group && afterBreak
                if groupTable {
                    var required: CGFloat = 0
                    if !table.headers.isEmpty { required += try layout(table.headers, bold: true).1 }
                    for (i, row) in table.rows.enumerated() {
                        if i.isMultiple(of: 128) { try Task.checkCancellation() }
                        required += try layout(row, bold: table.boldLastRow && i == table.rows.count - 1).1
                    }
                    guard y + required <= limit else { throw Failure.doesNotFit }
                }
                if !table.headers.isEmpty {
                    let h = try layout(table.headers, bold: true).1
                    if y + h > limit { try nextPage() }
                    try drawRow(table.headers, bold: true)
                }
                for (index, row) in table.rows.enumerated() {
                    if index.isMultiple(of: 128) { try Task.checkCancellation() }
                    let bold = table.boldLastRow && index == table.rows.count - 1
                    let height = try layout(row, bold: bold).1
                    if y + height > limit {
                        guard !groupTable else { throw Failure.doesNotFit }
                        try nextPage()
                        if !table.headers.isEmpty { try drawRow(table.headers, bold: true) }
                    }
                    try drawRow(row, bold: bold)
                }
                y += 8
            }
        }
        guard !expectingFooter else { throw Failure.invalid }
        let fm = FileManager.default
        var complete = false
        defer { if !complete { try? fm.removeItem(at: destination) } }
        try UIGraphicsPDFRenderer(bounds: page).writePDF(to: destination) { context in
            let cg = context.cgContext
            for operations in pages {
                if Task.isCancelled { return }
                context.beginPage()
                for (index, operation) in operations.enumerated() {
                    if index.isMultiple(of: 256), Task.isCancelled { return }
                    switch operation {
                    case .logo(let rect):
                        cg.saveGState(); cg.translateBy(x: rect.minX, y: rect.maxY); cg.scaleBy(x: 1, y: -1)
                        cg.draw(logo, in: CGRect(origin: .zero, size: rect.size)); cg.restoreGState()
                    case .box(let rect, let shaded):
                        if shaded { UIColor(white: 0.94, alpha: 1).setFill(); cg.fill(rect) }
                        UIColor.black.setStroke(); cg.stroke(rect, width: 0.6)
                    case .line(let item, let rect, let flush):
                        let text = NSAttributedString(string: item.text, attributes: [
                            NSAttributedString.Key(kCTFontAttributeName as String): item.font,
                            NSAttributedString.Key(kCTParagraphStyleAttributeName as String): rtl
                        ])
                        let line = CTLineCreateWithAttributedString(text)
                        cg.saveGState(); cg.textMatrix = CGAffineTransform(scaleX: 1, y: -1)
                        cg.textPosition = CGPoint(x: rect.minX + CGFloat(CTLineGetPenOffsetForFlush(line, flush, Double(rect.width))),
                                                  y: rect.minY + item.ascent)
                        CTLineDraw(line, cg); cg.restoreGState(); cg.textMatrix = .identity
                    }
                }
            }
        }
        try Task.checkCancellation()
        guard let document = PDFDocument(url: destination), !document.isLocked, document.pageCount == pages.count,
              (0..<document.pageCount).allSatisfy({ index in
                  guard let page = document.page(at: index), let text = page.string, !text.isEmpty else { return false }
                  return abs(page.bounds(for: .mediaBox).width - Self.page.width) < 2 && abs(page.bounds(for: .mediaBox).height - Self.page.height) < 2
              }) else { throw Failure.damaged }
        try fm.setAttributes([.protectionKey: FileProtectionType.complete], ofItemAtPath: destination.path)
        complete = true
    }
    /// يفحص القيم الرقمية والحسابات من نص PDF الناتج قبل عرض المسودة.
    static func verify(_ url: URL, required: [String]) throws {
        guard let document = PDFDocument(url: url), !document.isLocked, document.pageCount > 0 else { throw Failure.damaged }
        func key(_ value: String) -> String {
            ArabicText.latinDigits(value).uppercased().filter { $0.isASCII && ($0.isLetter || $0.isNumber) }
        }
        var text = "", tokens = Set<String>()
        for index in 0..<document.pageCount {
            try Task.checkCancellation()
            guard let page = document.page(at: index), let value = page.string else { throw Failure.damaged }
            let normalized = ArabicText.latinDigits(value).uppercased()
            for word in normalized.components(separatedBy: CharacterSet.alphanumerics.inverted) {
                let token = key(word)
                if !token.isEmpty { tokens.insert(token) }
            }
            text += key(value)
        }
        for (index, value) in required.enumerated() {
            if index.isMultiple(of: 256) { try Task.checkCancellation() }
            let expected = key(value)
            // المبلغ الأساسي قد يحتوي فواصل؛ قيم الجدول تُقرأ كمفردات مستقلة.
            guard !expected.isEmpty, tokens.contains(expected) || (index < 3 && text.contains(expected)) else { throw Failure.damaged }
        }
    }

}
