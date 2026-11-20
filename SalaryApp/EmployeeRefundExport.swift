import Foundation

nonisolated struct RefundLetterSettings: Sendable {
    var header = "جمهورية العراق\nوزارة الداخلية\nقيادة شرطة محافظة الأنبار\nقسم الحسابات"
    var number = ""
    var recipient = "مصرف الرافدين / فرع الأنبار"
    var first = ""
    var second = ""
    var third = ""
    var footer = "نسخة منه إلى:\nشعبة المصروفات / إجراء اللازم والمتابعة.\nالأرشيف."
}

nonisolated enum EmployeeRefundExport {
    static func make(_ batch: EmployeeRefund.Batch, date: Date, letters: RefundLetterSettings) throws -> [URL] {
        guard !letters.recipient.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              !letters.header.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw EmployeeRefund.error("أكمل الترويسة والجهة المخاطبة في بيانات الكتاب.", "Enter the letterhead and addressee.")
        }
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.calendar = Calendar(identifier: .gregorian)
        formatter.dateFormat = "yyyyMMdd"
        let day = formatter.string(from: date)
        formatter.dateFormat = "d / M / yyyy"
        let writtenDate = ReportSheet.easternDigits(formatter.string(from: date))
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("refund-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true, attributes: [.protectionKey: FileProtectionType.complete])
        do {
            var urls: [URL] = [], offset = 0
            for group in batch.groups {
                let data = EmployeeRefund.csv(group, batch: batch, day: day, offset: offset)
                let url = folder.appendingPathComponent("إعادة مبالغ - " + group.code + ".csv")
                try data.write(to: url, options: [.atomic, .completeFileProtection])
                urls.append(url)
                offset += group.payments.count
            }
            for rafidain in [true, false] {
                let groups = batch.groups.filter { $0.isRafidain == rafidain }
                guard !groups.isEmpty else { continue }
                let title = rafidain ? "الرافدين" : "الرشيد والمصارف الأخرى"
                let url = folder.appendingPathComponent("كتاب إعادة مبالغ - " + title + ".pdf")
                let blocks = try letter(groups, batch: batch, letters: letters, date: writtenDate)
                try TransferAdaptivePDF.write(blocks: blocks, destination: url, sakkalBold: nil, group: false, verifyNumericTableCells: true)
                urls.append(url)
            }
            return urls
        } catch {
            try? FileManager.default.removeItem(at: folder)
            throw error
        }
    }
    static func letter(_ groups: [EmployeeRefund.Group], batch: EmployeeRefund.Batch,
                                    letters: RefundLetterSettings, date: String) throws -> [TransferWordDocument.Block] {
        func display(_ amount: Int64) throws -> String {
            ReportSheet.easternDigits(try TransferWordValues.groupedAmount(Double(amount)))
        }
        let total = groups.reduce(Int64.zero) { $0 + $1.total }
        let body = "نرجو تحويل مبلغ قدره (\(try display(total))) دينار عراقي من حسابنا المرقم (\(batch.account)) إلى الحسابات الموطنة للمنتسبين المبينة في ملفات التحويل المرفقة، وذلك عن \(batch.reason)، وحسب التفصيل أدناه."
        var rows = try groups.map { [$0.bank.name, try display($0.total), $0.bank.bic] }
        if groups.count > 1 { rows.append(["المجموع الكلي", try display(total), ""]) }
        let signers = [letters.first, letters.second, letters.third]
        let cells = signers.allSatisfy(\.isEmpty) ? ["", "", ""] : try CustomTransferTemplate.signatureCells(signers, date: date)
        let reference = TransferWordGroupDraft.referenceText(number: ReportSheet.easternDigits(letters.number), date: date, adaptivePDF: true)
        let blocks: [TransferWordDocument.Block] = [
            .paragraph(.init(text: letters.header, halfPoints: 28, bold: true)),
            .paragraph(.init(text: reference, halfPoints: 28, bold: true)),
            .paragraph(.init(text: "إلى / " + letters.recipient, font: "Arial", halfPoints: 32, bold: true, centered: true)),
            .paragraph(.init(text: "م / إعادة مبالغ", font: "Arial", halfPoints: 32, bold: true, centered: true)),
            .paragraph(TransferWordGroupDraft.bodyParagraph(body, template: nil)),
            .table(.init(headers: ["المصرف", "المبلغ (دينار)", "SWIFT"], rows: rows, columnWeights: [4, 3, 3], boldLastRow: groups.count > 1)),
            .table(.init(headers: [], rows: [cells], font: "Times New Roman", halfPoints: 28, showBorders: false)),
            .paragraph(.init(text: letters.footer, halfPoints: 22, bold: true))
        ]
        return blocks
    }
}
