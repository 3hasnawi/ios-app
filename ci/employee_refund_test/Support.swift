func tr(_ ar: String, _ en: String) -> String { ar }
struct CustomTransferTemplate: Codable, Sendable {
    var header = "", number = "", footer = "", firstSigner = "", secondSigner = "", thirdSigner = ""
    var signatureVerticalOffset: Int?, signatureHorizontalOffset: Int?
    enum Part { case header, reference, recipient, subject, table, signatures, footer, attachments }
    func halfPoints(_ part: Part, default value: Int) -> Int { value }
    enum Kashida { case none }
    static func signatureCells(_ signers: [String], date: String) throws -> [String] { signers }
}
enum TransferWordGroupDraft {
    static func footerWritingSpace(_ text: String, adaptivePDF: Bool) -> String { text }
    static func referenceText(number: String, date: String, adaptivePDF: Bool) -> String { number + date }
    static func bodyParagraph(_ body: String, template: CustomTransferTemplate?) -> TransferWordDocument.Paragraph { .init(text: body) }
}
enum TransferWordValues {
    static func groupedAmount(_ amount: Double) throws -> String { String(Int64(amount)) }
}
enum ReportSheet {
    static func easternDigits(_ value: String) -> String { value }
}
enum TransferAdaptivePDF {
    static func write(blocks: [TransferWordDocument.Block], destination: URL, sakkalBold: Data?, group: Bool, signatureOffset: Int, verifyNumericTableCells: Bool, signatureHorizontalOffset: Int, template: CustomTransferTemplate?) throws {
        // منطق الدفعة قيد الاختبار؛ اختبار جهاز العرض مستقل.
        let hasBankTable = blocks.contains { block in
            if case .table(let table) = block { return table.showBorders }
            return false
        }
        precondition(verifyNumericTableCells == hasBankTable)
        try Data("test".utf8).write(to: destination)
    }
}
