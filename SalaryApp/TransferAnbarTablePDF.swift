import UIKit
import PDFKit

/// جدول تجريبي لكتاب الرافدين فرع الأنبار. يجب تفريغ الجدول القديم قبل استدعائه.
nonisolated enum TransferAnbarTablePDF {
    enum Failure: LocalizedError {
        case invalidSource, missingFont, tableMismatch
        case tableCellMismatch(row: Int, column: Int, actualCount: Int, expectedCount: Int)
        #if targetEnvironment(simulator)
        case syntheticMismatch(String)
        #endif
        var errorDescription: String? {
            switch self {
            case .invalidSource: tr("تعذّر التحقق من بيانات جدول فرع الأنبار.", "Could not verify the Anbar branch table data.")
            case .missingFont: tr("خط جدول فرع الأنبار غير متوفر.", "The Anbar branch table font is unavailable.")
            case .tableMismatch: tr("تعذّر مطابقة أسماء جدول فرع الأنبار ورتبهم ومبالغهم مع بيانات الشهر.", "Could not reconcile the rendered Anbar table names, ranks, and amounts with the month's data.")
            case .tableCellMismatch(let row, let column, let actualCount, let expectedCount):
                tr("تعذّر تدقيق جدول الأنبار في الصف \(row) والعمود \(column) (طول النص المقروء \(actualCount)، المتوقع \(expectedCount)).", "Anbar table verification failed at row \(row), column \(column) (read length \(actualCount), expected \(expectedCount)).")
            #if targetEnvironment(simulator)
            case .syntheticMismatch(let value): value
            #endif
            }
        }
    }
    private static let size = CGSize(width: 595.32, height: 841.92)
    private static let widths: [CGFloat] = [40.2, 64.7, 148.8, 74.4, 180]
    private static let header: CGFloat = 16.4
    private static let totalHeight: CGFloat = 14.5
    private static let inlineTop: CGFloat = 416.1
    private static let inlineBottom: CGFloat = 572.3
    private static let separateTop: CGFloat = 55
    private static let separateBottom: CGFloat = 808

    /// قد يعيد PDFKit شكل الهاء من خط Arial بالرمز U+06BE رغم أن المصدر U+0647.
    /// لا نعمّم ذلك على المصدر إذا كان يحتوي U+06BE حتى لا نغيّر معنى الاسم.
    static func matchesExtractedCell(_ read: String, expected: String) -> Bool {
        if read == expected { return true }
        guard !expected.contains("\u{06BE}") else { return false }
        return read.replacingOccurrences(of: "\u{06BE}", with: "\u{0647}") == expected
    }

    static func make(blankLetter: URL, plan: TransferIndividualPlan) async throws -> URL {
        guard case .rafidainAnbar = plan.kind, !plan.rows.isEmpty,
              plan.rows.allSatisfy({ !$0.rank.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }) else { throw Failure.invalidSource }
        let rows: [[String]] = plan.rows.enumerated().map { index, row in
            [String(index + 1), row.rank, row.name,
             String(format: "%.0f", locale: Locale(identifier: "en_US_POSIX"), row.amount), row.entity]
        }
        let total = String(format: "%.0f", locale: Locale(identifier: "en_US_POSIX"), plan.group.total)
        return try await PDFWork.run {
            guard let original = PDFDocument(url: blankLetter), !original.isLocked, original.pageCount == 1,
                  let page = original.page(at: 0), abs(page.bounds(for: .mediaBox).width - size.width) < 2,
                  abs(page.bounds(for: .mediaBox).height - size.height) < 2 else { throw Failure.invalidSource }
            guard let bold = UIFont(name: "Arial-BoldMT", size: 12),
                  let face = UIFont(name: "Arial-BoldMT", size: 10.8) else { throw Failure.missingFont }
            _ = try TransferSinglePageFit.fit(rows: [[total]], widths: [widths[3]], availableHeight: totalHeight,
                                               fontName: face.fontName, maximumSize: 10.8, minimumSize: 10.8, verticalPadding: 1)
            let inlineHeight = inlineBottom - inlineTop - header - totalHeight
            let inline = try? TransferSinglePageFit.fit(rows: rows, widths: widths, availableHeight: inlineHeight,
                fontName: face.fontName, maximumSize: 10.8, minimumSize: 10.8, verticalPadding: 1)
            let onSeparatePage = inline == nil
            let layout = try inline ?? TransferSinglePageFit.fit(rows: rows, widths: widths,
                availableHeight: separateBottom - separateTop - header - totalHeight, fontName: face.fontName,
                maximumSize: 10.8, minimumSize: 7, verticalPadding: 1)
            guard let font = UIFont(name: face.fontName, size: layout.fontSize) else { throw Failure.missingFont }
            let folder = FileManager.default.temporaryDirectory.appendingPathComponent("anbar-table-\(UUID().uuidString)", isDirectory: true)
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            let url = folder.appendingPathComponent("كتاب فرع الأنبار.pdf")
            var complete = false
            defer { if !complete { try? FileManager.default.removeItem(at: folder) } }
            try UIGraphicsPDFRenderer(bounds: CGRect(origin: .zero, size: size)).writePDF(to: url) { context in
                context.beginPage()
                let cg = context.cgContext
                cg.saveGState(); cg.translateBy(x: 0, y: size.height); cg.scaleBy(x: 1, y: -1)
                page.draw(with: .mediaBox, to: cg)
                cg.restoreGState()
                if onSeparatePage { context.beginPage() }
                let top = onSeparatePage ? separateTop : inlineTop
                var right = size.width - 35.72
                let positions = widths.map { width -> CGFloat in right -= width; return right }
                func cell(_ text: String, _ column: Int, _ y: CGFloat, _ height: CGFloat, _ textFont: UIFont, fill: UIColor? = nil, color: UIColor = .black) {
                    let rect = CGRect(x: positions[column], y: y, width: widths[column], height: height)
                    if let fill { fill.setFill(); cg.fill(rect) }
                    UIColor.black.setStroke(); cg.stroke(rect, width: 0.5)
                    PDFText.draw(text, in: rect.insetBy(dx: 4, dy: 0.5), font: textFont, color: color, wrap: true)
                }
                let headerColor = UIColor(red: 0.122, green: 0.306, blue: 0.471, alpha: 1)
                let stripeColor = UIColor(red: 0.918, green: 0.949, blue: 0.973, alpha: 1)
                let totalColor = UIColor(red: 0.851, green: 0.918, blue: 0.969, alpha: 1)
                for (i, title) in ["ت", "الرتبة", "الاسم الكامل", "المبلغ", "جهة التنفيذ"].enumerated() { cell(title, i, top, header, bold, fill: headerColor, color: .white) }
                var y = top + header
                for (index, pair) in zip(rows, layout.rowHeights).enumerated() {
                    let (row, height) = pair
                    for i in row.indices { cell(row[i], i, y, height, font, fill: index.isMultiple(of: 2) ? nil : stripeColor) }
                    y += height
                }
                for i in 0..<widths.count { cell(i == 2 ? "المجموع" : i == 3 ? total : "", i, y, totalHeight, face, fill: totalColor) }
            }
            guard let document = PDFDocument(url: url), !document.isLocked,
                  document.pageCount == (onSeparatePage ? 2 : 1),
                  let outputPage = document.page(at: onSeparatePage ? 1 : 0) else { throw Failure.tableMismatch }
            var right = size.width - 35.72
            let positions = widths.map { width -> CGFloat in right -= width; return right }
            func text(_ column: Int, _ y: CGFloat, _ height: CGFloat) -> String {
                let rect = CGRect(x: positions[column] + 0.2, y: size.height - y - height + 0.2,
                                  width: widths[column] - 0.4, height: height - 0.4)
                return outputPage.selection(for: rect)?.string ?? ""
            }
            func identity(_ value: String) -> String {
                let normalized = ArabicText.normalize(ArabicText.latinDigits(value.precomposedStringWithCompatibilityMapping))
                var scalars = String.UnicodeScalarView()
                for scalar in normalized.unicodeScalars where !CharacterSet.whitespacesAndNewlines.contains(scalar) && scalar.properties.generalCategory != .format {
                    scalars.append(scalar)
                }
                return String(scalars)
            }
            let top = onSeparatePage ? separateTop : inlineTop
            var y = top + header
            for (index, pair) in zip(rows, layout.rowHeights).enumerated() {
                let (row, height) = pair
                for column in row.indices {
                    let read = identity(text(column, y, height))
                    let expected = identity(row[column])
                    if !matchesExtractedCell(read, expected: expected) {
                        #if targetEnvironment(simulator)
                        if ProcessInfo.processInfo.arguments.contains("-transferFilesTest"), row[column] == "جهة وهمية" {
                            let actualCodes = read.unicodeScalars.map { String($0.value, radix: 16) }.joined(separator: ",")
                            let expectedCodes = expected.unicodeScalars.map { String($0.value, radix: 16) }.joined(separator: ",")
                            throw Failure.syntheticMismatch("ANBAR_SYNTHETIC_ACTUAL=\(actualCodes) EXPECTED=\(expectedCodes)")
                        }
                        #endif
                        throw Failure.tableCellMismatch(row: index + 1, column: column + 1, actualCount: read.count, expectedCount: expected.count)
                    }
                }
                y += height
            }
            guard identity(text(3, y, totalHeight)) == identity(total) else { throw Failure.tableMismatch }
            try FileManager.default.setAttributes([.protectionKey: FileProtectionType.complete], ofItemAtPath: url.path)
            complete = true
            return url
        }
    }
}
