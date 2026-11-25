import UIKit

nonisolated enum CommandDeductionPDF {
    static let page = CGRect(x: 0, y: 0, width: 841.68, height: 595.2)

    static func render(month: String, amounts: [CommandDeduction: Int64], in context: UIGraphicsPDFRendererContext) {
        context.beginPage(withBounds: page, pageInfo: [:])
        let cg = context.cgContext
        let widths: [CGFloat] = [32, 182.5, 54.5, 54.5, 54, 54.5, 149]
        let right: CGFloat = 737.4, top: CGFloat = 109.8, height: CGFloat = 17.2
        let headers = ["ت", "نوع الاستقطاع"] + FundReport.categories.map(\.name) + ["مجموع التسديد الشهري"]
        let formatter = NumberFormatter()
        formatter.locale = Locale(identifier: "ar_IQ@numbers=arab")
        formatter.numberStyle = .decimal
        func number(_ value: Int64) -> String {
            formatter.string(from: NSNumber(value: value)) ?? ReportSheet.easternDigits(String(value))
        }
        // نقيس عرض الحروف الفعلي حتى تبقى أسماء المديريات الطويلة داخل الخلية.
        func draw(_ value: String, in rect: CGRect) {
            var font = ReportFont.bold(14)
            let width = (value as NSString).size(withAttributes: [.font: font]).width
            if width > rect.width - 4 { font = ReportFont.bold(14 * (rect.width - 4) / width) }
            PDFText.draw(value, in: rect.insetBy(dx: 1, dy: 0), font: font)
        }
        let parts = month.split(separator: " ")
        let monthLabel: String
        if parts.count > 1, let year = parts.last, year.count == 4, year.allSatisfy(\.isNumber) {
            monthLabel = parts.dropLast().joined(separator: " ") + " / " + year
        } else { monthLabel = month }
        let width = widths.reduce(0, +)
        draw("جدول استقطاعات قيادة شرطة الانبار لشهر ( " + ReportSheet.easternDigits(monthLabel) + " )",
             in: CGRect(x: right - width, y: 73, width: width, height: 20))

        func cell(_ value: String, row: Int, column: Int, span: Int = 1) {
            let end = column + span
            let rect = CGRect(x: right - widths.prefix(end).reduce(0, +), y: top + CGFloat(row) * height,
                              width: widths[column..<end].reduce(0, +), height: height)
            if row == 0 || column == 0 { cg.setFillColor(UIColor.yellow.cgColor); cg.fill(rect) }
            cg.setStrokeColor(UIColor.black.cgColor)
            cg.stroke(rect, width: ReportTableStyle.borderWidth)
            draw(value, in: rect)
        }
        for (i, header) in headers.enumerated() { cell(header, row: 0, column: i) }
        for (i, kind) in CommandDeduction.allCases.enumerated() {
            let row = i + 1
            cell(String(row), row: row, column: 0)
            cell(kind.title, row: row, column: 1)
            if kind == .health {
                cell("لكل فرد بالعائلة " + number(Int64(HealthCareReport.perMember)), row: row, column: 2, span: 4)
            } else {
                let rates: [Int64] = kind == .fund ? FundReport.categories.map { Int64($0.rate) } : [4000, 2000, 2000, 0]
                for (index, rate) in rates.enumerated() { cell(number(rate), row: row, column: index + 2) }
            }
            cell(amounts[kind].map(number) ?? "—", row: row, column: 6)
        }
    }
}
