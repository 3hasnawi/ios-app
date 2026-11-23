import SwiftUI
import SwiftData

@MainActor
enum ContractBatchStore {
    static var busy: Bool { BackupManager.shared.isWorking || ImportCenter.shared.isBusy || StaffImportState.shared.isImporting }
    static func update(_ change: (inout ContractTransferArchive) throws -> Void) throws {
        guard !busy else { throw ContractTransferIssue(tr("انتظر انتهاء الاستيراد أو النسخ.", "Wait for import or backup to finish.")) }
        let defaults = UserDefaults.standard
        var archive = try ContractTransferArchive.read(defaults.data(forKey: ContractTransferArchive.key) ?? Data())
        try change(&archive)
        if archive.records.isEmpty { defaults.removeObject(forKey: ContractTransferArchive.key) }
        else { defaults.set(try archive.encoded(), forKey: ContractTransferArchive.key) }
        BackupManager.shared.markSettingChanged(ContractTransferArchive.key)
    }
}

struct ContractBatchOverview: View {
    let batch: ContractTransferBatch
    var body: some View {
        let heldEntities = batch.parts.filter { $0.entity.payrollRecipient == nil }
        let heldFund = batch.parts.filter { $0.entity.payrollRecipient == TransferPayrollRecipient.fundDonations.rawValue }
        Section(tr("حصة العقود المؤجلة", "Deferred contract amounts")) {
            LabeledContent(tr("جهات التنفيذ", "Execution entities"), value: Money.format(heldEntities.reduce(0) { $0 + ($1.portion(.deferred)?.group.total ?? 0) }))
            LabeledContent(tr("صندوق الشهداء", "Martyrs fund"), value: Money.format(heldFund.reduce(0) { $0 + ($1.portion(.deferred)?.group.total ?? 0) }))
            LabeledContent(tr("الضمان الاجتماعي للعقود", "Contract social security"), value: Money.format(batch.guarantee))
            LabeledContent(tr("المجموع المؤجل", "Deferred total"), value: Money.format(batch.held)).bold()
        }
    }
}

struct ContractBatchActivation: View {
    let batch: ContractTransferBatch
    let save: () throws -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var reviewed = false
    @State private var error: String?
    var body: some View {
        NavigationStack {
            List {
                Section { LabeledContent(tr("شهر الاستحقاق", "Funding month"), value: batch.month) }
                ContractBatchOverview(batch: batch)
                Section(tr("جهات التنفيذ", "Execution entities")) {
                    ForEach(batch.parts.filter { $0.entity.payrollRecipient == nil && !$0.deferred.isEmpty }, id: \.entity.id) { part in
                        LabeledContent(part.entity.group.title, value: Money.format(part.portion(.deferred)?.group.total))
                    }
                }
                Section {
                    Toggle(tr("تحققت أن تحويلات هذا الشهر لم تُنفّذ بعد", "I verified that this month's transfers have not been executed"), isOn: $reviewed)
                    Button(tr("اعتماد الفصل لهذا الشهر", "Approve split for this month")) {
                        do { try save(); dismiss() } catch { self.error = error.localizedDescription }
                    }.disabled(!reviewed)
                    if let error { Text(error).foregroundStyle(.red) }
                }
            }.navigationTitle(tr("مراجعة تأجيل العقود", "Review contract deferral")).navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement: .cancellationAction) { Button(tr("إلغاء", "Cancel")) { dismiss() } } }
        }
    }
}

struct ContractTransferRecordView: View {
    let id: UUID
    @AppStorage(ContractTransferArchive.key) private var stored = Data()
    @AppStorage(TransferReferencePack.settingsKey) private var references = Data()
    @AppStorage(TransferLetterCustomizationStore.settingsKey) private var customization = Data()
    @AppStorage(TransferHeaderFont.boldSettingsKey) private var font = Data()
    @Query private var months: [PayrollMonth]
    @Environment(\.dismiss) private var dismiss
    @State private var batch: ContractTransferBatch?
    @State private var record: ContractTransferArchive.Record?
    @State private var phase = ContractTransferPhase.current
    @State private var working = false
    @State private var error: String?
    @State private var confirm: ContractTransferDestination?
    @State private var cancel = false
    @State private var review = false
    private var sourceRevision: Date? {
        guard let batch, let month = months.first(where: { $0.name == batch.month }), month.employeeCount > 0 else { return nil }
        return month.importedAt
    }
    private var changed: Bool {
        guard let sourceRevision, let batch else { return false }
        return sourceRevision != batch.importedAt && sourceRevision != record?.reviewedImport
    }
    private func key(_ dest: ContractTransferDestination) -> String { phase.rawValue + "|" + dest.id }
    private func mutate(_ change: (inout ContractTransferArchive.Record) throws -> Void) throws {
        try ContractBatchStore.update { archive in
            guard let i = archive.records.firstIndex(where: { $0.id == id }) else { throw TransferIndividualPlan.Failure.invalidSource }
            try change(&archive.records[i])
            record = archive.records[i]
        }
    }
    var body: some View {
        List {
            if let batch, let record {
                Section {
                    LabeledContent(tr("شهر الاستحقاق", "Funding month"), value: batch.month)
                    if changed {
                        Text(tr("أُعيد استيراد الشهر بعد حفظ الدفعة. راجع الأسماء والمبالغ المحفوظة قبل متابعة إصدارها.", "The month was reimported. Review the saved names and amounts before issuing.")).foregroundStyle(.orange)
                        Button(tr("مراجعة واعتماد الدفعة المحفوظة", "Review and retain the saved batch")) { review = true }
                    }
                }
                ContractBatchOverview(batch: batch)
                Picker(tr("الدفعة", "Batch"), selection: $phase) {
                    ForEach(ContractTransferPhase.allCases, id: \.self) { Text($0.title).tag($0) }
                }.pickerStyle(.segmented)
                let destinations = ContractTransferDestination.all(batch, phase)
                ForEach(destinations) { dest in
                    Section {
                        LabeledContent(tr("المبلغ", "Amount"), value: Money.format(dest.total(batch, phase)))
                        ForEach(dest.entities(batch, phase)) { entity in
                            if let source = batch.parts.first(where: { $0.entity.id == entity.id }) {
                                NavigationLink {
                                    List {
                                        Section {
                                            LabeledContent(tr("المستفيد", "Beneficiary"), value: entity.beneficiaryName ?? entity.account?.entity ?? entity.group.title)
                                            if let a = entity.account { Text(a.branch); Text(a.iban.isEmpty ? a.localAccount ?? "" : a.iban).privacySensitive() }
                                        }
                                        ForEach(source.rows(phase)) { row in
                                            VStack(alignment: .leading) {
                                                LabeledContent(row.name, value: Money.format(row.amount))
                                                Text([row.rank, row.note].filter { !$0.isEmpty }.joined(separator: " · ")).font(.caption)
                                            }
                                        }
                                    }.navigationTitle(entity.group.title)
                                } label: { LabeledContent(entity.group.title, value: Money.format(entity.group.total)) }
                            }
                        }
                        Button(tr("تجهيز الكتاب وملفات الدفعة", "Prepare letter and batch files"), systemImage: "square.and.arrow.up") { export(batch, dest) }
                            .disabled(working || changed)
                        if let date = record.confirmed[key(dest)] {
                            LabeledContent(tr("تم التحويل", "Transferred")) { Text(date, style: .date) }
                        } else if record.prepared[key(dest)] != nil {
                            Button(tr("تأكيد تنفيذ التحويل", "Confirm bank execution"), systemImage: "checkmark.seal") { confirm = dest }.disabled(changed || working)
                        }
                    } header: { Text(dest.title(batch, phase)) }
                }
                if record.prepared.isEmpty {
                    Button(tr("إلغاء الفصل والعودة للتحويل الكامل", "Cancel split and restore full transfers"), role: .destructive) { cancel = true }.disabled(working)
                }
            } else if error == nil { ProgressView() }
            if let error { Text(error).foregroundStyle(.red) }
            if working { ProgressView() }
        }
        .navigationTitle(tr("دفعات تمويل العقود", "Contract funding batches")).navigationBarTitleDisplayMode(.inline)
        .task(id: stored) {
            do {
                let data = stored, target = id
                let found = try await Task.detached { try ContractTransferArchive.read(data).records.first { $0.id == target } }.value
                guard let found else { batch = nil; record = nil; return }
                if batch?.id != found.id {
                    let loaded = try await Task.detached { try found.batch() }.value
                    guard !Task.isCancelled, data == stored else { return }
                    batch = loaded
                }
                if !Task.isCancelled && data == stored { record = found }
            } catch { self.error = error.localizedDescription; batch = nil; record = nil }
        }
        .confirmationDialog(tr("تأكيد التنفيذ الفعلي لهذه المجموعة؟", "Confirm actual execution for this group?"),
            isPresented: Binding(get: { confirm != nil }, set: { if !$0 { confirm = nil } }), presenting: confirm) { dest in
            Button(tr("نعم، تم التحويل", "Yes, funds transferred")) {
                let k = key(dest)
                do { try mutate { r in
                    guard !changed, r.prepared[k] != nil, r.confirmed[k] == nil else { throw TransferIndividualPlan.Failure.invalidSource }
                    r.confirmed[k] = .now
                } } catch { self.error = error.localizedDescription }
            }
        }
        .alert(tr("اعتماد مبالغ وأسماء اللقطة الأصلية بعد المراجعة؟", "Keep the original amounts and names after review?"), isPresented: $review) {
            Button(tr("اعتماد اللقطة", "Keep snapshot")) {
                do { try mutate { $0.reviewedImport = sourceRevision } } catch { self.error = error.localizedDescription }
            }
            Button(tr("إلغاء", "Cancel"), role: .cancel) {}
        }
        .alert(tr("إلغاء تأجيل هذا الشهر؟", "Cancel deferral for this month?"), isPresented: $cancel) {
            Button(tr("إلغاء الفصل", "Remove split"), role: .destructive) {
                do {
                    try ContractBatchStore.update { archive in
                        guard let i = archive.records.firstIndex(where: { $0.id == id }), archive.records[i].prepared.isEmpty else { throw TransferIndividualPlan.Failure.invalidSource }
                        archive.records.remove(at: i)
                    }
                    dismiss()
                } catch { self.error = error.localizedDescription }
            }
            Button(tr("رجوع", "Back"), role: .cancel) {}
        }
    }

    private func export(_ batch: ContractTransferBatch, _ dest: ContractTransferDestination) {
        guard !working, !changed, !ContractBatchStore.busy else { return }
        let selected = phase, k = key(dest), revision = sourceRevision, ledger = stored
        let date = record?.prepared[k] ?? Date(), refs = references, style = customization, bold = font
        working = true; error = nil
        Task {
            defer { working = false }
            do {
                let urls = try await PDFWork.run {
                    try ContractTransferExport.files(batch: batch, phase: selected, destination: dest, references: refs,
                                                     customization: style, font: bold, date: date)
                }
                guard sourceRevision == revision, ledger == stored, !changed, phase == selected,
                      references == refs, customization == style, font == bold else { throw TransferWordDraftGenerator.Failure.sourceChanged }
                try mutate { r in if r.prepared[k] == nil { r.prepared[k] = date } }
                ActivityShare.present(urls)
            } catch { self.error = error.localizedDescription }
        }
    }
}

