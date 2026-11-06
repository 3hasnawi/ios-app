import Foundation

/// معاينة تنسيق محلية فقط؛ لا تقرأ المبالغ ولا تحفظ القالب أو تصدر كتاباً معتمداً.
nonisolated enum LetterTemplateQuickPreview {
    private static func displayed(_ text: String, placeholder: String) -> String {
        text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? placeholder : CustomTransferTemplate.formatBodyNumbers(text)
    }
    static func body(_ input: String) throws -> String {
        let normalized = displayed(input, placeholder: tr("متن الكتاب", "Letter body"))
        let pattern = try NSRegularExpression(pattern: "\\{\\{([^{}]+)\\}\\}")
        let matches = pattern.matches(in: normalized, range: NSRange(normalized.startIndex..., in: normalized))
        let result = NSMutableString(string: normalized)
        for match in matches.reversed() {
            let key = (normalized as NSString).substring(with: match.range(at: 1))
            result.replaceCharacters(in: match.range, with: "«\(key)»")
        }
        return result as String
    }
    static func make(template: CustomTransferTemplate, month: String) throws -> URL {
        guard (-60...60).contains(template.signatureVerticalOffset ?? 0),
              (-30...30).contains(template.signatureHorizontalOffset ?? 0),
              (template.fontSizes ?? [:]).allSatisfy({ key, value in
                  CustomTransferTemplate.FontPart.allCases.contains(where: { $0.rawValue == key }) && (8...24).contains(value)
              }) else { throw CustomTransferTemplate.invalid(tr("راجع أحجام الخط وموضع التواقيع.", "Review font sizes and signature positions.")) }
        let date = (try? TransferLetterCustomValues.outgoingDate(template.outgoingMonth, sourceMonth: month, eastern: true)) ?? " / ــ / ــــ"
        var signers = [template.firstSigner, template.secondSigner, template.thirdSigner]
        if signers.allSatisfy({ $0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }) {
            signers[0] = tr("التوقيع", "Signature")
        }
        // الإدخال متعدد الأسطر غير المكتمل يبقى قابلاً للمعاينة، دون حفظه أو اعتباره توقيعاً مكتملاً.
        signers = signers.map { value in
            let parts = value.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n")
                .components(separatedBy: "\n").map { $0.trimmingCharacters(in: .whitespaces) }
            guard parts.count > 1 else { return value }
            guard parts.count <= 3 else { return tr("توقيع غير مكتمل", "Incomplete signature") }
            return (0..<3).map { index in index < parts.count && !parts[index].isEmpty ? parts[index] : "…" }.joined(separator: "\n")
        }
        let signatures = try CustomTransferTemplate.signatureCells(signers, date: date)
        let header = displayed(template.header, placeholder: tr("الترويسة", "Header"))
        let number = displayed(template.number, placeholder: tr("العدد", "Reference"))
        let recipient = displayed(template.recipient, placeholder: tr("الجهة", "Recipient"))
        let subject = displayed(template.subject, placeholder: tr("الموضوع", "Subject"))
        let footer = displayed(template.footer, placeholder: tr("نسخة منه إلى", "Copies to"))
        var blocks: [TransferWordDocument.Block] = [
            .paragraph(.init(text: header, font: "Times New Roman", halfPoints: template.halfPoints(.header, default: 32), bold: true)),
            .paragraph(.init(text: TransferWordGroupDraft.referenceText(number: number, date: date, adaptivePDF: true), font: "Times New Roman", halfPoints: template.halfPoints(.reference, default: 30), bold: true)),
            .paragraph(.init(text: "إلى / " + recipient, font: "Arial", halfPoints: template.halfPoints(.recipient, default: 32), bold: true, centered: true)),
            .paragraph(.init(text: "م / " + subject, font: "Arial", halfPoints: template.halfPoints(.subject, default: 32), bold: true, centered: true)),
            .paragraph(TransferWordGroupDraft.bodyParagraph(try body(template.body), template: template))
        ]
        if let attachments = template.attachmentParagraph {
            blocks.append(.paragraph(.init(text: attachments, halfPoints: template.halfPoints(.attachments, default: 22))))
        }
        blocks.append(.table(.init(headers: [], rows: [signatures], font: "Times New Roman", halfPoints: template.halfPoints(.signatures, default: 32), columnWeights: [1, 1, 1], showBorders: false)))
        blocks.append(.paragraph(.init(text: TransferWordGroupDraft.footerWritingSpace(footer, adaptivePDF: true), halfPoints: template.halfPoints(.footer, default: 24), bold: true)))
        let fm = FileManager.default
        let dir = fm.temporaryDirectory.appendingPathComponent("letter-template-preview-\(UUID().uuidString)", isDirectory: true)
        try fm.createDirectory(at: dir, withIntermediateDirectories: true, attributes: [.protectionKey: FileProtectionType.complete])
        let url = dir.appendingPathComponent("preview.pdf")
        do {
            try TransferAdaptivePDF.write(blocks: blocks, destination: url, sakkalBold: nil, group: false,
                signatureOffset: template.signatureVerticalOffset ?? 0,
                signatureHorizontalOffset: template.signatureHorizontalOffset ?? 0, template: template)
            return url
        } catch {
            try? fm.removeItem(at: dir)
            throw error
        }
    }
}
