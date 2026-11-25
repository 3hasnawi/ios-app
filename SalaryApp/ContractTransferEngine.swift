import Foundation
import SwiftData

@ModelActor
actor ContractTransferEngine {
    private func classification(_ rank: String, person: String) throws -> Bool {
        let key = RankOrder.key(rank)
        // تمويل «عقد» يبقى ضمن الدفعة الحالية حتى عند فصل تمويل «عقود».
        let words = key.split(separator: " ")
        if words.contains("عقود") { return true }
        if words.contains("عقد") { return false }
        if RankOrder.recognizes(rank) || key.hasPrefix("موظف") ||
            ["مفوض", "شرطي", "رئيس عرفاء", "نائب عريف", "عريف", "وكيل"].contains(where: { key.hasPrefix($0) }) { return false }
        throw ContractTransferIssue(tr("راجع رتبة \(person) قبل الفصل: \(rank)", "Review the rank for \(person): \(rank)"))
    }

    func prepare(month: String, importedAt: Date, figures: TransferLetterFigures, entities: [TransferEntity],
                 payer: String, iban: String, links: Data, allowEmptyHold: Bool = false) throws -> ContractTransferBatch {
        guard figures.month == month, figures.contractTotal != nil else {
            throw ContractTransferIssue(tr("راجع الاسمي والتعديل المعتمد لضمان العقود في كشف البنك قبل الفصل.", "Review contract nominal pay and the approved guarantee before splitting."))
        }
        var payroll = FetchDescriptor<Employee>(predicate: #Predicate { $0.month == month })
        payroll.propertiesToFetch = [\.name, \.matchKey, \.rank, \.workplace, \.itemsData, \.detailsData]
        var employees: [String: [(rank: String, workplace: String)]] = [:]
        var fundRows: [(ReportRow, Bool)] = []
        var bankControl = 0.0
        let fundTitle = TransferPayrollRecipient.fundDonations.arabicTitle
        for (index, e) in try modelContext.fetch(payroll).enumerated() {
            if index.isMultiple(of: 256) { try Task.checkCancellation() }
            employees[e.matchKey, default: []].append((e.rank, e.workplace))
            let details = e.details, items = e.items
            func amount(_ titles: [String]) throws -> Double {
                for title in titles {
                    let key = ArabicText.normalize(title)
                    if let raw = (details[title] ?? details.first(where: { ArabicText.normalize($0.key) == key })?.value),
                       !raw.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                        guard let value = ArabicText.number(from: raw), ContractTransferBatch.validMoney(value) else {
                            throw ContractTransferIssue(tr("راجع \(title) للمنتسب \(e.name).", "Review \(title) for \(e.name)."))
                        }
                        return value
                    }
                    if let value = items[title] ?? items.first(where: { ArabicText.normalize($0.key) == key })?.value {
                        guard ContractTransferBatch.validMoney(value) else { throw TransferIndividualPlan.Failure.invalidSource }
                        return value
                    }
                }
                return 0
            }
            bankControl += try amount(["مجموع استقطاعات المصارف"])
            let fund = try amount(["صندوق الشهداء", "صندوق شهداء الشرطة"])
            if fund > 0 {
                fundRows.append((ReportRow(id: index, name: e.name, rank: e.rank, workplace: e.workplace,
                    amount: fund, note: "", entity: fundTitle, personKey: e.matchKey), try classification(e.rank, person: e.name)))
            }
        }
        guard !employees.isEmpty else { throw TransferLetterFigureError.missingPayroll }
        var entries: [String: [(ReportRow, Bool)]] = [:]
        let deductions = try modelContext.fetch(FetchDescriptor<Deduction>(predicate: #Predicate { $0.month == month }))
        for (index, d) in deductions.enumerated() {
            if index.isMultiple(of: 256) { try Task.checkCancellation() }
            if DeductionStatus.read(d.note, amount: d.amount) != nil { continue }
            guard let value = d.amount, ContractTransferBatch.validMoney(value), value > 0 else {
                throw ContractTransferIssue(tr("راجع مبلغ \(d.employeeName) لدى \(d.title).", "Review the deduction for \(d.employeeName) at \(d.title)."))
            }
            let matches = employees[d.matchKey] ?? []
            guard matches.count <= 1 else {
                throw ContractTransferIssue(tr("الاسم \(d.employeeName) مكرر في رواتب الشهر؛ راجع الربط قبل الفصل.", "Ambiguous payroll identity: \(d.employeeName)."))
            }
            let source = d.rank.trimmingCharacters(in: .whitespacesAndNewlines)
            let payRank = matches.first?.rank.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            let rank = source.isEmpty ? payRank : source
            let contract = try classification(rank, person: d.employeeName)
            if !source.isEmpty, !payRank.isEmpty, try classification(payRank, person: d.employeeName) != contract {
                throw ContractTransferIssue(tr("تعارض تصنيف \(d.employeeName) بين الرواتب والاستقطاعات.", "Contract classification conflicts for \(d.employeeName)."))
            }
            entries[d.title, default: []].append((.init(id: index, name: d.employeeName, rank: rank,
                workplace: matches.first?.workplace ?? "", amount: value, note: d.note, entity: d.title, personKey: d.matchKey), contract))
        }
        let execution = entities.filter { $0.payrollRecipient == nil }
        guard Set(entries.keys) == Set(execution.map { $0.group.title }), execution.reduce(0, { $0 + $1.group.total }) == bankControl,
              fundRows.reduce(0, { $0 + $1.0.amount }) == figures.policeFundDonations else {
            throw ContractTransferIssue(tr("مجموع الجهات أو صندوق الشهداء لا يطابق الرواتب؛ راجع كشف البنك قبل الفصل.", "Execution or fund totals differ from payroll; review the bank statement."))
        }
        let parts = try entities.sorted { $0.id < $1.id }.map { entity in
            if let issue = TransferRules.issues(entity).first { throw ContractTransferIssue("\(entity.group.title) — \(issue)") }
            guard entity.category != .unresolved else { throw ContractTransferIssue(tr("حدّد مصرف \(entity.group.title).", "Classify \(entity.group.title).")) }
            let rows = entity.payrollRecipient == TransferPayrollRecipient.fundDonations.rawValue ? fundRows : (entries[entity.group.title] ?? [])
            return ContractTransferPart(entity: entity, current: rows.filter { !$0.1 }.map(\.0).sorted(by: ReportRow.nameOrder),
                                        deferred: rows.filter(\.1).map(\.0).sorted(by: ReportRow.nameOrder))
        }
        let batch = ContractTransferBatch(id: UUID(), month: month, importedAt: importedAt, created: .now,
            figures: figures, payer: payer, iban: iban, links: links, parts: parts)
        try batch.validate(allowEmptyHold: allowEmptyHold)
        return batch
    }
}
