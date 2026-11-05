import SwiftUI

/// قائمة قابلة للبحث بدلاً من قائمة منبثقة طويلة يصعب تمريرها على الآيفون.
struct SavedLetterAccountPicker: View {
    let accounts: [SavedExecutionAccount]
    let choose: (SavedExecutionAccount) -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var query = ""

    private var matches: [SavedExecutionAccount] {
        let term = ArabicText.normalize(ArabicText.cleanQuery(query))
        let digits = ArabicText.latinDigits(query).trimmingCharacters(in: .whitespacesAndNewlines)
        let sorted = accounts.sorted { $0.entity.localizedStandardCompare($1.entity) == .orderedAscending }
        guard !term.isEmpty || !digits.isEmpty else { return sorted }
        return sorted.filter { account in
            (!term.isEmpty && (ArabicText.normalize(account.entity).contains(term) || ArabicText.normalize(account.branch).contains(term))) ||
            (!digits.isEmpty && (account.iban.localizedCaseInsensitiveContains(digits) || (account.localAccount ?? "").localizedCaseInsensitiveContains(digits)))
        }
    }

    var body: some View {
        NavigationStack {
            List {
                if matches.isEmpty { Text(tr("لا توجد حسابات مطابقة.", "No matching accounts.")).foregroundStyle(.secondary) }
                ForEach(matches) { account in
                    Button {
                        choose(account)
                        dismiss()
                    } label: {
                        VStack(alignment: .leading, spacing: 4) {
                            Text(account.entity).font(.headline).foregroundStyle(.primary)
                            if !account.branch.isEmpty { Text(account.branch).font(.caption).foregroundStyle(.secondary) }
                            if !account.iban.isEmpty { Text(account.iban).font(.caption.monospaced()).foregroundStyle(.secondary).privacySensitive() }
                            if let local = account.localAccount, !local.isEmpty { Text(local).font(.caption.monospaced()).foregroundStyle(.secondary).privacySensitive() }
                        }.padding(.vertical, 4)
                    }
                }
            }
            .adaptiveSearchable(text: $query, prompt: tr("ابحث عن الجهة أو المصرف أو رقم الحساب", "Search entity, bank or account number"))
            .navigationTitle(tr("الحسابات المحفوظة", "Saved accounts"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement: .cancellationAction) { Button(tr("إلغاء", "Cancel")) { dismiss() } } }
        }
    }
}
