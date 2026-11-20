import Foundation

nonisolated struct RefundLetterSettings: Codable, Sendable {
    var header = "جمهورية العراق\nوزارة الداخلية\nقيادة شرطة محافظة الأنبار\nقسم الحسابات"
    var number = ""
    var recipient = "مصرف الرافدين / فرع الأنبار"
    var first = ""
    var second = ""
    var third = ""
    var template: CustomTransferTemplate?
    var signatureOffset = 0
    static let copiesTo = "نسخة منه إلى\nشعبة المصروفات / شعبة الموازنة / شعبة الرواتب / إجراء اللازم والمتابعة."
    // حافظ على مفتاح footer في ترميز المسودات القديمة. النص المطبوع موحّد للكتابين.
    private(set) var footer = copiesTo
}

nonisolated struct RefundLetters: Codable, Sendable {
    var rafidain = RefundLetterSettings()
    var otherBanks = RefundLetterSettings()
}

nonisolated enum EmployeeRefundExport {
    static func make(_ batch: EmployeeRefund.Batch, date: Date, letters: RefundLetters) throws -> [URL] {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.calendar = Calendar(identifier: .gregorian)
        formatter.dateFormat = "yyyyMMdd"
        let day = formatter.string(from: date)
        formatter.dateFormat = "M / yyyy"
        let writtenDate = " / " + ReportSheet.easternDigits(formatter.string(from: date))
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("refund-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true, attributes: [.protectionKey: FileProtectionType.complete])
        do {
            var urls: [URL] = []
            for group in batch.groups {
                let data = EmployeeRefund.csv(group, batch: batch, day: day)
                let url = folder.appendingPathComponent("إعادة مبالغ - " + group.code + ".csv")
                try data.write(to: url, options: [.atomic, .completeFileProtection])
                urls.append(url)
            }
            for rafidain in [true, false] {
                let groups = batch.groups.filter { $0.isRafidain == rafidain }
                guard !groups.isEmpty else { continue }
                let title = rafidain ? "الرافدين" : "الرشيد والمصارف الأخرى"
                let url = folder.appendingPathComponent("كتاب إعادة مبالغ - " + title + ".pdf")
                let settings = rafidain ? letters.rafidain : letters.otherBanks
                let blocks = try letter(groups, batch: batch, letters: settings, date: writtenDate)
                try TransferAdaptivePDF.write(blocks: blocks, destination: url, sakkalBold: nil, group: false, signatureOffset: settings.signatureOffset,
                    verifyNumericTableCells: !rafidain, signatureHorizontalOffset: settings.template?.signatureHorizontalOffset ?? 0, template: settings.template, singlePageBankRefund: !rafidain)
                urls.append(url)
            }
            let rosterURL = folder.appendingPathComponent("قائمة منتسبي إعادة المبالغ.pdf")
            formatter.dateFormat = "yyyy/MM/dd"
            let sheet = roster(batch, date: ReportSheet.easternDigits(formatter.string(from: date)))
            try PDFReportWriter.write(sheet, to: rosterURL)
            urls.append(rosterURL)
            return urls
        } catch {
            try? FileManager.default.removeItem(at: folder)
            throw error
        }
    }
    static func roster(_ batch: EmployeeRefund.Batch, date: String) -> ReportSheet {
        var rows: [[String]] = [], amounts: [Double] = []
        for group in batch.groups {
            let bank = group.isRafidain ? "مصرف الرافدين" : group.bank.name
            for person in group.payments {
                rows.append([String(rows.count + 1), person.name, "", person.iban, bank])
                amounts.append(Double(person.amount))
            }
        }
        let title = ["قائمة منتسبي إعادة المبالغ", date, batch.source].filter { !$0.isEmpty }.joined(separator: " — ")
        return ReportSheet(header: batch.payer, title: title,
                           columns: [.init(title: "ت", width: 5, kind: .serial), .init(title: "اسم المنتسب", width: 32, kind: .text),
                                     .init(title: "المبلغ (د.ع)", width: 16, kind: .amount), .init(title: "رقم الحساب (IBAN)", width: 33, kind: .text),
                                     .init(title: "المصرف", width: 26, kind: .text)], rows: rows, amounts: amounts)
    }
    static func letter(_ groups: [EmployeeRefund.Group], batch: EmployeeRefund.Batch,
                                    letters: RefundLetterSettings, date: String) throws -> [TransferWordDocument.Block] {
        func display(_ amount: Int64) throws -> String {
            ReportSheet.easternDigits(try TransferWordValues.groupedAmount(Double(amount)))
        }
        guard let first = groups.first, groups.allSatisfy({ $0.isRafidain == first.isRafidain }),
              !letters.header.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              !letters.recipient.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw EmployeeRefund.error("راجع مجموعة المصارف وترويسة الكتاب والجهة المخاطبة.", "Check the bank group, letterhead and addressee.")
        }
        let total = groups.reduce(Int64.zero) { $0 + $1.total }
        let count = groups.reduce(0) { $0 + $1.payments.count }
        let destination: String
        if first.isRafidain {
            destination = "إلى حساب \(first.bank.name) المرقم (\(first.bank.bic)) وحسب ما مؤشر في قرص CD"
        } else if groups.count == 1 {
            destination = "إلى حساب \(first.bank.name)، رمز السويفت (\(first.bank.bic))، وحسب ما مؤشر في ملف CSV المرفق"
        } else {
            destination = "إلى حسابات المصارف المبينة في الجدول أدناه وحسب ما مؤشر في ملفات CSV المرفقة"
        }
        let from = batch.source.isEmpty ? "" : " من " + batch.source
        let body = "نرجو تحويل مبلغ قدره (\(try display(total))) (\(try ArabicAmountWords.dinars(total))) لا غيرها من حسابنا المرقم (\(batch.account)) المفتوح لديكم \(destination)، والذي يمثل إعادة مبالغ\(from) لمنتسبي قيادة شرطة محافظة الأنبار والبالغ عددهم (\(ReportSheet.easternDigits(String(count))))، وتزويدنا بالإشعار اللازم بذلك."
        let template = letters.template
        let signers = [letters.first, letters.second, letters.third]
        let cells = signers.allSatisfy(\.isEmpty) ? ["", "", ""] : try CustomTransferTemplate.signatureCells(signers, date: date)
        let reference = TransferWordGroupDraft.referenceText(number: ReportSheet.easternDigits(letters.number), date: date, adaptivePDF: true)
        var blocks: [TransferWordDocument.Block] = [
            .paragraph(.init(text: letters.header, halfPoints: template?.halfPoints(.header, default: 32) ?? 32, bold: true)),
            .paragraph(.init(text: reference, halfPoints: template?.halfPoints(.reference, default: 36) ?? 36, bold: true)),
            .paragraph(.init(text: "إلى / " + letters.recipient, font: "Arial", halfPoints: template?.halfPoints(.recipient, default: 32) ?? 32, bold: true, centered: true)),
            .paragraph(.init(text: "م / تحويل مبلغ", font: "Arial", halfPoints: template?.halfPoints(.subject, default: 32) ?? 32, bold: true, centered: true)),
            .paragraph(TransferWordGroupDraft.bodyParagraph(body, template: template))
        ]
        if !first.isRafidain {
            var rows = try groups.enumerated().map { index, group in
                [ReportSheet.easternDigits(String(index + 1)), group.bank.name, try display(group.total),
                 ReportSheet.easternDigits(String(group.payments.count)), group.bank.bic]
            }
            if groups.count > 1 { rows.append(["", "المجموع", try display(total), ReportSheet.easternDigits(String(count)), ""]) }
            blocks.append(.table(.init(headers: ["ت", "اسم المصرف", "المبلغ", "عدد المنتسبين", "Receiver BIC"], rows: rows,
                halfPoints: template?.halfPoints(.table, default: 19) ?? 19, columnWeights: [1, 8, 5, 4, 5], boldLastRow: groups.count > 1)))
        }
        blocks.append(.paragraph(.init(text: "المرفقات:\n١. قرص CD", halfPoints: template?.halfPoints(.attachments, default: 22) ?? 22)))
        blocks.append(.table(.init(headers: [], rows: [cells], font: "Times New Roman",
            halfPoints: template?.halfPoints(.signatures, default: 32) ?? 32, showBorders: false)))
        blocks.append(.paragraph(.init(text: RefundLetterSettings.copiesTo,
            halfPoints: template?.halfPoints(.footer, default: 24) ?? 24, bold: true)))
        return blocks
    }
}
