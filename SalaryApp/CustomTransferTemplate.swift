import Foundation

/// قالب المستخدم مستقل عن مبالغ الشهر؛ تُقرأ المبالغ مجدداً عند إعداد كل كتاب.
nonisolated struct CustomTransferTemplate: Codable, Identifiable, Equatable, Sendable {
    enum Source: String, Codable, CaseIterable, Sendable { case none, manual, payroll, execution }
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

    static let settingsKey = "customTransferTemplatesV1"
    private struct Archive: Codable { var version: Int; var templates: [CustomTransferTemplate] }
    static func decode(_ data: Data) throws -> [Self] {
        if data.isEmpty { return [] }
        guard data.count <= 2_000_000 else { throw invalid() }
        // لا نكتب فوق حقول أضافها إصدار أحدث لا يفهمه هذا الإصدار.
        let known: Set<String> = ["id", "title", "header", "number", "recipient", "subject", "body", "footer", "firstSigner", "secondSigner", "thirdSigner", "source", "sourceName", "account"]
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
    func validate() throws {
        let values = [title, header, number, recipient, subject, body, footer, firstSigner, secondSigner, thirdSigner, sourceName, account]
        guard values.allSatisfy({ $0.utf8.count <= 12_000 && !$0.unicodeScalars.contains(where: { $0.value < 32 && $0.value != 10 && $0.value != 13 && $0.value != 9 }) }),
              !title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { throw Self.invalid() }
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
}
