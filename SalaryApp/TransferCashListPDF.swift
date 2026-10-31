import UIKit
import CoreText
import PDFKit

/// قائمة مرفقة بكتاب أمانات الرواتب؛ تنمو على صفحات A4 دون قص الأسماء.
nonisolated enum TransferCashListPDF {
    enum Failure: LocalizedError {
        case empty, invalid, rowTooTall, tableMismatch
        case cellMismatch(row: Int, column: Int, readCount: Int, expectedCount: Int)
        var errorDescription: String? {
            switch self {
            case .empty: tr("لا توجد رواتب كاش للشهر المختار.", "There are no cash salaries for the selected month.")
            case .invalid: tr("تعذّر التحقق من بيانات قائمة الكاش.", "Could not validate the cash salary list.")
            case .rowTooTall: tr("بيانات أحد السطور أطول من مساحة الصفحة.", "A row is taller than the page.")
            case .tableMismatch: tr("تعذّر مطابقة بيانات قائمة الكاش المرسومة مع مصدر الرواتب.", "Could not reconcile the rendered cash salary list with the payroll source.")
            case .cellMismatch(let row, let column, let readCount, let expectedCount):
                tr("تعذّر تدقيق قائمة الكاش في الصف \(row) والعمود \(column) (طول المقروء \(readCount)، المتوقع \(expectedCount)).", "Cash list verification failed at row \(row), column \(column) (read length \(readCount), expected \(expectedCount)).")
            }
        }
    }
    private static let page = CGRect(x: 0, y: 0, width: 595, height: 842)
    private static let margin: CGFloat = 28
    private static let columns: [CGFloat] = [150, 80, 190, 119]
    private static let tableTop: CGFloat = 112
    private static let bottom: CGFloat = 806
    private static let headerHeight: CGFloat = 24
    private static let totalHeight: CGFloat = 24
    private static let rtl: CTParagraphStyle = {
        let direction = CTWritingDirection.rightToLeft
        return withUnsafeBytes(of: direction) { raw in
            var setting = CTParagraphStyleSetting(spec: .baseWritingDirection,
                valueSize: MemoryLayout<CTWritingDirection>.size, value: raw.baseAddress!)
            return CTParagraphStyleCreate(&setting, 1)
        }
    }()

    private static func money(_ amount: Double) -> String {
        let formatter = NumberFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.numberStyle = .decimal
        formatter.maximumFractionDigits = 0
        return formatter.string(from: NSNumber(value: amount)) ?? String(format: "%.0f", amount)
    }

    private static func lineCount(_ text: String, font: UIFont, width: CGFloat) -> Int {
        guard !text.isEmpty else { return 1 }
        let attrs: [NSAttributedString.Key: Any] = [
            .font: font, NSAttributedString.Key(kCTParagraphStyleAttributeName as String): rtl
        ]
        let value = NSAttributedString(string: text, attributes: attrs)
        let typesetter = CTTypesetterCreateWithAttributedString(value)
        var start = 0, lines = 0
        while start < value.length {
            let proposed = CTTypesetterSuggestLineBreak(typesetter, start, Double(width))
            guard proposed > 0 else { return Int.max }
            start += proposed
            lines += 1
        }
        return max(1, lines)
    }

    private static func rowHeight(_ row: TransferCashEmployee, font: UIFont) throws -> CGFloat {
        let values = [row.name, row.rank, row.workplace, money(row.amount)]
        let counts = zip(values, columns).map { lineCount($0.0, font: font, width: $0.1 - 8) }
        guard let maxLines = counts.max(), maxLines != Int.max else { throw Failure.rowTooTall }
        let height = max(20, ceil(CGFloat(maxLines) * font.lineHeight + 8))
        guard height <= bottom - tableTop - headerHeight - totalHeight else { throw Failure.rowTooTall }
        return height
    }

    static func make(_ list: TransferCashList) async throws -> URL {
        try await PDFWork.run {
            guard !list.employees.isEmpty else { throw Failure.empty }
            guard !list.month.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                  list.total.isFinite, list.total >= 0, list.total <= 9_007_199_254_740_991,
                  list.employees.allSatisfy({ !$0.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && !$0.rank.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && !$0.workplace.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && $0.amount.isFinite && $0.amount >= 0 && $0.amount.rounded() == $0.amount }) else { throw Failure.invalid }
            let font = ReportFont.regular(9.5)
            guard lineCount("قائمة رواتب الكاش · \(list.month)", font: ReportFont.bold(12), width: 531) == 1,
                  lineCount(money(list.total), font: ReportFont.bold(10), width: columns[3] - 8) == 1,
                  list.employees.allSatisfy({ lineCount(money($0.amount), font: font, width: columns[3] - 8) == 1 }) else { throw Failure.rowTooTall }
            let heights = try list.employees.map { try rowHeight($0, font: font) }
            let folder = FileManager.default.temporaryDirectory.appendingPathComponent("cash-list-\(UUID().uuidString)", isDirectory: true)
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            let url = folder.appendingPathComponent("قائمة رواتب كاش.pdf")
            var complete = false
            defer { if !complete { try? FileManager.default.removeItem(at: folder) } }
            let renderer = UIGraphicsPDFRenderer(bounds: page)
            var placements: [(page: Int, index: Int, y: CGFloat, height: CGFloat)] = []
            var totalPlacement: (page: Int, y: CGFloat)?
            var pageCount = 0
            try renderer.writePDF(to: url) { context in
                var index = 0
                var needsTotal = true
                repeat {
                    context.beginPage()
                    let cg = context.cgContext
                    PDFText.draw("قيادة شرطة محافظة الأنبار / قسم الحسابات", in: CGRect(x: margin, y: 30, width: 539, height: 23), font: ReportFont.bold(13))
                    PDFText.draw("قائمة رواتب الكاش · \(list.month)", in: CGRect(x: margin, y: 57, width: 539, height: 24), font: ReportFont.bold(12))
                    var x = page.width - margin
                    var positions: [CGFloat] = []
                    for width in columns { x -= width; positions.append(x) }
                    let labels = ["الاسم", "الرتبة", "محل العمل", "صافي الراتب"]
                    for i in labels.indices {
                        let cell = CGRect(x: positions[i], y: tableTop, width: columns[i], height: headerHeight)
                        UIColor(white: 0.92, alpha: 1).setFill(); cg.fill(cell)
                        UIColor.darkGray.setStroke(); cg.stroke(cell, width: 0.5)
                        PDFText.draw(labels[i], in: cell.insetBy(dx: 4, dy: 0), font: ReportFont.bold(10))
                    }
                    var y = tableTop + headerHeight
                    while index < list.employees.count, y + heights[index] <= bottom {
                        let row = list.employees[index], height = heights[index]
                        placements.append((pageCount, index, y, height))
                        let values = [row.name, row.rank, row.workplace, money(row.amount)]
                        for i in values.indices {
                            let cell = CGRect(x: positions[i], y: y, width: columns[i], height: height)
                            UIColor.darkGray.setStroke(); cg.stroke(cell, width: 0.5)
                            PDFText.draw(values[i], in: cell.insetBy(dx: 4, dy: 2), font: font, align: i == 3 ? .center : .right, wrap: true)
                        }
                        y += height; index += 1
                    }
                    if index == list.employees.count && y + totalHeight <= bottom {
                        let amountCell = CGRect(x: positions[3], y: y, width: columns[3], height: totalHeight)
                        let titleCell = CGRect(x: positions[2], y: y, width: columns[0] + columns[1] + columns[2], height: totalHeight)
                        UIColor(white: 0.92, alpha: 1).setFill(); cg.fill(amountCell); cg.fill(titleCell)
                        UIColor.darkGray.setStroke(); cg.stroke(amountCell, width: 0.5); cg.stroke(titleCell, width: 0.5)
                        PDFText.draw("المجموع · \(list.employees.count) راتب", in: titleCell.insetBy(dx: 4, dy: 0), font: ReportFont.bold(10), align: .right)
                        PDFText.draw(money(list.total), in: amountCell.insetBy(dx: 4, dy: 0), font: ReportFont.bold(10))
                        totalPlacement = (pageCount, y)
                        needsTotal = false
                    }
                    pageCount += 1
                } while index < list.employees.count || needsTotal
            }
            // يفحص النص المقروء من الصفحات بعد رسمها للتأكد من عدم سقوط سطر أو مبلغ.
            guard let document = PDFDocument(url: url), !document.isLocked, document.pageCount == pageCount,
                  placements.count == list.employees.count, let totalPlacement else { throw Failure.tableMismatch }
            var right = page.width - margin
            let positions = columns.map { width -> CGFloat in right -= width; return right }
            func text(pageIndex: Int, rect: CGRect) -> String {
                guard let output = document.page(at: pageIndex) else { return "" }
                let pdfRect = CGRect(x: rect.minX + 0.2, y: page.height - rect.maxY + 0.2,
                                     width: rect.width - 0.4, height: rect.height - 0.4)
                return output.selection(for: pdfRect)?.string ?? ""
            }
            func identity(_ value: String) -> String {
                let normalized = ArabicText.normalize(ArabicText.latinDigits(value.precomposedStringWithCompatibilityMapping))
                var scalars = String.UnicodeScalarView()
                for scalar in normalized.unicodeScalars where !CharacterSet.whitespacesAndNewlines.contains(scalar) && scalar.properties.generalCategory != .format {
                    scalars.append(scalar)
                }
                return String(scalars)
            }
            for (expectedIndex, placement) in placements.enumerated() {
                guard placement.index == expectedIndex else { throw Failure.tableMismatch }
                let row = list.employees[expectedIndex]
                let values = [row.name, row.rank, row.workplace, money(row.amount)]
                for column in values.indices {
                    let rect = CGRect(x: positions[column], y: placement.y, width: columns[column], height: placement.height)
                    let read = identity(text(pageIndex: placement.page, rect: rect))
                    let expected = identity(values[column])
                    let matches = column == 3 ? read == expected : TransferAnbarTablePDF.matchesExtractedCell(read, expected: expected)
                    guard matches else { throw Failure.cellMismatch(row: expectedIndex + 1, column: column + 1, readCount: read.count, expectedCount: expected.count) }
                }
            }
            let amountRect = CGRect(x: positions[3], y: totalPlacement.y, width: columns[3], height: totalHeight)
            guard identity(text(pageIndex: totalPlacement.page, rect: amountRect)) == identity(money(list.total)) else { throw Failure.tableMismatch }
            try FileManager.default.setAttributes([.protectionKey: FileProtectionType.complete], ofItemAtPath: url.path)
            complete = true
            return url
        }
    }
}
