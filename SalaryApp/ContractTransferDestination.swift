import Foundation

nonisolated enum ContractTransferDestination: Hashable, Identifiable {
    case group(TransferCategory), individual(String), guarantee
    var id: String {
        switch self {
        case .group(let category): "group|\(category.rawValue)"
        case .individual(let id): "entity|\(id)"
        case .guarantee: "guarantee"
        }
    }
    init?(entity: TransferEntity) {
        switch entity.category {
        case .rafidain, .otherBanks: self = .group(entity.category)
        case .anbar: self = .individual(entity.id)
        case .unresolved: return nil
        }
    }

    func entities(_ batch: ContractTransferBatch, _ phase: ContractTransferPhase) -> [TransferEntity] {
        batch.parts.compactMap { part in
            guard Self(entity: part.entity) == self else { return nil }
            return part.portion(phase)
        }
    }
    func title(_ batch: ContractTransferBatch, _ phase: ContractTransferPhase) -> String {
        switch self {
        case .group(let category): category.title
        case .individual: entities(batch, phase).first?.group.title ?? ""
        case .guarantee: tr("الضمان الاجتماعي للعقود", "Contract social security")
        }
    }
    func total(_ batch: ContractTransferBatch, _ phase: ContractTransferPhase) -> Double {
        self == .guarantee ? batch.guarantee : entities(batch, phase).reduce(0) { $0 + $1.group.total }
    }
    func originalTotal(in batch: ContractTransferBatch) -> Double {
        if self == .guarantee { return batch.guarantee }
        return batch.parts.reduce(0) { total, part in
            Self(entity: part.entity) == self ? total + part.entity.group.total : total
        }
    }

    static func all(_ batch: ContractTransferBatch, _ phase: ContractTransferPhase) -> [Self] {
        var result: [Self] = [TransferCategory.rafidain, .otherBanks].compactMap { batch.parts(phase, category: $0).isEmpty ? nil : .group($0) }
        result += batch.parts(phase, category: .anbar).map { .individual($0.id) }
        if phase == .deferred && batch.guarantee > 0 { result.append(.guarantee) }
        return result
    }
}

/// Groups the original book including recipients whose payments are unaffected.
nonisolated struct ContractExecutionLetterImpact: Identifiable {
    let id: ContractTransferDestination
    let title: String
    let original: Double
    let deferred: Double
    let recipients: [TransferEntity]
    var current: Double { original - deferred }

    static func affected(in batch: ContractTransferBatch) -> [Self] {
        let books = Dictionary(grouping: batch.parts) { ContractTransferDestination(entity: $0.entity) }
        var result: [Self] = []
        // Match the issue screen's order: grouped letters, then each individual Anbar letter.
        let destinations: [ContractTransferDestination] = [.group(.rafidain), .group(.otherBanks)] +
            batch.parts.compactMap { $0.entity.category == .anbar ? .individual($0.entity.id) : nil }
        for destination in destinations {
            guard let parts = books[destination] else { continue }
            let recipients = parts.compactMap { $0.portion(.deferred) }
            guard recipients.contains(where: { $0.payrollRecipient == nil }) else { continue }
            let title: String
            switch destination {
            case .group(let category): title = tr("كتاب \(category.title)", "\(category.title) letter")
            case .individual: title = tr("كتاب منفرد · \(parts[0].entity.group.title)", "Individual letter · \(parts[0].entity.group.title)")
            case .guarantee: continue
            }
            result.append(Self(id: destination, title: title,
                               original: parts.reduce(0) { $0 + $1.entity.group.total },
                               deferred: recipients.reduce(0) { $0 + $1.group.total }, recipients: recipients))
        }
        return result
    }
}
