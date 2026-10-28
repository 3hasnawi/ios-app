import Foundation
import UIKit
import CoreText

/// نسخة قيم ثابتة؛ لا تُمرَّر نماذج SwiftData إلى خيط تصدير PDF.
nonisolated struct StaffReportData: Sendable {
    struct Field: Sendable { let label: String; let value: String }
    struct Section: Sendable { let title: String; let fields: [Field]; var wide = false }
    let name: String
    let status: String
    let subtitle: String
    let sections: [Section]

    @MainActor init(_ member: StaffMember) {
        name = member.name
        status = member.status
        subtitle = [member.rank, member.workplace].filter { !$0.isEmpty }.joined(separator: " · ")
        let source = member.sections
        func value(_ section: String, _ names: [String]) -> String? {
            let keys = Set(names.map(ArabicText.normalize))
            return source.filter { $0.title == section }.flatMap(\.fields)
                .first { keys.contains(ArabicText.normalize($0.label)) }?.value
        }
        func anyValue(_ names: [String]) -> String? {
            let keys = Set(names.map(ArabicText.normalize))
            return source.flatMap(\.fields).first { keys.contains(ArabicText.normalize($0.label)) }?.value
        }
        func field(_ label: String, _ text: String?) -> Field? {
            guard let text = text?.trimmingCharacters(in: .whitespacesAndNewlines), !text.isEmpty else { return nil }
            return Field(label: label, value: text)
        }
        func group(_ title: String, _ fields: [Field?], wide: Bool = false) -> Section? {
            let present = fields.compactMap { $0 }
            return present.isEmpty ? nil : Section(title: title, fields: present, wide: wide)
        }
        let personal = group(tr("المعلومات الشخصية", "Personal information"), [
            field(tr("اسم الأم", "Mother's name"), value("المعلومات الشخصية", ["اسم الأم", "اسم الام الكامل", "اسم الام"]) ?? anyValue(["اسم الأم", "اسم الام الكامل", "اسم الام", "اسم الوالدة"])),
            field(tr("التولد", "Date of birth"), value("المعلومات الشخصية", ["التولد"])),
            field(tr("الحالة الزوجية", "Marital status"), value("المعلومات الشخصية", ["الحالة الزوجية"])),
            field(tr("اسم الزوجة / الزوج", "Spouse name"), value("المعلومات الشخصية", ["اسم الزوجة/ الزوج", "اسم الزوجة / الزوج"])),
            field(tr("التحصيل الدراسي", "Education"), value("التحصيل الدراسي", ["التحصيل الدراسي"])),
            field(tr("الحالة الوظيفية", "Employment status"), member.status)
        ])
        let work = group(tr("المعلومات الوظيفية ومكان العمل", "Employment and workplace"), [
            field(tr("الرتبة", "Rank"), member.rank),
            field(tr("المنصب الإداري", "Administrative position"), value("الوظيفية", ["المنصب الاداري"])),
            field(tr("المنصب", "Position"), value("الوظيفية", ["المنصب"])),
            field(tr("العنوان الوظيفي", "Job title"), value("الوظيفية", ["العنوان الوظيفي"])),
            field(tr("محل العمل", "Workplace"), member.workplace),
            field(tr("الرقم الإحصائي", "Statistical number"), member.statNumber),
            field(tr("الرقم الموحد", "Unified number"), member.unifiedNumber),
            field(tr("رقم أمر المنصب", "Position order number"), value("الوظيفية", ["رقم الامر الوزاري لاسناد المنصب"])),
            field(tr("تاريخ أمر المنصب", "Position order date"), value("الوظيفية", ["تاريخ امر المنصب"])),
            field(tr("المباشرة بالمنصب", "Position start date"), value("الوظيفية", ["تاريخ المباشرة بالمنصب"]))
        ])
        let appointment = group(tr("التعيين والمباشرة", "Appointment and service"), [
            field(tr("رقم أمر التعيين", "Appointment order number"), value("التعيين والمباشرة", ["رقم امر التعيين", "رقم الامر"])),
            field(tr("تاريخ أمر التعيين", "Appointment order date"), value("التعيين والمباشرة", ["تاريخ التعيين", "تاريخ الامر"])),
            field(tr("أول مباشرة بالخدمة", "First service date"), value("التعيين والمباشرة", ["تاريخ مباشرة بالخدمة لاول مرة"])),
            field(tr("جهة التعيين", "Appointing authority"), value("الوظيفية", ["جهة التعيين"])),
            field(tr("تاريخ مباشرة الإعادة", "Return to service date"), value("التعيين والمباشرة", ["تاريخ مباشرة بالاعادة"])),
            field(tr("رقم أمر الإعادة", "Return order number"), value("التعيين والمباشرة", ["رقم امر الاعادة للخدمة"])),
            field(tr("تاريخ أمر الإعادة", "Return order date"), value("التعيين والمباشرة", ["تاريخ امر الاعادة للخدمة"]))
        ])
        let promotion = group(tr("الترقية", "Promotion"), [
            field(tr("شهر الترقية", "Promotion month"), value("الترقية", ["شهر الترقية", "الشهر"])),
            field(tr("سنة الترقية", "Promotion year"), value("الترقية", ["سنة الترقية"])),
            field(tr("رقم أمر الترقية", "Promotion order number"), value("الترقية", ["رقم امر الترقية"])),
            field(tr("تاريخ الترقية", "Promotion date"), value("الترقية", ["تاريخ الترقية"]))
        ])
        let record = group(tr("السجل والملاحظات", "Record and notes"), [
            field(tr("تاريخ تحديث السجل", "Record updated"), value("السجل", ["تاريخ التحديث"])),
            field(tr("الملاحظات", "Notes"), value("الملاحظات", ["الملاحظات"]))
        ], wide: true)
        sections = [personal, work, appointment, promotion, record].compactMap { $0 }
    }
}

nonisolated enum StaffReportError: LocalizedError {
    case tooLong
    var errorDescription: String? {
        tr("البيانات طويلة ولا تتسع في صفحة واحدة بخط مقروء. لم يُنشأ تقرير ناقص.",
           "The data does not fit on one readable page. No incomplete report was created.")
    }
}

nonisolated enum StaffReport {
    private static let page = CGRect(x: 0, y: 0, width: 595, height: 842)
    private static let margin: CGFloat = 28
    private static let gap: CGFloat = 4
    private static let sectionHeight: CGFloat = 22
    private static let top: CGFloat = 170
    private static let bottom: CGFloat = 42
    private static let textPad: CGFloat = 5

    private static func lineCount(_ text: String, font: UIFont, width: CGFloat) -> Int {
        let attributed = NSAttributedString(string: text, attributes: [.font: font])
        let typesetter = CTTypesetterCreateWithAttributedString(attributed)
        var start = 0, count = 0
        while start < attributed.length {
            let n = CTTypesetterSuggestLineBreak(typesetter, start, Double(max(1, width)))
            start += max(1, n)
            count += 1
        }
        return max(1, count)
    }
    private static func height(_ field: StaffReportData.Field, font: UIFont, width: CGFloat) -> CGFloat {
        let labelWidth = width * 0.38
        let label = lineCount(field.label, font: font, width: labelWidth - 2 * textPad)
        let valueFont = ReportFont.bold(font.pointSize)
        let value = lineCount(field.value, font: valueFont, width: width - labelWidth - 2 * textPad)
        return max(20, max(CGFloat(label) * font.lineHeight, CGFloat(value) * valueFont.lineHeight) + 2 * textPad)
    }
    private static func rows(_ section: StaffReportData.Section) -> [[StaffReportData.Field]] {
        if section.wide { return section.fields.map { [$0] } }
        return stride(from: 0, to: section.fields.count, by: 2).map { Array(section.fields[$0..<min($0 + 2, section.fields.count)]) }
    }
    private static func requiredHeight(_ data: StaffReportData, font: UIFont) -> CGFloat {
        let width = page.width - 2 * margin
        return data.sections.reduce(0) { total, section in
            let cellWidth = section.wide ? width : width / 2
            let content = rows(section).reduce(CGFloat.zero) { sum, row in
                sum + (row.map { height($0, font: font, width: cellWidth) }.max() ?? 0)
            }
            return total + sectionHeight + content + gap
        }
    }

    static func make(_ data: StaffReportData, logo: Data?) async throws -> URL {
        try await PDFWork.run {
            let available = page.height - top - bottom
            let sizes: [CGFloat] = [10.5, 10, 9.5, 9, 8.5, 8, 7.5, 7, 6.5]
            func headerSize(_ text: String, width: CGFloat, height: CGFloat) -> CGFloat? {
                ([12, 11, 10, 9, 8, 7, 6.5] as [CGFloat]).first {
                    let font = ReportFont.bold($0)
                    return CGFloat(lineCount(text, font: font, width: width - 2 * textPad)) * font.lineHeight <= height
                }
            }
            let nameWidth = page.width - 2 * margin - 132
            guard let size = sizes.first(where: { requiredHeight(data, font: ReportFont.regular($0)) <= available }),
                  let nameSize = headerSize(data.name, width: nameWidth, height: 22),
                  let subtitleSize = headerSize(data.subtitle, width: nameWidth, height: 16),
                  let statusSize = headerSize(data.status, width: 120, height: 28) else { throw StaffReportError.tooLong }
            let folder = FileManager.default.temporaryDirectory.appendingPathComponent("staff-report-\(UUID().uuidString)", isDirectory: true)
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            let file = folder.appendingPathComponent("تقرير الملاك.pdf")
            let format = UIGraphicsPDFRendererFormat()
            do {
                try UIGraphicsPDFRenderer(bounds: page, format: format).writePDF(to: file) { context in
                    context.beginPage()
                    draw(data, logo: logo, fontSize: size, nameSize: nameSize, subtitleSize: subtitleSize, statusSize: statusSize)
                }
                return file
            } catch {
                try? FileManager.default.removeItem(at: folder)
                throw error
            }
        }
    }

    private static func draw(_ data: StaffReportData, logo: Data?, fontSize: CGFloat, nameSize: CGFloat, subtitleSize: CGFloat, statusSize: CGFloat) {
        guard let cg = UIGraphicsGetCurrentContext() else { return }
        let width = page.width - 2 * margin
        let font = ReportFont.regular(fontSize)
        let bold = ReportFont.bold(fontSize)
        func text(_ value: String, _ rect: CGRect, _ font: UIFont, align: NSTextAlignment = .right) {
            PDFText.draw(value, in: rect.insetBy(dx: textPad, dy: 1), font: font, align: align, wrap: true)
        }
        if let logo, let image = UIImage(data: logo) { image.draw(in: CGRect(x: page.midX - 22, y: 24, width: 44, height: 44)) }
        text(tr("وزارة الداخلية\nقيادة شرطة محافظة الأنبار", "Ministry of Interior\nAnbar Police Command"), CGRect(x: 335, y: 24, width: 230, height: 48), ReportFont.bold(10))
        let date = Date().formatted(.dateTime.year().month(.twoDigits).day(.twoDigits).locale(Locale(identifier: "en_US_POSIX")))
        text(tr("تقرير الملاك\nالتاريخ: \(date)", "Staff report\nDate: \(date)"), CGRect(x: margin, y: 24, width: 165, height: 44), ReportFont.regular(9), align: .left)
        cg.setStrokeColor(UIColor.darkGray.cgColor); cg.setLineWidth(0.7)
        cg.move(to: CGPoint(x: margin, y: 78)); cg.addLine(to: CGPoint(x: page.width - margin, y: 78)); cg.strokePath()
        text(tr("بيان معلومات المنتسب", "Employee information statement"), CGRect(x: margin, y: 83, width: width, height: 25), ReportFont.bold(15), align: .center)
        cg.stroke(CGRect(x: margin, y: 112, width: width, height: 48))
        let nameWidth = width - 132
        text(data.name, CGRect(x: margin + 128, y: 113, width: nameWidth, height: 24), ReportFont.bold(nameSize))
        text(data.subtitle, CGRect(x: margin + 128, y: 138, width: nameWidth, height: 16), ReportFont.regular(subtitleSize))
        text(data.status, CGRect(x: margin + 4, y: 120, width: 120, height: 30), ReportFont.regular(statusSize), align: .center)
        var y = top
        for section in data.sections {
            let titleRect = CGRect(x: margin, y: y, width: width, height: sectionHeight)
            cg.setFillColor(UIColor(white: 0.94, alpha: 1).cgColor); cg.fill(titleRect)
            text(section.title, titleRect, bold)
            y += sectionHeight
            let cellWidth = section.wide ? width : width / 2
            for row in rows(section) {
                let h = row.map { height($0, font: font, width: cellWidth) }.max() ?? 20
                for (index, field) in row.enumerated() {
                    let x = margin + width - CGFloat(index + 1) * cellWidth
                    let labelWidth = cellWidth * 0.38
                    let labelRect = CGRect(x: x + cellWidth - labelWidth, y: y, width: labelWidth, height: h)
                    let valueRect = CGRect(x: x, y: y, width: cellWidth - labelWidth, height: h)
                    cg.setStrokeColor(UIColor(white: 0.78, alpha: 1).cgColor); cg.setLineWidth(0.4)
                    cg.stroke(labelRect); cg.stroke(valueRect)
                    text(field.label, labelRect, font); text(field.value, valueRect, bold)
                }
                y += h
            }
            y += gap
        }
        let footer = CGRect(x: margin, y: page.height - 28, width: width, height: 16)
        text(tr("تقرير الملاك · صفحة ١ من ١", "Staff report · page 1 of 1"), footer, ReportFont.regular(8), align: .center)
    }
}

#if targetEnvironment(simulator)
enum StaffReportSelfTest {
    @MainActor static func runIfRequested() async {
        guard UserDefaults.standard.bool(forKey: "staffReportTest") else { return }
        func member(notes: String) -> StaffMember {
            StaffMember(name: "أحمد علي الدليمي", mother: "مريم صالح", statNumber: "123456", unifiedNumber: "654321",
                        rank: "نقيب", status: "مستمر بالخدمة", workplace: "قسم النجدة", sections: [
                StaffSection(title: "المعلومات الشخصية", fields: [StaffField(label: "اسم الأم", value: "مريم صالح"),
                    StaffField(label: "الحالة الزوجية", value: "متزوج"), StaffField(label: "اسم الزوجة/ الزوج", value: "سارة أحمد")]),
                StaffSection(title: "التحصيل الدراسي", fields: [StaffField(label: "التحصيل الدراسي", value: "بكالوريوس")]),
                StaffSection(title: "الوظيفية", fields: [StaffField(label: "المنصب", value: "آمر قسم")]),
                StaffSection(title: "التعيين والمباشرة", fields: [StaffField(label: "رقم امر التعيين", value: "1234"),
                    StaffField(label: "تاريخ التعيين", value: "2010/06/01"), StaffField(label: "تاريخ مباشرة بالخدمة لاول مرة", value: "2010/06/15")]),
                StaffSection(title: "الترقية", fields: [StaffField(label: "شهر الترقية", value: "تموز"), StaffField(label: "سنة الترقية", value: "2024"),
                    StaffField(label: "رقم امر الترقية", value: "5678"), StaffField(label: "تاريخ الترقية", value: "2024/07/01")]),
                StaffSection(title: "السجل", fields: [StaffField(label: "تاريخ التحديث", value: "2026/10/01")]),
                StaffSection(title: "الملاحظات", fields: [StaffField(label: "الملاحظات", value: notes)])
            ])
        }
        do {
            let data = StaffReportData(member(notes: "ملاحظة تجريبية"))
            let values = Set(data.sections.flatMap(\.fields).map(\.value))
            guard Set(["مريم صالح", "سارة أحمد", "بكالوريوس", "آمر قسم", "1234", "2010/06/15", "5678", "2026/10/01", "ملاحظة تجريبية"]).isSubset(of: values) else { throw StaffReportError.tooLong }
            let pdf = try await StaffReport.make(data, logo: nil)
            defer { try? FileManager.default.removeItem(at: pdf.deletingLastPathComponent()) }
            guard let document = CGPDFDocument(pdf as CFURL), document.numberOfPages == 1,
                  let page = document.page(at: 1), abs(page.getBoxRect(.mediaBox).width - 595) < 1,
                  abs(page.getBoxRect(.mediaBox).height - 842) < 1 else { throw StaffReportError.tooLong }
            let longData = StaffReportData(member(notes: String(repeating: "بيانات طويلة ", count: 1500)))
            var rejected = false
            do { _ = try await StaffReport.make(longData, logo: nil) } catch StaffReportError.tooLong { rejected = true }
            guard rejected else { throw StaffReportError.tooLong }
            LaunchOptions.writeReport("staffReport=ok fields=ok a4=ok overflow=protected")
        } catch {
            LaunchOptions.writeReport("staffReport=failed \(error.localizedDescription)")
        }
    }
}
#endif
