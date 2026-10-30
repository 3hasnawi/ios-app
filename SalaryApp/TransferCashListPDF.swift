import UIKit
import CoreText

/// قائمة مرفقة بكتاب أمانات الرواتب؛ تنمو على صفحات A4 دون قص الأسماء.
nonisolated enum TransferCashListPDF {
    enum Failure: LocalizedError {
        case empty, invalid, rowTooTall
        var errorDescription: String? {
            switch self {
            case .empty: tr("لا توجد رواتب كاش للشهر المختار.", "There are no cash salaries for the selected month.")
            case .invalid: tr("تعذّر التحقق من بيانات قائمة الكاش.", "Could not validate the cash salary list.")
            case .rowTooTall: tr("بيانات أحد السطور أطول من مساحة الصفحة.", "A row is taller than the page.")
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
                  list.employees.allSatisfy({ !$0.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && $0.amount.isFinite && $0.amount >= 0 && $0.amount.rounded() == $0.amount }) else { throw Failure.invalid }
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
                        needsTotal = false
                    }
                } while index < list.employees.count || needsTotal
            }
            complete = true
            return url
        }
    }
}
