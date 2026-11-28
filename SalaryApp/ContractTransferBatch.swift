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
    func validate(allowEmptyHold: Bool = false) throws {
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
        guard held > 0 || allowEmptyHold else { throw ContractTransferIssue(tr("لا توجد استقطاعات عقود للتأجيل.", "There are no contract deductions to defer.")) }
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
        let importedAt: Date
        let compressed: Data
        var prepared: [String: Date] = [:]
        var confirmed: [String: Date] = [:]
        var reviewedImport: Date?
        var cancelledAt: Date?
        func batch() throws -> ContractTransferBatch {
            let batch = try JSONDecoder().decode(ContractTransferBatch.self, from: ContractBatchPayload.unpack(compressed))
            try batch.validate()
            guard batch.month == month, batch.id == id, batch.importedAt == importedAt, batch.held == held, Set(confirmed.keys).isSubset(of: Set(prepared.keys)) else { throw TransferIndividualPlan.Failure.invalidSource }
            return batch
        }
    }
    var version = 1
    var records: [Record] = []
    func activeRecord(for month: String) -> Record? {
        records.first { $0.month == month && $0.cancelledAt == nil }
    }

    static func read(_ data: Data) throws -> Self {
        if data.isEmpty { return Self() }
        let archive = try PropertyListDecoder().decode(Self.self, from: data)
        let active = archive.records.filter { $0.cancelledAt == nil }
        guard archive.version == (active.count == archive.records.count ? 1 : 2),
              Set(active.map(\.month)).count == active.count,
              Set(archive.records.map(\.id)).count == archive.records.count else { throw TransferIndividualPlan.Failure.invalidSource }
        return archive
    }
    mutating func insert(_ batch: ContractTransferBatch) throws {
        try batch.validate()
        guard activeRecord(for: batch.month) == nil, !records.contains(where: { $0.id == batch.id }) else { throw TransferIndividualPlan.Failure.invalidSource }
        let payload = try ContractBatchPayload.pack(JSONEncoder().encode(batch))
        records.insert(Record(id: batch.id, month: batch.month, held: batch.held, importedAt: batch.importedAt, compressed: payload), at: 0)
    }
    mutating func cancel(_ reviewed: Record, at date: Date = .now) throws {
        guard let index = records.firstIndex(where: { $0.id == reviewed.id }),
              records[index].cancelledAt == nil,
              records[index].prepared == reviewed.prepared,
              records[index].confirmed == reviewed.confirmed else { throw TransferIndividualPlan.Failure.invalidSource }
        if records[index].prepared.isEmpty {
            records.remove(at: index)
        } else {
            records[index].cancelledAt = date
            version = 2
        }
    }

    var executionMarkers: [String] {
        var markers: [String] = []
        for record in records {
            let prefix = record.id.uuidString
            if record.cancelledAt != nil { markers.append(prefix + "|cancelled") }
            markers += record.prepared.keys.map { prefix + "|P|" + $0 }
            markers += record.confirmed.keys.map { prefix + "|C|" + $0 }
        }
        return markers.sorted()
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
            guard let restored = records.first(where: { $0.id == item.id }) else {
                throw ContractTransferIssue(tr("النسخة المختارة تفتقد دفعة العقود المحفوظة لشهر \(item.month).", "The saved contract batch for \(item.month) is missing."))
            }
            guard restored.compressed == item.compressed else {
                throw ContractTransferIssue(tr("تختلف بيانات دفعة العقود المحفوظة لشهر \(item.month).", "The saved contract batch for \(item.month) has different contents."))
            }
            guard (item.cancelledAt == nil || restored.cancelledAt == item.cancelledAt),
                  item.prepared.allSatisfy({ restored.prepared[$0.key] == $0.value }),
                  item.confirmed.allSatisfy({ restored.confirmed[$0.key] == $0.value }) else {
                throw ContractTransferIssue(tr("دفعة عقود \(item.month) · تختلف حالة الإلغاء أو التجهيز أو تأكيد التنفيذ.", "Contract batch for \(item.month) has a different cancellation, preparation, or confirmation record."))
            }
        }
    }
    func encoded() throws -> Data {
        let encoder = PropertyListEncoder()
        encoder.outputFormat = .binary
        return try encoder.encode(self)
    }
}

