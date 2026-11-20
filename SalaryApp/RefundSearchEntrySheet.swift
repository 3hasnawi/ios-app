import SwiftUI
import SwiftData

struct RefundSearchTarget: Identifiable, Sendable {
    let id: PersistentIdentifier
    let key: String
    let month: String

    @MainActor func isIn(_ draft: RefundDraft) -> Bool {
        draft.rows.contains {
            $0.id == id || (month != $0.candidate.month && ArabicText.matchKey($0.candidate.person.name) == key)
        }
    }
}

struct RefundSearchEntrySheet: View {
    let target: RefundSearchTarget
    @Environment(\.dismiss) private var dismiss
    @Environment(\.modelContext) private var context
    private let draft = RefundDraft.shared
    @State private var candidate: RefundCandidate?
    @State private var amount = ""
    @State private var error: String?
    @State private var saving = false

    var body: some View {
        NavigationStack {
            Form {
                if let candidate {
                    let person = candidate.person
                    Section {
                        Text(person.name).font(.headline)
                        LabeledContent(tr("شهر الرواتب", "Payroll month"), value: candidate.month)
                        LabeledContent(tr("المصرف", "Bank"), value: person.bank)
                        LabeledContent("IBAN", value: "…" + person.iban.suffix(4)).privacySensitive()
                        LabeledContent(tr("سويفت المنتسب", "Employee SWIFT"), value: person.payrollBIC)
                    }
                    Section {
                        TextField(tr("مبلغ الإعادة بالدينار", "Refund amount in IQD"), text: $amount)
                            .keyboardType(.numberPad)
                            .accessibilityIdentifier("searchRefundAmount")
                        Button(draft.rows.contains { $0.id == candidate.id }
                            ? tr("حفظ ومتابعة البحث", "Save and continue searching")
                            : tr("إضافة ومتابعة البحث", "Add and continue searching")) {
                            save(candidate)
                        }
                        .accessibilityIdentifier("saveSearchRefund")
                        .disabled(EmployeeRefund.amount(amount) == nil || !eligible(candidate) || saving || draft.working)
                    }
                } else if error == nil {
                    ProgressView()
                }
                if let error { Text(error).foregroundStyle(.red) }
            }
            .navigationTitle(tr("مبلغ الإعادة", "Refund amount"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(tr("إلغاء", "Cancel")) { dismiss() }.disabled(saving)
                }
            }
        }
        .interactiveDismissDisabled(saving)
        .presentationDetents([.medium, .large])
        .presentationDragIndicator(.visible)
        .task(id: target.id) {
            let month = draft.month ?? target.month, container = context.container, target = target
            do {
                let result = try await Task.detached {
                    try await RefundPayrollSearch(modelContainer: container).searchResult(target, month: month)
                }.value
                try Task.checkCancellation()
                candidate = result
                amount = draft.rows.first { $0.id == result.id }?.amount ?? ""
                if !eligible(result) {
                    error = tr("راجع الآيبان والسويفت في رواتب هذا المنتسب.", "Check the IBAN and SWIFT in this employee’s payroll row.")
                }
            } catch {
                if !Task.isCancelled { self.error = error.localizedDescription }
            }
        }
    }
    private func eligible(_ candidate: RefundCandidate) -> Bool {
        guard let code = EmployeeRefund.bankCode(candidate.person.iban) else { return false }
        return EmployeeRefund.validPayrollBIC(candidate.person.payrollBIC, bankCode: code)
    }
    private func save(_ candidate: RefundCandidate) {
        guard !saving else { return }
        saving = true
        let container = context.container, value = amount
        Task {
            defer { saving = false }
            do {
                try await Task.detached {
                    try await RefundPayrollSearch(modelContainer: container).verify([candidate], month: candidate.month)
                }.value
                try draft.add(candidate, amount: value)
                dismiss()
            } catch { self.error = error.localizedDescription }
        }
    }
}
