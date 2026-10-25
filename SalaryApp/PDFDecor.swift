import UIKit

/// لمسة أخيرة لكل كشف PDF: شعار الوزارة علامة مائية شفافة في وسط كل صفحة،
/// وتذييل في أسفل كل صفحة: يميناً اسم القسم، وفي الوسط رقم الصفحة، ويساراً تاريخ ووقت التصدير.
/// يعمل على الملف بعد كتابته، فيشمل كل الكشوف (القوائم، صندوق الشهداء، عقد الإطعام، الرعاية الصحية).
nonisolated enum PDFDecor {
    static let footerText = "( قيادة شرطة محافظة الانبار - قسم الحسابات )"
    /// شفافية الشعار: واضح ولا يغطي الأرقام.
    static let watermarkAlpha: CGFloat = 0.10

    /// يعيد كتابة الملف: العلامة المائية تحت محتوى كل صفحة، والتذييل فوقه.
    static func apply(to url: URL, date: Date = .now) throws {
        guard let source = CGPDFDocument(url as CFURL), source.numberOfPages > 0 else { return }
        let temp = url.deletingLastPathComponent().appendingPathComponent(".decor-\(UUID().uuidString).pdf")
        var mediaBox = CGRect(x: 0, y: 0, width: 595, height: 842)
        guard let context = CGContext(temp as CFURL, mediaBox: &mediaBox, nil) else { return }
        let logo = UIImage(named: "WatermarkLogo")?.cgImage ?? UIImage(named: "HomeLogo")?.cgImage
        let stamp = stampText(date)
        let pages = source.numberOfPages
        for index in 1...pages {
            guard let page = source.page(at: index) else { continue }
            var box = page.getBoxRect(.mediaBox)
            let info = [kCGPDFContextMediaBox as String: Data(bytes: &box, count: MemoryLayout<CGRect>.size)] as CFDictionary
            context.beginPDFPage(info)
            if let logo { drawWatermark(logo, in: box, context: context) }
            context.saveGState()
            context.drawPDFPage(page)
            context.restoreGState()
            drawFooter(in: box, page: index, of: pages, stamp: stamp, context: context)
            context.endPDFPage()
        }
        context.closePDF()
        _ = try FileManager.default.replaceItemAt(url, withItemAt: temp)
    }

    /// "٢٠٢٦/١٠/٢٥  ١٠:٤٢ م"
    static func stampText(_ date: Date) -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: "ar_IQ@numbers=arab")
        f.dateFormat = "yyyy/MM/dd   h:mm a"
        return f.string(from: date)
    }

    private static func drawWatermark(_ logo: CGImage, in box: CGRect, context: CGContext) {
        let side = min(box.width, box.height) * 0.62
        let aspect = CGFloat(logo.height) / CGFloat(max(logo.width, 1))
        let rect = CGRect(x: box.midX - side / 2, y: box.midY - side * aspect / 2, width: side, height: side * aspect)
        context.saveGState()
        context.setAlpha(watermarkAlpha)
        context.interpolationQuality = .high
        context.draw(logo, in: rect)
        context.restoreGState()
    }

    private static func drawFooter(in box: CGRect, page: Int, of pages: Int, stamp: String, context: CGContext) {
        let margin: CGFloat = 28
        let height: CGFloat = 13
        // إحداثيات PDF من الأسفل: خط فاصل ثم سطر التذييل.
        let lineY: CGFloat = 26
        context.saveGState()
        context.setStrokeColor(UIColor(white: 0.62, alpha: 1).cgColor)
        context.setLineWidth(0.5)
        context.move(to: CGPoint(x: margin, y: lineY))
        context.addLine(to: CGPoint(x: box.width - margin, y: lineY))
        context.strokePath()
        context.restoreGState()

        // الرسم بنظام UIKit (من الأعلى) داخل منطقة التذييل.
        UIGraphicsPushContext(context)
        context.saveGState()
        context.translateBy(x: 0, y: box.height)
        context.scaleBy(x: 1, y: -1)
        let top = box.height - lineY + 4
        let width = (box.width - margin * 2) / 3
        let font = UIFont.systemFont(ofSize: 8.5)
        let color = UIColor(white: 0.25, alpha: 1)
        text(footerText, CGRect(x: box.width - margin - width * 1.4, y: top, width: width * 1.4, height: height), font: .boldSystemFont(ofSize: 8.5), color: color, align: .right)
        text("صفحة \(arabicDigits(page)) من \(arabicDigits(pages))", CGRect(x: box.midX - width / 2, y: top, width: width, height: height), font: font, color: color, align: .center)
        text(stamp, CGRect(x: margin, y: top, width: width, height: height), font: font, color: color, align: .left)
        context.restoreGState()
        UIGraphicsPopContext()
    }

    private static func text(_ string: String, _ rect: CGRect, font: UIFont, color: UIColor, align: NSTextAlignment) {
        let style = NSMutableParagraphStyle()
        style.alignment = align
        style.baseWritingDirection = .rightToLeft
        style.lineBreakMode = .byTruncatingTail
        NSAttributedString(string: string, attributes: [.font: font, .foregroundColor: color, .paragraphStyle: style])
            .draw(with: rect, options: [.usesLineFragmentOrigin, .truncatesLastVisibleLine], context: nil)
    }

    private static func arabicDigits(_ n: Int) -> String { ReportSheet.easternDigits(String(n)) }
}
