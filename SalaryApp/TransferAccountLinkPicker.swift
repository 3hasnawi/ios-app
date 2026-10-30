import SwiftUI

/// اختيار صريح؛ لا يُخمّن التطبيق الحساب اعتماداً على تشابه الأسماء.
struct TransferAccountLinkPicker: View {
    let entity: TransferEntity
    let accounts: [SavedExecutionAccount]
    let selectedKey: String?
    let save: (String?) -> Bool
    @Environment(\.dismiss) private var dismiss
    @State private var query = ""
    @State private var pending: SavedExecutionAccount?
    @State private var unlink = false
    @State private var error = false

    private var filtered: [SavedExecutionAccount] {
        let term = ArabicText.normalize(ArabicText.cleanQuery(query))
        let sorted = accounts.sorted { $0.entity.localizedStandardCompare($1.entity) == .orderedAscending }
        guard !term.isEmpty else { return sorted }
        return sorted.filter { ArabicText.normalize($0.entity).contains(term) || ArabicText.normalize($0.branch).contains(term)
            || $0.iban.localizedCaseInsensitiveContains(ArabicText.latinDigits(query)) }
    }
    var body: some View {
        List {
            Section {
                LabeledContent(entity.payrollRecipient == nil ? tr("جهة الاستقطاع", "Deduction entity") : tr("بند الرواتب", "Payroll item"), value: entity.group.title)
                Text(tr("اختر الحساب المطابق من قائمة الحسابات المستوردة، وتحقق من اسم المستفيد والمصرف والرقم.", "Select the matching imported account and verify the beneficiary, bank and account number."))
                    .font(.footnote).foregroundStyle(.secondary)
            }
            Section {
                if filtered.isEmpty { Text(tr("لا توجد حسابات مطابقة.", "No matching accounts.")).foregroundStyle(.secondary) }
                ForEach(filtered) { account in
                    Button { pending = account } label: {
                        VStack(alignment: .leading, spacing: 5) {
                            HStack {
                                Text(account.entity).font(.headline).foregroundStyle(.primary)
                                Spacer()
                                if account.key == selectedKey { Image(systemName: "checkmark.circle.fill").foregroundStyle(.green) }
                            }
                            if !account.branch.isEmpty { Text(account.branch).font(.caption).foregroundStyle(.secondary) }
                            if !account.iban.isEmpty { Text(account.iban).font(.caption.monospaced()).foregroundStyle(.secondary).privacySensitive() }
                            if let local = account.localAccount { Text(tr("حساب محلي: \(local)", "Local account: \(local)")).font(.caption.monospaced()).foregroundStyle(.secondary).privacySensitive() }
                            if !account.swift.isEmpty { Text("SWIFT: \(account.swift)").font(.caption).foregroundStyle(.secondary) }
                        }.padding(.vertical, 4)
                    }
                }
            } header: { Text(tr("الحسابات المحفوظة", "Saved accounts")) }
            Section {
                Button(entity.payrollRecipient == nil ? tr("إزالة الربط واستخدام الاسم المطابق", "Remove link and match by name") : tr("إزالة ربط الحساب", "Remove account link")) { unlink = true }
            } footer: { Text(tr("بعد الربط تعود إلى ملفات التحويل لمراجعة التصنيف والمبالغ. لا ينشئ الربط تحويلاً.", "After linking, return to transfer files to review classification and amounts. Linking does not create a transfer.")) }
        }
        .adaptiveSearchable(text: $query, prompt: tr("ابحث عن الجهة أو المصرف أو IBAN", "Search entity, bank or IBAN"))
        .navigationTitle(tr("ربط حساب الجهة", "Link beneficiary account"))
        .navigationBarTitleDisplayMode(.inline)
        .toolbar { ToolbarItem(placement: .cancellationAction) { Button(tr("إلغاء", "Cancel")) { dismiss() } } }
        .alert(tr("تأكيد ربط الحساب؟", "Confirm account link?"), isPresented: Binding(get: { pending != nil }, set: { if !$0 { pending = nil } })) {
            Button(tr("ربط الحساب", "Link account")) {
                guard let account = pending else { return }
                if save(account.key) { pending = nil; dismiss() } else { pending = nil; error = true }
            }
            Button(tr("إلغاء", "Cancel"), role: .cancel) { pending = nil }
        } message: {
            if let account = pending {
                Text(tr("المصدر: \(entity.group.title)\nالحساب: \(account.entity)\nالمصرف: \(account.branch)\nIBAN: \(account.iban.isEmpty ? "—" : account.iban)\nالحساب المحلي: \(account.localAccount ?? "—")", "Source: \(entity.group.title)\nAccount: \(account.entity)\nBank: \(account.branch)\nIBAN: \(account.iban.isEmpty ? "—" : account.iban)\nLocal account: \(account.localAccount ?? "—")"))
            }
        }
        .alert(tr("إزالة الربط؟", "Remove link?"), isPresented: $unlink) {
            Button(tr("إزالة الربط", "Remove link"), role: .destructive) { if save(nil) { dismiss() } else { error = true } }
            Button(tr("إلغاء", "Cancel"), role: .cancel) { }
        } message: { Text(entity.payrollRecipient == nil ? tr("سيبحث التطبيق مجدداً عن حساب مطابق لاسم جهة الاستقطاع.", "The app will again look for an account matching the deduction entity name.") : tr("سيحتاج بند الرواتب إلى اختيار حساب من جديد.", "The payroll item will need an account selected again.")) }
        .alert(tr("تعذّر حفظ الربط", "Could not save link"), isPresented: $error) {
            Button(tr("حسناً", "OK"), role: .cancel) { }
        } message: { Text(tr("تحقق من انتهاء الاستيراد أو النسخ، ثم أعد فتح ملفات التحويل وحاول مجدداً.", "Wait for any import or backup to finish, reopen transfer files, and try again.")) }
    }
}
