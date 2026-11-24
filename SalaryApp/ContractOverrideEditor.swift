import SwiftUI

/// اعتماد مبلغ نهائي بديل لشهر واحد مع إعادة فحص أساس الحساب قبل الحفظ.
@MainActor struct ContractOverrideEditor: View {
    struct Request: Identifiable {
        let id = UUID()
        let summary: BankStatementSummary
        let editing: Bool
    }
    let request: Request
    private var summary: BankStatementSummary { request.summary }
    @Environment(\.dismiss) private var dismiss
    @State private var input: String
    @State private var error: String?
    @State private var working = false

    init(request: Request) {
        self.request = request
        let value = request.summary.contractContribution
        _input = State(initialValue: value.formatted(.number.grouping(.never).precision(.fractionLength(0)).locale(Locale(identifier: "en_US_POSIX"))))
    }
    var body: some View {
        NavigationStack {
            Form {
                Section(tr("الحساب التلقائي", "Automatic calculation")) {
                    LabeledContent(tr("الشهر", "Month"), value: summary.month)
                    LabeledContent(tr("بعد إضافة التوقيفات", "After retirement deductions"), value: Money.format(summary.calculatedContractContribution))
                    if summary.missingContractNominal > 0 {
                        Text(tr("ينقص الراتب الاسمي لـ \(Num.format(summary.missingContractNominal)) من العقود. حُسبت نسبة ١٢٪ من الاسمي المتوفر فقط، ثم أُضيفت التوقيفات.", "Nominal pay is missing for \(Num.format(summary.missingContractNominal)) contracts. The 12% uses available nominal pay only, plus retirement deductions."))
                            .font(.footnote).foregroundStyle(.orange)
                    }
                    if summary.contractOverrideStale {
                        Label(tr("تغيّر أساس العقود منذ الحفظ السابق؛ راجع المبلغ وأعد اعتماده.", "The contract basis changed since the previous save; review and confirm the amount."), systemImage: "exclamationmark.triangle").foregroundStyle(.orange)
                    }
                }
                Section {
                    if request.editing {
                        RefundAmountField(tr("المبلغ النهائي بالدينار", "Final amount in dinars"), identifier: "contractFinalAmount", text: $input)
                            .frame(minHeight: 44)
                    } else {
                        LabeledContent(tr("المبلغ للاعتماد", "Amount to approve"), value: Money.format(summary.contractContribution))
                    }
                    if let error { Text(error).foregroundStyle(.red) }
                    Button(request.editing ? tr("حفظ واعتماد المبلغ", "Save and approve amount") : tr("اعتماد", "Approve")) { Task { await save(reset: false) } }.disabled(working)
                    if request.editing && summary.manualContractOverride != nil {
                        Button(tr("العودة للحساب التلقائي", "Return to automatic calculation"), role: .destructive) { Task { await save(reset: true) } }.disabled(working)
                    }
                } footer: {
                    Text(tr("يُستخدم المبلغ المعتمد لهذا الشهر في كشف البنك ومبالغ المطابقة المقترحة وكتاب العقود.", "The approved monthly amount is used in the bank statement, suggested reconciliation amounts and contract letter."))
                }
            }
            .navigationTitle(tr("مبلغ العقود", "Contract amount"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement: .cancellationAction) { Button(tr("إلغاء", "Cancel")) { dismiss() }.disabled(working) } }
        }
    }
    private func save(reset: Bool) async {
        guard !working else { return }
        let amount: Double?
        if reset { amount = nil }
        else if !request.editing { amount = summary.contractContribution }
        else {
            guard let value = ArabicText.number(from: input), value.isFinite, value >= 0, value.rounded() == value, value <= Double(BankReconciliation.maximumAmount) else {
                error = tr("أدخل مبلغاً صحيحاً غير سالب بالدينار.", "Enter a nonnegative whole number of dinars."); return
            }
            amount = value
        }
        working = true; defer { working = false }
        do {
            let revision = BankStatementCache.shared.revision(for: summary.month)
            let container = SharedModel.container
            let fresh = try await BankStatementEngine(modelContainer: container).summary(month: summary.month)
            guard fresh.contractNominal == summary.contractNominal, fresh.contractRetirement == summary.contractRetirement,
                  fresh.contractCount == summary.contractCount, fresh.missingContractNominal == summary.missingContractNominal else {
                throw NSError(domain: "ContractOverride", code: 1, userInfo: [NSLocalizedDescriptionKey: tr("تغيّر أساس العقود؛ أعد فتح النافذة وراجع المبلغ.", "The contract basis changed; reopen the editor and review the amount.")])
            }
            if amount != nil {
                try await TransferLetterFigureEngine(modelContainer: container).validateContractBasis(fresh)
            }
            let stored = try ContractOverrides.currentData()
            guard BankStatementCache.shared.revision(for: summary.month) == revision,
                  !ImportCenter.shared.isBusy, !StaffImportState.shared.isImporting, !BackupManager.shared.isWorking,
                  try ContractOverrides.decode(stored)[summary.month] == summary.manualContractOverride else {
                throw NSError(domain: "ContractOverride", code: 2, userInfo: [NSLocalizedDescriptionKey: tr("تغيّرت البيانات؛ أعد فتح مبلغ العقود للمراجعة.", "Data changed. Reopen the contract amount to review it.")])
            }
            let entry = amount.map { ContractOverride(total: $0, nominal: fresh.contractNominal, retirement: fresh.contractRetirement, count: fresh.contractCount, missing: fresh.missingContractNominal) }
            let encoded = try ContractOverrides.setting(stored, month: summary.month, value: entry)
            UserDefaults.standard.set(encoded, forKey: ContractOverrides.settingsKey)
            BackupManager.shared.markSettingChanged(ContractOverrides.settingsKey)
            BankStatementCache.shared.invalidate()
            dismiss()
        } catch { self.error = error.localizedDescription }
    }
}
