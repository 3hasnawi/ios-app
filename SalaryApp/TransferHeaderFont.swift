import Foundation
import CoreGraphics
import CoreText

/// ينشئ خط الترويسة من ملف المالك محلياً دون إدراج الخط في التطبيق أو تسجيله على مستوى النظام.
nonisolated enum TransferHeaderFont {
    static let boldSettingsKey = "transferHeaderBoldFont"
    static let regularSettingsKey = "transferHeaderRegularFont"
    enum Style: Equatable, Sendable { case regular, bold }
    enum Failure: LocalizedError {
        case invalidFont, wrongFont, missingCharacters
        var errorDescription: String? {
            switch self {
            case .invalidFont: tr("ملف الخط غير صالح أو يتجاوز الحجم المسموح.", "The font file is invalid or too large.")
            case .wrongFont: tr("ملف الخط لا يطابق نوع خط الترويسة المطلوب.", "The font file does not match the required header style.")
            case .missingCharacters: tr("الخط لا يحتوي الحروف اللازمة للترويسة.", "The font does not contain the required header characters.")
            }
        }
    }
    static func make(data: Data, style: Style, size: CGFloat) throws -> CTFont {
        guard !data.isEmpty, data.count <= 3_000_000, size.isFinite, size >= 6, size <= 72,
              let provider = CGDataProvider(data: data as CFData), let graphicsFont = CGFont(provider) else { throw Failure.invalidFont }
        let expected = style == .bold ? "SakkalMajalla-Bold" : "SakkalMajalla"
        guard let name = graphicsFont.postScriptName as String?, name == expected else { throw Failure.wrongFont }
        let font = CTFontCreateWithGraphicsFont(graphicsFont, size, nil, nil)
        let sample = Array("ابتثجحخدذرزسشصضطظعغفقكلمنهويأإآءؤئةى0123456789".utf16)
        var glyphs = [CGGlyph](repeating: 0, count: sample.count)
        let complete = sample.withUnsafeBufferPointer { chars in
            glyphs.withUnsafeMutableBufferPointer { result in
                CTFontGetGlyphsForCharacters(font, chars.baseAddress!, result.baseAddress!, sample.count)
            }
        }
        guard complete, glyphs.allSatisfy({ $0 != 0 }) else { throw Failure.missingCharacters }
        return font
    }
    /// يمنع استبدال حرف غير متوفر بخط آخر عند كتابة نص الترويسة.
    static func requireSupported(_ text: String, font: CTFont) throws {
        let chars = Array(text.utf16)
        guard !chars.isEmpty else { throw Failure.missingCharacters }
        var glyphs = [CGGlyph](repeating: 0, count: chars.count)
        let complete = chars.withUnsafeBufferPointer { source in
            glyphs.withUnsafeMutableBufferPointer { result in
                CTFontGetGlyphsForCharacters(font, source.baseAddress!, result.baseAddress!, chars.count)
            }
        }
        guard complete, glyphs.allSatisfy({ $0 != 0 }) else { throw Failure.missingCharacters }
    }
}
