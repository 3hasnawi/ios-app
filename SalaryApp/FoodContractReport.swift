import SwiftUI
import UIKit

/// كشف عقد الإطعام: من بند "شركة عراق الجوال" في ورقة الرواتب، مجمّعاً حسب مبلغ الاستقطاع
/// (كم مشترك يُستقطع منه كل مبلغ، ومجموعه)، ثم تفاصيل النسبة:
/// حصة صندوق الشهداء ٣٪ من المجموع، ثم ضريبة ١٫٨٪ من الباقي، والباقي للشركة.
nonisolated enum FoodContractReport {
    static let fundRate = 0.03
    static let taxRate = 0.018

    struct Row: Sendable, Hashable, Identifiable {
        var id: Double { amount }
        let amount: Double
        let count: Int
        var total: Double { amount * Double(count) }
    }

    struct Split: Sendable {
        let total: Double
        var fund: Double { (total * FoodContractReport.fundRate).rounded() }
        var afterFund: Double { total - fund }
        var tax: Double { (afterFund * FoodContractReport.taxRate).rounded() }
        var company: Double { afterFund - tax }
    }

    static func applies(to title: String) -> Bool {
        ArabicText.normalize(title).contains(ArabicText.normalize("الجوال"))
    }

    static func rows(from people: [ReportRow]) -> [Row] {
        var counts: [Double: Int] = [:]
        for p in people where p.amount != 0 { counts[p.amount, default: 0] += 1 }
        return counts.map { Row(amount: $0.key, count: $0.value) }.sorted { $0.amount < $1.amount }
    }

    static func title(month: String) -> String {
        let header = UserDefaults.standard.string(forKey: ReportSheet.headerKey) ?? ReportSheet.defaultHeader
        return "تفاصيل نسبة عدد المشتركين في عقد الاطعام ( \(header) ) لشهر \(ReportSheet.easternDigits(month))"
    }

    static func fileName(month: String) -> String {
        "عقد الاطعام - \(month)".replacingOccurrences(of: "/", with: "-")
    }

    private static let splitLabels = ["مجموع الاستقطاع", "حصة صندوق شهداء ٣٪", "المبلغ بعد حسم حصة الصندوق", "ضريبة ١٫٨٪", "شركة عراق الجوال"]

    // MARK: - Excel

    static func writeExcel(_ rows: [Row], month: String, to url: URL) throws {
        let t = XLSXWriter.self
        let count = rows.reduce(0) { $0 + $1.count }
        let total = rows.reduce(0) { $0 + $1.total }
        let split = Split(total: total)
        var xml = "<row r=\"1\" ht=\"26\" customHeight=\"1\">" + t.textCell("A1", title(month: month), style: 1) + "</row>"
        xml += "<row r=\"2\" ht=\"24\" customHeight=\"1\">"
            + ["ت", "عدد المشتركين", "مبلغ الاستقطاع", "المجموع"].enumerated().map { t.textCell("\(t.letter($0.offset))2", $0.element, style: 2) }.joined()
            + "</row>"
        for (i, row) in rows.enumerated() {
            let r = i + 3
            xml += "<row r=\"\(r)\" ht=\"22\" customHeight=\"1\">"
                + "<c r=\"A\(r)\" s=\"3\"><v>\(i + 1)</v></c>"
                + "<c r=\"B\(r)\" s=\"4\"><v>\(row.count)</v></c>"
                + "<c r=\"C\(r)\" s=\"4\"><v>\(t.number(row.amount))</v></c>"
                + "<c r=\"D\(r)\" s=\"4\"><f>B\(r)*C\(r)</f><v>\(t.number(row.total))</v></c></row>"
        }
        let last = rows.count + 2
        let tr = last + 1
        xml += "<row r=\"\(tr)\" ht=\"26\" customHeight=\"1\">"
            + t.textCell("A\(tr)", "الإجمالي", style: 5)
            + "<c r=\"B\(tr)\" s=\"6\"><f>SUM(B3:B\(last))</f><v>\(count)</v></c>"
            + "<c r=\"C\(tr)\" s=\"3\"/>"
            + "<c r=\"D\(tr)\" s=\"6\"><f>SUM(D3:D\(last))</f><v>\(t.number(total))</v></c></row>"
        // تفاصيل النسبة (بعد سطر فارغ)، في العمودين B و C.
        let h = tr + 2
        xml += "<row r=\"\(h)\" ht=\"24\" customHeight=\"1\">" + t.textCell("B\(h)", "تفاصيل النسبة", style: 2) + "<c r=\"C\(h)\" s=\"2\"/></row>"
        xml += "<row r=\"\(h + 1)\" ht=\"22\" customHeight=\"1\">" + t.textCell("B\(h + 1)", "البيان", style: 2) + t.textCell("C\(h + 1)", "المبلغ", style: 2) + "</row>"
        let first = h + 2
        let formulas = [
            ("D\(tr)", split.total),
            ("ROUND(C\(first)*\(fundRate),0)", split.fund),
            ("C\(first)-C\(first + 1)", split.afterFund),
            ("ROUND(C\(first + 2)*\(taxRate),0)", split.tax),
            ("C\(first + 2)-C\(first + 3)", split.company),
        ]
        for (i, (formula, value)) in formulas.enumerated() {
            let r = first + i
            xml += "<row r=\"\(r)\" ht=\"22\" customHeight=\"1\">" + t.textCell("B\(r)", splitLabels[i], style: 7)
                + "<c r=\"C\(r)\" s=\"\(i == 4 ? 8 : 4)\"><f>\(formula)</f><v>\(t.number(value))</v></c></row>"
        }
        let sheet = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
        <sheetPr><pageSetUpPr fitToPage="1"/></sheetPr>
        <sheetViews><sheetView rightToLeft="1" workbookViewId="0"/></sheetViews>
        <sheetFormatPr defaultRowHeight="20"/>
        <cols><col min="1" max="1" width="7" customWidth="1"/><col min="2" max="2" width="28" customWidth="1"/><col min="3" max="3" width="22" customWidth="1"/><col min="4" max="4" width="22" customWidth="1"/></cols>
        <sheetData>\(xml)</sheetData>
        <mergeCells count="2"><mergeCell ref="A1:D1"/><mergeCell ref="B\(h):C\(h)"/></mergeCells>
        <printOptions horizontalCentered="1"/>
        <pageMargins left="0.5" right="0.5" top="0.6" bottom="0.6" header="0.3" footer="0.3"/>
        <pageSetup paperSize="9" orientation="portrait" fitToWidth="1" fitToHeight="0"/>
        </worksheet>
        """
        try XLSXWriter.package(worksheet: sheet, styles: styles, printTitles: nil, to: url)
    }

    private static let styles = """
    <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
    <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
    <numFmts count="1"><numFmt numFmtId="164" formatCode="#,##0"/></numFmts>
    <fonts count="3">
    <font><sz val="12"/><name val="Arial"/></font>
    <font><b/><sz val="13"/><name val="Arial"/></font>
    <font><b/><sz val="12"/><name val="Arial"/></font>
    </fonts>
    <fills count="5">
    <fill><patternFill patternType="none"/></fill>
    <fill><patternFill patternType="gray125"/></fill>
    <fill><patternFill patternType="solid"><fgColor rgb="FFD9D9D9"/><bgColor indexed="64"/></patternFill></fill>
    <fill><patternFill patternType="solid"><fgColor rgb="FFFFFF00"/><bgColor indexed="64"/></patternFill></fill>
    <fill><patternFill patternType="solid"><fgColor rgb="FFF2F2F2"/><bgColor indexed="64"/></patternFill></fill>
    </fills>
    <borders count="2">
    <border><left/><right/><top/><bottom/><diagonal/></border>
    <border><left style="thin"><color rgb="FF000000"/></left><right style="thin"><color rgb="FF000000"/></right><top style="thin"><color rgb="FF000000"/></top><bottom style="thin"><color rgb="FF000000"/></bottom><diagonal/></border>
    </borders>
    <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
    <cellXfs count="9">
    <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
    <xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1" applyAlignment="1"><alignment horizontal="center" vertical="center" readingOrder="2"/></xf>
    <xf numFmtId="0" fontId="2" fillId="2" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center" readingOrder="2"/></xf>
    <xf numFmtId="0" fontId="2" fillId="0" borderId="1" xfId="0" applyFont="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf>
    <xf numFmtId="164" fontId="2" fillId="0" borderId="1" xfId="0" applyNumberFormat="1" applyFont="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf>
    <xf numFmtId="0" fontId="2" fillId="0" borderId="1" xfId="0" applyFont="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center" readingOrder="2"/></xf>
    <xf numFmtId="164" fontId="2" fillId="3" borderId="1" xfId="0" applyNumberFormat="1" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf>
    <xf numFmtId="0" fontId="2" fillId="4" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="right" vertical="center" readingOrder="2"/></xf>
    <xf numFmtId="164" fontId="2" fillId="3" borderId="1" xfId="0" applyNumberFormat="1" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf>
    </cellXfs>
    <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
    </styleSheet>
    """

    // MARK: - PDF (A4 بالطول، أرقام هندية)

    static func writePDF(_ rows: [Row], month: String, to url: URL) throws {
        let page = CGRect(x: 0, y: 0, width: 595, height: 842)
        let margin: CGFloat = 40
        let width = page.width - margin * 2
        let ratios: [CGFloat] = [8, 30, 26, 30]
        let widths = ratios.map { $0 / ratios.reduce(0, +) * width }
        var xs: [CGFloat] = []
        var x = page.width - margin
        for w in widths { x -= w; xs.append(x) }
        let gray = UIColor(white: 0.85, alpha: 1)
        let yellow = UIColor(red: 1, green: 1, blue: 0, alpha: 1)
        let formatter = NumberFormatter()
        formatter.numberStyle = .decimal
        formatter.locale = Locale(identifier: "ar_IQ@numbers=arab")
        func num(_ v: Double) -> String { formatter.string(from: NSNumber(value: v)) ?? String(v) }
        let count = rows.reduce(0) { $0 + $1.count }
        let total = rows.reduce(0) { $0 + $1.total }
        let split = Split(total: total)

        func draw(_ text: String, _ rect: CGRect, size: CGFloat = 12, align: NSTextAlignment = .center) {
            let style = NSMutableParagraphStyle()
            style.alignment = align
            style.baseWritingDirection = .rightToLeft
            let s = NSAttributedString(string: text, attributes: [.font: UIFont.boldSystemFont(ofSize: size), .paragraphStyle: style, .foregroundColor: UIColor.black])
            let h = s.boundingRect(with: CGSize(width: rect.width - 8, height: 200), options: [.usesLineFragmentOrigin], context: nil).height
            s.draw(with: CGRect(x: rect.minX + 4, y: rect.midY - h / 2, width: rect.width - 8, height: h), options: [.usesLineFragmentOrigin], context: nil)
        }

        let rowH: CGFloat = 24
        let renderer = UIGraphicsPDFRenderer(bounds: page)
        try renderer.writePDF(to: url) { context in
            let cg = context.cgContext
            cg.setLineWidth(0.7)
            UIColor.black.setStroke()
            var y = margin
            func newPage() {
                context.beginPage()
                draw(title(month: month), CGRect(x: margin, y: margin, width: width, height: 44), size: 13)
                y = margin + 50
                for (c, t) in ["ت", "عدد المشتركين", "مبلغ الاستقطاع", "المجموع"].enumerated() {
                    let r = CGRect(x: xs[c], y: y, width: widths[c], height: rowH)
                    gray.setFill(); cg.fill(r); cg.stroke(r)
                    draw(t, r)
                }
                y += rowH
            }
            func cell(_ c: Int, _ fill: UIColor? = nil) -> CGRect {
                let r = CGRect(x: xs[c], y: y, width: widths[c], height: rowH)
                if let fill { fill.setFill(); cg.fill(r) }
                cg.stroke(r)
                return r
            }
            newPage()
            for (i, row) in rows.enumerated() {
                if y + rowH > page.height - margin { newPage() }
                draw(num(Double(i + 1)), cell(0))
                draw(num(Double(row.count)), cell(1))
                draw(num(row.amount), cell(2))
                draw(num(row.total), cell(3))
                y += rowH
            }
            draw("الإجمالي", cell(0))
            draw(num(Double(count)), cell(1, yellow))
            _ = cell(2)
            draw(num(total), cell(3, yellow), size: 13)
            y += rowH * 2
            if y + rowH * 7 > page.height - margin { context.beginPage(); y = margin }
            // تفاصيل النسبة
            // في العمودين الأوسطين (عدد المشتركين ومبلغ الاستقطاع).
            let labelW = widths[1] + widths[2]
            let headerRect = CGRect(x: xs[2], y: y, width: labelW, height: rowH)
            gray.setFill(); cg.fill(headerRect); cg.stroke(headerRect)
            draw("تفاصيل النسبة", headerRect)
            y += rowH
            let labelRect = { CGRect(x: xs[1], y: y, width: widths[1], height: rowH) }
            let amountRect = { CGRect(x: xs[2], y: y, width: widths[2], height: rowH) }
            var r1 = labelRect(), r2 = amountRect()
            gray.setFill(); cg.fill(r1); cg.fill(r2); cg.stroke(r1); cg.stroke(r2)
            draw("البيان", r1); draw("المبلغ", r2)
            for (i, value) in [split.total, split.fund, split.afterFund, split.tax, split.company].enumerated() {
                y += rowH
                r1 = labelRect(); r2 = amountRect()
                UIColor(white: 0.95, alpha: 1).setFill(); cg.fill(r1)
                if i == 4 { yellow.setFill(); cg.fill(r2) }
                cg.stroke(r1); cg.stroke(r2)
                draw(splitLabels[i], r1, size: 11.5, align: .right)
                draw(num(value), r2)
            }
        }
    }

    static func files(_ rows: [Row], month: String) async throws -> (excel: URL, pdf: URL) {
        try await Task.detached(priority: .userInitiated) {
            let folder = FileManager.default.temporaryDirectory.appendingPathComponent("report-export", isDirectory: true)
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            let name = fileName(month: month)
            let excel = folder.appendingPathComponent(name + ".xlsx")
            let pdf = folder.appendingPathComponent(name + ".pdf")
            try writeExcel(rows, month: month, to: excel)
            try writePDF(rows, month: month, to: pdf)
            return (excel, pdf)
        }.value
    }
}

/// ملخّص عقد الإطعام داخل صفحة البند.
struct FoodContractSection: View {
    let rows: [FoodContractReport.Row]

    var body: some View {
        let total = rows.reduce(0) { $0 + $1.total }
        let split = FoodContractReport.Split(total: total)
        Section {
            Grid(alignment: .leading, horizontalSpacing: 12, verticalSpacing: 7) {
                GridRow {
                    Text(tr("المشتركون", "Subscribers")).bold()
                    Text(tr("المبلغ", "Amount")).bold()
                    Text(tr("المجموع", "Total")).bold()
                }
                .font(.caption)
                .foregroundStyle(.secondary)
                ForEach(rows) { row in
                    GridRow {
                        Text(Num.format(row.count)).monospacedDigit()
                        Text(Money.format(row.amount)).monospacedDigit()
                        Text(Money.format(row.total)).monospacedDigit().fontWeight(.semibold)
                    }
                    .font(.subheadline)
                }
                Divider().gridCellColumns(3)
                GridRow {
                    Text(Num.format(rows.reduce(0) { $0 + $1.count })).bold().monospacedDigit()
                    Text(tr("الإجمالي", "Total")).bold()
                    Text(Money.format(total)).bold().monospacedDigit()
                }
                .font(.subheadline)
            }
            .padding(.vertical, 4)
        } header: {
            Text(tr("عقد الإطعام: حسب مبلغ الاستقطاع", "Food contract: by amount"))
        }
        Section(tr("تفاصيل النسبة", "Split")) {
            LabeledContent(tr("مجموع الاستقطاع", "Total deducted")) { Text(Money.format(split.total)).monospacedDigit() }
            LabeledContent(tr("حصة صندوق الشهداء ٣٪", "Martyrs fund 3%")) { Text(Money.format(split.fund)).monospacedDigit() }
            LabeledContent(tr("بعد حسم حصة الصندوق", "After fund share")) { Text(Money.format(split.afterFund)).monospacedDigit() }
            LabeledContent(tr("ضريبة ١٫٨٪", "Tax 1.8%")) { Text(Money.format(split.tax)).monospacedDigit() }
            LabeledContent(tr("شركة عراق الجوال", "Company")) { Text(Money.format(split.company)).monospacedDigit().bold() }
        }
    }
}
