import Foundation
import UIKit
import CoreText

/// مسودة للمراجعة فقط؛ لا تثبت عدد صفحات جدول Word ولا تصلح وحدها للاعتماد.
nonisolated enum TransferWordGroupDraft {
    struct LetterText: Sendable {
        let header: String
        let fixedNumber: String
        let committee: [String]
        let footer: String
        var bodyTemplate: String? = nil
        var signatureOffset: Int = 0
        var bodyMonth: String? = nil
        var outgoingMonth: String? = nil
        var template: CustomTransferTemplate? = nil
    }
    enum Failure: LocalizedError {
        case incomplete
        var errorDescription: String? { tr("تأكد من ترويسة الكتاب والعدد وأسماء اللجنة قبل إعداد المسودة.", "Check the letter header, reference number and committee names before preparing the draft.") }
    }
    static func letterText(from custom: TransferLetterCustomization, adaptivePDF: Bool = false) throws -> LetterText {
        if adaptivePDF, let template = custom.letterTemplate {
            try template.validate()
            _ = try CustomTransferTemplate.signatureCells([template.firstSigner, template.secondSigner, template.thirdSigner], date: " / ١ / ٢٠٢٦")
            var formatted = template
            formatted.header = CustomTransferTemplate.formatBodyNumbers(template.header)
            formatted.recipient = CustomTransferTemplate.formatBodyNumbers(template.recipient)
            formatted.subject = CustomTransferTemplate.formatBodyNumbers(template.subject)
            formatted.footer = CustomTransferTemplate.formatBodyNumbers(template.footer)
            return LetterText(header: formatted.header, fixedNumber: template.number,
                committee: [template.firstSigner, template.secondSigner, template.thirdSigner], footer: formatted.footer,
                bodyTemplate: template.body, signatureOffset: template.signatureVerticalOffset ?? 0,
                bodyMonth: custom.bodyMonth, outgoingMonth: custom.outgoingMonth, template: formatted)
        }
        guard let header = custom.header, let fixedNumber = custom.fixedNumber,
              let first = custom.committeeFirst, let second = custom.committeeSecond,
              let third = custom.committeeThird, let footer = custom.footer,
              (-60...60).contains(custom.signatureOffset ?? 0) else { throw Failure.incomplete }
        _ = try TransferLetterCustomValues.outgoingDate(custom.outgoingMonth, sourceMonth: "كانون الثاني 2026", eastern: false)
        if let month = custom.bodyMonth, !month.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            _ = try TransferLetterCustomValues.bodyMonth(month, source: month)
        }
        return LetterText(header: header, fixedNumber: fixedNumber, committee: [first, second, third], footer: footer,
            bodyTemplate: custom.bodyTemplate, signatureOffset: custom.signatureOffset ?? 0, bodyMonth: custom.bodyMonth, outgoingMonth: custom.outgoingMonth)
    }
    static func signatures(for text: LetterText, date: String, adaptivePDF: Bool) throws -> [String] {
        if adaptivePDF, text.template != nil { return try CustomTransferTemplate.signatureCells(text.committee, date: date) }
        return signatureCells(text.committee, date: date, adaptivePDF: adaptivePDF)
    }
    static func bodyParagraph(_ body: String, template: CustomTransferTemplate?) -> TransferWordDocument.Paragraph {
        guard let template else { return .init(text: body, halfPoints: 28) }
        let alignment: TransferWordDocument.Paragraph.Alignment = switch template.bodyAlignment ?? .justified {
        case .right: .right
        case .center: .center
        case .left: .left
        case .justified: .justified
        }
        let spacing: Int = switch template.bodySpacing ?? .medium {
        case .compact: 0
        case .medium: 3
        case .wide: 8
        }
        return .init(text: body, halfPoints: 28, bold: template.bodyBold ?? false, alignment: alignment, lineSpacing: spacing)
    }
    static func appendAttachments(_ text: LetterText, to blocks: inout [TransferWordDocument.Block]) {
        guard let attachment = text.template?.attachmentParagraph else { return }
        if let index = blocks.indices.last, case .paragraph(var previous) = blocks[index], previous.text.hasPrefix("المرفقات:") {
            let additional = String(attachment.dropFirst("المرفقات:".count)).trimmingCharacters(in: .whitespacesAndNewlines)
            if !additional.isEmpty { previous.text += "\n" + additional; blocks[index] = .paragraph(previous) }
        } else {
            blocks.append(.paragraph(.init(text: attachment, halfPoints: 22)))
        }
    }
    /// الصفات ثابتة كما ظهرت في النماذج الأصلية، والأسماء من تخصيص المستخدم.
    static func signatureCells(_ committee: [String], date: String, adaptivePDF: Bool, dates: [String]? = nil) -> [String] {
        guard committee.count == 3 else { return [] }
        var signatureDates = (dates?.count == 3 ? dates : nil) ?? [date, date, date]
        signatureDates[2] = thirdMemberDate(signatureDates[2])
        guard adaptivePDF else { return committee.enumerated().map { $0.element + "\n" + signatureDates[$0.offset] } }
        let names = committee.map { $0.replacingOccurrences(of: "ـ", with: "").split(whereSeparator: \.isWhitespace).joined(separator: " ") }
        return [
            "معاون مدير تدقيق\n\(names[0])\nالمصروفات\n\(signatureDates[0])",
            "العميد\n\(names[1])\nمدير الحسابات\n\(signatureDates[1])",
            "لواء الشرطة الحقوقي\n\(names[2])\nقائد شرطة محافظة الأنبار\n\(signatureDates[2])"
        ]
    }
    /// يُكتب اسم شهر العضو الثالث فقط؛ يبقى اليوم والسنة كما في تاريخ التوقيع الأصلي.
    static func thirdMemberDate(_ date: String) -> String {
        let pieces = date.components(separatedBy: "/").map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
        let months = ["كانون الثاني", "شباط", "آذار", "نيسان", "أيار", "حزيران", "تموز", "آب", "أيلول", "تشرين الأول", "تشرين الثاني", "كانون الأول"]
        guard pieces.count == 3, let month = Int(ArabicText.latinDigits(pieces[1])), (1...12).contains(month),
              let year = Int(ArabicText.latinDigits(pieces[2])), (1900...2200).contains(year) else { return date }
        return "\(pieces[0]) / \(months[month - 1]) / \(pieces[2])"
    }
    static func bodyValues(for text: LetterText, sourceMonth: String, amount: Double, adaptivePDF: Bool) throws -> [String: String] {
        let displayedMonth = try TransferLetterCustomValues.bodyMonth(adaptivePDF ? text.bodyMonth : nil, source: sourceMonth)
        return try TransferWordValues.make(month: displayedMonth, amount: amount, custom: .init(), requested: ["MONTH", "YEAR"])
    }
    /// أرقام المبلغ والسنة في المتن فقط؛ أرقام الحسابات تبقى كما هي.
    static func bodyNumber(_ value: String, adaptivePDF: Bool) -> String {
        adaptivePDF ? ReportSheet.easternDigits(value) : value
    }
    /// عرض ١٤ مسافة غير قابلة للكسر يقارب عرض خمسة أرقام بخط ساكال المعتمد.
    static func referenceText(number: String, date: String, adaptivePDF: Bool) -> String {
        let writingSpace = adaptivePDF ? String(repeating: "\u{00A0}", count: 14) : " "
        return "العدد: \(number)\nالتاريخ:\(writingSpace)\(date)"
    }
    /// تبقى مساحة الكتابة بين القوسين عند رسم PDF أو فتح Word.
    static func footerWritingSpace(_ footer: String, adaptivePDF: Bool = false) -> String {
        let fontName = adaptivePDF ? "TimesNewRomanPS-BoldMT" : "TimesNewRomanPSMT"
        guard let font = UIFont(name: fontName, size: 12) else {
            return footer.replacingOccurrences(of: "\\([ \t\u{00A0}]*\\)",
                                                with: "(" + String(repeating: "\u{00A0}", count: 50) + ")",
                                                options: .regularExpression)
        }
        let face = CTFontCreateWithName(font.fontName as CFString, 12, nil)
        let available: Double = adaptivePDF ? 519.3 : 510.3
        return fittedFooter(footer, width: available) { text in
            let value = NSAttributedString(string: text, attributes: [NSAttributedString.Key(kCTFontAttributeName as String): face])
            return CTLineGetTypographicBounds(CTLineCreateWithAttributedString(value), nil, nil, nil)
        }
    }

    /// قياس السطر كاملاً يحافظ على النص المخصص ويملأ القوسين حتى قريب من حافة مساحة الطباعة.
    static func fittedFooter(_ footer: String, width: Double, measure: (String) -> Double) -> String {
        guard width.isFinite, width > 16, let pattern = try? NSRegularExpression(pattern: "\\([ \t\u{00A0}]*\\)") else { return footer }
        return footer.components(separatedBy: "\n").map { line in
            let range = NSRange(line.startIndex..<line.endIndex, in: line)
            guard line.contains("التقدير"), let match = pattern.firstMatch(in: line, range: range) else {
                return line.replacingOccurrences(of: "\\([ \t\u{00A0}]*\\)",
                                                 with: "(" + String(repeating: "\u{00A0}", count: 50) + ")",
                                                 options: .regularExpression)
            }
            func candidate(_ count: Int) -> String {
                (line as NSString).replacingCharacters(in: match.range, with: "(" + String(repeating: "\u{00A0}", count: count) + ")")
            }
            var low = 0, high = 256
            while low < high {
                let middle = (low + high + 1) / 2
                let measured = measure(candidate(middle))
                if measured.isFinite && measured <= width - 8 { low = middle } else { high = middle - 1 }
            }
            return candidate(low)
        }.joined(separator: "\n")
    }
    static func write(plan: TransferWordGroupPlan, month: String, payerIBAN: String,
                      text: LetterText, destination: URL, sakkalBold: Data? = nil, adaptivePDF: Bool = false) throws {
        func nonempty(_ s: String) -> Bool { !s.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
        let normalizedPayer = ArabicText.latinDigits(payerIBAN).uppercased().filter { !$0.isWhitespace && $0 != "-" }
        guard TransferRules.validIBAN(payerIBAN), normalizedPayer == plan.payerIBAN, month == plan.month,
              nonempty(text.header), nonempty(text.fixedNumber),
              text.committee.count == 3, (text.template == nil ? text.committee.allSatisfy(nonempty) : text.committee.contains(where: nonempty)) else { throw Failure.incomplete }
        let signatureDate = try TransferLetterCustomValues.outgoingDate(adaptivePDF ? text.outgoingMonth : nil, sourceMonth: month, eastern: adaptivePDF)
        let letterNumber = adaptivePDF ? ReportSheet.easternDigits(text.fixedNumber) : text.fixedNumber
        let values = try TransferWordValues.make(month: month, amount: plan.total, custom: .init(),
            requested: ["AMOUNT", "AMOUNT_WORDS", "MONTH", "YEAR"])
        guard let amount = values["AMOUNT"], let words = values["AMOUNT_WORDS"],
              let monthName = values["MONTH"], let year = values["YEAR"] else { throw Failure.incomplete }
        let bodyPeriod = try Self.bodyValues(for: text, sourceMonth: month, amount: plan.total, adaptivePDF: adaptivePDF)
        guard let bodyMonth = bodyPeriod["MONTH"], let bodyYear = bodyPeriod["YEAR"] else { throw Failure.incomplete }
        let bodyTemplate = text.template == nil ? text.bodyTemplate : text.bodyTemplate.map(CustomTransferTemplate.formatBodyNumbers)
        let body = try TransferLetterBody.render(id: plan.category == .rafidain ? "rafidain_group" : "other_banks_group", custom: adaptivePDF ? bodyTemplate : nil, values: [
            "المبلغ": bodyNumber(amount, adaptivePDF: adaptivePDF), "المبلغ كتابة": words, "حسابنا": normalizedPayer, "الشهر": bodyMonth, "السنة": bodyNumber(bodyYear, adaptivePDF: adaptivePDF)
        ])
        let signatureCells = try Self.signatures(for: text, date: signatureDate, adaptivePDF: adaptivePDF)
        let signatures = TransferWordDocument.Table(headers: [], rows: [signatureCells], font: "Times New Roman", halfPoints: 32,
                                                     columnWeights: [1, 1, 1], showBorders: false)
        var blocks: [TransferWordDocument.Block] = [
            .paragraph(.init(text: text.header, font: text.template != nil && sakkalBold != nil ? "Sakkal Majalla" : "Times New Roman", halfPoints: 32, bold: true)),
            .paragraph(.init(text: Self.referenceText(number: letterNumber, date: signatureDate, adaptivePDF: adaptivePDF), font: adaptivePDF ? "Sakkal Majalla" : "Times New Roman", halfPoints: adaptivePDF ? 36 : 30, bold: true)),
            .paragraph(.init(text: "إلى / " + (text.template?.recipient ?? "مصرف الرافدين / فرع الأنبار"), font: "Arial", halfPoints: 32, bold: true, centered: adaptivePDF)),
            .paragraph(.init(text: "م / " + (text.template?.subject ?? "تحويل مبلغ"), font: "Arial", halfPoints: 32, bold: true, centered: true)),
            .paragraph(Self.bodyParagraph(body, template: text.template))
        ]
        if adaptivePDF {
            let count = plan.table.rows.count - 1
            guard count > 0 else { throw Failure.incomplete }
            blocks.append(.paragraph(.init(text: "المرفقات:\nقائمة أسماء الحسابات من تسلسل (1) لغاية تسلسل (\(count))\nقرص (CSV).", halfPoints: 24, bold: true)))
        }
        Self.appendAttachments(text, to: &blocks)
        blocks += [
            .table(signatures),
            .paragraph(.init(text: footerWritingSpace(text.footer, adaptivePDF: adaptivePDF), halfPoints: 24, bold: adaptivePDF)),
            .pageBreak,
            .paragraph(.init(text: "استقطاعات قيادة شرطة الأنبار لشهر (\(monthName) / \(bodyNumber(year, adaptivePDF: adaptivePDF)))", halfPoints: 28, bold: true, centered: true)),
            .table(plan.table)
        ]
        if adaptivePDF { try TransferAdaptivePDF.write(blocks: blocks, destination: destination, sakkalBold: sakkalBold, group: true, signatureOffset: text.signatureOffset, signatureHorizontalOffset: text.template?.signatureHorizontalOffset ?? 0) }
        else { try TransferWordDocument.write(blocks: blocks, destination: destination, sakkalBold: sakkalBold) }
    }
}
