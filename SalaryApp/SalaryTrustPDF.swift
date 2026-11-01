import UIKit
import CoreText
import PDFKit

/// يلف الخلايا الطويلة ويتحقق من النص المرسوم قبل إتاحة الكشف للمشاركة.
nonisolated enum SalaryTrustPDF {
    private static let page = CGRect(x: 0, y: 0, width: 842, height: 595)
    private static let widths: [CGFloat] = [28, 150, 75, 78, 125, 110, 110, 110]
    private static let margin: CGFloat = 28
    private static let bottom: CGFloat = 560
    private static let font = ReportFont.regular(9)
    private static let headerFont = ReportFont.bold(10)

    private static func lines(_ text: String, width: CGFloat, font: UIFont) throws -> Int {
        guard !text.isEmpty else { return 1 }
        let attributes: [NSAttributedString.Key: Any] = [.font: font, NSAttributedString.Key(kCTParagraphStyleAttributeName as String): PDFText.rtl]
        let value = NSAttributedString(string: text, attributes: attributes)
        let setter = CTTypesetterCreateWithAttributedString(value)
        var position = 0, count = 0
        while position < value.length {
            let length = CTTypesetterSuggestLineBreak(setter, position, Double(width))
            guard length > 0, length <= value.length - position else { throw SalaryTrust.invalid(tr("تعذّر تنسيق نص في الكشف.", "Could not lay out a report cell.")) }
            let line = CTTypesetterCreateLine(setter, CFRange(location: position, length: length))
            guard CTLineGetTypographicBounds(line, nil, nil, nil) - CTLineGetTrailingWhitespaceWidth(line) <= Double(width) + 0.5 else {
                throw SalaryTrust.invalid(tr("تعذّر احتواء نص في الكشف.", "A report cell cannot be fitted."))
            }
            position += length; count += 1
        }
        return count
    }

    static func write(_ sheet: ReportSheet) throws -> URL {
        guard sheet.columns.count == widths.count, !sheet.rows.isEmpty else { throw SalaryTrust.invalid(tr("الكشف فارغ.", "The report is empty.")) }
        let titleHeight = max(26, CGFloat(try lines(sheet.title, width: 778, font: ReportFont.bold(12))) * ReportFont.bold(12).lineHeight + 8)
        let headerHeight = max(24, CGFloat(try sheet.columns.indices.map { try lines(sheet.columns[$0].title, width: widths[$0] - 8, font: headerFont) }.max() ?? 1) * headerFont.lineHeight + 6)
        let headerLines = try lines(sheet.header, width: 778, font: ReportFont.bold(12))
        let top = margin + CGFloat(headerLines) * ReportFont.bold(12).lineHeight + 8
        let tableTop = top + titleHeight
        let maximumRow = bottom - tableTop - headerHeight
        guard maximumRow > 40 else { throw SalaryTrust.invalid(tr("عنوان الكشف أطول من مساحة الصفحة.", "The report heading is too long.")) }
        let heights = try sheet.rows.enumerated().map { index, row -> CGFloat in
            if index.isMultiple(of: 256) { try Task.checkCancellation() }
            guard row.count == widths.count else { throw SalaryTrust.invalid(tr("صف الكشف غير صالح.", "An invalid report row was found.")) }
            let count = try row.indices.map { try lines(row[$0], width: widths[$0] - 8, font: font) }.max() ?? 1
            let height = max(20, ceil(CGFloat(count) * font.lineHeight + 6))
            guard height <= maximumRow else { throw SalaryTrust.invalid(tr("أحد القيود أطول من صفحة PDF؛ صدّر Excel لهذا الكشف.", "One entry is taller than a PDF page; export Excel.")) }
            return height
        }
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("trust-report-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let url = folder.appendingPathComponent("كشف أمانات الرواتب.pdf")
        var complete = false
        defer { if !complete { try? FileManager.default.removeItem(at: folder) } }
        var right = page.width - margin
        let positions = widths.map { width -> CGFloat in right -= width; return right }
        var placements: [(page: Int, row: Int, y: CGFloat, height: CGFloat)] = []
        var pages = 0
        try UIGraphicsPDFRenderer(bounds: page).writePDF(to: url) { context in
            var index = 0
            while index < sheet.rows.count {
                context.beginPage()
                let cg = context.cgContext
                PDFText.draw(sheet.header, in: CGRect(x: margin, y: margin, width: 786, height: top - margin - 4), font: ReportFont.bold(12), wrap: true)
                let title = CGRect(x: margin, y: top, width: 786, height: titleHeight)
                UIColor(red: 0.12, green: 0.31, blue: 0.47, alpha: 1).setFill(); cg.fill(title)
                PDFText.draw(sheet.title, in: title.insetBy(dx: 4, dy: 2), font: ReportFont.bold(12), color: .white, wrap: true)
                for column in widths.indices {
                    let cell = CGRect(x: positions[column], y: tableTop, width: widths[column], height: headerHeight)
                    UIColor(white: 0.92, alpha: 1).setFill(); cg.fill(cell)
                    UIColor.darkGray.setStroke(); cg.stroke(cell, width: 0.5)
                    PDFText.draw(sheet.columns[column].title, in: cell.insetBy(dx: 4, dy: 2), font: headerFont, wrap: true)
                }
                var y = tableTop + headerHeight
                while index < sheet.rows.count && y + heights[index] <= bottom {
                    let height = heights[index]
                    placements.append((pages, index, y, height))
                    for column in widths.indices {
                        let cell = CGRect(x: positions[column], y: y, width: widths[column], height: height)
                        UIColor.darkGray.setStroke(); cg.stroke(cell, width: 0.5)
                        PDFText.draw(sheet.rows[index][column], in: cell.insetBy(dx: 4, dy: 2), font: font, wrap: true)
                    }
                    y += height; index += 1
                }
                pages += 1
            }
        }
        guard let document = PDFDocument(url: url), !document.isLocked, document.pageCount == pages, placements.count == sheet.rows.count else {
            throw SalaryTrust.invalid(tr("تعذّر تدقيق صفحات كشف الأمانات.", "Could not verify the trust report pages."))
        }
        func identity(_ value: String) -> String {
            let normalized = ArabicText.normalize(ArabicText.latinDigits(value.precomposedStringWithCompatibilityMapping))
            var scalars = String.UnicodeScalarView()
            for scalar in normalized.unicodeScalars where !CharacterSet.whitespacesAndNewlines.contains(scalar) && scalar.properties.generalCategory != .format {
                scalars.append(scalar)
            }
            return String(scalars)
        }
        for placement in placements {
            guard let output = document.page(at: placement.page) else { throw SalaryTrust.invalid(tr("تعذّر تدقيق صفحة من الكشف.", "Could not verify a report page.")) }
            for column in widths.indices {
                let expected = identity(sheet.rows[placement.row][column])
                if expected.isEmpty { continue }
                let rect = CGRect(x: positions[column] + 0.2, y: page.height - placement.y - placement.height + 0.2, width: widths[column] - 0.4, height: placement.height - 0.4)
                let actual = identity(output.selection(for: rect)?.string ?? "")
                let valid = column == 0 || column >= 5 ? actual == expected : TransferAnbarTablePDF.matchesExtractedCell(actual, expected: expected)
                guard valid else { throw SalaryTrust.invalid(tr("تعذّرت مطابقة الصف \(placement.row + 1) والعمود \(column + 1) في PDF؛ لم يُشارك الملف.", "PDF verification failed at row \(placement.row + 1), column \(column + 1); the file was not shared.")) }
            }
        }
        try FileManager.default.setAttributes([.protectionKey: FileProtectionType.complete], ofItemAtPath: url.path)
        complete = true
        return url
    }
}
