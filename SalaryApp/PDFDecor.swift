import UIKit
#if targetEnvironment(simulator)
import PDFKit
#endif

/// تذييل أسفل كل صفحة: يميناً اسم القسم، وفي الوسط رقم الصفحة، ويساراً تاريخ ووقت التصدير.
/// يعمل على الملف بعد كتابته، فيشمل كل الكشوف (القوائم، صندوق الشهداء، عقد الإطعام، الرعاية الصحية).
nonisolated enum PDFDecor {
    static let footerText = "( قيادة شرطة محافظة الانبار - قسم الحسابات )"
    /// يعيد كتابة الملف مع التذييل فقط.
    static func apply(to url: URL, date: Date = .now, footer: String = footerText) throws {
        func invalidPDF() -> NSError {
            NSError(domain: "PDFDecor", code: 1, userInfo: [NSLocalizedDescriptionKey: tr("تعذّر التحقق من صفحات الكشف؛ بقي الملف الأصلي دون استبدال.", "Could not verify the report pages; the original file was not replaced.")])
        }
        guard let source = CGPDFDocument(url as CFURL), source.numberOfPages > 0 else { throw invalidPDF() }
        let temp = url.deletingLastPathComponent().appendingPathComponent(".decor-\(UUID().uuidString).pdf")
        defer { try? FileManager.default.removeItem(at: temp) }
        var mediaBox = CGRect(x: 0, y: 0, width: 595, height: 842)
        guard let context = CGContext(temp as CFURL, mediaBox: &mediaBox, nil) else { throw invalidPDF() }
        var closed = false
        defer { if !closed { context.closePDF() } }
        let stamp = stampText(date)
        let pages = source.numberOfPages
        for index in 1...pages { try autoreleasepool {
            guard let page = source.page(at: index) else { throw invalidPDF() }
            var box = page.getBoxRect(.mediaBox)
            let info = [kCGPDFContextMediaBox as String: Data(bytes: &box, count: MemoryLayout<CGRect>.size)] as CFDictionary
            context.beginPDFPage(info)
            context.saveGState()
            context.drawPDFPage(page)
            context.restoreGState()
            drawFooter(in: box, page: index, of: pages, stamp: stamp, footer: footer, context: context)
            context.endPDFPage()
        } }
        context.closePDF()
        closed = true
        guard let result = CGPDFDocument(temp as CFURL), result.numberOfPages == pages else { throw invalidPDF() }
        for index in 1...pages where result.page(at: index) == nil { throw invalidPDF() }
        _ = try FileManager.default.replaceItemAt(url, withItemAt: temp)
    }

    /// "٢٠٢٦/١٠/٢٥  ١٠:٤٢ م"
    static func stampText(_ date: Date) -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: "ar_IQ@numbers=arab")
        f.dateFormat = "yyyy/MM/dd   h:mm a"
        return f.string(from: date)
    }

    private static func drawFooter(in box: CGRect, page: Int, of pages: Int, stamp: String, footer: String, context: CGContext) {
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
        let font = ReportFont.regular(8.5)
        let color = UIColor(white: 0.25, alpha: 1)
        text(footer, CGRect(x: box.width - margin - width * 1.4, y: top, width: width * 1.4, height: height), font: ReportFont.bold(8.5), color: color, align: .right)
        text("صفحة \(arabicDigits(page)) من \(arabicDigits(pages))", CGRect(x: box.midX - width / 2, y: top, width: width, height: height), font: font, color: color, align: .center)
        text(stamp, CGRect(x: margin, y: top, width: width, height: height), font: font, color: color, align: .left)
        context.restoreGState()
        UIGraphicsPopContext()
    }

    private static func text(_ string: String, _ rect: CGRect, font: UIFont, color: UIColor, align: NSTextAlignment) {
        PDFText.draw(string, in: rect, font: font, color: color, align: align)
    }

    private static func arabicDigits(_ n: Int) -> String { ReportSheet.easternDigits(String(n)) }

    #if targetEnvironment(simulator)
    /// اختبار وهمي للحفاظ على الملف عند الفشل وعدد الصفحات عند النجاح.
    static func selfTest() throws -> Bool {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("pdf-decor-test-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let damagedURL = directory.appendingPathComponent("damaged.pdf")
        let damaged = Data("not a PDF".utf8)
        try damaged.write(to: damagedURL)
        do { try apply(to: damagedURL); return false } catch { }
        guard try Data(contentsOf: damagedURL) == damaged else { return false }
        let file = directory.appendingPathComponent("report.pdf")
        let renderer = UIGraphicsPDFRenderer(bounds: CGRect(x: 0, y: 0, width: 595, height: 842))
        try renderer.writePDF(to: file) { context in
            for _ in 0..<2 {
                context.beginPage()
                ("بيانات وهمية" as NSString).draw(at: CGPoint(x: 60, y: 60), withAttributes: [.font: ReportFont.regular(12)])
            }
        }
        try apply(to: file)
        guard let output = CGPDFDocument(file as CFURL), output.numberOfPages == 2,
              output.page(at: 1) != nil, output.page(at: 2) != nil else { return false }
        guard try !FileManager.default.contentsOfDirectory(atPath: directory.path).contains(where: { $0.hasPrefix(".decor-") }) else { return false }
        // كشف عام متعدد الصفحات: يبقى النص الطويل كاملاً في الاتجاهين.
        let longName = "اسم منتسب وهمي طويل يتضمن اسم الاب والجد واللقب للتحقق من التفاف النص"
        let columns: [ReportSheet.Column] = [
            .init(title: "ت", width: 5, kind: .serial),
            .init(title: "الاسم الكامل", width: 50, kind: .text),
            .init(title: "التفاصيل", width: 60, kind: .text),
            .init(title: "المبلغ", width: 18, kind: .amount)
        ]
        let rows = (1...60).map { index in [String(index), longName, "بيانات تجريبية طويلة للمراجعة", ""] }
        let sheet = ReportSheet(header: "قيادة وهمية", title: "كشف تجريبي", columns: columns, rows: rows, amounts: Array(repeating: 100, count: rows.count))
        let report = directory.appendingPathComponent("wrapped-report.pdf")
        try PDFReportWriter.write(sheet, to: report)
        guard let pdf = PDFDocument(url: report), pdf.pageCount > 1 else { return false }
        var fullText = ""
        for pageNumber in 0..<pdf.pageCount {
            guard let page = pdf.page(at: pageNumber), page.bounds(for: .mediaBox).width > page.bounds(for: .mediaBox).height,
                  let text = page.string else { return false }
            fullText += text + " "
        }
        return ArabicText.matchKey(fullText).contains(ArabicText.matchKey(longName))
    }
    #endif
}
