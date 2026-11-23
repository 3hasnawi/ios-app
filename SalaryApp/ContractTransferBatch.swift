import Foundation

nonisolated struct ContractTransferIssue: LocalizedError {
    let reason: String
    init(_ reason: String) { self.reason = reason }
    var errorDescription: String? { reason }
}

nonisolated enum ContractTransferPhase: String, Codable, CaseIterable {
    case current, deferred
    var title: String { self == .current ? tr("الدفعة الحالية", "Current batch") : tr("دفعة العقود المؤجلة", "Deferred contracts") }
    func referenceScope(month: String) -> String { "0\(MonthName.sortKey(month))" + (self == .current ? "1" : "2") }
}

nonisolated struct ContractTransferPart: Codable {
    let entity: TransferEntity
    let current: [ReportRow]
    let deferred: [ReportRow]
    func rows(_ phase: ContractTransferPhase) -> [ReportRow] { phase == .current ? current : deferred }

    func portion(_ phase: ContractTransferPhase) -> TransferEntity? {
        let isFund = entity.payrollRecipient == TransferPayrollRecipient.fundDonations.rawValue
        if entity.payrollRecipient != nil && !isFund { return phase == .current ? entity : nil }
        let rows = rows(phase)
        guard !rows.isEmpty else { return nil }
        return TransferEntity(group: entity.group.forMonth(entity.group.month, rows: rows), account: entity.account,
                              category: entity.category, missingAmounts: 0, beneficiaryName: entity.beneficiaryName,
                              payrollRecipient: entity.payrollRecipient)
    }
}

nonisolated struct ContractTransferBatch: Codable, Identifiable {
    let id: UUID
    let month: String
    let importedAt: Date
    let created: Date
    let figures: TransferLetterFigures
    let payer: String
    let iban: String
    let links: Data
    let parts: [ContractTransferPart]

    var guarantee: Double { figures.contractTotal ?? 0 }
    func parts(_ phase: ContractTransferPhase, category: TransferCategory) -> [TransferEntity] {
        parts.compactMap { $0.portion(phase) }.filter { $0.category == category }
    }
    var held: Double {
        parts.reduce(guarantee) { sum, part in sum + (part.portion(.deferred)?.group.total ?? 0) }
    }
    func validate() throws {
        guard month == figures.month, MonthName.sortKey(month) > 0, figures.contractTotal != nil,
              Self.validMoney(guarantee), TransferRules.safeField(payer), TransferRules.validIBAN(iban),
              Set(parts.map { $0.entity.id }).count == parts.count else { throw TransferIndividualPlan.Failure.invalidSource }
        var total = guarantee
        for part in parts {
            let entity = part.entity
            let rows = part.current + part.deferred
            guard entity.group.month == month, entity.category != .unresolved, TransferRules.issues(entity).isEmpty,
                  Self.validMoney(entity.group.total),
                  rows.allSatisfy({ Self.validMoney($0.amount) && $0.amount > 0 && !$0.name.isEmpty && $0.entity == entity.group.title }),
                  Set(rows.map(\.id)).count == rows.count else { throw TransferIndividualPlan.Failure.invalidSource }
            if entity.payrollRecipient == nil {
                _ = try TransferIndividualPlan(kind: .ramadi, group: entity.group, rows: rows, missingAmounts: entity.missingAmounts)
            } else if entity.payrollRecipient != TransferPayrollRecipient.fundDonations.rawValue, !rows.isEmpty {
                throw TransferIndividualPlan.Failure.mismatchedRows
            }
            guard (part.portion(.current)?.group.total ?? 0) + (part.portion(.deferred)?.group.total ?? 0) == entity.group.total
            else { throw TransferIndividualPlan.Failure.mismatchedRows }
            total += entity.group.total
            guard Self.validMoney(total) else { throw TransferIndividualPlan.Failure.invalidSource }
        }
        guard held > 0 else { throw ContractTransferIssue(tr("لا توجد استقطاعات عقود للتأجيل.", "There are no contract deductions to defer.")) }
    }

    static func validMoney(_ value: Double) -> Bool {
        value.isFinite && value >= 0 && value <= 9_007_199_254_740_991 && value.rounded() == value
    }
}

// A sealed month keeps the original amounts and payees even after payroll retention expires.
nonisolated struct ContractTransferArchive: Codable {
    static let key = "contractTransferBatchesV1"
    struct Record: Codable, Identifiable {
        let id: UUID
        let month: String
        let held: Double
        let compressed: Data
        var prepared: [String: Date] = [:]
        var confirmed: [String: Date] = [:]
        var reviewedImport: Date?
        func batch() throws -> ContractTransferBatch {
            let batch = try JSONDecoder().decode(ContractTransferBatch.self, from: ContractBatchPayload.unpack(compressed))
            try batch.validate()
            guard batch.month == month, batch.id == id, batch.held == held, Set(confirmed.keys).isSubset(of: Set(prepared.keys)) else { throw TransferIndividualPlan.Failure.invalidSource }
            return batch
        }
    }
    var version = 1
    var records: [Record] = []
    static func read(_ data: Data) throws -> Self {
        if data.isEmpty { return Self() }
        let archive = try PropertyListDecoder().decode(Self.self, from: data)
        guard archive.version == 1, Set(archive.records.map(\.month)).count == archive.records.count,
              Set(archive.records.map(\.id)).count == archive.records.count else { throw TransferIndividualPlan.Failure.invalidSource }
        return archive
    }
    mutating func insert(_ batch: ContractTransferBatch) throws {
        try batch.validate()
        guard !records.contains(where: { $0.month == batch.month }) else { throw TransferIndividualPlan.Failure.invalidSource }
        let payload = try ContractBatchPayload.pack(JSONEncoder().encode(batch))
        records.insert(Record(id: batch.id, month: batch.month, held: batch.held, compressed: payload), at: 0)
    }
    static func fromSettings(_ settings: [String: Data]) throws -> Self {
        guard let raw = settings[key] else { return Self() }
        guard let data = try PropertyListSerialization.propertyList(from: raw, format: nil) as? Data else { throw TransferIndividualPlan.Failure.invalidSource }
        let archive = try read(data)
        guard !archive.records.isEmpty else { throw TransferIndividualPlan.Failure.invalidSource }
        for record in archive.records { _ = try record.batch() }
        return archive
    }
    func checkRestore(over old: Self) throws {
        for item in old.records {
            guard let restored = records.first(where: { $0.id == item.id && $0.compressed == item.compressed }),
                  item.prepared.allSatisfy({ restored.prepared[$0.key] == $0.value }),
                  item.confirmed.allSatisfy({ restored.confirmed[$0.key] == $0.value }) else {
                throw ContractTransferIssue(tr("النسخة المختارة تفتقد دفعة عقود محفوظة أو آخر حالة تنفيذها.", "The selected backup lacks an existing contract batch or its latest execution state."))
            }
        }
    }
    func encoded() throws -> Data {
        let encoder = PropertyListEncoder()
        encoder.outputFormat = .binary
        return try encoder.encode(self)
    }
}

