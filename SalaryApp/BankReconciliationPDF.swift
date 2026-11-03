import UIKit
import PDFKit
import CoreText

/// كشف المطابقة مستقل عن إثبات التحويل، ويحافظ على مسافة التوقيع في آخر صفحة.
nonisolated enum BankReconciliationPDF {
    enum Failure: LocalizedError {
        case empty, tooLong, damaged
        case verification(page: Int, area: String, row: Int)
        #if targetEnvironment(simulator)
        case debugVerification(actual: String, expected: String, column: Int)
        #endif
        var errorDescription: String? {
            switch self {
            case .empty: tr("لا توجد حركات لعرضها في الكشف.", "There are no movements to display.")
            case .tooLong: tr("أحد بيانات الحركة لا يتسع بوضوح؛ اختصر البيان ثم حاول مجدداً.", "A movement does not fit clearly; shorten its description and try again.")
            case .damaged: tr("تعذّر فتح ملف كشف المطابقة بعد إنشائه.", "Could not open the generated reconciliation statement.")
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
    private static let totalHeight: CGFloat = 26
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
    static func make(_ period: BankReconciliation.Period, complete: Bool = false, account: BankReconciliation.AccountDetails = .init()) async throws -> URL {
        try await PDFWork.run {
            let totals = try BankReconciliation.calculate(period)
            guard !totals.lines.isEmpty else { throw Failure.empty }
            // الأحجام (طلب المالك 2026-11-02، مطابق لويندوز ReconciliationPdf.Statement): Arial لكل الكشف؛ الترويسة 11 والعنوان 14،
            // رؤوس الأعمدة والمبالغ والمجموع عريض 11، البيان 10.5 ينزل إلى سطر آخر، الصف لا يقل عن 24 مع تظليل متناوب، والتواقيع عريض 16.
            let bold = ReportFont.bold(11)
            let signatureFont = ReportFont.bold(16)
            let headingFont = ReportFont.bold(14)
            let organizationFont = ReportFont.bold(11)
            let labelFont = ReportFont.bold(10.5)
            let valueFont = ReportFont.regular(10.5)
            let periodFont = ReportFont.bold(11)
            let font = ReportFont.regular(10.5)
            let amountFont = ReportFont.bold(11)
            let shade = UIColor(red: 0xF3 / 255, green: 0xF7 / 255, blue: 0xFB / 255, alpha: 1)
            let heading = "كشف مطابقة حساب البنك"
            let organization = ["جمهورية العراق", "وزارة الداخلية", "وكالة الوزارة لشؤون الشرطة", "قيادة شرطة محافظة الأنبار", "قسم الحسابات"]
            func value(_ raw: String) throws -> String {
                let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
                guard trimmed.count <= 100, !trimmed.unicodeScalars.contains(where: { $0.value < 32 }) else { throw Failure.tooLong }
                return trimmed.isEmpty ? "—" : trimmed
            }
            let periodText = "الــفــتــرة مــن \(date(period.start)) إلــى \(date(period.end))"
            guard lines(heading, font: headingFont, width: 250) == 1, lines(periodText, font: periodFont, width: 520) == 1,
                  organization.allSatisfy({ lines($0, font: organizationFont, width: 160) == 1 }) else { throw Failure.tooLong }
            // بيانات الحساب تحت العنوان يسار ترويسة الوزارة: (الاسم، الفرع) ثم (العملة، IBAN)؛ الحساب المحلي لا يظهر في الكشف.
            // السطر الأطول من المساحة يُقسم ولا يُقص. الرقم الثالث رمز التحقق من القراءة بعد الإنشاء.
            let accountLeft: CGFloat = margin, accountRight: CGFloat = 392, accountGap: CGFloat = 18, accountLine: CGFloat = 17
            let accountWidth = accountRight - accountLeft
            let groups: [[(String, String, Int)]] = [[("اسم الحساب", try value(account.name), 11), ("الفرع", try value(account.branch), 7)],
                                                     [("العملة", "IQD", 9), ("IBAN", try value(account.iban), 10)]]
            func segmentWidth(_ item: (String, String, Int)) -> CGFloat { textWidth(item.0 + ":", font: labelFont) + 5 + textWidth(item.1, font: valueFont) }
            var accountLines: [[(String, String, Int)]] = []
            for group in groups {
                var line: [(String, String, Int)] = []
                var width: CGFloat = 0
                for item in group {
                    let size = segmentWidth(item)
                    guard size <= accountWidth else { throw Failure.tooLong }
                    if !line.isEmpty && width + accountGap + size > accountWidth { accountLines.append(line); line = []; width = 0 }
                    width += (line.isEmpty ? 0 : accountGap) + size
                    line.append(item)
                }
                accountLines.append(line)
            }
            guard accountLines.count <= 4 else { throw Failure.tooLong }
            let organizationTop: CGFloat = 25, organizationLine: CGFloat = 16
            let accountTop: CGFloat = 56
            let contentBottom = max(organizationTop + CGFloat(organization.count) * organizationLine, accountTop + CGFloat(accountLines.count) * accountLine)
            // الفترة شريط بعرض الجدول فوقه مباشرة، و«مسودة» فوقها عند عدم الاكتمال.
            let periodHeight: CGFloat = 22, headerHeight: CGFloat = 26
            let periodTop = contentBottom + (complete ? 10 : 26)
            let tableTop = periodTop + periodHeight
            let rowTop = tableTop + headerHeight
            // تواقيع اللجنة عريض 16 بلا إطار ولا سطر توقيع، أسفل آخر صفحة؛ المنصب الطويل ينزل إلى سطر ثانٍ.
            let signatures = [BankReconciliation.signatureLines(period, first: true), BankReconciliation.signatureLines(period, first: false)]
            var signatureHeights: [[CGFloat]] = []
            for values in signatures {
                var heights: [CGFloat] = []
                for item in values {
                    let count = lines(item, font: signatureFont, width: 237)
                    guard count != Int.max, count <= 2, !item.unicodeScalars.contains(where: { $0.value < 32 }) else { throw Failure.tooLong }
                    heights.append(max(24, ceil(CGFloat(count) * signatureFont.lineHeight + 4)))
                }
                signatureHeights.append(heights)
            }
            let signatureHeight = signatureHeights.map { $0.reduce(0, +) }.max() ?? 0
            guard signatureHeight <= 170 else { throw Failure.tooLong }
            let signatureTop = 812 - signatureHeight
            let tableBottom = signatureTop - 16
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
            // البيان ينزل إلى سطر آخر بدل تصغير الخط، والكشف يمتد على صفحات.
            var rows: [(values: [String], height: CGFloat)] = []
            for values in valuesByRow {
                let description = lines(values[1], font: font, width: widths[1] - 8)
                guard description != Int.max, description <= 5, lines(values[0], font: font, width: widths[0] - 8) == 1,
                      (2...4).allSatisfy({ lines(values[$0], font: amountFont, width: widths[$0] - 8) == 1 }) else { throw Failure.tooLong }
                let height = max(24, ceil(CGFloat(description) * font.lineHeight + 6))
                guard height <= tableBottom - rowTop - totalHeight else { throw Failure.tooLong }
                rows.append((values, height))
            }
            guard zip(totalRow, widths).allSatisfy({ lines($0.0, font: bold, width: $0.1 - 6) == 1 }) else { throw Failure.tooLong }
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
                    UIColor(white: 0.45, alpha: 1).setStroke()
                    cg.stroke(CGRect(x: 21, y: 18, width: 553, height: 804), width: 0.6)
                    for (index, value) in organization.enumerated() {
                        PDFText.draw(value, in: CGRect(x: 400, y: organizationTop + CGFloat(index) * organizationLine, width: 162, height: organizationLine), font: organizationFont, align: .center)
                    }
                    PDFText.draw(heading, in: CGRect(x: 172.5, y: 27, width: 250, height: 24), font: headingFont)
                    for (lineIndex, segments) in accountLines.enumerated() {
                        let total = segments.reduce(CGFloat(0)) { $0 + segmentWidth($1) } + accountGap * CGFloat(segments.count - 1)
                        var right = (accountLeft + accountRight) / 2 + total / 2
                        let lineY = accountTop + CGFloat(lineIndex) * accountLine
                        for item in segments {
                            let label = item.0 + ":"
                            let labelWidth = textWidth(label, font: labelFont)
                            PDFText.draw(label, in: CGRect(x: right - labelWidth - 1, y: lineY, width: labelWidth + 2, height: accountLine), font: labelFont, align: .right)
                            right -= labelWidth + 5
                            let valueWidth = textWidth(item.1, font: valueFont)
                            let valueRect = CGRect(x: right - valueWidth - 0.5, y: lineY, width: valueWidth + 1, height: accountLine)
                            PDFText.draw(item.1, in: valueRect, font: valueFont, align: .center)
                            if item.1 != "—" { placements.append((pageCount, valueRect, item.1, item.2, lineIndex + 1)) }
                            right -= valueWidth + accountGap
                        }
                    }
                    if !complete { PDFText.draw("مسودة · المطابقة غير مكتملة", in: CGRect(x: margin, y: periodTop - 16, width: 531, height: 14), font: ReportFont.bold(9), color: .darkGray) }
                    let periodBar = CGRect(x: page.width - margin - 531, y: periodTop, width: 531, height: periodHeight)
                    shade.setFill(); cg.fill(periodBar)
                    UIColor.darkGray.setStroke(); cg.stroke(periodBar, width: 0.5)
                    PDFText.draw(periodText, in: periodBar.insetBy(dx: 4, dy: 1), font: periodFont)
                    var x = page.width - margin
                    var rects: [CGRect] = []
                    for (i, width) in widths.enumerated() {
                        x -= width
                        let cell = CGRect(x: x, y: tableTop, width: width, height: headerHeight)
                        rects.append(cell)
                        UIColor(white: 0.93, alpha: 1).setFill(); cg.fill(cell)
                        UIColor.darkGray.setStroke(); cg.stroke(cell, width: 0.5)
                        PDFText.draw(labels[i], in: cell.insetBy(dx: 3, dy: 1), font: bold)
                    }
                    var y = rowTop
                    while index < rows.count, y + rows[index].1 <= tableBottom {
                        let (values, height) = rows[index]
                        if index % 2 == 1 { shade.setFill(); cg.fill(CGRect(x: rects[4].minX, y: y, width: 531, height: height)) }
                        for i in values.indices {
                            let cell = CGRect(x: rects[i].minX, y: y, width: widths[i], height: height)
                            UIColor.darkGray.setStroke(); cg.stroke(cell, width: 0.5)
                            PDFText.draw(values[i], in: cell.insetBy(dx: 4, dy: 2), font: (2...4).contains(i) ? amountFont : font, align: i == 1 ? .right : .center, wrap: true)
                            if values[i] != "—" && !values[i].isEmpty { placements.append((pageCount, cell, values[i], i, index + 1)) }
                        }
                        y += height; index += 1
                    }
                    if index == rows.count {
                        if y + totalHeight > tableBottom { pageCount += 1; continue }
                        for i in totalRow.indices {
                            let cell = CGRect(x: rects[i].minX, y: y, width: widths[i], height: totalHeight)
                            UIColor(white: 0.93, alpha: 1).setFill(); cg.fill(cell)
                            UIColor.darkGray.setStroke(); cg.stroke(cell, width: 0.5)
                            PDFText.draw(totalRow[i], in: cell.insetBy(dx: 3, dy: 1), font: bold)
                            if !totalRow[i].isEmpty { placements.append((pageCount, cell, totalRow[i], i, rows.count + 1)) }
                        }
                        for (column, values) in signatures.enumerated() {
                            let x: CGFloat = column == 0 ? 306 : 44
                            var signatureY = signatureTop
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
