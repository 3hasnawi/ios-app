import UIKit
import CoreText

/// قياس جدول مجموعة التحويل قبل إخراجه: لا يُختزل نص أو يُنقل صف إلى صفحة ثانية.
nonisolated enum TransferSinglePageFit {
    struct Layout: Sendable {
        let fontSize: CGFloat
        let rowHeights: [CGFloat]
    }
    enum Failure: LocalizedError {
        case invalidLayout, missingFont, cannotFit
        var errorDescription: String? {
            switch self {
            case .invalidLayout: tr("بيانات جدول التحويل غير مكتملة.", "The transfer table data is incomplete.")
            case .missingFont: tr("خط الجدول المطلوب غير متوفر.", "The required table font is unavailable.")
            case .cannotFit: tr("لا تتسع جميع الجهات في صفحة واحدة بحجم خط واضح؛ راجع البيانات قبل التصدير.", "All entities do not fit legibly on one page; review the data before export.")
            }
        }
    }
    /// width وheight تحددان مساحة الجدول بعد حجز الترويسة والمجموع والهوامش.
    static func fit(rows: [[String]], widths: [CGFloat], availableHeight: CGFloat,
                    fontName: String, maximumSize: CGFloat = 10, minimumSize: CGFloat = 7) throws -> Layout {
        guard !rows.isEmpty, rows.count <= 2000, !widths.isEmpty,
              rows.allSatisfy({ $0.count == widths.count }), widths.allSatisfy({ $0.isFinite && $0 > 10 }),
              availableHeight.isFinite, availableHeight > 0, minimumSize >= 6,
              maximumSize >= minimumSize, maximumSize <= 24 else { throw Failure.invalidLayout }
        guard UIFont(name: fontName, size: maximumSize) != nil else { throw Failure.missingFont }
        let step: CGFloat = 0.5
        var size = maximumSize
        while size >= minimumSize {
            try Task.checkCancellation()
            guard let font = UIFont(name: fontName, size: size) else { throw Failure.missingFont }
            var heights: [CGFloat] = []
            for row in rows {
                var height = ceil(font.lineHeight) + 5
                for (text, width) in zip(row, widths) where !text.isEmpty {
                    let attributed = NSAttributedString(string: text, attributes: [.font: font])
                    let framesetter = CTFramesetterCreateWithAttributedString(attributed as CFAttributedString)
                    let measured = CTFramesetterSuggestFrameSizeWithConstraints(framesetter, CFRange(location: 0, length: 0), nil,
                        CGSize(width: width - 8, height: .greatestFiniteMagnitude), nil)
                    height = max(height, ceil(measured.height) + 5)
                }
                heights.append(height)
            }
            if heights.reduce(0, +) <= availableHeight { return Layout(fontSize: size, rowHeights: heights) }
            size -= step
        }
        throw Failure.cannotFit
    }
}
