import SwiftUI
import SwiftData
import UIKit

/// ترتيب الرتب من الأعلى إلى الأدنى، لترتيب الكشوف كما في النماذج.
nonisolated enum RankOrder {
    private static let order: [String] = [
        "فريق اول", "فريق", "لواء", "عميد", "عقيد", "مقدم", "رائد", "نقيب", "ملازم اول", "م اول", "ملازم",
        "موظف د1", "موظف د2", "موظف د3", "موظف د4", "موظف د5", "موظف د6", "موظف د7", "موظف د8", "موظف د9", "موظف د10",
        "مفوض1", "مفوض2", "مفوض3", "مفوض4", "مفوض5", "مفوض6", "مفوض7", "مفوض8",
        "ر ع", "ع م", "ن ع", "ش1 م", "ش م", "شرطي", "عقود", "عقد",
    ].map(key)

    private static let index: [String: Int] = Dictionary(order.enumerated().map { ($1, $0) }, uniquingKeysWith: { a, _ in a })

    static func key(_ rank: String) -> String {
        ArabicText.latinDigits(ArabicText.normalize(rank)).replacingOccurrences(of: ".", with: " ")
            .split(separator: " ").joined(separator: " ")
    }

    static func rank(_ text: String) -> Int { index[key(text)] ?? order.count }
}

/// اشتراكات الرعاية الصحية: قائمة المشتركين (مع عدد أفراد الأسرة = المبلغ ÷ ٣٥٠٠)،
/// وقائمة غير المشتركين (لهم رواتب ولم يُستقطع منهم).
nonisolated enum HealthCareReport {
    static let perMember = 3500.0

    struct Lists: Sendable {
        var subscribers: [ReportRow]
        var others: [ReportRow]
        /// مبالغ لا تقبل القسمة على \(Num.format(3500)) (الكشف لا يقبل الكسور).
        var irregular: [ReportRow] { subscribers.filter { !HealthCareReport.isExact($0.amount) } }
    }

    static func applies(to title: String) -> Bool {
        ArabicText.normalize(title).contains(ArabicText.normalize("الرعاية"))
    }

    /// العقود ورجال الأمن غير مشمولين بالرعاية الصحية: لا يدخلون قائمة غير المشتركين.
    static func notCovered(rank: String) -> Bool {
        let key = ArabicText.normalize(rank)
        return ["عقد", "عقود", "رجل امن", "رجل الامن", "رجال الامن"].contains { key.contains(ArabicText.normalize($0)) }
    }

    static func isExact(_ amount: Double) -> Bool { amount.truncatingRemainder(dividingBy: perMember) == 0 }

    static func family(_ amount: Double) -> Int { max(1, Int((amount / perMember).rounded())) }

    /// عدد أفراد الأسرة كما يُكتب في الكشف: عدد صحيح، أو الناتج بكسره إذا كان المبلغ شاذاً (ليُراجع).
    static func familyText(_ amount: Double) -> String {
        isExact(amount) ? String(family(amount)) : String(format: "%.2f", amount / perMember)
    }

    /// للتنبيه بعد استيراد ملف الرواتب.
    static func irregularCount(in employees: [ImportedEmployee]) -> Int {
        employees.reduce(0) { count, e in
            count + e.items.filter { applies(to: $0.key) && $0.value != 0 && !isExact($0.value) }.count
        }
    }

    static func title(month: String, others: Bool) -> String {
        let header = UserDefaults.standard.string(forKey: ReportSheet.headerKey) ?? ReportSheet.defaultHeader
        let month = ReportSheet.easternDigits(month)
        return others
            ? "غير المشتركين في الرعاية الصحية ل\(header) لشهر \(month)"
            : "اشتراكات الرعاية الصحية ل\(header) لشهر \(month)"
    }

    static func fileName(month: String) -> String {
        "اشتراكات الرعاية الصحية - \(month)".replacingOccurrences(of: "/", with: "-")
    }

    // MARK: - Excel (ورقتان)

    static func writeExcel(_ lists: Lists, month: String, to url: URL) throws {
        try XLSXWriter.package(sheets: [
            ("المشتركين", sheet(lists.subscribers, month: month, others: false)),
            ("الغير مشتركين", sheet(lists.others, month: month, others: true)),
        ], styles: styles, to: url)
    }

    private static func sheet(_ rows: [ReportRow], month: String, others: Bool) -> String {
        let t = XLSXWriter.self
        let headers = others ? ["ت", "الرتبة", "الاسم", "المبلغ"] : ["ت", "الرتبة", "الاسم", "المبلغ", "عدد افراد الاسرة"]
        let last = t.letter(headers.count - 1)
        var xml = "<row r=\"1\" ht=\"34\" customHeight=\"1\">" + t.textCell("A1", title(month: month, others: others), style: 1)
            + (1..<headers.count).map { "<c r=\"\(t.letter($0))1\" s=\"1\"/>" }.joined() + "</row>"
        xml += "<row r=\"2\" ht=\"22\" customHeight=\"1\">"
            + headers.enumerated().map { t.textCell("\(t.letter($0.offset))2", $0.element, style: 2) }.joined() + "</row>"
        for (i, row) in rows.enumerated() {
            let r = i + 3
            xml += "<row r=\"\(r)\" ht=\"20\" customHeight=\"1\">"
                + "<c r=\"A\(r)\" s=\"3\"><v>\(i + 1)</v></c>"
                + t.textCell("B\(r)", row.rank, style: 4)
                + t.textCell("C\(r)", row.name, style: 4)
                + "<c r=\"D\(r)\" s=\"3\"><v>\(t.number(row.amount))</v></c>"
                + (others ? "" : isExact(row.amount)
                    ? "<c r=\"E\(r)\" s=\"3\"><v>\(family(row.amount))</v></c>"
                    : "<c r=\"E\(r)\" s=\"5\"><v>\(familyText(row.amount))</v></c>")
                + "</row>"
        }
        let end = max(2, rows.count + 2)
        let widths: [Double] = others ? [7, 13, 42, 14] : [7, 13, 42, 14, 17]
        let cols = widths.enumerated().map { "<col min=\"\($0.offset + 1)\" max=\"\($0.offset + 1)\" width=\"\($0.element)\" customWidth=\"1\"/>" }.joined()
        let tab = others ? "FFFF0000" : "FF70AD47"
        return """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
        <sheetPr><tabColor rgb="\(tab)"/><pageSetUpPr fitToPage="1"/></sheetPr>
        <sheetViews><sheetView rightToLeft="1" workbookViewId="0"><pane ySplit="2" topLeftCell="A3" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>
        <sheetFormatPr defaultRowHeight="20"/>
        <cols>\(cols)</cols>
        <sheetData>\(xml)</sheetData>
        <autoFilter ref="A2:\(last)\(end)"/>
        <mergeCells count="1"><mergeCell ref="A1:\(last)1"/></mergeCells>
        <printOptions horizontalCentered="1"/>
        <pageMargins left="0.5" right="0.5" top="0.6" bottom="0.6" header="0.3" footer="0.3"/>
        <pageSetup paperSize="9" orientation="portrait" fitToWidth="1" fitToHeight="0"/>
        </worksheet>
        """
    }

    private static let styles = """
    <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
    <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
    <fonts count="3">
    <font><sz val="12"/><name val="Arial"/></font>
    <font><b/><sz val="16"/><name val="Arial"/></font>
    <font><b/><sz val="12"/><name val="Arial"/></font>
    </fonts>
    <fills count="5">
    <fill><patternFill patternType="none"/></fill>
    <fill><patternFill patternType="gray125"/></fill>
    <fill><patternFill patternType="solid"><fgColor rgb="FFFFFF00"/><bgColor indexed="64"/></patternFill></fill>
    <fill><patternFill patternType="solid"><fgColor rgb="FFD9D9D9"/><bgColor indexed="64"/></patternFill></fill>
    <fill><patternFill patternType="solid"><fgColor rgb="FFFF7C80"/><bgColor indexed="64"/></patternFill></fill>
    </fills>
    <borders count="2">
    <border><left/><right/><top/><bottom/><diagonal/></border>
    <border><left style="thin"><color rgb="FF000000"/></left><right style="thin"><color rgb="FF000000"/></right><top style="thin"><color rgb="FF000000"/></top><bottom style="thin"><color rgb="FF000000"/></bottom><diagonal/></border>
    </borders>
    <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
    <cellXfs count="6">
    <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
    <xf numFmtId="0" fontId="1" fillId="2" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center" readingOrder="2"/></xf>
    <xf numFmtId="0" fontId="2" fillId="3" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center" readingOrder="2"/></xf>
    <xf numFmtId="0" fontId="2" fillId="0" borderId="1" xfId="0" applyFont="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf>
    <xf numFmtId="0" fontId="2" fillId="0" borderId="1" xfId="0" applyFont="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center" readingOrder="2"/></xf>
    <xf numFmtId="0" fontId="2" fillId="4" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf>
    </cellXfs>
    <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
    </styleSheet>
    """

    // MARK: - PDF (A4 بالطول: المشتركون ثم غير المشتركين من صفحة جديدة)

    static func writePDF(_ lists: Lists, month: String, to url: URL) throws {
        let page = CGRect(x: 0, y: 0, width: 595, height: 842)
        let margin: CGFloat = 32
        let width = page.width - margin * 2
        let yellow = UIColor(red: 1, green: 1, blue: 0, alpha: 1)
        let gray = UIColor(white: 0.85, alpha: 1)
        let rowH: CGFloat = 20

        func draw(_ text: String, _ rect: CGRect, size: CGFloat = 10.5, bold: Bool = true) {
            let font = bold ? ReportFont.bold(size) : ReportFont.regular(size)
            PDFText.draw(text, in: rect.insetBy(dx: 3, dy: 0), font: font)
        }

        let renderer = UIGraphicsPDFRenderer(bounds: page)
        try renderer.writePDF(to: url) { context in
            let cg = context.cgContext
            cg.setLineWidth(0.6)
            UIColor.black.setStroke()
            for others in [false, true] {
                let rows = others ? lists.others : lists.subscribers
                let headers = others ? ["ت", "الرتبة", "الاسم", "المبلغ"] : ["ت", "الرتبة", "الاسم", "المبلغ", "عدد افراد الاسرة"]
                let ratios: [CGFloat] = others ? [7, 13, 42, 14] : [7, 13, 42, 14, 17]
                let widths = ratios.map { $0 / ratios.reduce(0, +) * width }
                var xs: [CGFloat] = []
                var x = page.width - margin
                for w in widths { x -= w; xs.append(x) }
                let perPage = Int((page.height - margin * 2 - 34 - rowH - 16) / rowH)
                let pages = max(1, Int(ceil(Double(rows.count) / Double(perPage))))
                for p in 0..<pages { autoreleasepool {
                    context.beginPage()
                    let titleRect = CGRect(x: margin, y: margin, width: width, height: 30)
                    yellow.setFill(); cg.fill(titleRect); cg.stroke(titleRect)
                    draw(title(month: month, others: others), titleRect, size: 13)
                    var y = titleRect.maxY + 4
                    for (c, h) in headers.enumerated() {
                        let r = CGRect(x: xs[c], y: y, width: widths[c], height: rowH)
                        gray.setFill(); cg.fill(r); cg.stroke(r)
                        draw(h, r)
                    }
                    y += rowH
                    let start = p * perPage
                    for i in start..<min(rows.count, start + perPage) {
                        let row = rows[i]
                        let values = [String(i + 1), row.rank, row.name, Money.plain(row.amount)] + (others ? [] : [familyText(row.amount)])
                        for (c, v) in values.enumerated() {
                            let r = CGRect(x: xs[c], y: y, width: widths[c], height: rowH)
                            if c == 4 && !isExact(row.amount) {
                                UIColor(red: 1, green: 0.49, blue: 0.5, alpha: 1).setFill(); cg.fill(r)
                            }
                            cg.stroke(r)
                            draw(v, r)
                        }
                        y += rowH
                    }
                } }   // autoreleasepool لكل صفحة
            }
        }
    }

    static func files(_ lists: Lists, month: String) async throws -> (excel: URL, pdf: URL) {
        try await PDFWork.run {
            let folder = FileManager.default.temporaryDirectory.appendingPathComponent("report-export", isDirectory: true)
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            let name = fileName(month: month)
            let excel = folder.appendingPathComponent(name + ".xlsx")
            let pdf = folder.appendingPathComponent(name + ".pdf")
            try writeExcel(lists, month: month, to: excel)
            try writePDF(lists, month: month, to: pdf)
            try PDFDecor.apply(to: pdf)
            return (excel, pdf)
        }
    }
}

extension Money {
    /// رقم بلا فواصل (كما في كشف الرعاية الصحية).
    nonisolated static func plain(_ value: Double) -> String {
        value == value.rounded() ? String(Int64(value)) : String(value)
    }
}

extension ReportEngine {
    /// المشتركون (لهم مبلغ في البند) وغير المشتركين (لهم راتب ولا مبلغ)، مرتبين حسب الرتبة ثم ترتيب الملف.
    func healthLists(month: String, title: String) throws -> HealthCareReport.Lists {
        var e = FetchDescriptor<Employee>(predicate: #Predicate { $0.month == month })
        e.propertiesToFetch = [\.name, \.rank, \.workplace, \.itemsData, \.netSalary]
        var subscribers: [ReportRow] = []
        var others: [ReportRow] = []
        for (i, emp) in try modelContext.fetch(e).enumerated() {
            if let amount = PayrollItems.decode(emp.itemsData)[title], amount != 0 {
                subscribers.append(ReportRow(id: i, name: emp.name, rank: emp.rank, workplace: emp.workplace, amount: amount, note: ""))
            } else if (emp.netSalary ?? 0) > 0, !HealthCareReport.notCovered(rank: emp.rank) {
                others.append(ReportRow(id: i, name: emp.name, rank: emp.rank, workplace: emp.workplace, amount: 0, note: ""))
            }
        }
        let order: (ReportRow, ReportRow) -> Bool = { a, b in
            let ra = RankOrder.rank(a.rank), rb = RankOrder.rank(b.rank)
            return ra != rb ? ra < rb : a.id < b.id
        }
        return HealthCareReport.Lists(subscribers: subscribers.sorted(by: order), others: others.sorted(by: order))
    }
}

/// ملخّص الرعاية الصحية داخل صفحة البند.
struct HealthCareSection: View {
    let lists: HealthCareReport.Lists

    var body: some View {
        let families = Dictionary(grouping: lists.subscribers, by: { HealthCareReport.family($0.amount) }).mapValues(\.count)
        let irregular = lists.irregular
        if !irregular.isEmpty {
            Section {
                ForEach(irregular) { row in
                    LabeledContent {
                        Text("\(Money.format(row.amount)) ÷ \(Money.format(HealthCareReport.perMember)) = \(Num.digits(HealthCareReport.familyText(row.amount)))")
                            .monospacedDigit()
                            .foregroundStyle(.red)
                    } label: {
                        VStack(alignment: .leading, spacing: 1) {
                            Text(row.name).font(.subheadline.weight(.semibold))
                            if !row.rank.isEmpty { Text(row.rank).font(.caption).foregroundStyle(.secondary) }
                        }
                    }
                }
            } header: {
                Label(tr("مبالغ لا تقبل القسمة على \(Num.format(3500)) (\(Num.format(irregular.count)))", "Amounts not divisible by 3,500 (\(irregular.count))"),
                      systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(.red)
            } footer: {
                Text(tr("الكشف لا يقبل الكسور: راجع هذه المبالغ في ملف الرواتب. تظهر في الملف المصدّر بخانة حمراء.",
                        "The list doesn't accept fractions: check these amounts in the payroll file. They are marked red in the export."))
            }
        }
        Section {
            LabeledContent(tr("المشتركون", "Subscribers")) {
                Text(Num.format(lists.subscribers.count)).monospacedDigit().bold()
            }
            LabeledContent(tr("غير المشتركين (لهم رواتب)", "Not subscribed (paid)")) {
                Text(Num.format(lists.others.count)).monospacedDigit().foregroundStyle(.red)
            }
            ForEach(families.keys.sorted(), id: \.self) { n in
                LabeledContent(tr("عائلة من \(Num.format(n)) (\(Money.format(Double(n) * HealthCareReport.perMember)))",
                                  "Family of \(n) (\(Money.format(Double(n) * HealthCareReport.perMember)))")) {
                    Text(Num.format(families[n] ?? 0)).monospacedDigit()
                }
                .font(.subheadline)
            }
        } header: {
            Text(tr("الرعاية الصحية: \(Num.format(3500)) لكل فرد", "Health care: 3,500 per member"))
        }
    }
}
