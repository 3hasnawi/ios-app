import UIKit

/// يقيس الخلايا قبل إنشاء PDF حتى لا يُقص اسم أو مبلغ عند توزيع الصفحات.
nonisolated enum HealthCarePDFLayout {
    struct Layout {
        let widths: [CGFloat]
        let titleHeight: CGFloat
        let headerHeight: CGFloat
        let heights: [CGFloat]
        let pages: [Range<Int>]
    }
    static let page = CGRect(x: 0, y: 0, width: 595, height: 842)
    static let margin: CGFloat = 32
    static let bottom: CGFloat = 794 // يترك مكاناً لتذييل PDFDecor
    static func make(title: String, headers: [String], rows: [[String]], ratios: [CGFloat]) throws -> Layout {
        func invalid() -> NSError { NSError(domain: "HealthCarePDFLayout", code: 1, userInfo: [NSLocalizedDescriptionKey: "تعذّر عرض بيانات كشف الرعاية دون اقتطاع؛ راجع البيانات أو صدّر Excel."]) }
        guard headers.count == ratios.count, !headers.isEmpty, ratios.allSatisfy({ $0 > 0 && $0.isFinite }), rows.allSatisfy({ $0.count == headers.count }) else { throw invalid() }
        let width = page.width - margin * 2
        let widths = ratios.map { $0 / ratios.reduce(0, +) * width }
        let titleFont = ReportFont.bold(13), headerFont = ReportFont.bold(11)
        let titleHeight = max(30, ceil(CGFloat(try EntityDeductionPDF.lines(title, width: width - 6, font: titleFont)) * titleFont.lineHeight + 8))
        var headerHeight: CGFloat = 24
        for i in headers.indices {
            let lines = try EntityDeductionPDF.lines(headers[i], width: widths[i] - 6, font: headerFont)
            headerHeight = max(headerHeight, ceil(CGFloat(lines) * headerFont.lineHeight + 8))
        }
        let available = bottom - (margin + titleHeight + 4 + headerHeight)
        guard available >= 24 else { throw invalid() }
        var heights: [CGFloat] = []
        for (index, row) in rows.enumerated() {
            if index.isMultiple(of: 256) { try Task.checkCancellation() }
            var height: CGFloat = 24
            for i in row.indices {
                let font = i == 2 ? ReportFont.bold(11.5) : (i == 1 ? ReportFont.regular(10.5) : ReportFont.bold(11))
                let count = try EntityDeductionPDF.lines(row[i], width: widths[i] - 6, font: font)
                if i != 1 && i != 2 && count > 1 { throw invalid() }
                height = max(height, ceil(CGFloat(count) * font.lineHeight + 8))
            }
            guard height <= available else { throw invalid() }
            heights.append(height)
        }
        var pages: [Range<Int>] = [], start = 0
        while start < heights.count {
            var end = start, used: CGFloat = 0
            while end < heights.count && used + heights[end] <= available { used += heights[end]; end += 1 }
            guard end > start else { throw invalid() }
            pages.append(start..<end); start = end
        }
        if pages.isEmpty { pages = [0..<0] }
        return Layout(widths: widths, titleHeight: titleHeight, headerHeight: headerHeight, heights: heights, pages: pages)
    }
}
