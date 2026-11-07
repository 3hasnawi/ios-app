import Foundation
import UIKit
import CoreImage
import ImageIO

/// المعالجة اختيارية، وتُطبق على نسخة مؤقتة من الصور قبل إنشاء PDF فقط.
nonisolated enum OfficialBookScanEnhancement {
    enum Mode: String, CaseIterable, Identifiable, Sendable {
        case original, gentle, monochrome
        var id: String { rawValue }
        var title: String {
            switch self {
            case .original: tr("الأصل", "Original")
            case .gentle: tr("تحسين خفيف", "Gentle enhancement")
            case .monochrome: tr("أبيض وأسود", "Black and white")
            }
        }
    }
    enum Quality: String, CaseIterable, Identifiable, Sendable {
        case high, compact
        var id: String { rawValue }
        var title: String {
            switch self {
            case .high: tr("جودة عالية", "High quality")
            case .compact: tr("حجم أصغر", "Smaller file")
            }
        }
        var maxPixels: Int { self == .high ? 3000 : 1800 }
        var jpegQuality: CGFloat { self == .high ? 0.94 : 0.84 }
    }

    /// إنشاء نسخة بالحجم المطلوب؛ لا تُغيّر بيانات الالتقاط المحفوظة في المحرر.
    static func render(_ data: Data, mode: Mode, maxPixels: Int, jpegQuality: CGFloat) throws -> Data {
        try Task.checkCancellation()
        guard (1...4000).contains(maxPixels), (0.5...1.0).contains(jpegQuality),
              let source = CGImageSourceCreateWithData(data as CFData, nil),
              let image = CGImageSourceCreateThumbnailAtIndex(source, 0, [
                kCGImageSourceCreateThumbnailFromImageAlways: true,
                kCGImageSourceCreateThumbnailWithTransform: true,
                kCGImageSourceThumbnailMaxPixelSize: maxPixels
              ] as CFDictionary) else { throw OfficialBookError.damagedPDF }
        var output = CIImage(cgImage: image)
        if mode == .gentle {
            guard let shadows = CIFilter(name: "CIHighlightShadowAdjust") else { throw OfficialBookError.damagedPDF }
            shadows.setValue(output, forKey: kCIInputImageKey)
            shadows.setValue(0.15, forKey: "inputShadowAmount")
            shadows.setValue(1.0, forKey: "inputHighlightAmount")
            guard let lifted = shadows.outputImage else { throw OfficialBookError.damagedPDF }
            output = lifted
        }
        if mode != .original {
            guard let filter = CIFilter(name: "CIColorControls") else { throw OfficialBookError.damagedPDF }
            filter.setValue(output, forKey: kCIInputImageKey)
            filter.setValue(mode == .monochrome ? 0.0 : 1.0, forKey: kCIInputSaturationKey)
            filter.setValue(mode == .monochrome ? 1.12 : 1.05, forKey: kCIInputContrastKey)
            filter.setValue(mode == .monochrome ? 0.015 : 0.008, forKey: kCIInputBrightnessKey)
            guard let processed = filter.outputImage else { throw OfficialBookError.damagedPDF }
            output = processed
        }
        try Task.checkCancellation()
        let context = CIContext(options: [.cacheIntermediates: false])
        guard let cgImage = context.createCGImage(output, from: output.extent),
              let encoded = UIImage(cgImage: cgImage).jpegData(compressionQuality: jpegQuality),
              !encoded.isEmpty else { throw OfficialBookError.damagedPDF }
        return encoded
    }

    static func prepare(_ originals: [Data], mode: Mode, quality: Quality) throws -> [Data] {
        guard (1...3).contains(originals.count) else { throw OfficialBookError.invalidPages }
        guard originals.allSatisfy({ !$0.isEmpty && CGImageSourceCreateWithData($0 as CFData, nil) != nil }) else {
            throw OfficialBookError.damagedPDF
        }
        if mode == .original && quality == .high { return originals }
        return try originals.map { data in
            try autoreleasepool {
                try render(data, mode: mode, maxPixels: quality.maxPixels, jpegQuality: quality.jpegQuality)
            }
        }
    }

    #if targetEnvironment(simulator)
    /// صفحة وهمية لاختبار بقاء الأصل وسلامة أوضاع المعالجة والأخطاء.
    static func selfTest() throws -> Bool {
        let format = UIGraphicsImageRendererFormat()
        format.scale = 1
        let renderer = UIGraphicsImageRenderer(size: CGSize(width: 80, height: 100), format: format)
        let sample = renderer.image { context in
            UIColor.white.setFill()
            context.fill(CGRect(x: 0, y: 0, width: 80, height: 100))
            UIColor.blue.setFill()
            context.fill(CGRect(x: 20, y: 20, width: 30, height: 30))
            UIColor.black.setFill()
            context.fill(CGRect(x: 10, y: 70, width: 60, height: 2))
        }
        guard let original = sample.pngData() else { return false }
        guard try prepare([original], mode: .original, quality: .high) == [original] else { return false }
        for mode in Mode.allCases {
            for quality in Quality.allCases {
                let pages = try prepare([original], mode: mode, quality: quality)
                guard pages.count == 1, let source = CGImageSourceCreateWithData(pages[0] as CFData, nil),
                      let image = CGImageSourceCreateImageAtIndex(source, 0, nil),
                      image.width == 80, image.height == 100 else { return false }
            }
        }
        do { _ = try prepare([Data("invalid".utf8)], mode: .original, quality: .high); return false }
        catch { return true }
    }
    #endif
}
