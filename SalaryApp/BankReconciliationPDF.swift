import UIKit
import PDFKit
import CoreText

/// كشف المطابقة مستقل عن إثبات التحويل، ويحافظ على مسافة التوقيع في آخر صفحة.
nonisolated enum BankReconciliationPDF {
    enum Failure: LocalizedError {
        case empty, tooLong, damaged, missingImages
        case verification(page: Int, area: String, row: Int)
        #if targetEnvironment(simulator)
        case debugVerification(actual: String, expected: String, column: Int)
        #endif
        var errorDescription: String? {
            switch self {
            case .empty: tr("لا توجد حركات لعرضها في الكشف.", "There are no movements to display.")
            case .tooLong: tr("أحد بيانات الحركة لا يتسع بوضوح؛ اختصر البيان ثم حاول مجدداً.", "A movement does not fit clearly; shorten its description and try again.")
            case .damaged: tr("تعذّر فتح ملف كشف المطابقة بعد إنشائه.", "Could not open the generated reconciliation statement.")
            case .missingImages: tr("تعذّر تحميل شعار الوزارة للكشف.", "Could not load the ministry logo for the statement.")
            #if targetEnvironment(simulator)
            case .debugVerification(let actual, let expected, let column):
                "PDF verification debug column \(column): actual=[\(actual)] expected=[\(expected)]"
            #endif
            case .verification(let page, let area, let row):
                tr("تعذّر التحقق من \(area) في الصفحة \(page)، السطر \(row). لم يُصدّر الكشف.", "Could not verify \(area) on page \(page), line \(row). The statement was not exported.")
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
        return ReportSheet.easternDigits(formatter.string(from: value))
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
    /// قد يعيد PDFKit ترتيب أجزاء التاريخ المعروضة باتجاه معاكس دون تغيير أرقامها.
    static func matchesDate(_ actual: String, expected: String) -> Bool {
        func parts(_ value: String) -> [String]? {
            let normalized = ArabicText.latinDigits(value.precomposedStringWithCompatibilityMapping)
            guard normalized.unicodeScalars.allSatisfy({ CharacterSet.decimalDigits.contains($0) || CharacterSet.whitespacesAndNewlines.contains($0) || $0 == "/" || $0.properties.generalCategory == .format }) else { return nil }
            let runs = normalized.components(separatedBy: CharacterSet.decimalDigits.inverted).filter { !$0.isEmpty }
            return runs.isEmpty ? nil : runs
        }
        guard let left = parts(actual), let right = parts(expected) else { return false }
        return left == right || left == Array(right.reversed())
    }
    /// قد تنقل قراءة PDF السنة من آخر البيان العربي إلى أوله لاتجاه النص.
    static func matchesDescription(_ actual: String, expected: String) -> Bool {
        if TransferAnbarTablePDF.matchesExtractedCell(actual, expected: expected) { return true }
        func trailingDigits(_ value: String) -> (String, String)? {
            let suffix = value.reversed().prefix { $0.isASCII && $0.isNumber }
            guard !suffix.isEmpty else { return nil }
            let digits = String(suffix.reversed())
            return (String(value.dropLast(digits.count)), digits)
        }
        guard let (text, digits) = trailingDigits(expected), !text.isEmpty, actual.hasPrefix(digits) else { return false }
        let readText = String(actual.dropFirst(digits.count))
        return TransferAnbarTablePDF.matchesExtractedCell(readText, expected: text)
    }
    /// سياق المطبوع يستخدم إحداثيات UIKit؛ تعكس الصورة كي يظهر الشعار باتجاهه الصحيح.
    private static func drawImage(_ image: CGImage, in rect: CGRect, alpha: CGFloat, context: CGContext) {
        context.saveGState()
        context.setAlpha(alpha)
        context.interpolationQuality = .high
        context.translateBy(x: 0, y: page.height)
        context.scaleBy(x: 1, y: -1)
        context.draw(image, in: CGRect(x: rect.minX, y: page.height - rect.maxY, width: rect.width, height: rect.height))
        context.restoreGState()
    }
    static func make(_ period: BankReconciliation.Period, complete: Bool = false) async throws -> URL {
        try await PDFWork.run {
            let totals = try BankReconciliation.calculate(period)
            guard !totals.lines.isEmpty else { throw Failure.empty }
            let font = ReportFont.regular(9), bold = ReportFont.bold(9)
            let headingFont = ReportFont.bold(13)
            let heading = "كشف مطابقة حساب البنك لقيادة شرطة محافظة الانبار"
            guard let logo = UIImage(named: "HomeLogo")?.cgImage,
                  let watermark = UIImage(named: "WatermarkLogo")?.cgImage ?? UIImage(named: "HomeLogo")?.cgImage else { throw Failure.missingImages }
            guard lines(heading, font: headingFont, width: 487) == 1 else { throw Failure.tooLong }
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
            let signatures = [BankReconciliation.signatureLines(period, first: true), BankReconciliation.signatureLines(period, first: false)]
            guard zip(totalRow, widths).allSatisfy({ lines($0.0, font: bold, width: $0.1 - 6) == 1 }),
                  signatures.flatMap({ $0 }).allSatisfy({ lines($0, font: bold, width: 217) == 1 && !$0.unicodeScalars.contains(where: { $0.value < 32 }) }) else { throw Failure.tooLong }
            let destination = FileManager.default.temporaryDirectory.appendingPathComponent("bank-reconciliation-\(UUID().uuidString).pdf")
            var done = false
            defer { if !done { try? FileManager.default.removeItem(at: destination) } }
            var placements: [(Int, CGRect, String, Int, Int)] = []
            var pageCount = 0
            let renderer = UIGraphicsPDFRenderer(bounds: page)
            try renderer.writePDF(to: destination) { context in
                let cg = context.cgContext
                var index = 0, finalDrawn = false
                repeat {
                    context.beginPage()
                    // يبقى الشعار خلف الحركات ولا يغيّر بيانات الكشف أو حساباته.
                    drawImage(watermark, in: CGRect(x: 120, y: 280, width: 355, height: 355), alpha: 0.06, context: cg)
                    UIColor(white: 0.45, alpha: 1).setStroke()
                    cg.stroke(CGRect(x: 21, y: 18, width: 553, height: 804), width: 0.6)
                    drawImage(logo, in: CGRect(x: 36, y: 27, width: 72, height: 72), alpha: 1, context: cg)
                    for (index, value) in ["جمهورية العراق", "وزارة الداخلية", "قيادة شرطة محافظة الأنبار", "قسم الحسابات"].enumerated() {
                        PDFText.draw(value, in: CGRect(x: 320, y: 27 + CGFloat(index) * 15, width: 235, height: 15), font: bold, align: .right)
                    }
                    let headingBox = CGRect(x: 50, y: 115, width: 495, height: 35)
                    UIColor.darkGray.setStroke(); cg.stroke(headingBox, width: 0.7)
                    PDFText.draw(heading, in: headingBox.insetBy(dx: 4, dy: 2), font: headingFont)
                    PDFText.draw("الفترة من \(date(period.start)) إلى \(date(period.end))", in: CGRect(x: margin, y: 157, width: 531, height: 20), font: font)
                    if !complete { PDFText.draw("مسودة · المطابقة غير مكتملة", in: CGRect(x: margin, y: 176, width: 531, height: 13), font: bold, color: .darkGray) }
                    var x = page.width - margin
                    var rects: [CGRect] = []
                    for (i, width) in widths.enumerated() {
                        x -= width
                        let cell = CGRect(x: x, y: 193, width: width, height: 24)
                        rects.append(cell)
                        UIColor(white: 0.93, alpha: 1).setFill(); cg.fill(cell)
                        UIColor.darkGray.setStroke(); cg.stroke(cell, width: 0.5)
                        PDFText.draw(labels[i], in: cell.insetBy(dx: 3, dy: 1), font: bold)
                    }
                    var y: CGFloat = 217
                    while index < rows.count, y + rows[index].1 <= 665 {
                        let (values, height) = rows[index]
                        for i in values.indices {
                            let cell = CGRect(x: rects[i].minX, y: y, width: widths[i], height: height)
                            UIColor.darkGray.setStroke(); cg.stroke(cell, width: 0.5)
                            PDFText.draw(values[i], in: cell.insetBy(dx: 4, dy: 2), font: font, align: i == 1 ? .right : .center, wrap: true)
                            if values[i] != "—" && !values[i].isEmpty { placements.append((pageCount, cell, values[i], i, index + 1)) }
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
                            if !totalRow[i].isEmpty { placements.append((pageCount, cell, totalRow[i], i, rows.count + 1)) }
                        }
                        for (column, values) in signatures.enumerated() {
                            let x: CGFloat = column == 0 ? 315 : 55
                            for (line, value) in values.enumerated() {
                                let rect = CGRect(x: x, y: 700 + CGFloat(line) * 18, width: 225, height: 18)
                                PDFText.draw(value, in: rect, font: bold)
                                if !value.isEmpty { placements.append((pageCount, rect, value, line == 3 ? 6 : 5, line + 1)) }
                            }
                        }
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
            for (pageIndex, rect, expected, column, row) in placements {
                guard let output = pdf.page(at: pageIndex) else { throw Failure.damaged }
                let box = CGRect(x: rect.minX + 0.2, y: page.height - rect.maxY + 0.2, width: rect.width - 0.4, height: rect.height - 0.4)
                let actual = identity(output.selection(for: box)?.string ?? "")
                let wanted = identity(expected)
                let isDate = column == 0 || column == 6
                let matches = actual == wanted || (isDate && matchesDate(actual, expected: wanted)) ||
                    (column == 1 && matchesDescription(actual, expected: wanted)) ||
                    (column == 5 && TransferAnbarTablePDF.matchesExtractedCell(actual, expected: wanted))
                guard matches else {
                    #if targetEnvironment(simulator)
                    if ProcessInfo.processInfo.arguments.contains("-transferFilesTest") {
                        throw Failure.debugVerification(actual: actual, expected: wanted, column: column)
                    }
                    #endif
                    let area = column >= 5 ? tr("التوقيع", "signature") : [tr("التاريخ", "date"), tr("البيان", "description"), tr("الدائن", "credit"), tr("المدين", "debit"), tr("الرصيد", "balance")][column]
                    throw Failure.verification(page: pageIndex + 1, area: area, row: row)
                }
            }
            done = true
            return destination
        }
    }
}
