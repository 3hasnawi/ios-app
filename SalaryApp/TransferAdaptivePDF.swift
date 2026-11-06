import UIKit
import CoreText
import PDFKit

/// مسودة كتاب A4 مرنة؛ تقيس السطور والخلايا قبل الرسم وترفض قصّ المحتوى.
/// لا تعني اجتياز التحقق على الجهاز أو السماح بالمشاركة.
nonisolated enum TransferAdaptivePDF {
    enum Failure: LocalizedError {
        case invalid, noFont, doesNotFit, groupTableDoesNotFit, signatureDoesNotFit, signaturePlacement, damaged
        case diagnostic(String)
        var errorDescription: String? {
            switch self {
            case .invalid: tr("بيانات الكتاب غير مكتملة.", "The letter data is incomplete.")
            case .noFont: tr("تعذّر تحميل الخط أو الشعار اللازم لإعداد الكتاب.", "Could not load the font or logo needed to prepare the letter.")
            case .doesNotFit: tr("لا يتسع أحد الحقول أو جدول المجموعة بوضوح؛ راجع البيانات.", "A field or the group table does not fit legibly; review the data.")
            case .diagnostic(let area): "PDF layout test: \(area)"
            case .groupTableDoesNotFit: tr("جدول المجموعة لا يتسع في صفحة واحدة بوضوح؛ راجع أطوال الأسماء وعدد الجهات.", "The group table does not fit legibly on one page; review entity names and count.")
            case .signatureDoesNotFit: tr("أحد أسماء اللجنة أو صفاتها لا يتسع في سطر التوقيع؛ راجع أسماء اللجنة.", "A committee name or title does not fit on its signature line; review the names.")
            case .signaturePlacement: tr("موضع اللجنة يتداخل مع المتن أو الهامش السفلي؛ عدّل موضع اللجنة أو اختصر النص.", "The committee overlaps the body or footer; adjust its position or shorten the text.")
            case .damaged: tr("تعذّر التحقق من صفحات مسودة الكتاب.", "The letter draft pages could not be verified.")
            }
        }
    }
    private static func fitFailure(_ area: String) -> Failure {
        #if targetEnvironment(simulator)
        if UserDefaults.standard.bool(forKey: "wordTransferTest") { return .diagnostic(area) }
        #endif
        return .doesNotFit
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
        let kashida: CustomTransferTemplate.Kashida?
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
    private static func lines(_ text: String, font: CTFont, width: CGFloat, underline: Bool = false, justify: Bool = false, spacing: CGFloat = 3, kashida: CustomTransferTemplate.Kashida? = nil) throws -> [TextLine] {
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
            let fullCount = CTTypesetterSuggestLineBreak(typesetter, offset, Double(width))
            guard fullCount > 0, fullCount <= attr.length - offset else { throw fitFailure("line-break") }
            let fullText = (text as NSString).substring(with: NSRange(location: offset, length: fullCount))
            let reserve: CGFloat = switch kashida {
            case .some(.low): 12
            case .some(.medium): 24
            case .some(.high): 36
            case .some(.none), nil: 0
            }
            // اترك مساحة للمد عند لف السطر، ولكن لا تقسّم السطر الأخير أو الفقرة المنتهية يدوياً.
            var count = fullCount
            if justify && reserve > 0 && offset + fullCount < attr.length && !fullText.hasSuffix("\n") {
                let shorter = CTTypesetterSuggestLineBreak(typesetter, offset, Double(max(8, width - reserve)))
                if shorter > 0 && shorter <= fullCount { count = shorter }
            }
            let line = CTTypesetterCreateLine(typesetter, CFRange(location: offset, length: count))
            let measured = CTLineGetTypographicBounds(line, nil, nil, nil) - CTLineGetTrailingWhitespaceWidth(line)
            guard measured <= Double(width) + 0.5 else { throw fitFailure("line-width") }
            let value = (text as NSString).substring(with: NSRange(location: offset, length: count))
            result.append(TextLine(text: value, font: font, underline: underline,
                                   justify: justify && offset + count < attr.length && !value.hasSuffix("\n"),
                                   kashida: kashida, height: height, ascent: CTFontGetAscent(font)))
            guard result.count <= 20_000 else { throw fitFailure("line-count") }
            offset += count
        }
        return result
    }
    /// تمديد بصري محدود لوصلات الحروف؛ لا يتغير النص المحفوظ في القالب.
    static func stretchArabic(_ text: String, font: CTFont, width: CGFloat, mode: CustomTransferTemplate.Kashida?) -> String {
        let fraction: CGFloat = switch mode {
        case .some(.low): 0.6
        case .some(.medium): 0.8
        case .some(.high): 0.95
        case .some(.none), nil: 0
        }
        guard fraction > 0, width > 0 else { return text }
        let characters = Array(text)
        guard characters.count >= 2, characters.count <= 4096 else { return text }
        // الاقتصار على الوصلات العربية البسيطة يحمي الحسابات والأرقام وعلامات التشكيل.
        let forwardJoining = Set("بتثجحخسشصضطظعغفقكلمنهيئ")
        let letters = Set("ابتثجحخدذرزسشصضطظعغفقكلمنهويأإآؤئىة")
        let positions = (0..<(characters.count - 1)).filter { forwardJoining.contains(characters[$0]) && letters.contains(characters[$0 + 1]) }
        guard !positions.isEmpty else { return text }
        func measured(_ value: String) -> CGFloat {
            let attributed = NSAttributedString(string: value, attributes: [
                NSAttributedString.Key(kCTFontAttributeName as String): font,
                NSAttributedString.Key(kCTParagraphStyleAttributeName as String): rtl
            ])
            let line = CTLineCreateWithAttributedString(attributed)
            return CGFloat(CTLineGetTypographicBounds(line, nil, nil, nil) - CTLineGetTrailingWhitespaceWidth(line))
        }
        let originalWidth = measured(text)
        let limit = min(width - 0.5, originalWidth + max(0, width - originalWidth) * fraction)
        guard limit > originalWidth else { return text }
        var counts = [Int](repeating: 0, count: characters.count)
        var result = text
        // حد أعلى للمحاولات والمد في كل موضع لتجنب التمديد المبالغ فيه.
        for attempt in 0..<min(positions.count * 2, 48) {
            let index = positions[attempt % positions.count]
            counts[index] += 1
            var candidate = ""
            for (i, character) in characters.enumerated() {
                candidate.append(character)
                if counts[i] > 0 { candidate += String(repeating: "ـ", count: counts[i]) }
            }
            if measured(candidate) <= limit { result = candidate } else { counts[index] -= 1 }
        }
        return result
    }
    static func write(blocks: [TransferWordDocument.Block], destination: URL,
                      sakkalBold: Data?, group: Bool, signatureOffset: Int = 0,
                      verifyNumericTableCells: Bool = false, signatureHorizontalOffset: Int = 0, template: CustomTransferTemplate? = nil) throws {
        guard (-60...60).contains(signatureOffset), (-30...30).contains(signatureHorizontalOffset), blocks.count >= 7, !FileManager.default.fileExists(atPath: destination.path),
              case let .paragraph(header) = blocks[0], case let .paragraph(reference) = blocks[1],
              let logo = UIImage(named: "IraqEmblem")?.cgImage else { throw Failure.invalid }
        let headerFont = try font(header, sakkal: sakkalBold)
        let refFont = try font(reference, sakkal: sakkalBold)
        let headingWidth: CGFloat = 190
        let headerLines = try lines(header.text, font: headerFont, width: headingWidth)
        guard !headerLines.isEmpty, headerLines.reduce(CGFloat.zero, { $0 + $1.height }) <= 123 else { throw fitFailure("heading") }
        // الفراغ بعد «التاريخ» للكتابة فقط، وليس جزءاً من الرقم أو التاريخ؛ نقلله عند حاجة الخط للمساحة.
        var referenceText = reference.text
        func trimDateSpace() -> Bool {
            guard let marker = referenceText.range(of: "التاريخ:"), marker.upperBound < referenceText.endIndex,
                  referenceText[marker.upperBound] == "\u{00A0}" else { return false }
            referenceText.remove(at: marker.upperBound)
            return true
        }
        var refLines: [TextLine]
        while true {
            do {
                let candidate = try lines(referenceText, font: refFont, width: headingWidth)
                if !candidate.isEmpty && candidate.reduce(CGFloat.zero, { $0 + $1.height }) <= 58 {
                    refLines = candidate; break
                }
                guard trimDateSpace() else { throw fitFailure("reference-height") }
            } catch is CancellationError { throw CancellationError() }
            catch {
                guard trimDateSpace() else { throw error }
            }
        }
        var pages: [[Draw]] = [[]]
        // تحفظ حدود خلايا المبالغ للتحقق من كل مبلغ في موضعه، حتى إن فصل PDFKit الأرقام بفواصل أسطر.
        var numericCells: [(page: Int, rect: CGRect, expected: String)] = []
        var y: CGFloat = 23
        // يترك المتن مساحة ثابتة للتواقيع والنسخ أسفل صفحة الكتاب.
        var limit: CGFloat = 590
        var expectingFooter = false
        var signatureEnd: CGFloat?
        func add(_ operation: Draw) { pages[pages.count - 1].append(operation) }
        func nextPage() throws {
            guard pages.count < 4000 else { throw fitFailure("page-count") }
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
            let closing = TransferWordDocument.Paragraph(text: "للتفضل بالاطلاع وإجراء اللازم وإعلامنا... مع التقدير.", font: "Times New Roman", halfPoints: template?.halfPoints(.closing, default: 32) ?? 32, bold: true, centered: true)
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
                    let salutation = TransferWordDocument.Paragraph(text: "تحية طيبة ..", font: "Times New Roman", halfPoints: template?.halfPoints(.salutation, default: 28) ?? 28, bold: true)
                    let salutationFont = try font(salutation, sakkal: sakkalBold)
                    try addLines(lines(salutation.text, font: salutationFont, width: contentWidth), x: margin, width: contentWidth, flush: 1)
                    y += 7; needsClosing = true
                }
                let face = try font(p, sakkal: sakkalBold)
                let justify = p.alignment == .justified || (p.alignment == nil && blockIndex == 2)
                let flush: CGFloat = switch p.alignment {
                case .left: 0
                case .center: 0.5
                case .right, .justified: 1
                case nil: p.centered ? 0.5 : 1
                }
                let ls = try lines(p.text, font: face, width: contentWidth,
                                   underline: p.centered && p.text.hasPrefix("م /"),
                                   justify: justify,
                                   spacing: expectingFooter ? 0 : CGFloat(p.lineSpacing ?? 3), kashida: p.kashida)
                if expectingFooter {
                    let footerHeight = ls.reduce(CGFloat.zero, { $0 + $1.height })
                    let footerBottom: CGFloat = 832
                    guard let signatureEnd, footerHeight <= footerBottom - margin else { throw fitFailure("footer-height") }
                    y = footerBottom - footerHeight; limit = footerBottom
                    guard y >= signatureEnd + 8 else { throw Failure.signaturePlacement }
                    expectingFooter = false
                }
                try addLines(ls, x: margin, width: contentWidth, flush: flush)
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
                guard widths.allSatisfy({ $0 > 10 }) else { throw fitFailure("column-width") }
                let groupTable = group && afterBreak
                let signature = !table.showBorders
                let tableRight = (table.alignRight ? page.width - margin : margin + (contentWidth + tableWidth) / 2) + (signature ? CGFloat(signatureHorizontalOffset) : 0)
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
                            if values[index].isEmpty {
                                cells.append([])
                                continue
                            }
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
                    guard height <= bottom - margin else { throw fitFailure("row-height") }
                    return (cells, height)
                }
                func drawRow(_ values: [String], bold: Bool) throws {
                    let (cells, height) = try layout(values, bold: bold, halfPoints: chosenHalfPoints)
                    guard y + height <= limit else { throw fitFailure("row-position") }
                    var right = tableRight
                    for index in 0..<count {
                        right -= widths[index]
                        let rect = CGRect(x: right, y: y, width: widths[index], height: height)
                        if table.showBorders { add(.box(rect, bold)) }
                        let value = values[index]
                        if verifyNumericTableCells && table.showBorders && value.contains(where: \.isNumber) &&
                            value.allSatisfy({ $0.isNumber || $0.isWhitespace || $0 == "," || $0 == "٬" }) {
                            numericCells.append((pages.count - 1, rect, value))
                        }
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
                    for size in stride(from: table.halfPoints, through: min(table.halfPoints, 24), by: -1) {
                        do {
                            let height = try layout(table.rows[0], bold: true, halfPoints: size).1
                            if height <= 125 { chosenHalfPoints = size; found = true; break }
                        } catch Failure.signatureDoesNotFit { continue }
                    }
                    guard found else { throw Failure.signatureDoesNotFit }
                    let signatureStart = CGFloat(610 + signatureOffset)
                    let signatureHeight = try layout(table.rows[0], bold: true, halfPoints: chosenHalfPoints).1
                    guard y + 8 <= signatureStart, signatureStart + signatureHeight <= bottom else { throw Failure.signaturePlacement }
                    y = signatureStart; limit = bottom
                    try drawRow(table.rows[0], bold: true)
                    signatureEnd = y
                    expectingFooter = true
                    continue
                }
                if groupTable {
                    var found = false
                    for size in stride(from: template?.fontSizes?[CustomTransferTemplate.FontPart.table.rawValue] == nil ? min(table.halfPoints, 22) : table.halfPoints, through: 14, by: -1) {
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
                        let displayed = item.justify ? stretchArabic(item.text, font: item.font, width: rect.width, mode: item.kashida) : item.text
                        let text = NSAttributedString(string: displayed, attributes: [
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
        if verifyNumericTableCells && numericCells.isEmpty { throw Failure.damaged }
        func digits(_ value: String) -> String {
            ArabicText.latinDigits(value).filter { $0.isASCII && $0.isNumber }
        }
        for cell in numericCells {
            try Task.checkCancellation()
            guard let output = document.page(at: cell.page) else { throw Failure.damaged }
            let box = CGRect(x: cell.rect.minX + 0.2, y: page.height - cell.rect.maxY + 0.2,
                             width: cell.rect.width - 0.4, height: cell.rect.height - 0.4)
            let actual = digits(output.selection(for: box)?.string ?? "")
            guard !actual.isEmpty, actual == digits(cell.expected) else { throw Failure.damaged }
        }
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
        let numericSeparators = CharacterSet(charactersIn: ",٬")
        let tokenSeparators = CharacterSet.alphanumerics.union(numericSeparators).inverted
        for index in 0..<document.pageCount {
            try Task.checkCancellation()
            guard let page = document.page(at: index), let value = page.string else { throw Failure.damaged }
            let normalized = ArabicText.latinDigits(value).uppercased()
            for word in normalized.components(separatedBy: tokenSeparators) {
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
