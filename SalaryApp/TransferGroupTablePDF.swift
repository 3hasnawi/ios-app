import UIKit

/// صفحة جدول تجريبية، ولا تُستخدم للتصدير قبل مطابقتها مع النموذج الأصلي.
nonisolated enum TransferGroupTablePDF {
    enum Failure: LocalizedError {
        case invalidData, invalidTotal, missingFont
        var errorDescription: String? {
            switch self {
            case .invalidData: tr("بيانات جهات التحويل غير مكتملة أو مكررة.", "Transfer entities are incomplete or duplicated.")
            case .invalidTotal: tr("تعذّر التأكد من مجموع جهات التحويل.", "Could not verify the transfer entity total.")
            case .missingFont: tr("خط جدول التحويل غير متوفر على الجهاز.", "The transfer table font is unavailable.")
            }
        }
    }
    private static let page = CGRect(x: 0, y: 0, width: 595, height: 842)
    private static let widths: [CGFloat] = [30, 144, 170, 105, 90]
    private static let top: CGFloat = 55
    private static let bottom: CGFloat = 808
    private static let header: CGFloat = 27
    private static let totalHeight: CGFloat = 27
    private static func number(_ value: Double) -> String {
        String(format: "%.0f", locale: Locale(identifier: "en_US_POSIX"), value)
    }
    static func make(entities: [TransferEntity], category: TransferCategory, month: String) async throws -> URL {
        guard category == .rafidain || category == .otherBanks, !entities.isEmpty,
              Set(entities.map(\.id)).count == entities.count,
              entities.allSatisfy({ $0.category == category && $0.group.month == month && TransferRules.issues($0).isEmpty }) else { throw Failure.invalidData }
        let total = entities.reduce(0.0) { $0 + $1.group.total }
        guard total.isFinite, total > 0, total.rounded() == total, total <= 9_007_199_254_740_991 else { throw Failure.invalidTotal }
        let rows = entities.enumerated().map { index, entity in
            [String(index + 1), entity.beneficiaryName ?? entity.group.title,
             ArabicText.latinDigits(entity.account?.iban ?? "").uppercased().filter { !$0.isWhitespace && $0 != "-" },
             entity.account?.branch ?? "", number(entity.group.total)]
        }
        return try await PDFWork.run {
            guard let bold = UIFont(name: "Arial-BoldMT", size: 10),
                  let fontName = UIFont(name: "ArialMT", size: 10)?.fontName else { throw Failure.missingFont }
            _ = try TransferSinglePageFit.fit(rows: [[number(total)]], widths: [widths[4]],
                availableHeight: totalHeight, fontName: bold.fontName, maximumSize: 10, minimumSize: 10)
            let fitting = try TransferSinglePageFit.fit(rows: rows, widths: widths,
                availableHeight: bottom - top - header - totalHeight, fontName: fontName)
            guard let font = UIFont(name: fontName, size: fitting.fontSize) else { throw Failure.missingFont }
            let folder = FileManager.default.temporaryDirectory.appendingPathComponent("transfer-group-\(UUID().uuidString)", isDirectory: true)
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            let url = folder.appendingPathComponent("جدول التحويل.pdf")
            var complete = false
            defer { if !complete { try? FileManager.default.removeItem(at: folder) } }
            try UIGraphicsPDFRenderer(bounds: page).writePDF(to: url) { context in
                context.beginPage()
                let cg = context.cgContext
                var right = page.width - 28
                let positions = widths.map { width -> CGFloat in right -= width; return right }
                func cell(_ text: String, _ index: Int, _ y: CGFloat, _ height: CGFloat, _ face: UIFont, fill: Bool = false) {
                    let box = CGRect(x: positions[index], y: y, width: widths[index], height: height)
                    if fill { UIColor(white: 0.92, alpha: 1).setFill(); cg.fill(box) }
                    UIColor.black.setStroke(); cg.stroke(box, width: 0.5)
                    PDFText.draw(text, in: box.insetBy(dx: 4, dy: 2), font: face, wrap: true)
                }
                for (index, label) in ["ت", "الجهة", "رقم الايبان", "الفرع", "المبلغ"].enumerated() {
                    cell(label, index, top, header, bold, fill: true)
                }
                var y = top + header
                for (row, height) in zip(rows, fitting.rowHeights) {
                    for index in row.indices { cell(row[index], index, y, height, font) }
                    y += height
                }
                let labelBox = CGRect(x: positions[3], y: y, width: widths.prefix(4).reduce(0, +), height: totalHeight)
                UIColor(white: 0.92, alpha: 1).setFill(); cg.fill(labelBox)
                UIColor.black.setStroke(); cg.stroke(labelBox, width: 0.5)
                PDFText.draw("المجموع", in: labelBox.insetBy(dx: 4, dy: 2), font: bold)
                cell(number(total), 4, y, totalHeight, bold, fill: true)
            }
            try FileManager.default.setAttributes([.protectionKey: FileProtectionType.complete], ofItemAtPath: url.path)
            complete = true
            return url
        }
    }
}
