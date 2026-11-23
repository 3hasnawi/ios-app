import SwiftUI
import SwiftData

struct ContractTransferReview: View {
    let original: ContractTransferBatch
    let stamp: Date
    let keep: () throws -> Void
    @Environment(\.dismiss) private var dismiss
    @AppStorage(ExecutionAccountStore.storageKey) private var accounts = Data()
    @AppStorage(TransferRules.settingsKey) private var categories = Data()
    @AppStorage("transferPayrollAccountLinks") private var links = Data()
    @State private var changes: [Change]?
    @State private var freshTotal = 0.0
    @State private var error: String?
    struct Change: Identifiable {
        let id: String
        let old: Double
        let new: Double
    }
    private static func comparison(_ old: ContractTransferBatch, _ new: ContractTransferBatch) -> [Change] {
        func rows(_ part: ContractTransferPart?) -> [[String]: Int] {
            guard let part else { return [:] }
            var counts: [[String]: Int] = [:]
            for phase in ContractTransferPhase.allCases {
                for row in part.rows(phase) {
                    let key = [phase.rawValue, row.name, row.rank, row.workplace, row.note, String(row.amount)]
                    counts[key, default: 0] += 1
                }
            }
            return counts
        }
        let before = Dictionary(uniqueKeysWithValues: old.parts.map { ($0.entity.group.title,$0) })
        let after = Dictionary(uniqueKeysWithValues: new.parts.map { ($0.entity.group.title,$0) })
        var result: [Change] = []
        for title in Set(before.keys).union(after.keys).sorted() {
            let a = before[title], b = after[title]
            if rows(a) != rows(b) || a?.entity.group.total != b?.entity.group.total ||
                a?.entity.account?.iban != b?.entity.account?.iban || a?.entity.account?.swift != b?.entity.account?.swift ||
                a?.entity.account?.localAccount != b?.entity.account?.localAccount || a?.entity.category != b?.entity.category {
                result.append(.init(id: title, old: a?.portion(.deferred)?.group.total ?? 0, new: b?.portion(.deferred)?.group.total ?? 0))
            }
        }
        if old.guarantee != new.guarantee {
            result.append(.init(id: tr("الضمان الاجتماعي للعقود", "Contract social security"), old: old.guarantee, new: new.guarantee))
        }
        return result
    }
    var body: some View {
        NavigationStack {
            List {
                if let changes {
                    LabeledContent(tr("المؤجل المحفوظ", "Saved deferred total"), value: Money.format(original.held))
                    LabeledContent(tr("المؤجل حسب الاستيراد الجديد", "Deferred in the new import"), value: Money.format(freshTotal))
                    if changes.isEmpty { Text(tr("تطابقت المبالغ والقيود والحسابات.", "Amounts, entries and accounts match.")) }
                    else { Section(tr("جهات تغيّرت مبالغها أو تفاصيلها", "Changed amounts or details")) {
                        ForEach(changes) { item in VStack(alignment: .leading) {
                            Text(item.id)
                            Text(tr("المحفوظ \(Money.format(item.old)) · الجديد \(Money.format(item.new))",
                                    "Saved \(Money.format(item.old)) · New \(Money.format(item.new))")).font(.caption)
                        } }
                    } }
                    Button(tr("راجعت الفروق وأعتمد الدفعة الأصلية", "Keep the original batch after review")) {
                        do { try keep(); dismiss() } catch { self.error = error.localizedDescription }
                    }
                } else if error == nil { ProgressView() }
                if let error { Text(error).foregroundStyle(.red) }
            }.navigationTitle(tr("مراجعة الاستيراد الجديد", "Review reimport")).navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement: .cancellationAction) { Button(tr("رجوع", "Back")) { dismiss() } } }
        }.task {
            do {
                let month = original.month, source = SharedModel.container, a = accounts, l = links, c = categories
                let snapshot = original, revision = stamp
                let fresh = try await Task.detached {
                    var figures = try await TransferLetterFigureEngine(modelContainer: source).figures(month: month)
                    // The user may retain an explicitly reviewed incomplete basis, but never introduce a new one silently.
                    figures.contractNominalReviewed = snapshot.figures.contractNominalReviewed && snapshot.figures.missingContractNominal == figures.missingContractNominal
                    let (groups, missing) = try await TransferDataEngine(modelContainer: source).read(month: month)
                    let entities = TransferEntityResolution.make(groups: groups, missing: missing, figures: figures,
                        accounts: try ExecutionAccountStore.decode(a), categories: c.isEmpty ? [:] : try JSONDecoder().decode([String:String].self, from: c),
                        payrollLinks: try TransferAccountLinks.decode(l), linksValid: true)
                    return try await ContractTransferEngine(modelContainer: source).prepare(month: month, importedAt: revision, figures: figures,
                        entities: entities, payer: snapshot.payer, iban: snapshot.iban, links: l, allowEmptyHold: true)
                }.value
                guard !Task.isCancelled, a == accounts, c == categories, l == links else { throw TransferWordDraftGenerator.Failure.sourceChanged }
                changes = Self.comparison(original, fresh); freshTotal = fresh.held
            } catch { self.error = error.localizedDescription }
        }
    }
}

