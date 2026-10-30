import UIKit
import CoreText

/// قياس جدول مجموعة التحويل قبل إخراجه: لا يُختزل نص أو يُنقل صف إلى صفحة ثانية.
nonisolated enum TransferSinglePageFit {
    private static let rtl: CTParagraphStyle = {
        let direction = CTWritingDirection.rightToLeft
        return withUnsafeBytes(of: direction) { raw in
            var setting = CTParagraphStyleSetting(spec: .baseWritingDirection,
                valueSize: MemoryLayout<CTWritingDirection>.size, value: raw.baseAddress!)
            return CTParagraphStyleCreate(&setting, 1)
        }
    }()
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
                    fontName: String, maximumSize: CGFloat = 10, minimumSize: CGFloat = 7,
                    verticalPadding: CGFloat = 5) throws -> Layout {
        guard !rows.isEmpty, rows.count <= 2000, !widths.isEmpty,
              rows.allSatisfy({ $0.count == widths.count }), widths.allSatisfy({ $0.isFinite && $0 > 10 }),
              availableHeight.isFinite, availableHeight > 0, minimumSize >= 6,
              maximumSize >= minimumSize, maximumSize <= 24, verticalPadding.isFinite,
              verticalPadding >= 1, verticalPadding <= 20 else { throw Failure.invalidLayout }
        guard UIFont(name: fontName, size: maximumSize) != nil else { throw Failure.missingFont }
        let step: CGFloat = 0.5
        var size = maximumSize
        while size >= minimumSize {
            try Task.checkCancellation()
            guard let font = UIFont(name: fontName, size: size) else { throw Failure.missingFont }
            var heights: [CGFloat] = []
            for row in rows {
                var height = font.lineHeight + verticalPadding
                for (text, width) in zip(row, widths) where !text.isEmpty {
                    let attributed = NSAttributedString(string: text, attributes: [
                        .font: font, NSAttributedString.Key(kCTParagraphStyleAttributeName as String): rtl
                    ])
                    let typesetter = CTTypesetterCreateWithAttributedString(attributed)
                    var offset = 0, lineCount = 0
                    while offset < attributed.length {
                        let count = CTTypesetterSuggestLineBreak(typesetter, offset, Double(width - 8))
                        guard count > 0, count <= attributed.length - offset else { throw Failure.cannotFit }
                        offset += count
                        lineCount += 1
                    }
                    height = max(height, font.lineHeight * CGFloat(lineCount) + verticalPadding)
                }
                heights.append(height)
            }
            if heights.reduce(0, +) <= availableHeight { return Layout(fontSize: size, rowHeights: heights) }
            size -= step
        }
        throw Failure.cannotFit
    }
}
