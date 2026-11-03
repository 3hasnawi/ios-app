import UIKit
import CoreText

/// كشف جهة واحدة: A4 عمودي، مع قياس التفاف كل خلية قبل إنشاء أي صفحة.
nonisolated enum EntityDeductionPDF {
    struct Layout {
        let widths: [CGFloat]
        let positions: [CGFloat]
        let headerHeight: CGFloat
        let titleHeight: CGFloat
        let columnsHeight: CGFloat
        let rowHeights: [CGFloat]
        let pages: [Range<Int>]
    }
    private static let page = CGRect(x: 0, y: 0, width: 595, height: 842)
    private static let margin: CGFloat = 28
    private static let bottom: CGFloat = 796
    private static let nameFont = ReportFont.bold(11.5)
    private static let textFont = ReportFont.regular(10.5)
    private static let headingFont = ReportFont.bold(11)
    private static let amountFont = ReportFont.bold(11)

    private static func invalid(_ message: String) -> NSError {
        NSError(domain: "EntityDeductionPDF", code: 1, userInfo: [NSLocalizedDescriptionKey: message])
    }
    private static func lines(_ text: String, width: CGFloat, font: UIFont) throws -> Int {
        guard width > 1 else { throw invalid("أحد أعمدة الكشف أضيق من أن يحتوي البيانات.") }
        guard !text.isEmpty else { return 1 }
        let attrs: [NSAttributedString.Key: Any] = [.font: font, NSAttributedString.Key(kCTParagraphStyleAttributeName as String): PDFText.rtl]
        let value = NSAttributedString(string: text, attributes: attrs)
        let setter = CTTypesetterCreateWithAttributedString(value)
        var position = 0, count = 0
        while position < value.length {
            let length = CTTypesetterSuggestLineBreak(setter, position, Double(width))
            guard length > 0, length <= value.length - position else { throw invalid("تعذّر تنسيق نص في كشف الاستقطاعات؛ صدّر Excel.") }
            let line = CTTypesetterCreateLine(setter, CFRange(location: position, length: length))
            guard CTLineGetTypographicBounds(line, nil, nil, nil) - CTLineGetTrailingWhitespaceWidth(line) <= Double(width) + 0.5 else {
                throw invalid("تعذّر احتواء نص في كشف الاستقطاعات؛ صدّر Excel.")
            }
            position += length; count += 1
        }
        return count
    }
    private static func money(_ value: Double) -> String {
        let f = NumberFormatter(); f.numberStyle = .decimal; f.locale = Locale(identifier: "en_US"); f.maximumFractionDigits = 2
        return f.string(from: NSNumber(value: value)) ?? String(value)
    }
    private static func isName(_ column: ReportSheet.Column) -> Bool { column.title.contains("الاسم") || column.title.hasPrefix("اسم") }

    static func prepare(_ sheet: ReportSheet) throws -> Layout {
        guard sheet.entityDeduction, !sheet.columns.isEmpty, sheet.rows.count == sheet.amounts.count,
              sheet.rows.allSatisfy({ $0.count == sheet.columns.count }), sheet.amounts.allSatisfy({ $0.isFinite }), sheet.total.isFinite,
              sheet.columns.filter({ $0.kind == .amount }).count == 1 else {
            throw invalid("بيانات كشف الاستقطاعات غير مكتملة.")
        }
        let widths = PDFReportWriter.columnWidths(sheet, tableWidth: page.width - margin * 2)
        var right = page.width - margin
        let positions = widths.map { width -> CGFloat in right -= width; return right }
        let headerFont = ReportFont.bold(14), titleFont = ReportFont.bold(14)
        let headerHeight = max(24, ceil(CGFloat(try lines(sheet.header, width: page.width - margin * 2 - 8, font: headerFont)) * headerFont.lineHeight + 4))
        let titleHeight = max(30, ceil(CGFloat(try lines(sheet.title, width: page.width - margin * 2 - 8, font: titleFont)) * titleFont.lineHeight + 8))
        let columnsLines = try sheet.columns.indices.map { try lines(sheet.columns[$0].title, width: widths[$0] - 8, font: headingFont) }.max() ?? 1
        let columnsHeight = max(24, ceil(CGFloat(columnsLines) * headingFont.lineHeight + 6))
        let tableTop = margin + headerHeight + titleHeight + columnsHeight
        let room = bottom - tableTop
        guard room > 48 else { throw invalid("عنوان الكشف طويل جداً لصفحة A4 العمودية.") }
        let rowHeights: [CGFloat] = try sheet.rows.enumerated().map { index, row in
            if index.isMultiple(of: 256) { try Task.checkCancellation() }
            var height: CGFloat = 24
            for c in sheet.columns.indices {
                let column = sheet.columns[c]
                let value: String = switch column.kind { case .serial: String(index + 1); case .amount: money(sheet.amounts[index]); case .text: row[c] }
                let font: UIFont = switch column.kind { case .serial, .amount: amountFont; case .text: isName(column) ? nameFont : textFont }
                let count = try lines(value, width: widths[c] - 8, font: font)
                if column.kind != .text && count > 1 { throw invalid("مبلغ أو تسلسل لا يتسع في الكشف العمودي؛ صدّر Excel.") }
                height = max(height, ceil(CGFloat(count) * font.lineHeight + 8))
            }
            guard height + (sheet.showTotal ? columnsHeight : 0) <= room else { throw invalid("أحد صفوف الكشف أطول من الصفحة العمودية؛ صدّر Excel.") }
            return height
        }
        var pages: [Range<Int>] = [], start = 0
        while start < rowHeights.count {
            var end = start, used: CGFloat = 0
            while end < rowHeights.count && used + rowHeights[end] <= room { used += rowHeights[end]; end += 1 }
            guard end > start else { throw invalid("تعذّر تقسيم كشف الاستقطاعات إلى صفحات.") }
            if end == rowHeights.count && sheet.showTotal && used + columnsHeight > room { end -= 1 }
            guard end > start else { throw invalid("تعذّر إظهار مجموع الكشف في الصفحة الأخيرة.") }
            pages.append(start..<end); start = end
        }
        if pages.isEmpty { pages = [0..<0] }
        if sheet.showTotal, sheet.columns.indices.contains(sheet.amountColumn),
           try lines(money(sheet.total), width: widths[sheet.amountColumn] - 8, font: ReportFont.bold(11.5)) > 1 {
            throw invalid("مجموع الكشف لا يتسع في الصفحة العمودية؛ صدّر Excel.")
        }
        return Layout(widths: widths, positions: positions, headerHeight: headerHeight, titleHeight: titleHeight,
                      columnsHeight: columnsHeight, rowHeights: rowHeights, pages: pages)
    }

    static func render(_ sheet: ReportSheet, layout: Layout, in context: UIGraphicsPDFRendererContext) {
        let dark = UIColor(red: 0.12, green: 0.31, blue: 0.47, alpha: 1)
        let light = UIColor(red: 0.87, green: 0.92, blue: 0.97, alpha: 1)
        let line = UIColor(red: 0.61, green: 0.72, blue: 0.83, alpha: 1)
        let zebra = UIColor(red: 243/255, green: 247/255, blue: 251/255, alpha: 1)
        let tableWidth = page.width - margin * 2
        func cell(_ c: Int, _ y: CGFloat, _ height: CGFloat) -> CGRect { CGRect(x: layout.positions[c], y: y, width: layout.widths[c], height: height) }
        func text(_ value: String, _ rect: CGRect, _ font: UIFont, _ color: UIColor = .black, _ alignment: NSTextAlignment = .center, wrap: Bool = false) {
            PDFText.draw(value, in: rect.insetBy(dx: 4, dy: 0), font: font, color: color, align: alignment, wrap: wrap)
        }
        for (pageIndex, range) in layout.pages.enumerated() { autoreleasepool {
            context.beginPage(withBounds: page, pageInfo: [:])
            let cg = context.cgContext
            text(sheet.header, CGRect(x: margin, y: margin, width: tableWidth, height: layout.headerHeight), ReportFont.bold(14), wrap: true)
            let title = CGRect(x: margin, y: margin + layout.headerHeight, width: tableWidth, height: layout.titleHeight)
            dark.setFill(); cg.fill(title); text(sheet.title, title, ReportFont.bold(14), .white, wrap: true)
            let columnsY = title.maxY
            for c in sheet.columns.indices {
                let rect = cell(c, columnsY, layout.columnsHeight); light.setFill(); cg.fill(rect); line.setStroke(); cg.stroke(rect, width: 0.5)
                text(sheet.columns[c].title, rect, headingFont, wrap: true)
            }
            var y = columnsY + layout.columnsHeight
            for index in range {
                let height = layout.rowHeights[index]
                for c in sheet.columns.indices {
                    let rect = cell(c, y, height)
                    if index % 2 == 1 { zebra.setFill(); cg.fill(rect) }
                    line.setStroke(); cg.stroke(rect, width: 0.5)
                    switch sheet.columns[c].kind {
                    case .serial: text(String(index + 1), rect, amountFont)
                    case .amount: text(money(sheet.amounts[index]), rect, amountFont)
                    case .text: text(sheet.rows[index][c], rect, isName(sheet.columns[c]) ? nameFont : textFont, .black, .right, wrap: true)
                    }
                }
                y += height
            }
            if pageIndex == layout.pages.count - 1, sheet.showTotal, sheet.amountColumn > 0 {
                let amount = sheet.amountColumn
                let label = (0..<amount).map { cell($0, y, layout.columnsHeight) }.reduce(CGRect.null) { $0.union($1) }
                for c in sheet.columns.indices { light.setFill(); cg.fill(cell(c, y, layout.columnsHeight)) }
                line.setStroke(); cg.stroke(label, width: 0.5)
                for c in amount..<sheet.columns.count { cg.stroke(cell(c, y, layout.columnsHeight), width: 0.5) }
                text("المجموع", label, ReportFont.bold(11.5))
                text(money(sheet.total), cell(amount, y, layout.columnsHeight), ReportFont.bold(11.5))
            }
        } }
    }
}
