import SwiftUI
import UIKit
import ZIPFoundation

/// كشف قابل للتصدير بشكل النموذج المعتمد:
/// رأس (اسم الجهة) ← شريط أزرق بعنوان الكشف ← رؤوس الأعمدة ← الصفوف ← سطر المجموع.
nonisolated struct ReportSheet: Sendable {
    enum Kind: Sendable { case serial, text, amount }

    struct Column: Sendable {
        let title: String
        /// عرض العمود بوحدات Excel (عدد الحروف تقريباً)، ويُستخدم نسبياً في PDF.
        let width: Double
        let kind: Kind
    }

    let header: String
    let title: String
    let columns: [Column]
    /// قيم الصفوف نصوصاً، وعمود المبلغ رقماً في `amounts`.
    let rows: [[String]]
    let amounts: [Double]
    var total: Double { amounts.reduce(0, +) }
    var amountColumn: Int { columns.firstIndex { $0.kind == .amount } ?? 0 }
    /// A4 بالعرض إذا كان مجموع عرض الأعمدة أكبر مما تتسعه الصفحة بالطول.
    var landscape: Bool { columns.reduce(0) { $0 + $1.width } > 96 }

    /// عرض كل عمود بحسب أطول قيمة فيه (مع حد أدنى وأعلى).
    func fitted() -> ReportSheet {
        let formatter = NumberFormatter()
        formatter.numberStyle = .decimal
        formatter.locale = Locale(identifier: "en_US")
        let maxAmount = formatter.string(from: NSNumber(value: max(total, amounts.max() ?? 0)))?.count ?? 8
        let fittedColumns = columns.enumerated().map { c, column -> Column in
            let longest: Int
            switch column.kind {
            case .serial: longest = String(rows.count).count
            case .amount: longest = maxAmount
            case .text: longest = rows.reduce(0) { max($0, $1[c].count) }
            }
            let width = min(48, max(Double(column.title.count) + 2, Double(longest) * 1.08 + 2, column.kind == .serial ? 4 : 8))
            return Column(title: column.title, width: width.rounded(), kind: column.kind)
        }
        return ReportSheet(header: header, title: title, columns: fittedColumns, rows: rows, amounts: amounts)
    }

    /// "ايلول 2026" ← "ايلول ٢٠٢٦" كما في النموذج.
    static func easternDigits(_ text: String) -> String {
        String(text.map { c in
            guard let d = c.wholeNumberValue, c.isASCII else { return c }
            return Character(UnicodeScalar(UInt32(0x0660 + d))!)
        })
    }

    static var defaultHeader: String { "قيادة شرطة محافظة الانبار" }
    static let headerKey = "reportHeader"

    /// من مجموعة تقرير وصفوفها.
    static func make(group: ReportGroup, rows: [ReportRow]) -> ReportSheet {
        build(group: group, rows: rows).fitted()
    }

    private static func build(group: ReportGroup, rows: [ReportRow]) -> ReportSheet {
        let header = UserDefaults.standard.string(forKey: headerKey) ?? defaultHeader
        let month = easternDigits(group.month)
        switch group.kind {
        case .deductions:
            return ReportSheet(
                header: header,
                title: "قائمة الاستقطاعات لـ شهر \(month) - \(group.title)",
                columns: [
                    Column(title: "ت", width: 5, kind: .serial),
                    Column(title: "الرتبة", width: 11, kind: .text),
                    Column(title: "الاسم الكامل", width: 34, kind: .text),
                    Column(title: "المبلغ", width: 14, kind: .amount),
                    Column(title: "جهة التنفيذ", width: 28, kind: .text),
                    Column(title: "رقم وتاريخ الاضبارة", width: 32, kind: .text),
                ],
                rows: rows.enumerated().map { i, r in
                    [String(i + 1), r.rank, r.name, "", r.entity.isEmpty ? group.title : r.entity, r.note]
                },
                amounts: rows.map(\.amount)
            )
        case .payroll:
            return ReportSheet(
                header: header,
                title: "قائمة \(group.title) لـ شهر \(month)",
                columns: [
                    Column(title: "ت", width: 5, kind: .serial),
                    Column(title: "الرتبة", width: 11, kind: .text),
                    Column(title: "الاسم الكامل", width: 34, kind: .text),
                    Column(title: "المبلغ", width: 14, kind: .amount),
                    Column(title: "محل العمل", width: 36, kind: .text),
                ],
                rows: rows.enumerated().map { i, r in [String(i + 1), r.rank, r.name, "", r.workplace] },
                amounts: rows.map(\.amount)
            )
        }
    }

    static func fileName(for group: ReportGroup) -> String {
        "\(group.title) - \(group.month)"
            .replacingOccurrences(of: "/", with: "-")
            .replacingOccurrences(of: ":", with: "-")
    }
}

// MARK: - Excel

nonisolated enum XLSXWriter {
    /// يكتب ملف xlsx بالتنسيق (اتجاه من اليمين، ألوان، حدود، دمج، معادلة المجموع).
    static func write(_ sheet: ReportSheet, to url: URL) throws {
        let fm = FileManager.default
        let root = fm.temporaryDirectory.appendingPathComponent("xlsx-\(UUID().uuidString)", isDirectory: true)
        defer { try? fm.removeItem(at: root) }
        let files: [String: String] = [
            "[Content_Types].xml": contentTypes,
            "_rels/.rels": rootRels,
            "xl/workbook.xml": workbook,
            "xl/_rels/workbook.xml.rels": workbookRels,
            "xl/styles.xml": styles,
            "xl/worksheets/sheet1.xml": worksheet(sheet),
        ]
        for (path, text) in files {
            let file = root.appendingPathComponent(path)
            try fm.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
            try Data(text.utf8).write(to: file)
        }
        try? fm.removeItem(at: url)
        let archive = try Archive(url: url, accessMode: .create)
        for path in files.keys.sorted(by: { $0 == "[Content_Types].xml" ? true : ($1 == "[Content_Types].xml" ? false : $0 < $1) }) {
            try archive.addEntry(with: path, relativeTo: root, compressionMethod: .deflate)
        }
    }

    private static func letter(_ index: Int) -> String {
        var n = index + 1
        var s = ""
        while n > 0 {
            let r = (n - 1) % 26
            s = String(UnicodeScalar(65 + r)!) + s
            n = (n - 1) / 26
        }
        return s
    }

    private static func esc(_ text: String) -> String {
        text.replacingOccurrences(of: "&", with: "&amp;")
            .replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;")
            .replacingOccurrences(of: "\"", with: "&quot;")
            .filter { $0 == "\n" || $0 == "\t" || !($0.asciiValue.map { $0 < 32 } ?? false) }
    }

    private static func textCell(_ ref: String, _ text: String, style: Int) -> String {
        "<c r=\"\(ref)\" s=\"\(style)\" t=\"inlineStr\"><is><t xml:space=\"preserve\">\(esc(text))</t></is></c>"
    }

    private static func number(_ value: Double) -> String {
        value == value.rounded() ? String(Int64(value)) : String(value)
    }

    private static func worksheet(_ sheet: ReportSheet) -> String {
        let last = letter(sheet.columns.count - 1)
        var rows = ""
        // 1: اسم الجهة، 2: عنوان الكشف، 3: رؤوس الأعمدة
        rows += "<row r=\"1\" ht=\"24\" customHeight=\"1\">" + textCell("A1", sheet.header, style: 1) + "</row>"
        rows += "<row r=\"2\" ht=\"30\" customHeight=\"1\">" + textCell("A2", sheet.title, style: 2)
            + (1..<sheet.columns.count).map { "<c r=\"\(letter($0))2\" s=\"2\"/>" }.joined() + "</row>"
        rows += "<row r=\"3\" ht=\"22\" customHeight=\"1\">"
            + sheet.columns.enumerated().map { textCell("\(letter($0.offset))3", $0.element.title, style: 3) }.joined() + "</row>"
        for (i, values) in sheet.rows.enumerated() {
            let r = i + 4
            var cells = ""
            for (c, column) in sheet.columns.enumerated() {
                let ref = "\(letter(c))\(r)"
                switch column.kind {
                case .serial: cells += "<c r=\"\(ref)\" s=\"5\"><v>\(i + 1)</v></c>"
                case .amount: cells += "<c r=\"\(ref)\" s=\"6\"><v>\(number(sheet.amounts[i]))</v></c>"
                case .text: cells += textCell(ref, values[c], style: 4)
                }
            }
            rows += "<row r=\"\(r)\" ht=\"20\" customHeight=\"1\">\(cells)</row>"
        }
        // سطر المجموع: "المجموع" في الأعمدة قبل المبلغ، والمبلغ بمعادلة SUM.
        let totalRow = sheet.rows.count + 4
        let amountCol = sheet.amountColumn
        var totalCells = ""
        for c in 0..<sheet.columns.count {
            let ref = "\(letter(c))\(totalRow)"
            if c == 0 {
                totalCells += textCell(ref, "المجموع", style: 7)
            } else if c == amountCol {
                let col = letter(amountCol)
                let formula = sheet.rows.isEmpty ? "0" : "SUM(\(col)4:\(col)\(totalRow - 1))"
                totalCells += "<c r=\"\(ref)\" s=\"8\"><f>\(formula)</f><v>\(number(sheet.total))</v></c>"
            } else {
                totalCells += "<c r=\"\(ref)\" s=\"\(c < amountCol ? 7 : 9)\"/>"
            }
        }
        rows += "<row r=\"\(totalRow)\" ht=\"22\" customHeight=\"1\">\(totalCells)</row>"

        var merges = ["A1:\(last)1", "A2:\(last)2"]
        if amountCol > 1 { merges.append("A\(totalRow):\(letter(amountCol - 1))\(totalRow)") }
        let cols = sheet.columns.enumerated().map { "<col min=\"\($0.offset + 1)\" max=\"\($0.offset + 1)\" width=\"\($0.element.width)\" customWidth=\"1\"/>" }.joined()

        return """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
        <sheetPr><pageSetUpPr fitToPage="1"/></sheetPr>
        <sheetViews><sheetView rightToLeft="1" workbookViewId="0"><pane ySplit="3" topLeftCell="A4" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>
        <sheetFormatPr defaultRowHeight="20"/>
        <cols>\(cols)</cols>
        <sheetData>\(rows)</sheetData>
        <mergeCells count="\(merges.count)">\(merges.map { "<mergeCell ref=\"\($0)\"/>" }.joined())</mergeCells>
        <printOptions horizontalCentered="1"/>
        <pageMargins left="0.4" right="0.4" top="0.5" bottom="0.5" header="0.3" footer="0.3"/>
        <pageSetup paperSize="9" orientation="\(sheet.landscape ? "landscape" : "portrait")" fitToWidth="1" fitToHeight="0"/>
        </worksheet>
        """
    }

    private static let styles = """
    <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
    <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
    <numFmts count="1"><numFmt numFmtId="164" formatCode="#,##0"/></numFmts>
    <fonts count="5">
    <font><sz val="11"/><name val="Arial"/></font>
    <font><b/><sz val="14"/><name val="Arial"/></font>
    <font><b/><sz val="15"/><color rgb="FFFFFFFF"/><name val="Arial"/></font>
    <font><b/><sz val="11"/><name val="Arial"/></font>
    <font><b/><sz val="12"/><name val="Arial"/></font>
    </fonts>
    <fills count="4">
    <fill><patternFill patternType="none"/></fill>
    <fill><patternFill patternType="gray125"/></fill>
    <fill><patternFill patternType="solid"><fgColor rgb="FF1F4E79"/><bgColor indexed="64"/></patternFill></fill>
    <fill><patternFill patternType="solid"><fgColor rgb="FFDDEBF7"/><bgColor indexed="64"/></patternFill></fill>
    </fills>
    <borders count="2">
    <border><left/><right/><top/><bottom/><diagonal/></border>
    <border><left style="thin"><color rgb="FF9BB7D4"/></left><right style="thin"><color rgb="FF9BB7D4"/></right><top style="thin"><color rgb="FF9BB7D4"/></top><bottom style="thin"><color rgb="FF9BB7D4"/></bottom><diagonal/></border>
    </borders>
    <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
    <cellXfs count="10">
    <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
    <xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1" applyAlignment="1"><alignment horizontal="center" vertical="center" readingOrder="2"/></xf>
    <xf numFmtId="0" fontId="2" fillId="2" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center" readingOrder="2"/></xf>
    <xf numFmtId="0" fontId="3" fillId="3" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center" readingOrder="2"/></xf>
    <xf numFmtId="0" fontId="0" fillId="0" borderId="1" xfId="0" applyBorder="1" applyAlignment="1"><alignment horizontal="right" vertical="center" readingOrder="2"/></xf>
    <xf numFmtId="0" fontId="3" fillId="0" borderId="1" xfId="0" applyFont="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf>
    <xf numFmtId="164" fontId="4" fillId="0" borderId="1" xfId="0" applyNumberFormat="1" applyFont="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf>
    <xf numFmtId="0" fontId="3" fillId="3" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center" readingOrder="2"/></xf>
    <xf numFmtId="164" fontId="4" fillId="3" borderId="1" xfId="0" applyNumberFormat="1" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf>
    <xf numFmtId="0" fontId="0" fillId="3" borderId="1" xfId="0" applyFill="1" applyBorder="1"/>
    </cellXfs>
    <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
    </styleSheet>
    """

    private static let contentTypes = """
    <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
    <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
    <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
    <Default Extension="xml" ContentType="application/xml"/>
    <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
    <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
    <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
    </Types>
    """

    private static let rootRels = """
    <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
    <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
    </Relationships>
    """

    private static let workbook = """
    <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
    <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
    <bookViews><workbookView/></bookViews>
    <sheets><sheet name="الكشف" sheetId="1" r:id="rId1"/></sheets>
    <definedNames><definedName name="_xlnm.Print_Titles" localSheetId="0">'الكشف'!$1:$3</definedName></definedNames>
    </workbook>
    """

    private static let workbookRels = """
    <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
    <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
    <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
    </Relationships>
    """
}

// MARK: - PDF

nonisolated enum PDFReportWriter {
    /// A4 بالطول أو بالعرض بحسب عرض الأعمدة، يتكرر العنوان ورؤوس الأعمدة في كل صفحة،
    /// والمجموع في آخر صفحة، ورقم الصفحة في الأسفل.
    static func write(_ sheet: ReportSheet, to url: URL) throws {
        let page = sheet.landscape ? CGRect(x: 0, y: 0, width: 842, height: 595) : CGRect(x: 0, y: 0, width: 595, height: 842)
        let margin: CGFloat = 28
        let tableWidth = page.width - margin * 2
        let rowHeight: CGFloat = 20
        let headerTop: CGFloat = margin
        let titleHeight: CGFloat = 30
        let columnsHeight: CGFloat = 22
        let tableTop = headerTop + 24 + titleHeight + columnsHeight
        let bottom = page.height - margin - 18
        let perPage = max(1, Int((bottom - tableTop) / rowHeight))
        let pages = max(1, Int(ceil(Double(sheet.rows.count + 1) / Double(perPage))))

        let totalWidth = sheet.columns.reduce(0) { $0 + $1.width }
        let widths = sheet.columns.map { CGFloat($0.width / totalWidth) * tableWidth }
        // الأعمدة من اليمين إلى اليسار.
        var xs: [CGFloat] = []
        var x = page.width - margin
        for w in widths { x -= w; xs.append(x) }

        let dark = UIColor(red: 0.12, green: 0.31, blue: 0.47, alpha: 1)
        let light = UIColor(red: 0.87, green: 0.92, blue: 0.97, alpha: 1)
        let line = UIColor(red: 0.61, green: 0.72, blue: 0.83, alpha: 1)

        func draw(_ text: String, in rect: CGRect, font: UIFont, color: UIColor = .black, align: NSTextAlignment = .center) {
            let style = NSMutableParagraphStyle()
            style.alignment = align
            style.baseWritingDirection = .rightToLeft
            style.lineBreakMode = .byTruncatingTail
            let attributed = NSAttributedString(string: text, attributes: [.font: font, .foregroundColor: color, .paragraphStyle: style])
            let size = attributed.boundingRect(with: CGSize(width: rect.width - 8, height: .greatestFiniteMagnitude),
                                               options: [.usesLineFragmentOrigin], context: nil).size
            let h = min(size.height, rect.height)
            attributed.draw(with: CGRect(x: rect.minX + 4, y: rect.midY - h / 2, width: rect.width - 8, height: h),
                            options: [.usesLineFragmentOrigin, .truncatesLastVisibleLine], context: nil)
        }

        func cellRect(_ c: Int, y: CGFloat, height: CGFloat) -> CGRect {
            CGRect(x: xs[c], y: y, width: widths[c], height: height)
        }

        let amountFormatter = NumberFormatter()
        amountFormatter.numberStyle = .decimal
        amountFormatter.locale = Locale(identifier: "en_US")
        amountFormatter.maximumFractionDigits = 2
        func money(_ v: Double) -> String { amountFormatter.string(from: NSNumber(value: v)) ?? String(v) }

        let renderer = UIGraphicsPDFRenderer(bounds: page)
        try renderer.writePDF(to: url) { context in
            let cg = context.cgContext
            for p in 0..<pages {
                context.beginPage()
                draw(sheet.header, in: CGRect(x: margin, y: headerTop, width: tableWidth, height: 22), font: .boldSystemFont(ofSize: 14))
                let titleRect = CGRect(x: margin, y: headerTop + 24, width: tableWidth, height: titleHeight)
                dark.setFill(); cg.fill(titleRect)
                draw(sheet.title, in: titleRect, font: .boldSystemFont(ofSize: 14), color: .white)
                let colY = titleRect.maxY
                for (c, column) in sheet.columns.enumerated() {
                    let r = cellRect(c, y: colY, height: columnsHeight)
                    light.setFill(); cg.fill(r)
                    line.setStroke(); cg.stroke(r, width: 0.5)
                    draw(column.title, in: r, font: .boldSystemFont(ofSize: 10.5))
                }
                var y = tableTop
                let start = p * perPage
                let end = min(sheet.rows.count, start + perPage)
                if start < end {
                    for i in start..<end {
                        for (c, column) in sheet.columns.enumerated() {
                            let r = cellRect(c, y: y, height: rowHeight)
                            line.setStroke(); cg.stroke(r, width: 0.5)
                            switch column.kind {
                            case .serial: draw(String(i + 1), in: r, font: .boldSystemFont(ofSize: 10))
                            case .amount: draw(money(sheet.amounts[i]), in: r, font: .boldSystemFont(ofSize: 10.5))
                            case .text: draw(sheet.rows[i][c], in: r, font: .systemFont(ofSize: 10), align: .right)
                            }
                        }
                        y += rowHeight
                    }
                }
                if p == pages - 1 {
                    let amountCol = sheet.amountColumn
                    let labelRect = (0..<max(1, amountCol)).map { cellRect($0, y: y, height: columnsHeight) }.reduce(CGRect.null) { $0.union($1) }
                    for c in 0..<sheet.columns.count {
                        let r = cellRect(c, y: y, height: columnsHeight)
                        light.setFill(); cg.fill(r)
                    }
                    line.setStroke()
                    cg.stroke(labelRect, width: 0.5)
                    for c in amountCol..<sheet.columns.count { cg.stroke(cellRect(c, y: y, height: columnsHeight), width: 0.5) }
                    draw("المجموع", in: labelRect, font: .boldSystemFont(ofSize: 11))
                    draw(money(sheet.total), in: cellRect(amountCol, y: y, height: columnsHeight), font: .boldSystemFont(ofSize: 11))
                }
                if pages > 1 {
                    draw("صفحة \(p + 1) من \(pages)", in: CGRect(x: margin, y: page.height - margin - 14, width: tableWidth, height: 14),
                         font: .systemFont(ofSize: 9), color: .darkGray)
                }
            }
        }
    }
}

// MARK: - نافذة التصدير

struct ReportExportSheet: View {
    let group: ReportGroup
    let rows: [ReportRow]
    @Environment(\.dismiss) private var dismiss
    @State private var excel: URL?
    @State private var pdf: URL?
    @State private var failed: String?

    var body: some View {
        NavigationStack {
            List {
                Section {
                    VStack(alignment: .leading, spacing: 4) {
                        Text(group.title).font(.headline)
                        Text(tr("\(group.month) · \(Num.format(rows.count)) موظف · المجموع \(Money.format(group.total))",
                                "\(group.month) · \(rows.count) employees · total \(Money.format(group.total))"))
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                }
                Section {
                    if let excel, let pdf {
                        ShareLink(item: excel) {
                            Label {
                                VStack(alignment: .leading, spacing: 2) {
                                    Text("Excel").font(.body.weight(.semibold))
                                    Text(tr("جدول بالتنسيق ومعادلة المجموع", "Formatted table with a SUM formula")).font(.caption).foregroundStyle(.secondary)
                                }
                            } icon: {
                                Image(systemName: "tablecells.fill").foregroundStyle(.green)
                            }
                        }
                        ShareLink(item: pdf) {
                            Label {
                                VStack(alignment: .leading, spacing: 2) {
                                    Text("PDF").font(.body.weight(.semibold))
                                    Text(tr("كشف جاهز للطباعة بحجم A4", "Print-ready A4 list")).font(.caption).foregroundStyle(.secondary)
                                }
                            } icon: {
                                Image(systemName: "doc.richtext.fill").foregroundStyle(.red)
                            }
                        }
                    } else if let failed {
                        Text(failed).foregroundStyle(.red)
                    } else {
                        HStack {
                            ProgressView()
                            Text(tr("جارٍ تجهيز الملفات…", "Preparing files…")).foregroundStyle(.secondary)
                        }
                    }
                } header: {
                    Text(tr("الصيغة", "Format"))
                }
            }
            .navigationTitle(tr("تصدير الكشف", "Export list"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(tr("إغلاق", "Close"), systemImage: "xmark") { dismiss() }
                }
            }
        }
        .environment(\.layoutDirection, AppSettings.isEnglish ? .leftToRight : .rightToLeft)
        .presentationDetents([.medium, .large])
        .task {
            do {
                let files = try await ReportFiles.make(group: group, rows: rows)
                excel = files.excel
                pdf = files.pdf
            } catch {
                failed = error.localizedDescription
            }
        }
    }
}

enum ReportFiles {
    /// يكتب الملفين في tmp/report-export (في الخلفية).
    static func make(group: ReportGroup, rows: [ReportRow]) async throws -> (excel: URL, pdf: URL) {
        let sheet = ReportSheet.make(group: group, rows: rows)
        let name = ReportSheet.fileName(for: group)
        return try await Task.detached(priority: .userInitiated) {
            let folder = FileManager.default.temporaryDirectory.appendingPathComponent("report-export", isDirectory: true)
            try? FileManager.default.removeItem(at: folder)
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            let excel = folder.appendingPathComponent(name + ".xlsx")
            let pdf = folder.appendingPathComponent(name + ".pdf")
            try XLSXWriter.write(sheet, to: excel)
            try PDFReportWriter.write(sheet, to: pdf)
            return (excel, pdf)
        }.value
    }
}
