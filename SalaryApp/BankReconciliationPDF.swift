import UIKit
import PDFKit
import CoreText

/// كشف المطابقة مستقل عن إثبات التحويل، ويحافظ على مسافة التوقيع في آخر صفحة.
nonisolated enum BankReconciliationPDF {
    enum Failure: LocalizedError {
        case empty, tooLong, damaged
        var errorDescription: String? {
            switch self {
            case .empty: tr("لا توجد حركات لعرضها في الكشف.", "There are no movements to display.")
            case .tooLong: tr("أحد بيانات الحركة لا يتسع بوضوح؛ اختصر البيان ثم حاول مجدداً.", "A movement does not fit clearly; shorten its description and try again.")
            case .damaged: tr("تعذّر التحقق من كشف المطابقة بعد إنشائه.", "Could not verify the reconciliation statement after generation.")
            }
        }
    }
    private static let page = CGRect(x: 0, y: 0, width: 595, height: 842)
    private static let widths: [CGFloat] = [78, 189, 88, 88, 88]
    private static let margin: CGFloat = 32
    private static func number(_ value: Int64) throws -> String {
        let prefix = value < 0 ? "−" : ""
        return prefix + (try TransferWordValues.groupedAmount(Double(abs(value))))
    }
    private static func date(_ value: Date) -> String {
        let formatter = DateFormatter()
        formatter.calendar = Calendar(identifier: .gregorian)
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(identifier: "Asia/Baghdad") ?? .current
        formatter.dateFormat = "yyyy/MM/dd"
        return formatter.string(from: value)
    }
    private static func lines(_ value: String, font: UIFont, width: CGFloat) -> Int {
        let text = NSAttributedString(string: value, attributes: [.font: font])
        let setter = CTTypesetterCreateWithAttributedString(text)
        var offset = 0, count = 0
        while offset < text.length {
            let n = CTTypesetterSuggestLineBreak(setter, offset, Double(width))
            if n <= 0 { return Int.max }
            offset += n; count += 1
        }
        return max(1, count)
    }
    static func make(_ period: BankReconciliation.Period, complete: Bool = false) async throws -> URL {
        try await PDFWork.run {
            let totals = try BankReconciliation.calculate(period)
            guard !totals.lines.isEmpty else { throw Failure.empty }
            let font = ReportFont.regular(9), bold = ReportFont.bold(9)
            let labels = ["التاريخ", "البيان", "دائن", "مدين", "الرصيد"]
            var rows: [([String], CGFloat)] = []
            if period.openingBalance != 0 {
                rows.append(([date(period.start), "الرصيد الافتتاحي", "—", "—", try number(period.openingBalance)], 22))
            }
            for line in totals.lines {
                let item = line.movement
                let values = [date(item.date), item.description, item.direction == .credit ? try number(item.amount) : "—",
                              item.direction == .debit ? try number(item.amount) : "—", try number(line.balance)]
                let counts = zip(values, widths).map { lines($0.0, font: font, width: $0.1 - 8) }
                guard counts.allSatisfy({ $0 != Int.max && $0 <= 5 }), counts[0] == 1,
                      counts[2] == 1, counts[3] == 1, counts[4] == 1 else { throw Failure.tooLong }
                rows.append((values, max(22, CGFloat(counts.max() ?? 1) * font.lineHeight + 8)))
            }
            let totalRow = ["", "المجموع", try number(totals.credits), try number(totals.debits), try number(totals.balance)]
            guard zip(totalRow, widths).allSatisfy({ lines($0.0, font: bold, width: $0.1 - 6) == 1 }),
                  [period.memberFirst, period.memberSecond].allSatisfy({ lines($0, font: bold, width: 217) <= 2 && !$0.unicodeScalars.contains(where: { $0.value < 32 }) }) else { throw Failure.tooLong }
            let destination = FileManager.default.temporaryDirectory.appendingPathComponent("bank-reconciliation-\(UUID().uuidString).pdf")
            var done = false
            defer { if !done { try? FileManager.default.removeItem(at: destination) } }
            var placements: [(Int, CGRect, String, Int)] = []
            var pageCount = 0
            let renderer = UIGraphicsPDFRenderer(bounds: page)
            try renderer.writePDF(to: destination) { context in
                let cg = context.cgContext
                var index = 0, finalDrawn = false
                repeat {
                    context.beginPage()
                    PDFText.draw("كشف مطابقة حساب المصرف", in: CGRect(x: margin, y: 20, width: 531, height: 28), font: ReportFont.bold(16))
                    PDFText.draw("الفترة من \(date(period.start)) إلى \(date(period.end))", in: CGRect(x: margin, y: 52, width: 531, height: 20), font: font)
                    if !complete { PDFText.draw("مسودة · المطابقة غير مكتملة", in: CGRect(x: margin, y: 65, width: 531, height: 15), font: bold, color: .darkGray) }
                    var x = page.width - margin
                    var rects: [CGRect] = []
                    for (i, width) in widths.enumerated() {
                        x -= width
                        let cell = CGRect(x: x, y: 82, width: width, height: 24)
                        rects.append(cell)
                        UIColor(white: 0.93, alpha: 1).setFill(); cg.fill(cell)
                        UIColor.darkGray.setStroke(); cg.stroke(cell, width: 0.5)
                        PDFText.draw(labels[i], in: cell.insetBy(dx: 3, dy: 1), font: bold)
                    }
                    var y: CGFloat = 106
                    while index < rows.count, y + rows[index].1 <= 665 {
                        let (values, height) = rows[index]
                        for i in values.indices {
                            let cell = CGRect(x: rects[i].minX, y: y, width: widths[i], height: height)
                            UIColor.darkGray.setStroke(); cg.stroke(cell, width: 0.5)
                            PDFText.draw(values[i], in: cell.insetBy(dx: 4, dy: 2), font: font, align: i == 1 ? .right : .center, wrap: true)
                            if values[i] != "—" && !values[i].isEmpty { placements.append((pageCount, cell, values[i], i)) }
                        }
                        y += height; index += 1
                    }
                    if index == rows.count {
                        if y + 24 > 665 { pageCount += 1; continue }
                        for i in totalRow.indices {
                            let cell = CGRect(x: rects[i].minX, y: y, width: widths[i], height: 24)
                            UIColor(white: 0.93, alpha: 1).setFill(); cg.fill(cell)
                            UIColor.darkGray.setStroke(); cg.stroke(cell, width: 0.5)
                            PDFText.draw(totalRow[i], in: cell.insetBy(dx: 3, dy: 1), font: bold)
                            if !totalRow[i].isEmpty { placements.append((pageCount, cell, totalRow[i], i)) }
                        }
                        PDFText.draw("عضو اللجنة الأول\n\(period.memberFirst)", in: CGRect(x: 315, y: 705, width: 225, height: 60), font: bold, wrap: true)
                        PDFText.draw("عضو اللجنة الثاني\n\(period.memberSecond)", in: CGRect(x: 55, y: 705, width: 225, height: 60), font: bold, wrap: true)
                        finalDrawn = true
                    }
                    pageCount += 1
                } while !finalDrawn
            }
            try FileManager.default.setAttributes([.protectionKey: FileProtectionType.complete], ofItemAtPath: destination.path)
            guard let pdf = PDFDocument(url: destination), pdf.pageCount == pageCount, !pdf.isLocked else { throw Failure.damaged }
            func identity(_ value: String) -> String {
                let normalized = ArabicText.normalize(ArabicText.latinDigits(value.precomposedStringWithCompatibilityMapping))
                var scalars = String.UnicodeScalarView()
                for scalar in normalized.unicodeScalars where !CharacterSet.whitespacesAndNewlines.contains(scalar) && scalar.properties.generalCategory != .format { scalars.append(scalar) }
                return String(scalars)
            }
            for (pageIndex, rect, expected, column) in placements {
                guard let output = pdf.page(at: pageIndex) else { throw Failure.damaged }
                let box = CGRect(x: rect.minX + 0.2, y: page.height - rect.maxY + 0.2, width: rect.width - 0.4, height: rect.height - 0.4)
                let actual = identity(output.selection(for: box)?.string ?? "")
                let wanted = identity(expected)
                let matches = column == 1 ? TransferAnbarTablePDF.matchesExtractedCell(actual, expected: wanted) : actual == wanted
                guard matches else { throw Failure.damaged }
            }
            done = true
            return destination
        }
    }
}
