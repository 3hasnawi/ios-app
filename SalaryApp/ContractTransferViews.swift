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

struct ContractHistoryView: View {
    @AppStorage(ContractTransferArchive.key) private var data = Data()
    @State private var archive = ContractTransferArchive()
    @State private var selection: ContractTransferArchive.Record?
    @State private var confirming = false
    @State private var failure: String?

    var body: some View {
        List(archive.visibleRecords) { item in
            NavigationLink { ContractTransferRecordView(id: item.id) } label: {
                VStack(alignment: .leading) {
                    LabeledContent(item.month, value: Money.format(item.held))
                    if let date = item.cancelledAt {
                        LabeledContent(tr("أُلغي التأجيل", "Deferral cancelled")) { Text(date, style: .date) }
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
            .contextMenu {
                if item.cancelledAt != nil {
                    Button(role: .destructive) {
                        selection = item
                        confirming = true
                    } label: { Label(tr("حذف الدفعة الملغاة", "Delete cancelled batch"), systemImage: "trash") }
                }
            }
        }
        .overlay {
            if archive.visibleRecords.isEmpty {
                ContentUnavailableView(tr("لا توجد دفعات محفوظة", "No saved batches"), systemImage: "tray")
            }
        }
        .navigationTitle(tr("دفعات العقود المؤجلة", "Deferred contract batches"))
        .onChange(of: data, initial: true) {
            do { archive = try ContractTransferArchive.read(data) }
            catch { archive = .init(); failure = error.localizedDescription }
        }
        .alert(tr("حذف الدفعة الملغاة؟", "Delete cancelled batch?"), isPresented: $confirming, presenting: selection) { item in
            Button(tr("حذف", "Delete"), role: .destructive) {
                do { try ContractBatchStore.update { try $0.removeCancelled(item) } }
                catch { failure = error.localizedDescription }
            }
            Button(tr("إلغاء", "Cancel"), role: .cancel) {}
        } message: { item in
            Text(tr("شهر الاستحقاق", "Funding month") + " · " + item.month + "\n" + Money.format(item.held) + "\n" +
                 tr("تُحذف من القائمة وتُحفظ إثباتات التنفيذ.", "The batch is removed from the list. Transfer evidence is retained."))
        }
        .alert(tr("تعذّر فتح السجل أو تعديله", "Could not open or update the history"), isPresented: Binding(get: { failure != nil }, set: { if !$0 { failure = nil } })) {
            Button(tr("موافق", "OK")) {}
        } message: { Text(failure ?? "") }
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
                let letters = ContractExecutionLetterImpact.affected(in: batch)
                if letters.isEmpty {
                    Section(tr("كتب جهات التنفيذ المتأثرة", "Affected execution letters")) {
                        Text(tr("لا توجد استقطاعات عقود مفصولة من كتب جهات التنفيذ.", "No contract deductions were separated from execution letters."))
                    }
                }
                ForEach(letters) { letter in
                    Section {
                        LabeledContent(tr("مبلغ الكتاب قبل الفصل", "Letter total before deferral"), value: Money.format(letter.original))
                        LabeledContent(tr("المؤجل من الكتاب", "Deferred from this letter"), value: Money.format(letter.deferred))
                        LabeledContent(tr("المتبقي للتحويل الحالي", "Remaining in current transfer"), value: Money.format(letter.current)).bold()
                        ContractRecipientListLink(batch: batch, phase: .deferred, entities: letter.recipients, title: letter.title)
                    } header: {
                        Label(letter.title, systemImage: "doc.text")
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

private struct ContractRecipientListLink: View {
    let batch: ContractTransferBatch
    let phase: ContractTransferPhase
    let entities: [TransferEntity]
    let title: String

    var body: some View {
        NavigationLink {
            List {
                Section(phase.title) { ContractRecipientRows(batch: batch, phase: phase, entities: entities) }
            }
            .navigationTitle(title)
            .navigationBarTitleDisplayMode(.inline)
        } label: {
            LabeledContent(tr("عرض الجهات", "View recipients"), value: Num.format(entities.count))
        }
    }
}

private struct ContractRecipientRows: View {
    let batch: ContractTransferBatch
    let phase: ContractTransferPhase
    let entities: [TransferEntity]

    var body: some View {
        ForEach(entities) { entity in
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
    @State private var cancellation: ContractTransferArchive.Record?
    @State private var review = false
    @State private var reviewStamp: Date?
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
            guard let i = archive.records.firstIndex(where: { $0.id == id && $0.cancelledAt == nil }) else { throw TransferIndividualPlan.Failure.invalidSource }
            try change(&archive.records[i])
            record = archive.records[i]
        }
    }
    var body: some View {
        List {
            if let batch, let record {
                Section {
                    LabeledContent(tr("شهر الاستحقاق", "Funding month"), value: batch.month)
                    if let date = record.cancelledAt {
                        LabeledContent(tr("أُلغي التأجيل", "Deferral cancelled")) { Text(date, style: .date) }
                    } else if changed {
                        Text(tr("أُعيد استيراد الشهر بعد حفظ الدفعة. راجع الأسماء والمبالغ المحفوظة قبل متابعة إصدارها.", "The month was reimported. Review the saved names and amounts before issuing.")).foregroundStyle(.orange)
                        Button(tr("مراجعة واعتماد الدفعة المحفوظة", "Review and retain the saved batch")) { reviewStamp = sourceRevision; review = true }
                    }
                }
                ContractBatchOverview(batch: batch)
                Picker(tr("الدفعة", "Batch"), selection: $phase) {
                    ForEach(ContractTransferPhase.allCases, id: \.self) { Text($0.title).tag($0) }
                }.pickerStyle(.segmented)
                let destinations = ContractTransferDestination.all(batch, phase)
                ForEach(destinations) { dest in
                    Section {
                        let entities = dest.entities(batch, phase)
                        if case .group = dest {
                            LabeledContent(tr("المبلغ قبل الفصل", "Amount before split"), value: Money.format(dest.originalTotal(in: batch)))
                            LabeledContent(tr("مبلغ الدفعة الحالية", "Current batch amount"), value: Money.format(dest.total(batch, .current))).bold()
                            if phase == .deferred {
                                LabeledContent(tr("مبلغ الدفعة المؤجلة", "Deferred batch amount"), value: Money.format(dest.total(batch, .deferred)))
                            }
                            ContractRecipientListLink(batch: batch, phase: phase, entities: entities, title: dest.title(batch, phase))
                        } else {
                            LabeledContent(tr("المبلغ", "Amount"), value: Money.format(dest.total(batch, phase)))
                            ContractRecipientRows(batch: batch, phase: phase, entities: entities)
                        }
                        if record.cancelledAt == nil {
                            Button(tr("تجهيز الكتاب وملفات الدفعة", "Prepare letter and batch files"), systemImage: "square.and.arrow.up") { export(batch, dest) }
                                .disabled(working || changed)
                        }
                        if let date = record.confirmed[key(dest)] {
                            LabeledContent(tr("تم التحويل", "Transferred")) { Text(date, style: .date) }
                        } else if let date = record.prepared[key(dest)], record.cancelledAt != nil {
                            LabeledContent(tr("تاريخ تجهيز الملفات", "Files prepared")) { Text(date, style: .date) }
                        } else if record.prepared[key(dest)] != nil {
                            Button(tr("تأكيد تنفيذ التحويل", "Confirm bank execution"), systemImage: "checkmark.seal") { confirm = dest }.disabled(changed || working)
                        }
                    } header: { Text(dest.title(batch, phase)) }
                }
                if record.cancelledAt == nil {
                    Button(tr("إلغاء الفصل والعودة للتحويل الكامل", "Cancel split and restore full transfers"), role: .destructive) { cancellation = record }.disabled(working)
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
        .sheet(isPresented: $review) {
            if let batch, let stamp = reviewStamp {
                ContractTransferReview(original: batch, stamp: stamp) {
                    guard stamp == sourceRevision else { throw TransferWordDraftGenerator.Failure.sourceChanged }
                    try mutate { $0.reviewedImport = stamp }
                }
            }
        }
        .alert(tr("إلغاء تأجيل هذا الشهر؟", "Cancel deferral for this month?"), isPresented: Binding(get: { cancellation != nil }, set: { if !$0 { cancellation = nil } }), presenting: cancellation) { reviewed in
            Button(tr("إلغاء الفصل", "Remove split"), role: .destructive) {
                do {
                    try ContractBatchStore.update { try $0.cancel(reviewed) }
                    dismiss()
                } catch { self.error = error.localizedDescription }
            }
            Button(tr("رجوع", "Back"), role: .cancel) {}
        } message: { reviewed in
            if !reviewed.confirmed.isEmpty {
                Text(tr("سيشمل التحويل الكامل المبالغ المؤكدة سابقاً. يبقى سجل التنفيذ محفوظاً؛ راجعه قبل الإرسال لتجنب تكرار الصرف.", "Full transfers include previously confirmed amounts. Review the saved execution history before sending to avoid duplicate payment."))
            } else if !reviewed.prepared.isEmpty {
                Text(tr("ستعود مبالغ العقود إلى تحويلات الشهر. استخدم الملفات الشاملة الجديدة واترك ملفات الدفعة السابقة.", "Contract amounts will return to monthly transfers. Use the new full transfer files and set aside the previous batch files."))
            } else {
                Text(tr("ستعود استقطاعات العقود إلى تحويلات هذا الشهر.", "Contract deductions will return to this month’s full transfers."))
            }
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

