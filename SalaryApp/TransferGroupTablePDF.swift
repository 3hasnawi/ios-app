import UIKit
import PDFKit

/// صفحة جدول المجموعة؛ يبقى التصدير معطلاً حتى التحقق من المرجع على الجهاز.
nonisolated enum TransferGroupTablePDF {
    struct Checked: Sendable { let url: URL; let lines: [TransferReconcileLine] }
    enum Failure: LocalizedError {
        case invalidData, invalidTotal, missingFont, tableMismatch
        case cellMismatch(row: Int, column: Int, readCount: Int, expectedCount: Int)
        var errorDescription: String? {
            switch self {
            case .invalidData: tr("بيانات جهات التحويل غير مكتملة أو مكررة.", "Transfer entities are incomplete or duplicated.")
            case .invalidTotal: tr("تعذّر التأكد من مجموع جهات التحويل.", "Could not verify the transfer entity total.")
            case .missingFont: tr("خط جدول التحويل غير متوفر على الجهاز.", "The transfer table font is unavailable.")
            case .tableMismatch: tr("تعذّر مطابقة الأسماء والحسابات والمبالغ المرسومة في جدول التحويل مع بياناته.", "Could not reconcile the rendered transfer table names, accounts, and amounts with its source.")
            case .cellMismatch(let row, let column, let readCount, let expectedCount):
                tr("تعذّر تدقيق جدول التحويل في الصف \(row) والعمود \(column) (طول المقروء \(readCount)، المتوقع \(expectedCount)).", "Transfer table verification failed at row \(row), column \(column) (read length \(readCount), expected \(expectedCount)).")
            }
        }
    }
    private static let page = CGRect(x: 0, y: 0, width: 595.32, height: 841.92)
    private static let widths: [CGFloat] = [21.6, 137.9, 176.1, 120.5, 90.5]
    private static let top: CGFloat = 86.5
    private static let bottom: CGFloat = 808
    private static let header: CGFloat = 15
    private static let totalHeight: CGFloat = 20
    private static let monthNames = ["كانون الثاني", "شباط", "آذار", "نيسان", "أيار", "حزيران", "تموز", "آب", "أيلول", "تشرين الأول", "تشرين الثاني", "كانون الأول"]
    private static func number(_ value: Double) -> String {
        String(format: "%.0f", locale: Locale(identifier: "en_US_POSIX"), value)
    }
    static func make(entities: [TransferEntity], category: TransferCategory, month: String) async throws -> URL {
        try await makeChecked(entities: entities, category: category, month: month).url
    }
    static func makeChecked(entities: [TransferEntity], category: TransferCategory, month: String) async throws -> Checked {
        guard category == .rafidain || category == .otherBanks, !entities.isEmpty,
              Set(entities.map(\.id)).count == entities.count,
              entities.allSatisfy({ $0.category == category && $0.group.month == month && TransferRules.issues($0).isEmpty }) else { throw Failure.invalidData }
        let total = entities.reduce(0.0) { $0 + $1.group.total }
        guard total.isFinite, total > 0, total.rounded() == total, total <= 9_007_199_254_740_991 else { throw Failure.invalidTotal }
        let period = try TransferLetterPeriod(month)
        let title = "استقطاعات قيادة شرطة الانبار لشهر ( \(monthNames[period.month - 1]) / \(period.year) )"
        let rows = entities.enumerated().map { index, entity in
            [String(index + 1), entity.beneficiaryName ?? entity.group.title,
             ArabicText.latinDigits(entity.account?.iban ?? "").uppercased().filter { !$0.isWhitespace && $0 != "-" },
             entity.account?.branch ?? "", number(entity.group.total)]
        }
        return try await PDFWork.run {
            guard let bold = UIFont(name: "TimesNewRomanPS-BoldMT", size: 11),
                  let titleFont = UIFont(name: "TimesNewRomanPS-BoldMT", size: 14.04),
                  let fontName = UIFont(name: "Arial-BoldMT", size: 10.8)?.fontName else { throw Failure.missingFont }
            _ = try TransferSinglePageFit.fit(rows: [[title]], widths: [546.6], availableHeight: 25,
                fontName: titleFont.fontName, maximumSize: 14.04, minimumSize: 14.04, verticalPadding: 2)
            _ = try TransferSinglePageFit.fit(rows: [[number(total)]], widths: [widths[4]],
                availableHeight: totalHeight, fontName: bold.fontName, maximumSize: 11, minimumSize: 11, verticalPadding: 2)
            let fitting = try TransferSinglePageFit.fit(rows: rows, widths: widths,
                availableHeight: bottom - top - header - totalHeight, fontName: fontName,
                maximumSize: 10.8, minimumSize: 7, verticalPadding: 2)
            guard let font = UIFont(name: fontName, size: fitting.fontSize) else { throw Failure.missingFont }
            let folder = FileManager.default.temporaryDirectory.appendingPathComponent("transfer-group-\(UUID().uuidString)", isDirectory: true)
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            let url = folder.appendingPathComponent("جدول التحويل.pdf")
            var complete = false
            defer { if !complete { try? FileManager.default.removeItem(at: folder) } }
            try UIGraphicsPDFRenderer(bounds: page).writePDF(to: url) { context in
                context.beginPage()
                let cg = context.cgContext
                PDFText.draw(title, in: CGRect(x: 23.5, y: 60.8, width: 546.6, height: 15.6), font: titleFont)
                var right = page.width - 25.22
                let positions = widths.map { width -> CGFloat in right -= width; return right }
                func cell(_ text: String, _ index: Int, _ y: CGFloat, _ height: CGFloat, _ face: UIFont, fill: Bool = false) {
                    let box = CGRect(x: positions[index], y: y, width: widths[index], height: height)
                    if fill { UIColor(red: 1, green: 1, blue: 0, alpha: 1).setFill(); cg.fill(box) }
                    UIColor.black.setStroke(); cg.stroke(box, width: 0.5)
                    PDFText.draw(text, in: box.insetBy(dx: 4, dy: 1), font: face, wrap: true)
                }
                for (index, label) in ["ت", "المديرية", "رقم الايبان", "الفرع", "المبلغ"].enumerated() {
                    cell(label, index, top, header, bold, fill: true)
                }
                var y = top + header
                for (row, height) in zip(rows, fitting.rowHeights) {
                    for index in row.indices { cell(row[index], index, y, height, font) }
                    y += height
                }
                let labelBox = CGRect(x: positions[3], y: y, width: widths.prefix(4).reduce(0, +), height: totalHeight)
                UIColor(red: 1, green: 1, blue: 0, alpha: 1).setFill(); cg.fill(labelBox)
                UIColor.black.setStroke(); cg.stroke(labelBox, width: 0.5)
                PDFText.draw("المجموع", in: labelBox.insetBy(dx: 4, dy: 1), font: bold)
                cell(number(total), 4, y, totalHeight, bold, fill: true)
            }
            // نقرأ خلايا الملف الناتج، لا بيانات الرسم وحدها؛ اختلاف النص أو تعذر قراءته يوقف الجدول.
            guard let document = PDFDocument(url: url), !document.isLocked, document.pageCount == 1,
                  let outputPage = document.page(at: 0) else { throw Failure.tableMismatch }
            var right = page.width - 25.22
            let positions = widths.map { width -> CGFloat in right -= width; return right }
            func text(_ column: Int, _ y: CGFloat, _ height: CGFloat) -> String {
                let rect = CGRect(x: positions[column] + 0.2, y: page.height - y - height + 0.2,
                                  width: widths[column] - 0.4, height: height - 0.4)
                return outputPage.selection(for: rect)?.string ?? ""
            }
            func extracted(_ column: Int, _ y: CGFloat, _ height: CGFloat) -> String {
                ArabicText.latinDigits(text(column, y, height)).uppercased().filter { $0.isASCII && ($0.isLetter || $0.isNumber) }
            }
            func identity(_ value: String) -> String {
                let normalized = ArabicText.normalize(ArabicText.latinDigits(value.precomposedStringWithCompatibilityMapping))
                var scalars = String.UnicodeScalarView()
                for scalar in normalized.unicodeScalars where !CharacterSet.whitespacesAndNewlines.contains(scalar) && scalar.properties.generalCategory != .format {
                    scalars.append(scalar)
                }
                return String(scalars)
            }
            var y = top + header
            var verified: [TransferReconcileLine] = []
            for (index, pair) in zip(rows, fitting.rowHeights).enumerated() {
                let (row, height) = pair
                for column in row.indices {
                    let read = [0, 2, 4].contains(column) ? extracted(column, y, height) : identity(text(column, y, height))
                    let expected = [0, 2, 4].contains(column) ? row[column] : identity(row[column])
                    let equal = [0, 2, 4].contains(column) ? read == expected : TransferAnbarTablePDF.matchesExtractedCell(read, expected: expected)
                    guard equal else { throw Failure.cellMismatch(row: index + 1, column: column + 1, readCount: read.count, expectedCount: expected.count) }
                }
                guard let amount = Int64(extracted(4, y, height)) else { throw Failure.tableMismatch }
                verified.append(TransferReconcileLine(sourceID: entities[index].id, title: entities[index].group.title, amount: amount))
                y += height
            }
            guard extracted(4, y, totalHeight) == number(total) else { throw Failure.tableMismatch }
            try FileManager.default.setAttributes([.protectionKey: FileProtectionType.complete], ofItemAtPath: url.path)
            complete = true
            return Checked(url: url, lines: verified)
        }
    }
}
