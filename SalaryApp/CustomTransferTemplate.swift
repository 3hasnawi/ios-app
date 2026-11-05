import Foundation

/// قالب المستخدم مستقل عن مبالغ الشهر؛ تُقرأ المبالغ مجدداً عند إعداد كل كتاب.
nonisolated struct CustomTransferTemplate: Codable, Identifiable, Equatable, Sendable {
    enum Source: String, Codable, CaseIterable, Sendable { case none, manual, payroll, execution }
    enum BodyAlignment: String, Codable, CaseIterable, Sendable { case justified, right, center, left }
    enum BodySpacing: String, Codable, CaseIterable, Sendable { case compact, medium, wide }
    var id = UUID()
    var title = ""
    var header = ""
    var number = ""
    var recipient = ""
    var subject = ""
    var body = ""
    var footer = ""
    var firstSigner = ""
    var secondSigner = ""
    var thirdSigner = ""
    var source: Source = .none
    var sourceName = ""
    var account = ""
    // Optional fields keep templates created by older versions readable.
    var bodyAlignment: BodyAlignment? = nil
    var bodySpacing: BodySpacing? = nil
    var bodyBold: Bool? = nil
    var signatureVerticalOffset: Int? = nil
    var signatureHorizontalOffset: Int? = nil
    var attachments: String? = nil
    var foodContractReference: String? = nil
    var bodyMonth: String? = nil
    var outgoingMonth: String? = nil

    static let settingsKey = "customTransferTemplatesV1"
    private struct Archive: Codable { var version: Int; var templates: [CustomTransferTemplate] }
    static func decode(_ data: Data) throws -> [Self] {
        if data.isEmpty { return [] }
        guard data.count <= 2_000_000 else { throw invalid() }
        // لا نكتب فوق حقول أضافها إصدار أحدث لا يفهمه هذا الإصدار.
        let known: Set<String> = ["id", "title", "header", "number", "recipient", "subject", "body", "footer", "firstSigner", "secondSigner", "thirdSigner", "source", "sourceName", "account", "bodyAlignment", "bodySpacing", "bodyBold", "signatureVerticalOffset", "signatureHorizontalOffset", "attachments", "foodContractReference", "bodyMonth", "outgoingMonth"]
        guard let root = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              Set(root.keys).isSubset(of: ["version", "templates"]),
              let records = root["templates"] as? [[String: Any]],
              records.allSatisfy({ Set($0.keys).isSubset(of: known) }) else { throw invalid() }
        let archive = try JSONDecoder().decode(Archive.self, from: data)
        guard archive.version == 1, archive.templates.count <= 250,
              Set(archive.templates.map(\.id)).count == archive.templates.count else { throw invalid() }
        for template in archive.templates { try template.validate() }
        return archive.templates
    }
    static func encode(_ templates: [Self]) throws -> Data {
        let data = try JSONEncoder().encode(Archive(version: 1, templates: templates))
        _ = try decode(data)
        return data
    }
    static func invalid(_ message: String = "تعذّرت قراءة قالب الكتاب أو بياناته؛ لم تُستبدل البيانات.") -> NSError {
        NSError(domain: "CustomTransferTemplate", code: 1, userInfo: [NSLocalizedDescriptionKey: message])
    }
    /// القوالب القديمة تخزن الاسم فقط، أما الحقل الجديد فيحتوي الصفة والاسم والمنصب.
    static func signatureCells(_ signers: [String], date: String) throws -> [String] {
        guard signers.count == 3 else { throw invalid("راجع حقول التواقيع.") }
        let active = signers.enumerated().filter { !$0.element.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
        guard !active.isEmpty else { throw invalid("أدخل توقيعاً واحداً على الأقل.") }
        let legacy = TransferWordGroupDraft.signatureCells(signers, date: date, adaptivePDF: true)
        let rendered = try active.map { index, signer in
            let normalized = signer.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n")
                .trimmingCharacters(in: .whitespacesAndNewlines)
            let parts = normalized.components(separatedBy: "\n").map { $0.trimmingCharacters(in: .whitespaces) }
            if parts.count == 1 && !parts[0].isEmpty { return legacy[index] }
            guard parts.count == 3, parts.allSatisfy({ !$0.isEmpty }) else {
                throw invalid("اكتب لكل عضو ثلاثة أسطر: العنوان الوظيفي، ثم الاسم، ثم المنصب.")
            }
            let signatureDate = index == 2 ? TransferWordGroupDraft.thirdMemberDate(date) : date
            return parts.joined(separator: "\n") + "\n" + signatureDate
        }
        switch rendered.count {
        case 1: return ["", "", rendered[0]] // العمود الأخير على يسار صفحة الكتاب.
        case 2: return [rendered[0], "", rendered[1]]
        default: return rendered
        }
    }
    func validate() throws {
        let values = [title, header, number, recipient, subject, body, footer, firstSigner, secondSigner, thirdSigner, sourceName, account, attachments ?? "", foodContractReference ?? "", bodyMonth ?? "", outgoingMonth ?? ""]
        guard (-60...60).contains(signatureVerticalOffset ?? 0), (-30...30).contains(signatureHorizontalOffset ?? 0),
              values.allSatisfy({ $0.utf8.count <= 12_000 && !$0.unicodeScalars.contains(where: { $0.value < 32 && $0.value != 10 && $0.value != 13 && $0.value != 9 }) }),
              !title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { throw Self.invalid() }
    }
    /// وصف اختياري يطبع بعد عبارة الختام؛ لا يرفق ملفاً خارجياً مع الكتاب.
    var attachmentParagraph: String? {
        let text = (attachments ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return nil }
        let formatted = Self.formatBodyNumbers(text)
        return formatted.hasPrefix("المرفقات:") ? formatted : "المرفقات: " + formatted
    }
    func render(_ values: [String: String]) throws -> String {
        let pattern = try NSRegularExpression(pattern: "\\{\\{[^{}]+\\}\\}")
        let matches = pattern.matches(in: body, range: NSRange(body.startIndex..., in: body))
        let result = NSMutableString(string: body)
        for match in matches.reversed() {
            let token = (body as NSString).substring(with: match.range)
            let key = String(token.dropFirst(2).dropLast(2))
            guard let value = values[key], !value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                throw Self.invalid("الحقل \(token) غير متاح؛ راجع مصدر المبلغ أو الحساب.")
            }
            result.replaceCharacters(in: match.range, with: value)
        }
        let output = result as String
        guard !output.contains("{{"), !output.contains("}}") else { throw Self.invalid("راجع الحقول بين الأقواس في متن الكتاب.") }
        return output
    }

    /// تنسيق أرقام المتن المكتوبة يدوياً مع إبقاء IBAN وأرقام الحسابات الطويلة كما هي.
    static func formatBodyNumbers(_ text: String) -> String {
        guard let regex = try? NSRegularExpression(pattern: "[0-9٠-٩۰-۹]+(?:[,٬][0-9٠-٩۰-۹]{3})*") else { return text }
        let source = text as NSString
        let matches = regex.matches(in: text, range: NSRange(location: 0, length: source.length))
        let ibanPattern = try? NSRegularExpression(pattern: "(?i)(?<![A-Z0-9])IQ(?:[\\s-]*[A-Z0-9]){21}(?![A-Z0-9])")
        let latinText = ArabicText.latinDigits(text)
        let ibanRanges = ibanPattern?.matches(in: latinText, range: NSRange(location: 0, length: (latinText as NSString).length)).map(\.range) ?? []
        let output = NSMutableString(string: text)
        let asciiLetters = CharacterSet(charactersIn: "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ")
        for match in matches.reversed() {
            let range = match.range
            if ibanRanges.contains(where: { NSIntersectionRange($0, range).length > 0 }) { continue }
            let before = range.location > 0 ? source.substring(with: NSRange(location: range.location - 1, length: 1)) : ""
            let afterIndex = NSMaxRange(range)
            let after = afterIndex < source.length ? source.substring(with: NSRange(location: afterIndex, length: 1)) : ""
            if before.rangeOfCharacter(from: asciiLetters) != nil || after.rangeOfCharacter(from: asciiLetters) != nil { continue }
            let original = source.substring(with: range)
            let digits = ArabicText.latinDigits(original).replacingOccurrences(of: ",", with: "").replacingOccurrences(of: "٬", with: "")
            guard digits.count < 13 else { continue }
            let prefix = source.substring(to: range.location).trimmingCharacters(in: .whitespaces)
            let suffix = source.substring(from: afterIndex).trimmingCharacters(in: .whitespaces)
            let datePart = prefix.hasSuffix("/") || suffix.hasPrefix("/") || prefix.hasSuffix("-") || suffix.hasPrefix("-")
            let nearby = String(prefix.suffix(32))
            let amountContext = ["مبلغ", "قيمته", "قيمتها", "مقداره", "دينار"].contains(where: { nearby.contains($0) }) || suffix.hasPrefix("دينار")
            let yearContext = ["سنة", "سنه", "عام"].contains(where: { prefix.hasSuffix($0) })
            var formatted = digits
            if digits.count >= 4 && (amountContext || original.contains(",") || original.contains("٬")) && !yearContext && !datePart,
               let amount = Double(digits), let grouped = try? TransferWordValues.groupedAmount(amount) { formatted = grouped }
            output.replaceCharacters(in: range, with: ReportSheet.easternDigits(formatted))
        }
        return output as String
    }
}
