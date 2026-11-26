func tr(_ ar: String, _ en: String) -> String { ar }
enum TransferWordGroupDraft {
    static func footerWritingSpace(_ text: String, adaptivePDF: Bool) -> String { text }
    static func bodyParagraph(_ body: String, template: CustomTransferTemplate?) -> TransferWordDocument.Paragraph { .init(text: body) }
}
enum TransferWordValues { enum Failure: Error { case invalidAmount } }
