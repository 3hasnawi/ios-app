import Foundation

nonisolated enum ContractTransferExport {
    static func files(batch: ContractTransferBatch, phase: ContractTransferPhase, destination: ContractTransferDestination,
                      references: Data, customization: Data, font: Data, date: Date) throws -> [URL] {
        try batch.validate()
        guard ContractTransferDestination.all(batch, phase).contains(destination) else { throw TransferIndividualPlan.Failure.invalidSource }
        let store = try customization.isEmpty ? TransferLetterCustomizationStore() : JSONDecoder().decode(TransferLetterCustomizationStore.self, from: customization)
        if let hashes = store.wordReferenceHashes { try TransferWordDefaults.verifyHashes(hashes, data: references) }
        let selected = destination.entities(batch, phase)
        let scope = phase.referenceScope(month: batch.month)
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true, attributes: [.protectionKey: FileProtectionType.complete])
        var complete = false
        defer { if !complete { try? FileManager.default.removeItem(at: dir) } }
        let letter = dir.appendingPathComponent("كتاب - \(phase.title).pdf")
        var result = [letter]
        let individual = selected.first
        if case .individual = destination, let individual, !individual.isDirectAnbarBank && !TransferRules.isAnbarBankSource(individual.group.title) {
            // The linked local account is the payee, including when two creditors use the same branch.
            let plan = try TransferWordEntityPlan.splitPayment(individual, payer: batch.iban)
            var common = store.effective(for: "ramadi")
            common.bodyTemplate = nil
            if var template = common.letterTemplate {
                template.body = TransferLetterBody.template(for: "ramadi") ?? ""
                template.recipient = "مصرف الرافدين / فرع الأنبار"
                template.subject = "تحويل مبلغ"
                common.letterTemplate = template
            }
            let text = try TransferWordGroupDraft.letterText(from: common, adaptivePDF: true)
            try TransferWordEntityDraft.write(plan: plan, text: text, destination: letter, sakkalBold: font.isEmpty ? nil : font, adaptivePDF: true)
            try TransferAdaptivePDF.verify(letter, required: [plan.payerIBAN, plan.beneficiaryAccount ?? "", String(Int64(plan.amount))])
        } else {
            let book: String
            var rows: TransferIndividualPlan?
            switch destination {
            case .group(let category): book = category == .rafidain ? "rafidain_group" : "other_banks_group"
            case .guarantee: book = "contracts"
            case .individual:
                guard let individual, let source = batch.parts.first(where: { $0.entity.id == individual.id }) else { throw TransferIndividualPlan.Failure.invalidSource }
                book = "rafidain_anbar"
                rows = try TransferIndividualPlan(kind: .rafidainAnbar, group: individual.group, rows: source.rows(phase), missingAmounts: 0)
            }
            let generated = try TransferWordDraftGenerator.make(bookID: book, figures: batch.figures, entities: selected,
                entity: individual, individualPlan: rows, cashList: nil, references: references, accountLinks: batch.links,
                payerName: batch.payer, payerIBAN: batch.iban, customization: store.effective(for: book),
                sakkalBold: font.isEmpty ? nil : font, adaptivePDF: true, referenceScope: scope, valueDate: date)
            defer { try? FileManager.default.removeItem(at: generated.letter.deletingLastPathComponent()) }
            try FileManager.default.moveItem(at: generated.letter, to: letter)
            if let csv = generated.csv {
                let file = dir.appendingPathComponent(csv.lastPathComponent)
                try FileManager.default.moveItem(at: csv, to: file)
                result.append(file)
            }
        }
        let sources = Dictionary(uniqueKeysWithValues: batch.parts.map { ($0.entity.id, $0) })
        var sheets: [ReportSheet] = []
        for entity in selected {
            guard let source = sources[entity.id] else { throw TransferIndividualPlan.Failure.invalidSource }
            let rows = source.rows(phase)
            if entity.payrollRecipient == nil {
                _ = try TransferIndividualPlan(kind: .ramadi, group: entity.group, rows: rows, missingAmounts: 0)
                sheets.append(ReportSheet.make(group: entity.group, rows: rows))
            } else if entity.payrollRecipient == TransferPayrollRecipient.fundDonations.rawValue {
                let fund = FundReport.summary(rows.map { ($0.rank, $0.amount) })
                guard fund.reduce(0, { $0 + $1.total }) == entity.group.total else { throw TransferIndividualPlan.Failure.mismatchedRows }
                let file = dir.appendingPathComponent("كشف صندوق الشهداء.pdf")
                try FundReport.writePDF(fund, month: batch.month, to: file)
                result.append(file)
            }
        }
        if !sheets.isEmpty {
            let file = dir.appendingPathComponent("كشوف جهات التنفيذ - \(phase.title).pdf")
            try PDFReportWriter.write(sheets, to: file)
            result.append(file)
        }
        complete = true
        return result
    }
}

