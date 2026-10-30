import UIKit
import PDFKit

/// يصنع صور أحرف رقم الشهر من مرجع PDF الخاص، دون نسخ الخط إلى التطبيق أو نشره.
nonisolated struct TransferMonthDigitGlyph: Sendable {
    let png: Data
    let width: CGFloat
    let height: CGFloat
}

nonisolated enum TransferMonthDigits {
    enum Failure: LocalizedError {
        case invalidReference, missingDigits
        var errorDescription: String? {
            switch self {
            case .invalidReference: tr("مرجع أرقام الشهر غير صالح.", "The month digit reference is invalid.")
            case .missingDigits: tr("مرجع الأرقام لا يحتوي الأرقام العشرة بوضوح.", "The reference does not clearly contain all ten digits.")
            }
        }
    }
    private static func hasInk(_ image: UIImage) -> Bool {
        guard let cgImage = image.cgImage, cgImage.width > 0, cgImage.height > 0,
              cgImage.width <= 1200, cgImage.height <= 1200 else { return false }
        let width = cgImage.width, height = cgImage.height
        var bytes = [UInt8](repeating: 0, count: width * height * 4)
        return bytes.withUnsafeMutableBytes { buffer in
            let info = CGImageAlphaInfo.premultipliedLast.rawValue | CGBitmapInfo.byteOrder32Big.rawValue
            guard let cg = CGContext(data: buffer.baseAddress, width: width, height: height, bitsPerComponent: 8,
                                     bytesPerRow: width * 4, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: info) else { return false }
            cg.draw(cgImage, in: CGRect(x: 0, y: 0, width: CGFloat(width), height: CGFloat(height)))
            let raw = buffer.bindMemory(to: UInt8.self)
            for i in stride(from: 0, to: raw.count, by: 4) {
                if raw[i + 3] > 64 && raw[i] < 180 && raw[i + 1] < 180 && raw[i + 2] < 180 { return true }
            }
            return false
        }
    }
    static func extract(from data: Data) throws -> [TransferMonthDigitGlyph] {
        guard let document = PDFDocument(data: data), !document.isLocked, document.pageCount == 1,
              let page = document.page(at: 0), let string = page.string else { throw Failure.invalidReference }
        let text = string as NSString
        let range = text.range(of: "0123456789")
        guard range.location != NSNotFound, range.length == 10 else { throw Failure.missingDigits }
        var glyphs: [TransferMonthDigitGlyph] = []
        let bounds = page.bounds(for: .mediaBox)
        for index in 0..<10 {
            try Task.checkCancellation()
            let box = page.characterBounds(at: range.location + index)
            guard box.width.isFinite, box.height.isFinite, box.width > 1, box.height > 1,
                  box.width < 100, box.height < 100, bounds.contains(box) else { throw Failure.missingDigits }
            let format = UIGraphicsImageRendererFormat()
            format.scale = 6
            format.opaque = false
            let image = UIGraphicsImageRenderer(size: box.size, format: format).image { context in
                let cg = context.cgContext
                cg.translateBy(x: -box.minX, y: box.maxY)
                cg.scaleBy(x: 1, y: -1)
                page.draw(with: .mediaBox, to: cg)
            }
            guard hasInk(image), let png = image.pngData(), !png.isEmpty else { throw Failure.invalidReference }
            glyphs.append(TransferMonthDigitGlyph(png: png, width: box.width, height: box.height))
        }
        return glyphs
    }
}
