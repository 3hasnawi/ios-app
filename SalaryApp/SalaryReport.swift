import UIKit

/// تقرير راتب موظف لشهر واحد بشكل تقرير "برنامج الرواتب الالكتروني الموحد".
nonisolated struct SalaryReport: Sendable {
    struct Line: Sendable {
        let label: String
        let value: Double
        /// عدد (مثل عدد العلاوات) يُكتب بلا فواصل.
        var isCount = false
    }

    struct Bank: Sendable {
        let bank: String
        let branch: String
        let amount: Double
    }

    let month: String
    let name: String
    let rank: String
    let agency: String
    let directorate: String
    let section: String
    let entitlements: [Line]
    let entitlementTotal: Double?
    let deductions: [Line]
    let deductionTotal: Double?
    let net: Double?
    let info: [(String, String)]
    let banks: [Bank]
    let notes: String
    /// الشهر مستورد قبل حفظ المخصصات وبيانات الموظف: يحتاج إعادة استيراد ملف الرواتب.
    let missingDetails: Bool

    /// مجموع المخصصات لا يساوي "الاستحقاق" في الملف، أو مجموع الاستقطاعات لا يساوي "مجموع الاستقطاعات".
    var mismatches: [String] {
        var out: [String] = []
        if let total = entitlementTotal {
            let sum = entitlements.filter { !$0.isCount }.reduce(0) { $0 + $1.value }
            if abs(sum - total) > 0.5 { out.append(tr("مجموع المخصصات (\(Money.format(sum))) لا يساوي الاستحقاق الكلي (\(Money.format(total)))",
                                                       "Allowances (\(Money.format(sum))) ≠ total entitlement (\(Money.format(total)))")) }
        }
        if let total = deductionTotal {
            let sum = deductions.reduce(0) { $0 + $1.value }
            if abs(sum - total) > 0.5 { out.append(tr("مجموع الاستقطاعات المفصّلة (\(Money.format(sum))) لا يساوي مجموع الاستقطاعات (\(Money.format(total)))",
                                                       "Itemised deductions (\(Money.format(sum))) ≠ total deductions (\(Money.format(total)))")) }
        }
        return out
    }

    // MARK: - البناء من بيانات الموظف

    struct Input: Sendable {
        let month: String
        let name: String
        let rank: String
        let workplace: String
        let net: Double?
        let items: [String: Double]
        let details: [String: String]
        /// استقطاعات ملف الاستقطاعات لهذا الموظف في هذا الشهر (الجهة، المبلغ).
        let bankDeductions: [(String, Double)]
    }

    static func make(_ input: Input, hideZeros: Bool, hidePersonal: Bool) -> SalaryReport {
        let d = input.details
        func text(_ key: String) -> String { (d[key] ?? "").trimmingCharacters(in: .whitespacesAndNewlines) }
        func number(_ key: String) -> Double { ArabicText.number(from: text(key)) ?? input.items[key] ?? 0 }
        func titled(_ base: String, _ nameKey: String) -> String {
            let extra = text(nameKey)
            return extra.isEmpty ? base : "\(base) \(extra)"
        }

        // المخصصات بترتيب التقرير الرسمي.
        let temporaryName = number("استقطاع مؤقت") == 0 ? text("اسم استقطاع المؤقته") : ""
        var entitlements: [Line] = [
            Line(label: "الراتب الاسمي", value: number("الاسمي")),
            Line(label: "عدد العلاوات", value: number("عدد العلاوات"), isCount: true),
            Line(label: "الخطورة", value: number("الخطورة")),
            Line(label: "الارزاق", value: number("الارزاق")),
            Line(label: "الاستثنائية", value: number("الاستثنائية")),
            Line(label: temporaryName.isEmpty ? "مخصصات مؤقتة" : temporaryName, value: number("مؤقتة")),
            Line(label: titled("الشهادة", text("اسم الشهدة").isEmpty ? "اسم الشهادة" : "اسم الشهدة"), value: number("الشهادة")),
            Line(label: "الاطفال", value: number("الاطفال")),
            Line(label: "الزوجية", value: number("الزوجية")),
            Line(label: titled("المنصب", "اسم المنصب"), value: number("منصب")),
            Line(label: titled("مخصصات الصنف", "اسم الصنف"), value: number("مخصصات صنف")),
            Line(label: "الجامعية", value: number("جامعية")),
            Line(label: "مخصصات النقل", value: number("مخصصات نقل")),
            Line(label: titled("اللقب العلمي", "اسم اللقب العلمي"), value: number("لقب علمي")),
            Line(label: text("اسم المخصصات الاخرى").isEmpty ? "مخصصات اخرى" : text("اسم المخصصات الاخرى"), value: number("مخصصات اخرى")),
            Line(label: "اضافية", value: number("اضافية")),
        ]

        // الاستقطاعات.
        var deductions: [Line] = [
            Line(label: "التوقيفات التقاعدية", value: number("توقيفات تقاعدية")),
            Line(label: "الضريبة", value: number("الضريبة")),
            Line(label: "الحماية الاجتماعية", value: number("حماية الاجتماعية")),
            Line(label: "الضمان الصحي", value: number("الرعاية الصحية")),
            Line(label: "النشاط الصحي", value: number("نشاط صحي")),
            Line(label: "صندوق الشهداء", value: number("صندوق الشهداء")),
            Line(label: "النشاط الرياضي", value: number("نشاط رياضي")),
            Line(label: "التامين الصحي", value: number("تامين صحي")),
            Line(label: "التضمين", value: number("تضمين")),
            Line(label: "الايراد النهائي", value: number("ايراد نهائي")),
            Line(label: "رسم الطابع", value: number("رسم طابع")),
            Line(label: text("اسم الاستقطاع"), value: number("مبلغ الاستقطاع")),
            Line(label: text("2اسم الاستقطاع"), value: number("2مبلغ الاستقطاع")),
            Line(label: number("استقطاع مؤقت") == 0 ? "" : text("اسم استقطاع المؤقته"), value: number("استقطاع مؤقت")),
            Line(label: "استقطاع نسبة", value: number("% استقطاع 3")),
            Line(label: "استقطاعات المصارف", value: number("مجموع استقطاعات المصارف")),
        ]

        let missing = d.isEmpty
        if missing {
            // شهر مستورد قبل هذا الإصدار: الاستقطاعات المحفوظة فقط.
            entitlements = []
            deductions = input.items
                .filter { !$0.key.contains("مجموع") }
                .sorted { $0.key < $1.key }
                .map { Line(label: $0.key, value: $0.value) }
        }
        if hideZeros {
            entitlements.removeAll { $0.value == 0 }
            deductions.removeAll { $0.value == 0 }
        }

        func personal(_ value: String) -> String {
            guard hidePersonal, value.count > 4 else { return value }
            return "•••• " + value.suffix(4)
        }
        let info: [(String, String)] = [
            ("التحصيل الدراسي", text("اسم الشهدة").isEmpty ? text("اسم الشهادة") : text("اسم الشهدة")),
            ("اسم المنصب", text("اسم المنصب")),
            ("اسم الصنف", text("اسم الصنف")),
            ("اسم مصرف التوطين", text("توطين المصرف")),
            ("IBAN", personal(text("IBAN"))),
            ("الرقم الموحد", personal(text("الرقم الموحد"))),
            ("الرقم الوظيفي", personal(text("الرقم الوظيفي"))),
            ("رقم الضمان الصحي", personal(text("رقم الضمان الصحي"))),
        ]

        let banks = input.bankDeductions.map { title, amount -> Bank in
            let parts = title.split(separator: "/", maxSplits: 1).map { $0.trimmingCharacters(in: .whitespaces) }
            return Bank(bank: parts.first ?? title, branch: parts.count > 1 ? parts[1] : "", amount: amount)
        }

        let total = ArabicText.number(from: text("الاستحقاق"))
        let deductionTotal = ArabicText.number(from: text("مجموع الاستقطاعات")) ?? input.items["مجموع الاستقطاعات"]
        return SalaryReport(
            month: input.month,
            name: input.name,
            rank: input.rank,
            agency: text("اسم الوكالة"),
            directorate: text("اسم المديرية"),
            section: text("NO_markaz").isEmpty ? input.workplace : text("NO_markaz"),
            entitlements: entitlements,
            entitlementTotal: total,
            deductions: deductions,
            deductionTotal: deductionTotal,
            net: input.net,
            info: info,
            banks: banks,
            notes: [text("ملاحظات عامه"), text("ملاحظات")].filter { !$0.isEmpty }.joined(separator: " — "),
            missingDetails: missing
        )
    }

    static func fileName(name: String, months: [String]) -> String {
        let suffix = months.count == 1 ? months[0] : "\(months.count) أشهر"
        return "تقرير راتب - \(name) - \(suffix)"
            .replacingOccurrences(of: "/", with: "-")
            .replacingOccurrences(of: ":", with: "-")
    }
}

// MARK: - PDF

nonisolated enum SalaryReportPDF {
    /// صفحة A4 لكل شهر، ثم العلامة المائية والتذييل.
    static func write(_ reports: [SalaryReport], to url: URL) throws {
        let page = CGRect(x: 0, y: 0, width: 595, height: 842)
        let renderer = UIGraphicsPDFRenderer(bounds: page)
        let logo = UIImage(named: "HomeLogo")
        try renderer.writePDF(to: url) { context in
            for report in reports {
                context.beginPage()
                draw(report, page: page, logo: logo, cg: context.cgContext)
            }
        }
        try PDFDecor.apply(to: url)
    }

    private static let line = UIColor(red: 0.55, green: 0.6, blue: 0.68, alpha: 1)
    private static let head = UIColor(red: 0.86, green: 0.9, blue: 0.96, alpha: 1)
    private static let soft = UIColor(red: 0.93, green: 0.95, blue: 0.98, alpha: 1)

    static func digits(_ text: String) -> String { ReportSheet.easternDigits(text) }

    static func money(_ value: Double) -> String {
        let f = NumberFormatter()
        f.numberStyle = .decimal
        f.locale = Locale(identifier: "en_US")
        f.maximumFractionDigits = 2
        return digits(f.string(from: NSNumber(value: value)) ?? String(value)).replacingOccurrences(of: ",", with: "٬")
    }

    private static func text(_ string: String, _ rect: CGRect, size: CGFloat, bold: Bool = false, align: NSTextAlignment = .right, color: UIColor = .black) {
        let style = NSMutableParagraphStyle()
        style.alignment = align
        style.baseWritingDirection = .rightToLeft
        style.lineBreakMode = .byTruncatingTail
        let attributed = NSAttributedString(string: string, attributes: [
            .font: bold ? ReportFont.bold(size) : ReportFont.regular(size),
            .foregroundColor: color, .paragraphStyle: style,
        ])
        let h = min(attributed.boundingRect(with: CGSize(width: rect.width, height: 200), options: [.usesLineFragmentOrigin], context: nil).height, rect.height)
        attributed.draw(with: CGRect(x: rect.minX, y: rect.midY - h / 2, width: rect.width, height: h),
                        options: [.usesLineFragmentOrigin, .truncatesLastVisibleLine], context: nil)
    }

    private static func draw(_ r: SalaryReport, page: CGRect, logo: UIImage?, cg: CGContext) {
        let m: CGFloat = 30
        let width = page.width - m * 2
        let right = page.width - m

        // الرأس: الجهة يميناً، الشعار في الوسط، الشهر والسنة يساراً.
        text("جمهورية العراق", CGRect(x: right - 170, y: 26, width: 170, height: 18), size: 12, align: .center)
        text("وزارة الداخلية", CGRect(x: right - 170, y: 46, width: 170, height: 18), size: 12, align: .center)
        text(ReportSheet.defaultHeader, CGRect(x: right - 170, y: 66, width: 170, height: 18), size: 10.5, bold: true, align: .center)
        if let logo {
            let side: CGFloat = 86
            logo.draw(in: CGRect(x: page.midX - side / 2, y: 18, width: side, height: side * logo.size.height / max(logo.size.width, 1)))
        }
        let parts = r.month.split(separator: " ")
        let monthName = parts.dropLast().joined(separator: " ")
        let year = parts.last.map(String.init) ?? ""
        text("الشهر : \(monthName.isEmpty ? r.month : monthName)", CGRect(x: m, y: 70, width: 150, height: 16), size: 10)
        text("السنة : \(digits(year))", CGRect(x: m, y: 88, width: 150, height: 16), size: 10)
        text("برنامج الرواتب الالكتروني الموحد", CGRect(x: m, y: 110, width: width, height: 18), size: 12.5, align: .center)
        text("تقرير راتب", CGRect(x: m, y: 128, width: width, height: 20), size: 14, bold: true, align: .center)

        // أسطر البيانات بخط منقّط.
        var y: CGFloat = 156
        func dataLine(_ pairs: [(String, String)]) {
            var x = right
            for (label, value) in pairs {
                let labelWidth = CGFloat(label.count) * 6.2 + 8
                text(label, CGRect(x: x - labelWidth, y: y, width: labelWidth, height: 18), size: 11)
                x -= labelWidth + 4
                let valueWidth = min(max(CGFloat(value.count) * 6.6 + 12, 90), x - m)
                text(value, CGRect(x: x - valueWidth, y: y, width: valueWidth, height: 18), size: 11, bold: true)
                x -= valueWidth + 14
            }
            cg.saveGState()
            cg.setStrokeColor(UIColor(white: 0.55, alpha: 1).cgColor)
            cg.setLineWidth(0.6)
            cg.setLineDash(phase: 0, lengths: [2, 2])
            cg.move(to: CGPoint(x: m, y: y + 20)); cg.addLine(to: CGPoint(x: right, y: y + 20)); cg.strokePath()
            cg.restoreGState()
            y += 26
        }
        dataLine([("ندرج ادناه تفاصيل راتب", r.rank.isEmpty ? "—" : r.rank), ("/", r.name)])
        dataLine([("منسوب", r.agency.isEmpty ? "—" : r.agency), ("مديرية", r.directorate.isEmpty ? "—" : r.directorate)])
        dataLine([("قسم", r.section.isEmpty ? "—" : r.section)])

        // جدولا المخصصات والاستقطاعات.
        y += 4
        let gap: CGFloat = 16
        let half = (width - gap) / 2
        let rowH: CGFloat = 15.5
        let rows = max(r.entitlements.count, r.deductions.count, r.missingDetails ? 4 : 1)
        func table(x: CGFloat, title: String, header: String, lines: [SalaryReport.Line], totalLabel: String, total: Double?, note: String?) {
            text(title, CGRect(x: x, y: y, width: half, height: 16), size: 11.5, bold: true, align: .center)
            var ty = y + 18
            let labelW = half * 0.6
            func cells(_ label: String, _ value: String, fill: UIColor?, bold: Bool) {
                let lr = CGRect(x: x + half - labelW, y: ty, width: labelW, height: rowH)
                let vr = CGRect(x: x, y: ty, width: half - labelW, height: rowH)
                if let fill { fill.setFill(); cg.fill(lr); cg.fill(vr) }
                line.setStroke(); cg.stroke(lr, width: 0.5); cg.stroke(vr, width: 0.5)
                text(label, lr.insetBy(dx: 4, dy: 0), size: 9.5, bold: true)
                text(value, vr.insetBy(dx: 3, dy: 0), size: 9.5, bold: bold, align: .center)
                ty += rowH
            }
            cells(header, "المبلغ", fill: head, bold: true)
            for i in 0..<rows {
                if i < lines.count {
                    let l = lines[i]
                    cells(l.label, l.isCount ? digits(String(Int(l.value))) : money(l.value), fill: nil, bold: false)
                } else {
                    cells("", "", fill: nil, bold: false)
                }
            }
            cells(totalLabel, total.map(money) ?? "—", fill: soft, bold: true)
            if let note {
                text(note, CGRect(x: x, y: y + 18 + rowH + 4, width: half, height: rowH * 3), size: 9, align: .center, color: .darkGray)
            }
        }
        table(x: right - half, title: "الراتب المستحق", header: "اسم المخصصات", lines: r.entitlements,
              totalLabel: "الاستحقاق الكلي", total: r.entitlementTotal,
              note: r.missingDetails ? "أعد استيراد ملف رواتب هذا الشهر لإظهار المخصصات" : nil)
        table(x: m, title: "الاستقطاعات", header: "اسم الاستقطاع", lines: r.deductions,
              totalLabel: "مجموع الاستقطاعات", total: r.deductionTotal, note: nil)
        y += 18 + rowH * CGFloat(rows + 2) + 10

        // صافي الراتب.
        let netRect = CGRect(x: m, y: y, width: width, height: 26)
        soft.setFill(); cg.fill(netRect)
        UIColor(white: 0.2, alpha: 1).setStroke(); cg.stroke(netRect, width: 1.2)
        text("صافي الراتب", CGRect(x: page.midX, y: y, width: width / 2 - 20, height: 26), size: 13, bold: true, align: .center)
        text(r.net.map(money) ?? "—", CGRect(x: m + 20, y: y, width: width / 2 - 20, height: 26), size: 13, bold: true, align: .center)
        y += 38

        // بيانات الموظف (يميناً) واستقطاعات المصارف (يساراً).
        let infoX = right - half
        text("بيانات الموظف", CGRect(x: infoX, y: y, width: half, height: 16), size: 11.5, bold: true, align: .center)
        text("استقطاعات المصارف", CGRect(x: m, y: y, width: half, height: 16), size: 11.5, bold: true, align: .center)
        var iy = y + 18
        for (label, value) in r.info {
            let lr = CGRect(x: infoX + half * 0.55, y: iy, width: half * 0.45, height: rowH + 1)
            let vr = CGRect(x: infoX, y: iy, width: half * 0.55, height: rowH + 1)
            line.setStroke(); cg.stroke(lr, width: 0.5); cg.stroke(vr, width: 0.5)
            text(label, lr.insetBy(dx: 4, dy: 0), size: 9.5, bold: true)
            text(label == "IBAN" ? value : digits(value), vr.insetBy(dx: 3, dy: 0), size: 9, align: .center)
            iy += rowH + 1
        }

        var by = y + 18
        let bw = [half * 0.42, half * 0.33, half * 0.25]   // المصرف، الفرع، المبلغ (من اليمين)
        func bankRow(_ values: [String], fill: UIColor?, bold: Bool) {
            var x = m + half
            for (i, v) in values.enumerated() {
                x -= bw[i]
                let rect = CGRect(x: x, y: by, width: bw[i], height: rowH + 1)
                if let fill { fill.setFill(); cg.fill(rect) }
                line.setStroke(); cg.stroke(rect, width: 0.5)
                text(v, rect.insetBy(dx: 3, dy: 0), size: 9, bold: bold || i == 0, align: i == 0 ? .right : .center)
            }
            by += rowH + 1
        }
        bankRow(["المصرف", "الفرع", "المبلغ"], fill: head, bold: true)
        if r.banks.isEmpty {
            bankRow(["لا توجد", "", ""], fill: nil, bold: false)
        } else {
            for b in r.banks { bankRow([b.bank, b.branch, money(b.amount)], fill: nil, bold: false) }
            if r.banks.count > 1 { bankRow(["المجموع", "", money(r.banks.reduce(0) { $0 + $1.amount })], fill: soft, bold: true) }
        }

        // الملاحظات.
        let ny = max(iy, by) + 10
        let notesRect = CGRect(x: m, y: ny, width: width, height: 34)
        line.setStroke(); cg.stroke(notesRect, width: 0.5)
        text("الملاحظات", CGRect(x: right - 70, y: ny, width: 64, height: 34), size: 10, bold: true)
        if !r.notes.isEmpty { text(r.notes, CGRect(x: m + 6, y: ny, width: width - 84, height: 34), size: 9.5) }
    }
}

extension SalaryReportPDF {
    /// صورة الصفحة الأولى للمعاينة داخل نافذة المشاركة.
    static func preview(_ url: URL) -> UIImage? {
        guard let doc = CGPDFDocument(url as CFURL), let page = doc.page(at: 1) else { return nil }
        let box = page.getBoxRect(.mediaBox)
        let scale: CGFloat = 1.6
        let format = UIGraphicsImageRendererFormat()
        format.scale = 1
        return UIGraphicsImageRenderer(size: CGSize(width: box.width * scale, height: box.height * scale), format: format).image { ctx in
            UIColor.white.setFill()
            ctx.fill(CGRect(origin: .zero, size: CGSize(width: box.width * scale, height: box.height * scale)))
            let cg = ctx.cgContext
            cg.translateBy(x: 0, y: box.height * scale)
            cg.scaleBy(x: scale, y: -scale)
            cg.drawPDFPage(page)
        }
    }
}
