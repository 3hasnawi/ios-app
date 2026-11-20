func tr(_ ar: String, _ en: String) -> String { ar }
struct CustomTransferTemplate {
    enum Kashida { case none }
    static func signatureCells(_ signers: [String], date: String) throws -> [String] { signers }
}
enum TransferWordGroupDraft {
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
    static func write(blocks: [TransferWordDocument.Block], destination: URL, sakkalBold: Data?, group: Bool, verifyNumericTableCells: Bool) throws {
        // منطق الدفعة قيد الاختبار؛ اختبار جهاز العرض مستقل.
        try Data("test".utf8).write(to: destination)
    }
}
