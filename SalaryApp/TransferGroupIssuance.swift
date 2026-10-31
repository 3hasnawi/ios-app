import SwiftUI
import SwiftData

/// مقارنة تأكيد المجموعة الحالي مع الشهر نفسه أو آخر شهر مؤكّد سابق.
nonisolated enum TransferGroupIssuance {
    struct Change: Identifiable {
        let id: String
        let title: String
        let details: [String]
    }
    enum Failure: LocalizedError {
        case ambiguous
        var errorDescription: String? { tr("تعذّر تدقيق المجموعة لوجود جهات متكررة أو هوية غير واضحة.", "Could not review the group because sources are duplicated or ambiguous.") }
    }
    static func latest(records: [TransferIssuedRecord], category: TransferCategory, month: String) -> TransferIssuedRecord? {
        var result: TransferIssuedRecord?
        for record in records where record.groupCategory == category.rawValue && record.month == month {
            if let previous = result, previous.confirmedAt > record.confirmedAt { continue }
            result = record
        }
        return result
    }
    static func previous(records: [TransferIssuedRecord], category: TransferCategory, month: String) -> TransferIssuedRecord? {
        let target = MonthName.sortKey(month)
        guard target > 0 else { return nil }
        var result: TransferIssuedRecord?
        for record in records where record.groupCategory == category.rawValue {
            let candidate = MonthName.sortKey(record.month)
            guard candidate > 0, candidate < target else { continue }
            if let previous = result {
                let previousMonth = MonthName.sortKey(previous.month)
                if previousMonth > candidate || (previousMonth == candidate && previous.confirmedAt > record.confirmedAt) { continue }
            }
            result = record
        }
        return result
    }
    static func differences(old: [TransferIssuedLine], current: [TransferIssuedLine]) throws -> [Change] {
        func index(_ values: [TransferIssuedLine]) throws -> [String: TransferIssuedLine] {
            var result: [String: TransferIssuedLine] = [:]
            for value in values {
                guard let key = value.sourceIdentity, !key.isEmpty, result[key] == nil else { throw Failure.ambiguous }
                result[key] = value
            }
            return result
        }
        let before = try index(old), now = try index(current)
        var changes: [Change] = []
        for item in current {
            guard let key = item.sourceIdentity else { throw Failure.ambiguous }
            guard let previous = before[key] else {
                changes.append(Change(id: "new|" + key, title: item.title, details: [tr("جهة جديدة · المبلغ \(Money.format(item.amount))", "New entity · amount \(Money.format(item.amount))")]))
                continue
            }
            var details: [String] = []
            if previous.amount != item.amount { details.append(tr("المبلغ: \(Money.format(previous.amount)) ← \(Money.format(item.amount))", "Amount: \(Money.format(previous.amount)) → \(Money.format(item.amount))")) }
            if previous.count != item.count { details.append(tr("عدد المنتسبين: \(Num.format(previous.count)) ← \(Num.format(item.count))", "People: \(Num.format(previous.count)) → \(Num.format(item.count))")) }
            func accountKey(_ value: String?) -> String {
                ArabicText.latinDigits(value ?? "").uppercased().filter { !$0.isWhitespace && $0 != "-" }
            }
            func masked(_ value: String?) -> String {
                let number = accountKey(value)
                return number.isEmpty ? tr("غير متوفر", "Unavailable") : "••••" + String(number.suffix(4))
            }
            let beforeName = previous.beneficiary ?? previous.title
            let currentName = item.beneficiary ?? item.title
            if accountKey(previous.iban) != accountKey(item.iban) {
                details.append(tr("IBAN: \(masked(previous.iban)) ← \(masked(item.iban))", "IBAN: \(masked(previous.iban)) → \(masked(item.iban))"))
            }
            if accountKey(previous.localAccount) != accountKey(item.localAccount) {
                details.append(tr("الحساب المحلي: \(masked(previous.localAccount)) ← \(masked(item.localAccount))", "Local account: \(masked(previous.localAccount)) → \(masked(item.localAccount))"))
            }
            if ArabicText.normalize(previous.branch) != ArabicText.normalize(item.branch) {
                details.append(tr("المصرف أو الفرع: \(previous.branch) ← \(item.branch)", "Bank or branch: \(previous.branch) → \(item.branch)"))
            }
            if accountKey(previous.swift) != accountKey(item.swift) {
                details.append(tr("تغيّر رمز SWIFT.", "SWIFT code changed."))
            }
            if ArabicText.normalize(beforeName) != ArabicText.normalize(currentName) {
                details.append(tr("المستفيد: \(beforeName) ← \(currentName)", "Beneficiary: \(beforeName) → \(currentName)"))
            }
            if previous.detailHash != item.detailHash && previous.detailHash != nil && item.detailHash != nil {
                details.append(tr("تغيّرت تفاصيل قائمة الأسماء أو الرتب أو المبالغ.", "Name list details, ranks, or individual amounts changed."))
            }
            if previous.detailHash == nil && key.hasPrefix("deduction|") {
                details.append(tr("التأكيد السابق لا يحتوي بصمة قائمة الأسماء؛ راجع القائمة.", "The previous confirmation has no name-list fingerprint; review the list."))
            }
            if !details.isEmpty { changes.append(Change(id: "changed|" + key, title: item.title, details: details)) }
        }
        for item in old {
            guard let key = item.sourceIdentity else { throw Failure.ambiguous }
            if now[key] == nil {
                changes.append(Change(id: "missing|" + key, title: item.title, details: [tr("جهة غير موجودة في المجموعة الحالية · المبلغ السابق \(Money.format(item.amount))", "Entity absent from the current group · previous amount \(Money.format(item.amount))")]))
            }
        }
        return changes
    }
}

/// تأكيد يدوي للقائمة الحالية، دون اعتبار المعاينة أو مشاركة الملف إصداراً.
struct TransferGroupIssuanceSection: View {
    let category: TransferCategory
    let month: String
    let entities: [TransferEntity]
    let sourceKey: String
    let sourceRevision: Int
    let accounts: Data
    let categories: Data
    let accountLinks: Data
    let payerName: String
    let payerIBAN: String
    let canConfirm: Bool
    @Query private var months: [PayrollMonth]
    @AppStorage(TransferIssuance.settingsKey) private var stored = Data()
    @AppStorage(ExecutionAccountStore.storageKey) private var currentAccounts = Data()
    @AppStorage(TransferRules.settingsKey) private var currentCategories = Data()
    @AppStorage("transferPayrollAccountLinks") private var currentLinks = Data()
    @AppStorage("bankStatementAccountName") private var currentPayerName = ""
    @AppStorage("bankStatementIBAN") private var currentPayerIBAN = ""
    @State private var bankCache = BankStatementCache.shared
    @State private var checkedLines: [TransferIssuedLine]?
    @State private var checkedStorage = Data()
    @State private var error: String?
    @State private var note = ""
    @State private var checking = false
    @State private var saved = false

    private var current: Bool {
        let record = months.first { $0.name == month }
        return record != nil && sourceRevision == bankCache.revision && currentAccounts == accounts &&
            currentCategories == categories && currentLinks == accountLinks && currentPayerName == payerName && currentPayerIBAN == payerIBAN &&
            "\(String(describing: record?.persistentModelID))|\(record?.name ?? "")|\(record?.importedAt.timeIntervalSince1970 ?? 0)" == sourceKey
    }
    private var currentLines: [TransferIssuedLine] { entities.map(TransferIssuedLine.init) }
    private var loadID: String {
        let rows = currentLines.map { "\($0.id):\($0.amount):\($0.count):\($0.iban):\($0.branch):\($0.swift)" }.joined(separator: "|")
        return "\(sourceKey)|\(sourceRevision)|\(stored.hashValue)|\(accounts.hashValue)|\(categories.hashValue)|\(accountLinks.hashValue)|\(rows)"
    }
    private var ledger: TransferIssuance.Ledger? { try? TransferIssuance.decode(stored) }
    private var sameMonth: TransferIssuedRecord? { ledger.flatMap { TransferGroupIssuance.latest(records: $0.records, category: category, month: month) } }
    private var history: [TransferIssuedRecord] {
        (ledger?.records ?? []).filter { $0.groupCategory == category.rawValue && $0.month == month }
            .sorted { $0.confirmedAt > $1.confirmedAt }
    }
    private var baseline: TransferIssuedRecord? {
        guard let ledger else { return nil }
        return sameMonth ?? TransferGroupIssuance.previous(records: ledger.records, category: category, month: month)
    }
    private var changes: [TransferGroupIssuance.Change]? {
        guard let baseline, let checkedLines else { return nil }
        return try? TransferGroupIssuance.differences(old: baseline.lines, current: checkedLines)
    }
    private var hasManualOverlap: Bool {
        let ids = Set(entities.map(\.id))
        return (ledger?.records ?? []).contains { record in
            record.month == month && record.groupCategory == nil && record.lines.contains { ids.contains($0.id) }
        }
    }
    private var payerChanged: Bool {
        guard let baseline, let oldName = baseline.payerName, let oldIBAN = baseline.payerIBAN else { return false }
        return oldName != payerName || oldIBAN != payerIBAN
    }
    private var hasChanges: Bool { !(changes ?? []).isEmpty || payerChanged }
    var body: some View {
        Section {
            statusContent
        } header: { Text(tr("تدقيق إصدار المجموعة", "Group issuance review")) } footer: {
            Text(tr("التأكيد يسجّل مراجعتك اليدوية للقائمة الحالية، ولا يثبت تنفيذ التحويل المصرفي.", "Confirmation records your manual review of the current list; it does not prove a bank transfer."))
        }
        .task(id: loadID) { await refresh() }
        .alert(tr("تم تسجيل التأكيد", "Confirmation recorded"), isPresented: $saved) { Button(tr("حسناً", "OK"), role: .cancel) {} }
    }
    @ViewBuilder private var statusContent: some View {
        if !current {
            Text(tr("تغيّرت بيانات الشهر؛ أعد فتح ملفات التحويل.", "Month data changed; reopen transfer files.")).foregroundStyle(.orange)
        } else if checking {
            ProgressView(tr("تدقيق المجموعة", "Checking group"))
        } else if let error {
            Label(error, systemImage: "exclamationmark.triangle").foregroundStyle(.orange)
        } else if let checkedLines {
            if hasManualOverlap {
                Text(tr("توجد إقرارات يدوية سابقة لبعض الجهات؛ راجعها في تدقيق الكتب الصادرة لتجنب تسجيل الإصدار مرتين.", "Some entities already have manual confirmations; review them in issued letter review to avoid duplicate issuance records."))
                    .font(.footnote).foregroundStyle(.orange)
            }
            if let baseline { comparisonContent(baseline) }
            else {
                Label(tr("لم يُسجّل تأكيد لهذه المجموعة بعد.", "No confirmation has been recorded for this group."), systemImage: "doc.badge.clock").foregroundStyle(.secondary)
            }
            historyContent
            if sameMonth == nil || hasChanges { confirmationContent(checkedLines) }
        }
    }
    @ViewBuilder private func comparisonContent(_ baseline: TransferIssuedRecord) -> some View {
        if let sameMonth {
            if changes == nil {
                Label(tr("تعذّر تدقيق تأكيد المجموعة", "Could not review group confirmation"), systemImage: "exclamationmark.triangle").foregroundStyle(.orange)
            } else {
                Label(hasChanges ? tr("تغيّرت بيانات المجموعة بعد التأكيد", "Group data changed after confirmation") : tr("المجموعة مؤكّدة ولا توجد تغييرات", "Group confirmed with no changes"), systemImage: hasChanges ? "exclamationmark.triangle" : "checkmark.circle")
                    .foregroundStyle(hasChanges ? Color.orange : Color.green)
            }
            LabeledContent(tr("آخر تأكيد", "Last confirmation")) { Text(sameMonth.confirmedAt, style: .date) }
            if let oldNote = sameMonth.note, !oldNote.isEmpty {
                LabeledContent(tr("ملاحظة التأكيد", "Confirmation note"), value: oldNote)
            }
            if let details = sameMonth.groupChanges, !details.isEmpty {
                DisclosureGroup(tr("تفاصيل التغييرات المؤكدة", "Confirmed change details")) {
                    ForEach(Array(details.enumerated()), id: \.offset) { _, detail in
                        Text(detail).font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
        } else {
            Label(tr("لم تُؤكّد هذه المجموعة لهذا الشهر", "This group is not confirmed for this month"), systemImage: "exclamationmark.circle").foregroundStyle(.orange)
        }
        Text(sameMonth == nil ? tr("المقارنة مع آخر تأكيد في \(baseline.month).", "Compared with the last confirmation in \(baseline.month).") : tr("التغييرات مقارنة بآخر تأكيد لهذا الشهر.", "Changes since the latest confirmation for this month."))
            .font(.footnote).foregroundStyle(.secondary)
        if payerChanged { Text(tr("تغيّرت بيانات الجهة الدافعة؛ راجع الحساب.", "Payer details changed; review the account.")).foregroundStyle(.orange) }
        if let changes {
            ForEach(changes) { change in
                VStack(alignment: .leading, spacing: 4) {
                    Text(change.title).font(.subheadline.weight(.semibold))
                    ForEach(change.details, id: \.self) { detail in Text(detail).font(.caption).foregroundStyle(.orange) }
                }.padding(.vertical, 4)
            }
        } else {
            Text(tr("تعذّرت مقارنة الجهات؛ راجع السجل والبيانات.", "Could not compare entities; review the ledger and data.")).foregroundStyle(.orange)
        }
    }
    @ViewBuilder private var historyContent: some View {
        if history.count > 1 {
            DisclosureGroup(tr("سجل تأكيدات هذا الشهر", "This month's confirmation history")) {
                ForEach(history) { record in
                    VStack(alignment: .leading, spacing: 4) {
                        Text(record.confirmedAt, style: .date).font(.subheadline.weight(.semibold))
                        Text(record.confirmedAt, style: .time).font(.caption).foregroundStyle(.secondary)
                        if let note = record.note, !note.isEmpty { Text(note).font(.caption) }
                        ForEach(Array((record.groupChanges ?? []).enumerated()), id: \.offset) { _, detail in
                            Text(detail).font(.caption).foregroundStyle(.secondary)
                        }
                    }.padding(.vertical, 4)
                }
            }
        }
    }
    @ViewBuilder private func confirmationContent(_ lines: [TransferIssuedLine]) -> some View {
        TextField(tr("ملاحظة عن المراجعة أو التغييرات (اختياري)", "Review or change note (optional)"), text: $note, axis: .vertical)
            .lineLimit(2...4)
        Button(hasChanges ? tr("تأكيد التغييرات وإصدار المجموعة", "Confirm changes and group issuance") : tr("تأكيد إصدار المجموعة", "Confirm group issuance"), systemImage: "checkmark.seal") { confirm(lines) }
            .disabled(!canConfirm || !current || checkedStorage != stored || note.count > 2000 || (baseline != nil && changes == nil))
        if !canConfirm { Text(tr("أكمل بيانات المجموعة والحسابات قبل التأكيد.", "Complete group and account details before confirming.")).font(.footnote).foregroundStyle(.secondary) }
    }
    private func refresh() async {
        checkedLines = nil; error = nil
        guard current else { return }
        guard (try? TransferIssuance.decode(stored)) != nil else { error = tr("تعذّر قراءة سجل التأكيدات؛ لم يتم تغييره.", "Could not read confirmations; nothing was changed."); return }
        let snapshot = currentLines, sourceEntities = entities, storage = stored
        guard Set(snapshot.map(\.id)).count == snapshot.count,
              Set(snapshot.compactMap(\.sourceIdentity)).count == snapshot.count else { error = TransferGroupIssuance.Failure.ambiguous.localizedDescription; return }
        let required = Set(sourceEntities.filter { $0.payrollRecipient == nil }.map(\.id))
        checking = true
        defer { checking = false }
        do {
            let container = SharedModel.container, monthSnapshot = month
            let work = Task.detached(priority: .utility) {
                try await TransferIssuedDetails.load(entities: sourceEntities, ids: required, month: monthSnapshot, cashID: "", cashAmount: nil, container: container)
            }
            let hashes = try await withTaskCancellationHandler { try await work.value } onCancel: { work.cancel() }
            guard !Task.isCancelled, current, stored == storage, currentLines == snapshot else { return }
            guard required.isSubset(of: Set(hashes.keys)) else {
                error = tr("تعذّر تدقيق بعض قوائم الأسماء؛ راجع بيانات الجهات.", "Some name lists could not be checked; review the entities.")
                return
            }
            checkedLines = snapshot.map { line in var item = line; item.detailHash = hashes[line.id]; return item }
            checkedStorage = storage
        } catch is CancellationError { return }
        catch { if !Task.isCancelled { self.error = error.localizedDescription } }
    }
    private func confirm(_ lines: [TransferIssuedLine]) {
        guard current, canConfirm, !checking, !lines.isEmpty, checkedStorage == stored,
              baseline == nil || changes != nil, note.count <= 2000, !BackupManager.shared.isWorking, !ImportCenter.shared.isBusy,
              !StaffImportState.shared.isImporting, var ledger else { return }
        let bare = lines.map { line in var value = line; value.detailHash = nil; return value }
        guard bare == currentLines else { error = tr("تغيّرت بيانات المجموعة؛ أعد تدقيقها.", "Group data changed; review it again."); return }
        let comment = note.trimmingCharacters(in: .whitespacesAndNewlines)
        var recordedChanges = (changes ?? []).map { change in change.title + ": " + change.details.joined(separator: "؛ ") }
        if payerChanged { recordedChanges.append(tr("تغيّرت بيانات الجهة الدافعة.", "Payer details changed.")) }
        let record = TransferIssuedRecord(id: UUID(), month: month, book: category.title, confirmedAt: .now, lines: lines,
            groupCategory: category.rawValue, note: comment.isEmpty ? nil : comment, payerName: payerName, payerIBAN: payerIBAN,
            groupChanges: recordedChanges.isEmpty ? nil : recordedChanges)
        ledger.records.append(record)
        do {
            let data = try JSONEncoder().encode(ledger)
            _ = try TransferIssuance.decode(data)
            stored = data; BackupManager.shared.markChanged(); note = ""; saved = true
        } catch { self.error = tr("تعذّر حفظ تأكيد المجموعة؛ لم يُغيّر السجل.", "Could not save the group confirmation; the ledger was unchanged.") }
    }
}
