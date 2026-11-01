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
    private static let tableBottom: CGFloat = 665
    private static let totalHeight: CGFloat = 24
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
        let text = NSAttributedString(string: value, attributes: [.font: font, NSAttributedString.Key(kCTParagraphStyleAttributeName as String): PDFText.rtl])
        let setter = CTTypesetterCreateWithAttributedString(text)
        var offset = 0, count = 0
        while offset < text.length {
            let n = CTTypesetterSuggestLineBreak(setter, offset, Double(width))
            if n <= 0 || n > text.length - offset { return Int.max }
            let line = CTTypesetterCreateLine(setter, CFRange(location: offset, length: n))
            if CTLineGetTypographicBounds(line, nil, nil, nil) - CTLineGetTrailingWhitespaceWidth(line) > Double(width) + 0.5 { return Int.max }
            offset += n; count += 1
        }
        return max(1, count)
    }
    private static func textWidth(_ value: String, font: UIFont) -> CGFloat {
        let text = NSAttributedString(string: value, attributes: [.font: font, NSAttributedString.Key(kCTParagraphStyleAttributeName as String): PDFText.rtl])
        let line = CTLineCreateWithAttributedString(text)
        return CGFloat(CTLineGetTypographicBounds(line, nil, nil, nil) - CTLineGetTrailingWhitespaceWidth(line))
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
    /// قد يشمل تحديد PDFKit شرطة الخانة المجاورة عند ملامسة حدود الخلايا.
    static func trimAdjacentPlaceholder(_ actual: String, expected: String) -> String {
        guard !expected.contains("—") else { return actual }
        return actual.trimmingCharacters(in: CharacterSet(charactersIn: "—"))
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
    static func make(_ period: BankReconciliation.Period, complete: Bool = false, account: BankReconciliation.AccountDetails = .init()) async throws -> URL {
        try await PDFWork.run {
            let totals = try BankReconciliation.calculate(period)
            guard !totals.lines.isEmpty else { throw Failure.empty }
            let bold = ReportFont.bold(9)
            let signatureFont = ReportFont.bold(12)
            let headingFont = ReportFont.bold(12)
            let organizationFont = ReportFont.bold(10)
            let metadataFont = ReportFont.regular(10)
            let periodFont = ReportFont.bold(8)
            let heading = "كشف مطابقة حساب البنك"
            let organization = ["جمهورية العراق", "وزارة الداخلية", "وكالة الوزارة لشؤون الشرطة", "قيادة شرطة محافظة الأنبار", "قسم الحسابات"]
            guard let watermark = UIImage(named: "WatermarkLogo")?.cgImage ?? UIImage(named: "HomeLogo")?.cgImage else { throw Failure.missingImages }
            func value(_ raw: String) throws -> String {
                let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
                guard trimmed.count <= 100, !trimmed.unicodeScalars.contains(where: { $0.value < 32 }) else { throw Failure.tooLong }
                return trimmed.isEmpty ? "—" : trimmed
            }
            let accountName = try value(account.name)
            let metadata = [("اسم الحساب", accountName, 11), ("الفرع", try value(account.branch), 7),
                            ("الحساب", try value(account.localAccount), 8), ("العملة", "IQD", 9), ("IBAN", try value(account.iban), 10)]
            guard lines(heading, font: headingFont, width: 223) == 1,
                  organization.allSatisfy({ lines($0, font: organizationFont, width: 150) == 1 }),
                  metadata.allSatisfy({ lines($0.0 + ":", font: metadataFont, width: 80) == 1 }) else { throw Failure.tooLong }
            let organizationTop: CGFloat = 25
            let headingTop: CGFloat = 25
            let periodTop: CGFloat = 46
            let metadataTop: CGFloat = 25
            let periodText = "الــفــتــرة مــن \(date(period.start)) إلــى \(date(period.end))"
            guard lines(periodText, font: periodFont, width: 223) == 1 else { throw Failure.tooLong }
            // يظل العنوان والفترة في المنتصف وتتكيف خانات الحساب مع المساحة المتاحة يسارهما.
            let metadataRight = min(306, page.midX - max(textWidth(heading, font: headingFont), textWidth(periodText, font: periodFont)) / 2 - 8)
            var metadataHeights: [CGFloat] = []
            var metadataLabelWidths: [CGFloat] = []
            var metadataValueWidths: [CGFloat] = []
            for item in metadata {
                let labelWidth = ceil(textWidth(item.0 + ":", font: metadataFont) + 2)
                let valueWidth = metadataRight - margin - labelWidth - 6
                guard valueWidth >= 50 else { throw Failure.tooLong }
                let count = lines(item.1, font: metadataFont, width: valueWidth)
                guard count != Int.max, count <= 2 else { throw Failure.tooLong }
                metadataLabelWidths.append(labelWidth)
                metadataValueWidths.append(valueWidth)
                metadataHeights.append(max(19, ceil(CGFloat(count) * metadataFont.lineHeight + 4)))
            }
            let contentBottom = max(metadataTop + metadataHeights.reduce(0, +), organizationTop + 75, periodTop + 16)
            let tableTop = contentBottom + 18
            let rowTop = tableTop + 24
            guard rowTop + totalHeight < tableBottom else { throw Failure.tooLong }
            let labels = ["التاريخ", "البيان", "دائن", "مدين", "الرصيد"]
            var valuesByRow: [[String]] = []
            if period.openingBalance != 0 {
                valuesByRow.append([date(period.start), "الرصيد الافتتاحي", "—", "—", try number(period.openingBalance)])
            }
            for line in totals.lines {
                let item = line.movement
                valuesByRow.append([date(item.date), item.description, item.direction == .credit ? try number(item.amount) : "—",
                                    item.direction == .debit ? try number(item.amount) : "—", try number(line.balance)])
            }
            let totalRow = ["", "المجموع", try number(totals.credits), try number(totals.debits), try number(totals.balance)]
            // نستخدم أكبر خط يتيح صفحة واحدة، وإلا نحافظ على الحجم الواضح ونقسم الكشف.
            func layout(size: CGFloat, minimumHeight: CGFloat) -> [(values: [String], height: CGFloat)]? {
                let candidate = ReportFont.regular(size)
                var result: [(values: [String], height: CGFloat)] = []
                for values in valuesByRow {
                    let counts = zip(values, widths).map { lines($0.0, font: candidate, width: $0.1 - 8) }
                    guard counts.allSatisfy({ $0 != Int.max && $0 <= 5 }), counts[0] == 1,
                          counts[2] == 1, counts[3] == 1, counts[4] == 1 else { return nil }
                    let height = max(minimumHeight, ceil(CGFloat(counts.max() ?? 1) * candidate.lineHeight + 4))
                    guard height <= tableBottom - rowTop - totalHeight else { return nil }
                    result.append((values, height))
                }
                return result
            }
            let candidates: [(CGFloat, CGFloat)] = [(9, 18), (8.5, 17), (8, 16)]
            var firstValid: (UIFont, [(values: [String], height: CGFloat)])?
            var singlePage: (UIFont, [(values: [String], height: CGFloat)])?
            for (size, height) in candidates {
                guard let rows = layout(size: size, minimumHeight: height) else { continue }
                let candidate = (ReportFont.regular(size), rows)
                if firstValid == nil { firstValid = candidate }
                if rows.reduce(CGFloat(0), { $0 + $1.height }) + totalHeight <= tableBottom - rowTop {
                    singlePage = candidate
                    break
                }
            }
            guard let selected = singlePage ?? firstValid else { throw Failure.tooLong }
            let font = selected.0, rows = selected.1
            let signatures = [BankReconciliation.signatureLines(period, first: true), BankReconciliation.signatureLines(period, first: false)]
            guard zip(totalRow, widths).allSatisfy({ lines($0.0, font: bold, width: $0.1 - 6) == 1 }) else { throw Failure.tooLong }
            var signatureHeights: [[CGFloat]] = []
            for values in signatures {
                var heights: [CGFloat] = []
                for item in values {
                    let count = lines(item, font: signatureFont, width: 237)
                    guard count != Int.max, count <= 2, !item.unicodeScalars.contains(where: { $0.value < 32 }) else { throw Failure.tooLong }
                    heights.append(max(22, ceil(CGFloat(count) * signatureFont.lineHeight + 3)))
                }
                guard heights.reduce(0, +) <= 126 else { throw Failure.tooLong }
                signatureHeights.append(heights)
            }
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
                    var metadataY = metadataTop
                    for (index, item) in metadata.enumerated() {
                        let height = metadataHeights[index]
                        let valueRect = CGRect(x: margin, y: metadataY, width: metadataValueWidths[index], height: height)
                        let labelRect = CGRect(x: metadataRight - metadataLabelWidths[index], y: metadataY, width: metadataLabelWidths[index], height: height)
                        PDFText.draw(item.0 + ":", in: labelRect, font: metadataFont, align: .right)
                        PDFText.draw(item.1, in: valueRect, font: metadataFont, align: .left, wrap: true)
                        if item.1 != "—" { placements.append((pageCount, valueRect, item.1, item.2, index + 1)) }
                        metadataY += height
                    }
                    for (index, value) in organization.enumerated() {
                        PDFText.draw(value, in: CGRect(x: 412, y: organizationTop + CGFloat(index) * 15, width: 150, height: 15), font: organizationFont, align: .center)
                    }
                    PDFText.draw(heading, in: CGRect(x: 167.5, y: headingTop, width: 260, height: 20), font: headingFont)
                    PDFText.draw(periodText, in: CGRect(x: 167.5, y: periodTop, width: 260, height: 16), font: periodFont)
                    if !complete { PDFText.draw("مسودة · المطابقة غير مكتملة", in: CGRect(x: margin, y: contentBottom + 2, width: 531, height: 14), font: bold, color: .darkGray) }
                    var x = page.width - margin
                    var rects: [CGRect] = []
                    for (i, width) in widths.enumerated() {
                        x -= width
                        let cell = CGRect(x: x, y: tableTop, width: width, height: 24)
                        rects.append(cell)
                        UIColor(white: 0.93, alpha: 1).setFill(); cg.fill(cell)
                        UIColor.darkGray.setStroke(); cg.stroke(cell, width: 0.5)
                        PDFText.draw(labels[i], in: cell.insetBy(dx: 3, dy: 1), font: bold)
                    }
                    var y = rowTop
                    while index < rows.count, y + rows[index].1 <= tableBottom {
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
                        if y + totalHeight > tableBottom { pageCount += 1; continue }
                        for i in totalRow.indices {
                            let cell = CGRect(x: rects[i].minX, y: y, width: widths[i], height: 24)
                            UIColor(white: 0.93, alpha: 1).setFill(); cg.fill(cell)
                            UIColor.darkGray.setStroke(); cg.stroke(cell, width: 0.5)
                            PDFText.draw(totalRow[i], in: cell.insetBy(dx: 3, dy: 1), font: bold)
                            if !totalRow[i].isEmpty { placements.append((pageCount, cell, totalRow[i], i, rows.count + 1)) }
                        }
                        for (column, values) in signatures.enumerated() {
                            let x: CGFloat = column == 0 ? 314 : 36
                            var signatureY: CGFloat = 690
                            for (line, value) in values.enumerated() {
                                let height = signatureHeights[column][line]
                                let rect = CGRect(x: x, y: signatureY, width: 245, height: height)
                                PDFText.draw(value, in: rect.insetBy(dx: 4, dy: 0), font: signatureFont, color: .black, wrap: true)
                                if !value.isEmpty { placements.append((pageCount, rect, value, line == 3 ? 6 : 5, line + 1)) }
                                signatureY += height
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
                let wanted = identity(expected)
                let actual = trimAdjacentPlaceholder(identity(output.selection(for: box)?.string ?? ""), expected: wanted)
                let isDate = column == 0 || column == 6
                let matches = actual == wanted || (isDate && matchesDate(actual, expected: wanted)) ||
                    (column == 1 && matchesDescription(actual, expected: wanted)) ||
                    ([5, 7, 11].contains(column) && TransferAnbarTablePDF.matchesExtractedCell(actual, expected: wanted))
                guard matches else {
                    #if targetEnvironment(simulator)
                    if ProcessInfo.processInfo.arguments.contains("-transferFilesTest") {
                        throw Failure.debugVerification(actual: actual, expected: wanted, column: column)
                    }
                    #endif
                    let area: String
                    switch column {
                    case 0: area = tr("التاريخ", "date")
                    case 1: area = tr("البيان", "description")
                    case 2: area = tr("الدائن", "credit")
                    case 3: area = tr("المدين", "debit")
                    case 4: area = tr("الرصيد", "balance")
                    case 5, 6: area = tr("التوقيع", "signature")
                    case 7: area = tr("الفرع", "branch")
                    case 8: area = tr("رقم الحساب", "account number")
                    case 9: area = tr("العملة", "currency")
                    case 10: area = "IBAN"
                    default: area = tr("اسم الحساب", "account name")
                    }
                    throw Failure.verification(page: pageIndex + 1, area: area, row: row)
                }
            }
            done = true
            return destination
        }
    }
}
