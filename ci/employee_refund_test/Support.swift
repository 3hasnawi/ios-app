func tr(_ ar: String, _ en: String) -> String { ar }
struct CustomTransferTemplate: Codable, Sendable {
    var header = "", number = "", footer = "", firstSigner = "", secondSigner = "", thirdSigner = ""
    var signatureVerticalOffset: Int?, signatureHorizontalOffset: Int?
    enum FontPart: String { case header, reference, recipient, subject, table, signatures, footer, attachments, closing, salutation }
    var fontSizes: [String: Int]?
    func halfPoints(_ part: FontPart, default value: Int) -> Int { value }
    enum Kashida { case none, low, medium, high }
}
enum TransferWordGroupDraft {
    static func footerWritingSpace(_ text: String, adaptivePDF: Bool) -> String { text }
    static func bodyParagraph(_ body: String, template: CustomTransferTemplate?) -> TransferWordDocument.Paragraph { .init(text: body) }
}
enum TransferWordValues { enum Failure: Error { case invalidAmount } }
