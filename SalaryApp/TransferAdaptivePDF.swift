import UIKit
import CoreText
import PDFKit

/// مسودة كتاب A4 مرنة؛ تقيس السطور والخلايا قبل الرسم وترفض قصّ المحتوى.
/// لا تعني اجتياز التحقق على الجهاز أو السماح بالمشاركة.
nonisolated enum TransferAdaptivePDF {
    enum Failure: LocalizedError {
        case invalid, noFont, doesNotFit, groupTableDoesNotFit, signatureDoesNotFit, damaged
        var errorDescription: String? {
            switch self {
            case .invalid: tr("بيانات الكتاب غير مكتملة.", "The letter data is incomplete.")
            case .noFont: tr("استورد خط ساكال العريض والشعار قبل إعداد الكتاب.", "Import the bold Sakkal font and logo before preparing the letter.")
            case .doesNotFit: tr("لا يتسع أحد الحقول أو جدول المجموعة بوضوح؛ راجع البيانات.", "A field or the group table does not fit legibly; review the data.")
            case .groupTableDoesNotFit: tr("جدول المجموعة لا يتسع في صفحة واحدة بوضوح؛ راجع أطوال الأسماء وعدد الجهات.", "The group table does not fit legibly on one page; review entity names and count.")
            case .signatureDoesNotFit: tr("أحد أسماء اللجنة أو صفاتها لا يتسع في سطر التوقيع؛ راجع أسماء اللجنة.", "A committee name or title does not fit on its signature line; review the names.")
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
        let underline: Bool
        let justify: Bool
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
    private static func lines(_ text: String, font: CTFont, width: CGFloat, underline: Bool = false, justify: Bool = false, spacing: CGFloat = 3) throws -> [TextLine] {
        guard text.utf8.count <= 100_000, width > 8 else { throw Failure.invalid }
        if text.isEmpty { return [] }
        let attr = NSAttributedString(string: text, attributes: [
            NSAttributedString.Key(kCTFontAttributeName as String): font,
            NSAttributedString.Key(kCTParagraphStyleAttributeName as String): rtl
        ])
        let typesetter = CTTypesetterCreateWithAttributedString(attr)
        let height = ceil(CTFontGetAscent(font) + CTFontGetDescent(font) + CTFontGetLeading(font)) + spacing
        var result: [TextLine] = [], offset = 0
        while offset < attr.length {
            if result.count.isMultiple(of: 128) { try Task.checkCancellation() }
            let count = CTTypesetterSuggestLineBreak(typesetter, offset, Double(width))
            guard count > 0, count <= attr.length - offset else { throw Failure.doesNotFit }
            let line = CTTypesetterCreateLine(typesetter, CFRange(location: offset, length: count))
            let measured = CTLineGetTypographicBounds(line, nil, nil, nil) - CTLineGetTrailingWhitespaceWidth(line)
            guard measured <= Double(width) + 0.5 else { throw Failure.doesNotFit }
            let value = (text as NSString).substring(with: NSRange(location: offset, length: count))
            result.append(TextLine(text: value, font: font, underline: underline,
                                   justify: justify && offset + count < attr.length && !value.hasSuffix("\n"),
                                   height: height, ascent: CTFontGetAscent(font)))
            guard result.count <= 20_000 else { throw Failure.doesNotFit }
            offset += count
        }
        return result
    }
    static func write(blocks: [TransferWordDocument.Block], destination: URL,
                      sakkalBold: Data?, group: Bool) throws {
        guard blocks.count >= 7, !FileManager.default.fileExists(atPath: destination.path),
              case let .paragraph(header) = blocks[0], case let .paragraph(reference) = blocks[1],
              let logo = UIImage(named: "IraqEmblem")?.cgImage else { throw Failure.invalid }
        let headerFont = try font(header, sakkal: sakkalBold)
        let refFont = try font(reference, sakkal: sakkalBold)
        let headingWidth: CGFloat = 190
        let headerLines = try lines(header.text, font: headerFont, width: headingWidth)
        let refLines = try lines(reference.text, font: refFont, width: headingWidth)
        guard !headerLines.isEmpty, !refLines.isEmpty,
              headerLines.reduce(CGFloat.zero, { $0 + $1.height }) <= 123,
              refLines.reduce(CGFloat.zero, { $0 + $1.height }) <= 58 else { throw Failure.doesNotFit }
        var pages: [[Draw]] = [[]]
        var y: CGFloat = 23
        // يترك المتن مساحة ثابتة للتواقيع والنسخ أسفل صفحة الكتاب.
        var limit: CGFloat = 590
        var expectingFooter = false
        var signatureEnd: CGFloat?
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
        // مواضع ثابتة مأخوذة من هيئة النماذج: الترويسة يساراً وشعار الجمهورية وسط الصفحة.
        add(.logo(CGRect(x: 271.2, y: 36.1, width: 86.4, height: 107.9)))
        try addLines(headerLines, x: 28, width: headingWidth, flush: 0.5)
        y = max(150, y + 8)
        try addLines(refLines, x: 28, width: headingWidth, flush: 1)
        y = max(220, y + 12)
        var afterBreak = false
        var needsClosing = false
        func addClosing() throws {
            let closing = TransferWordDocument.Paragraph(text: "للتفضل بالاطلاع وإجراء اللازم وإعلامنا... مع التقدير.", font: "Times New Roman", halfPoints: 32, bold: true, centered: true)
            let closingFont = try font(closing, sakkal: sakkalBold)
            y += 15
            try addLines(lines(closing.text, font: closingFont, width: contentWidth), x: margin, width: contentWidth, flush: 0.5)
            needsClosing = false
        }
        for (blockIndex, block) in blocks.dropFirst(2).enumerated() {
            try Task.checkCancellation()
            switch block {
            case .pageBreak:
                guard !expectingFooter else { throw Failure.invalid }
                try nextPage(); limit = bottom; afterBreak = true
            case .paragraph(let p):
                if needsClosing && p.text.hasPrefix("المرفقات:") { try addClosing() }
                if blockIndex == 2 {
                    y += 18
                    let salutation = TransferWordDocument.Paragraph(text: "تحية طيبة ..", font: "Times New Roman", halfPoints: 28, bold: true)
                    let salutationFont = try font(salutation, sakkal: sakkalBold)
                    try addLines(lines(salutation.text, font: salutationFont, width: contentWidth), x: margin, width: contentWidth, flush: 1)
                    y += 7; needsClosing = true
                }
                let face = try font(p, sakkal: sakkalBold)
                let ls = try lines(p.text, font: face, width: contentWidth,
                                   underline: p.centered && p.text.hasPrefix("م /"),
                                   justify: blockIndex == 2,
                                   spacing: expectingFooter ? 0 : 3)
                if expectingFooter {
                    let footerHeight = ls.reduce(CGFloat.zero, { $0 + $1.height })
                    let footerBottom: CGFloat = 832
                    guard let signatureEnd, footerHeight <= footerBottom - margin else { throw Failure.doesNotFit }
                    y = footerBottom - footerHeight; limit = footerBottom
                    guard y >= signatureEnd + 8 else { throw Failure.doesNotFit }
                    expectingFooter = false
                }
                try addLines(ls, x: margin, width: contentWidth, flush: p.centered ? 0.5 : 1)
                if !ls.isEmpty { y += 6 }
            case .table(let table):
                if needsClosing && table.showBorders && !afterBreak {
                    try addClosing()
                    y += 18 // فراغ سطر قبل الجدول.
                }
                let count = table.headers.isEmpty ? (table.rows.first?.count ?? 0) : table.headers.count
                let weights = table.columnWeights ?? Array(repeating: 1, count: count)
                guard count > 0, count <= 12, weights.count == count, weights.allSatisfy({ $0 > 0 }),
                      table.rows.allSatisfy({ $0.count == count }), table.rows.count < 50_000 else { throw Failure.invalid }
                guard table.widthFraction > 0, table.widthFraction <= 1 else { throw Failure.invalid }
                let sum = weights.reduce(0, +)
                let tableWidth = contentWidth * CGFloat(table.widthFraction)
                let widths = weights.map { tableWidth * CGFloat($0) / CGFloat(sum) }
                guard widths.allSatisfy({ $0 > 10 }) else { throw Failure.doesNotFit }
                let tableRight = table.alignRight ? page.width - margin : margin + (contentWidth + tableWidth) / 2
                let groupTable = group && afterBreak
                let signature = !table.showBorders
                var chosenHalfPoints = table.halfPoints
                var fonts: [Int: CTFont] = [:]
                func layout(_ values: [String], bold: Bool, halfPoints: Int) throws -> ([[TextLine]], CGFloat) {
                    guard values.count == count else { throw Failure.invalid }
                    let fontKey = halfPoints * 2 + (bold ? 1 : 0)
                    let cellFont: CTFont
                    if let cached = fonts[fontKey] { cellFont = cached }
                    else {
                        cellFont = try font(.init(text: "", font: table.font, halfPoints: halfPoints, bold: bold), sakkal: sakkalBold)
                        fonts[fontKey] = cellFont
                    }
                    let spacing: CGFloat = groupTable ? 0.5 : (signature ? 1 : 3)
                    let padding: CGFloat = groupTable ? 2 : 6
                    var cells: [[TextLine]] = []
                    var tallest: CGFloat = 0
                    for index in values.indices {
                        let cell: [TextLine]
                        if signature {
                            let parts = values[index].components(separatedBy: "\n")
                            guard parts.count == 4 else { throw Failure.signatureDoesNotFit }
                            var singleLines: [TextLine] = []
                            for part in parts {
                                let measured = try lines(part, font: cellFont, width: widths[index] - 8, spacing: spacing)
                                guard measured.count == 1 else { throw Failure.signatureDoesNotFit }
                                singleLines.append(contentsOf: measured)
                            }
                            cell = singleLines
                        } else {
                            cell = try lines(values[index], font: cellFont, width: widths[index] - 8, spacing: spacing)
                        }
                        tallest = max(tallest, cell.reduce(CGFloat.zero) { $0 + $1.height })
                        cells.append(cell)
                    }
                    let minimumHeight = ceil(CGFloat(halfPoints) / 2 * 1.3) + padding
                    let height = max(minimumHeight, tallest + padding)
                    guard height <= bottom - margin else { throw Failure.doesNotFit }
                    return (cells, height)
                }
                func drawRow(_ values: [String], bold: Bool) throws {
                    let (cells, height) = try layout(values, bold: bold, halfPoints: chosenHalfPoints)
                    guard y + height <= limit else { throw Failure.doesNotFit }
                    var right = tableRight
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
                if signature {
                    guard table.rows.count == 1 else { throw Failure.invalid }
                    if needsClosing { try addClosing() }
                    var found = false
                    for size in stride(from: table.halfPoints, through: 24, by: -1) {
                        do {
                            let height = try layout(table.rows[0], bold: true, halfPoints: size).1
                            if height <= 125 { chosenHalfPoints = size; found = true; break }
                        } catch Failure.signatureDoesNotFit { continue }
                    }
                    guard found else { throw Failure.signatureDoesNotFit }
                    y = 610; limit = 736
                    try drawRow(table.rows[0], bold: true)
                    signatureEnd = y
                    expectingFooter = true
                    continue
                }
                if groupTable {
                    var found = false
                    for size in stride(from: min(table.halfPoints, 22), through: 14, by: -1) {
                        try Task.checkCancellation()
                        var required: CGFloat = 0
                        if !table.headers.isEmpty { required += try layout(table.headers, bold: true, halfPoints: size).1 }
                        for (i, row) in table.rows.enumerated() {
                            if i.isMultiple(of: 128) { try Task.checkCancellation() }
                            required += try layout(row, bold: table.boldLastRow && i == table.rows.count - 1, halfPoints: size).1
                        }
                        if y + required <= limit { chosenHalfPoints = size; found = true; break }
                    }
                    guard found else { throw Failure.groupTableDoesNotFit }
                }
                if !table.headers.isEmpty {
                    let h = try layout(table.headers, bold: true, halfPoints: chosenHalfPoints).1
                    if y + h > limit { try nextPage() }
                    try drawRow(table.headers, bold: true)
                }
                for (index, row) in table.rows.enumerated() {
                    if index.isMultiple(of: 128) { try Task.checkCancellation() }
                    let bold = table.boldLastRow && index == table.rows.count - 1
                    let height = try layout(row, bold: bold, halfPoints: chosenHalfPoints).1
                    if y + height > limit {
                        guard !groupTable else { throw Failure.groupTableDoesNotFit }
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
                        let originalLine = CTLineCreateWithAttributedString(text)
                        let line = item.justify ? (CTLineCreateJustifiedLine(originalLine, 1, Double(rect.width)) ?? originalLine) : originalLine
                        cg.saveGState(); cg.textMatrix = CGAffineTransform(scaleX: 1, y: -1)
                        let x = rect.minX + CGFloat(CTLineGetPenOffsetForFlush(line, flush, Double(rect.width)))
                        cg.textPosition = CGPoint(x: x, y: rect.minY + item.ascent)
                        CTLineDraw(line, cg); cg.restoreGState(); cg.textMatrix = .identity
                        if item.underline {
                            let lineWidth = CGFloat(CTLineGetTypographicBounds(line, nil, nil, nil) - CTLineGetTrailingWhitespaceWidth(line))
                            cg.saveGState(); UIColor.black.setStroke(); cg.setLineWidth(0.7)
                            cg.move(to: CGPoint(x: x, y: rect.minY + item.ascent + 2))
                            cg.addLine(to: CGPoint(x: x + lineWidth, y: rect.minY + item.ascent + 2))
                            cg.strokePath(); cg.restoreGState()
                        }
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
