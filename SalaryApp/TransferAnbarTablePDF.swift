import UIKit
import PDFKit

/// جدول تجريبي لكتاب الرافدين فرع الأنبار. يجب تفريغ الجدول القديم قبل استدعائه.
nonisolated enum TransferAnbarTablePDF {
    enum Failure: LocalizedError {
        case invalidSource, missingFont
        var errorDescription: String? {
            switch self {
            case .invalidSource: tr("تعذّر التحقق من بيانات جدول فرع الأنبار.", "Could not verify the Anbar branch table data.")
            case .missingFont: tr("خط جدول فرع الأنبار غير متوفر.", "The Anbar branch table font is unavailable.")
            }
        }
    }
    private static let size = CGSize(width: 595, height: 842)
    private static let widths: [CGFloat] = [30, 70, 170, 95, 143]
    private static let header: CGFloat = 20
    private static let totalHeight: CGFloat = 20
    private static let inlineTop: CGFloat = 416.1
    private static let inlineBottom: CGFloat = 572.3
    private static let separateTop: CGFloat = 55
    private static let separateBottom: CGFloat = 808

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
            guard let bold = UIFont(name: "Arial-BoldMT", size: 10),
                  let face = UIFont(name: "ArialMT", size: 10) else { throw Failure.missingFont }
            _ = try TransferSinglePageFit.fit(rows: [[total]], widths: [widths[3]], availableHeight: totalHeight,
                                               fontName: bold.fontName, maximumSize: 10, minimumSize: 10)
            let inlineHeight = inlineBottom - inlineTop - header - totalHeight
            let inline = try? TransferSinglePageFit.fit(rows: rows, widths: widths, availableHeight: inlineHeight, fontName: face.fontName)
            let onSeparatePage = inline == nil
            let layout = try inline ?? TransferSinglePageFit.fit(rows: rows, widths: widths,
                availableHeight: separateBottom - separateTop - header - totalHeight, fontName: face.fontName)
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
                var right = size.width - 36
                let positions = widths.map { width -> CGFloat in right -= width; return right }
                func cell(_ text: String, _ column: Int, _ y: CGFloat, _ height: CGFloat, _ textFont: UIFont, fill: Bool = false) {
                    let rect = CGRect(x: positions[column], y: y, width: widths[column], height: height)
                    if fill { UIColor(white: 0.92, alpha: 1).setFill(); cg.fill(rect) }
                    UIColor.black.setStroke(); cg.stroke(rect, width: 0.5)
                    PDFText.draw(text, in: rect.insetBy(dx: 4, dy: 2), font: textFont, wrap: true)
                }
                for (i, title) in ["ت", "الرتبة", "الاسم الكامل", "المبلغ", "جهة التنفيذ"].enumerated() { cell(title, i, top, header, bold, fill: true) }
                var y = top + header
                for (row, height) in zip(rows, layout.rowHeights) {
                    for i in row.indices { cell(row[i], i, y, height, font) }
                    y += height
                }
                for i in 0..<widths.count { cell(i == 2 ? "المجموع" : i == 3 ? total : "", i, y, totalHeight, bold, fill: true) }
            }
            try FileManager.default.setAttributes([.protectionKey: FileProtectionType.complete], ofItemAtPath: url.path)
            complete = true
            return url
        }
    }
}
