import SwiftUI

/// اعتماد مبلغ نهائي بديل لشهر واحد مع إعادة فحص أساس الحساب قبل الحفظ.
@MainActor struct ContractOverrideEditor: View {
    let summary: BankStatementSummary
    @Environment(\.dismiss) private var dismiss
    @State private var input: String
    @State private var error: String?
    @State private var working = false

    init(summary: BankStatementSummary) {
        self.summary = summary
        let value = summary.manualContractOverride?.total ?? summary.calculatedContractContribution
        _input = State(initialValue: value.formatted(.number.grouping(.never).precision(.fractionLength(0)).locale(Locale(identifier: "en_US_POSIX"))))
    }
    var body: some View {
        NavigationStack {
            Form {
                Section(tr("الحساب التلقائي", "Automatic calculation")) {
                    LabeledContent(tr("الشهر", "Month"), value: summary.month)
                    LabeledContent(tr("بعد إضافة التوقيفات", "After retirement deductions"), value: Money.format(summary.calculatedContractContribution))
                    if summary.contractOverrideStale {
                        Label(tr("تغيّر أساس العقود منذ الحفظ السابق؛ راجع المبلغ وأعد اعتماده.", "The contract basis changed since the previous save; review and confirm the amount."), systemImage: "exclamationmark.triangle").foregroundStyle(.orange)
                    }
                }
                Section {
                    TextField(tr("المبلغ النهائي بالدينار", "Final amount in dinars"), text: $input).keyboardType(.numberPad)
                    if let error { Text(error).foregroundStyle(.red) }
                    Button(tr("حفظ المبلغ", "Save amount")) { Task { await save(reset: false) } }.disabled(working)
                    if summary.manualContractOverride != nil {
                        Button(tr("العودة للحساب التلقائي", "Return to automatic calculation"), role: .destructive) { Task { await save(reset: true) } }.disabled(working)
                    }
                } footer: {
                    Text(tr("يُستخدم المبلغ في كشف البنك وكتاب العقود، ويظهر فرق التعديل في الكتاب.", "The amount is used in the bank statement and contract letter, with the adjustment shown in the letter."))
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
        else {
            guard let value = ArabicText.number(from: input), value.isFinite, value >= 0, value.rounded() == value, value <= 9_007_199_254_740_991 else {
                error = tr("أدخل مبلغاً صحيحاً غير سالب بالدينار.", "Enter a nonnegative whole number of dinars."); return
            }
            amount = value
        }
        working = true; defer { working = false }
        do {
            let container = SharedModel.container
            let fresh = try await BankStatementEngine(modelContainer: container).summary(month: summary.month)
            guard fresh.contractNominal == summary.contractNominal, fresh.contractRetirement == summary.contractRetirement,
                  fresh.contractCount == summary.contractCount, fresh.missingContractNominal == summary.missingContractNominal else {
                throw NSError(domain: "ContractOverride", code: 1, userInfo: [NSLocalizedDescriptionKey: tr("تغيّر أساس العقود؛ أعد فتح النافذة وراجع المبلغ.", "The contract basis changed; reopen the editor and review the amount.")])
            }
            if amount != nil {
                guard fresh.contractCount > 0, fresh.missingContractNominal == 0 else {
                    throw NSError(domain: "ContractOverride", code: 2, userInfo: [NSLocalizedDescriptionKey: tr("أكمل مراجعة اسمي العقود قبل حفظ المبلغ اليدوي.", "Review missing contract nominal amounts before saving.")])
                }
                _ = try await TransferLetterFigureEngine(modelContainer: container).figures(month: summary.month)
            }
            let entry = amount.map { ContractOverride(total: $0, nominal: fresh.contractNominal, retirement: fresh.contractRetirement, count: fresh.contractCount, missing: fresh.missingContractNominal) }
            let encoded = try ContractOverrides.setting(try ContractOverrides.currentData(), month: summary.month, value: entry)
            UserDefaults.standard.set(encoded, forKey: ContractOverrides.settingsKey)
            BackupManager.shared.markSettingChanged(ContractOverrides.settingsKey)
            BankStatementCache.shared.invalidate()
            dismiss()
        } catch { self.error = error.localizedDescription }
    }
}
