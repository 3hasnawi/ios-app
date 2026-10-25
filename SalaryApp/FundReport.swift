import SwiftUI
import UIKit

/// كشف صندوق الشهداء بالشكل المعتمد: مجمّع حسب الفئة (ضباط، موظفين، مراتب، عقود)
/// مع العدد والمقرر لكل فرد ومجموع ما استُقطع فعلاً.
nonisolated enum FundReport {
    struct Category: Sendable, Hashable {
        let name: String
        /// المقرر لكل فرد.
        let rate: Double
    }

    /// بالترتيب الذي يظهر في الكشف.
    static let categories: [Category] = [
        Category(name: "ضباط", rate: 5000),
        Category(name: "موظفين", rate: 4000),
        Category(name: "مراتب", rate: 4000),
        Category(name: "عقود", rate: 2000),
    ]

    struct Row: Sendable, Hashable, Identifiable {
        var id: String { category.name }
        let category: Category
        var count: Int
        var total: Double
    }

    /// يُطبَّق على بند "صندوق الشهداء" من ورقة الرواتب.
    static func applies(to title: String) -> Bool {
        ArabicText.normalize(title).contains(ArabicText.normalize("شهداء"))
    }

    private static let officerRanks: Set<String> = Set([
        "لواء", "فريق", "فريق اول", "عميد", "عقيد", "مقدم", "رائد", "نقيب",
        "ملازم اول", "م اول", "م. اول", "م . اول", "ملازم",
    ].map(key))

    private static func key(_ rank: String) -> String {
        ArabicText.normalize(rank).replacingOccurrences(of: ".", with: " ")
            .split(separator: " ").joined(separator: " ")
    }

    /// الفئة من الرتبة: العقود، ثم الموظفون (موظف د…)، ثم الضباط، وكل ما عداها مراتب (من شرطي إلى ر . ع).
    static func category(of rank: String) -> Int {
        let k = key(rank)
        if k.split(separator: " ").contains(where: { $0 == "عقد" || $0 == "عقود" }) { return 3 }
        if k.hasPrefix(key("موظف")) { return 1 }
        if officerRanks.contains(k) { return 0 }
        return 2
    }

    static func summary(_ people: [(rank: String, amount: Double)]) -> [Row] {
        var rows = categories.map { Row(category: $0, count: 0, total: 0) }
        for p in people where p.amount != 0 {
            let i = category(of: p.rank)
            rows[i].count += 1
            rows[i].total += p.amount
        }
        return rows
    }

    // MARK: - العنوان

    static func title(month: String) -> String {
        let header = UserDefaults.standard.string(forKey: ReportSheet.headerKey) ?? ReportSheet.defaultHeader
        let words = month.split(separator: " ").map(String.init)
        let year = words.last.flatMap { $0.allSatisfy(\.isNumber) ? $0 : nil }
        let name = year == nil ? month : words.dropLast().joined(separator: " ")
        let yearText = year.map { " لسنة (\(ReportSheet.easternDigits($0)))" } ?? ""
        return "\(header) / تبرعات صندوق شهداء الشرطة لشهر( \(name) )\(yearText)"
    }

    static func fileName(month: String) -> String {
        "صندوق الشهداء - \(month)".replacingOccurrences(of: "/", with: "-")
    }

    // MARK: - Excel

    static func writeExcel(_ rows: [Row], month: String, to url: URL) throws {
        let t = XLSXWriter.self
        let columns: [(String, Double)] = [("ت", 5), ("الرتبة", 16), ("العدد", 14), ("دعم النشاط الاجتماعي", 24), ("المجموع", 22), ("الملاحظات", 24)]
        var xml = ""
        xml += "<row r=\"1\" ht=\"28\" customHeight=\"1\">" + t.textCell("A1", title(month: month), style: 1) + "</row>"
        xml += "<row r=\"2\" ht=\"26\" customHeight=\"1\">"
            + columns.enumerated().map { t.textCell("\(t.letter($0.offset))2", $0.element.0, style: 2) }.joined() + "</row>"
        for (i, row) in rows.enumerated() {
            let r = i + 3
            xml += "<row r=\"\(r)\" ht=\"26\" customHeight=\"1\">"
                + "<c r=\"A\(r)\" s=\"3\"><v>\(i + 1)</v></c>"
                + t.textCell("B\(r)", row.category.name, style: 4)
                + "<c r=\"C\(r)\" s=\"5\"><v>\(row.count)</v></c>"
                + "<c r=\"D\(r)\" s=\"5\"><v>\(t.number(row.category.rate))</v></c>"
                + "<c r=\"E\(r)\" s=\"5\"><v>\(t.number(row.total))</v></c>"
                + "<c r=\"F\(r)\" s=\"6\"/></row>"
        }
        let last = rows.count + 2
        let tr = last + 1
        let count = rows.reduce(0) { $0 + $1.count }
        let total = rows.reduce(0) { $0 + $1.total }
        xml += "<row r=\"\(tr)\" ht=\"28\" customHeight=\"1\">"
            + t.textCell("A\(tr)", "المجموع", style: 7) + "<c r=\"B\(tr)\" s=\"7\"/>"
            + "<c r=\"C\(tr)\" s=\"8\"><f>SUM(C3:C\(last))</f><v>\(count)</v></c>"
            + "<c r=\"D\(tr)\" s=\"8\"/>"
            + "<c r=\"E\(tr)\" s=\"9\"><f>SUM(E3:E\(last))</f><v>\(t.number(total))</v></c>"
            + "<c r=\"F\(tr)\" s=\"6\"/></row>"
        let cols = columns.enumerated().map { "<col min=\"\($0.offset + 1)\" max=\"\($0.offset + 1)\" width=\"\($0.element.1)\" customWidth=\"1\"/>" }.joined()
        let sheet = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
        <sheetPr><pageSetUpPr fitToPage="1"/></sheetPr>
        <sheetViews><sheetView rightToLeft="1" workbookViewId="0"/></sheetViews>
        <sheetFormatPr defaultRowHeight="20"/>
        <cols>\(cols)</cols>
        <sheetData>\(xml)</sheetData>
        <mergeCells count="2"><mergeCell ref="A1:F1"/><mergeCell ref="A\(tr):B\(tr)"/></mergeCells>
        <printOptions horizontalCentered="1"/>
        <pageMargins left="0.5" right="0.5" top="0.6" bottom="0.6" header="0.3" footer="0.3"/>
        <pageSetup paperSize="9" orientation="portrait" fitToWidth="1" fitToHeight="0"/>
        </worksheet>
        """
        try XLSXWriter.package(worksheet: sheet, styles: styles, printTitles: nil, to: url)
    }

    /// عنوان عريض بلا تعبئة، رؤوس رمادية، أرقام عريضة، سطر مجموع رمادي ومجموع المبالغ أخضر.
    private static let styles = """
    <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
    <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
    <numFmts count="1"><numFmt numFmtId="164" formatCode="#,##0"/></numFmts>
    <fonts count="3">
    <font><sz val="12"/><name val="Arial"/></font>
    <font><b/><sz val="15"/><name val="Arial"/></font>
    <font><b/><sz val="14"/><name val="Arial"/></font>
    </fonts>
    <fills count="4">
    <fill><patternFill patternType="none"/></fill>
    <fill><patternFill patternType="gray125"/></fill>
    <fill><patternFill patternType="solid"><fgColor rgb="FFBFBFBF"/><bgColor indexed="64"/></patternFill></fill>
    <fill><patternFill patternType="solid"><fgColor rgb="FF92D050"/><bgColor indexed="64"/></patternFill></fill>
    </fills>
    <borders count="2">
    <border><left/><right/><top/><bottom/><diagonal/></border>
    <border><left style="thin"><color rgb="FF000000"/></left><right style="thin"><color rgb="FF000000"/></right><top style="thin"><color rgb="FF000000"/></top><bottom style="thin"><color rgb="FF000000"/></bottom><diagonal/></border>
    </borders>
    <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
    <cellXfs count="10">
    <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
    <xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1" applyAlignment="1"><alignment horizontal="right" vertical="center" readingOrder="2"/></xf>
    <xf numFmtId="0" fontId="2" fillId="2" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center" readingOrder="2"/></xf>
    <xf numFmtId="0" fontId="2" fillId="0" borderId="1" xfId="0" applyFont="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf>
    <xf numFmtId="0" fontId="2" fillId="0" borderId="1" xfId="0" applyFont="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center" readingOrder="2"/></xf>
    <xf numFmtId="164" fontId="2" fillId="0" borderId="1" xfId="0" applyNumberFormat="1" applyFont="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf>
    <xf numFmtId="0" fontId="0" fillId="0" borderId="1" xfId="0" applyBorder="1"/>
    <xf numFmtId="0" fontId="2" fillId="2" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center" readingOrder="2"/></xf>
    <xf numFmtId="164" fontId="2" fillId="2" borderId="1" xfId="0" applyNumberFormat="1" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf>
    <xf numFmtId="164" fontId="2" fillId="3" borderId="1" xfId="0" applyNumberFormat="1" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf>
    </cellXfs>
    <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
    </styleSheet>
    """

    // MARK: - PDF (A4 بالطول، أرقام هندية كما في النموذج)

    static func writePDF(_ rows: [Row], month: String, to url: URL) throws {
        let page = CGRect(x: 0, y: 0, width: 595, height: 842)
        let margin: CGFloat = 36
        let width = page.width - margin * 2
        let ratios: [CGFloat] = [5, 16, 14, 24, 22, 24]
        let widths = ratios.map { $0 / ratios.reduce(0, +) * width }
        var xs: [CGFloat] = []
        var x = page.width - margin
        for w in widths { x -= w; xs.append(x) }
        let gray = UIColor(white: 0.75, alpha: 1)
        let green = UIColor(red: 0.57, green: 0.82, blue: 0.31, alpha: 1)
        let formatter = NumberFormatter()
        formatter.numberStyle = .decimal
        formatter.locale = Locale(identifier: "ar_IQ@numbers=arab")
        func num(_ v: Double) -> String { formatter.string(from: NSNumber(value: v)) ?? String(v) }

        func draw(_ text: String, _ rect: CGRect, bold: Bool = true, size: CGFloat = 13, align: NSTextAlignment = .center) {
            let style = NSMutableParagraphStyle()
            style.alignment = align
            style.baseWritingDirection = .rightToLeft
            let font = bold ? UIFont.boldSystemFont(ofSize: size) : UIFont.systemFont(ofSize: size)
            let s = NSAttributedString(string: text, attributes: [.font: font, .paragraphStyle: style, .foregroundColor: UIColor.black])
            let h = s.boundingRect(with: CGSize(width: rect.width - 8, height: 200), options: [.usesLineFragmentOrigin], context: nil).height
            s.draw(with: CGRect(x: rect.minX + 4, y: rect.midY - h / 2, width: rect.width - 8, height: h), options: [.usesLineFragmentOrigin], context: nil)
        }

        let renderer = UIGraphicsPDFRenderer(bounds: page)
        try renderer.writePDF(to: url) { context in
            context.beginPage()
            let cg = context.cgContext
            cg.setLineWidth(0.8)
            UIColor.black.setStroke()
            // العنوان قد يأخذ سطرين في الصفحة الطولية.
            draw(title(month: month), CGRect(x: margin, y: margin, width: width, height: 48), size: 15, align: .right)
            var y = margin + 58
            let h: CGFloat = 30
            func cell(_ c: Int, _ fill: UIColor? = nil) -> CGRect {
                let r = CGRect(x: xs[c], y: y, width: widths[c], height: h)
                if let fill { fill.setFill(); cg.fill(r) }
                cg.stroke(r)
                return r
            }
            for (c, title) in ["ت", "الرتبة", "العدد", "دعم النشاط الاجتماعي", "المجموع", "الملاحظات"].enumerated() {
                draw(title, cell(c, gray), size: 12.5)
            }
            for (i, row) in rows.enumerated() {
                y += h
                draw(num(Double(i + 1)), cell(0))
                draw(row.category.name, cell(1))
                draw(num(Double(row.count)), cell(2))
                draw(num(row.category.rate), cell(3))
                draw(num(row.total), cell(4))
                _ = cell(5)
            }
            y += h
            let label = cell(0, gray).union(cell(1, gray))
            gray.setFill(); cg.fill(label); cg.stroke(label)
            draw("المجموع", label)
            draw(num(Double(rows.reduce(0) { $0 + $1.count })), cell(2, gray))
            _ = cell(3, gray)
            draw(num(rows.reduce(0) { $0 + $1.total }), cell(4, green), size: 14)
            _ = cell(5)
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

extension ReportEngine {
    /// ملخّص صندوق الشهداء حسب الفئة لشهر.
    func fundSummary(month: String, title: String) throws -> [FundReport.Row] {
        var e = FetchDescriptor<Employee>(predicate: #Predicate { $0.month == month })
        e.propertiesToFetch = [\.rank, \.itemsData]
        let people = try modelContext.fetch(e).compactMap { emp -> (rank: String, amount: Double)? in
            guard let amount = PayrollItems.decode(emp.itemsData)[title] else { return nil }
            return (emp.rank, amount)
        }
        return FundReport.summary(people)
    }
}

/// ملخّص الفئات داخل صفحة البند (يظهر فوق قائمة الأسماء).
struct FundSummarySection: View {
    let rows: [FundReport.Row]

    var body: some View {
        Section {
            Grid(alignment: .leading, horizontalSpacing: 10, verticalSpacing: 8) {
                GridRow {
                    Text(tr("الفئة", "Category")).bold()
                    Text(tr("العدد", "Count")).bold()
                    Text(tr("المقرر", "Rate")).bold()
                    Text(tr("المجموع", "Total")).bold()
                }
                .font(.caption)
                .foregroundStyle(.secondary)
                ForEach(rows) { row in
                    GridRow {
                        Text(row.category.name).font(.subheadline.weight(.semibold))
                        Text(Num.format(row.count)).monospacedDigit()
                        Text(Money.format(row.category.rate)).monospacedDigit().foregroundStyle(.secondary)
                        Text(Money.format(row.total)).monospacedDigit().fontWeight(.semibold)
                    }
                    .font(.subheadline)
                }
                Divider().gridCellColumns(4)
                GridRow {
                    Text(tr("المجموع", "Total")).bold()
                    Text(Num.format(rows.reduce(0) { $0 + $1.count })).bold().monospacedDigit()
                    Text("")
                    Text(Money.format(rows.reduce(0) { $0 + $1.total })).bold().monospacedDigit().foregroundStyle(.green)
                }
                .font(.subheadline)
            }
            .padding(.vertical, 4)
        } header: {
            Text(tr("حسب الفئة", "By category"))
        }
    }
}
