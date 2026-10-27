import SwiftUI
import SwiftData
import UIKit

/// حساب جهة تنفيذ مستقل عن الأشهر، يُحدَّث بالاسم عند استيراد ملف IBAN جديد.
@Model
final class ExecutionAccount {
    var key: String
    var entity: String
    var iban: String
    var branch: String
    var swift: String

    init(entity: String, iban: String, branch: String, swift: String) {
        self.key = ArabicText.normalize(entity)
        self.entity = entity
        self.iban = iban
        self.branch = branch
        self.swift = swift
    }
}

@ModelActor
actor ExecutionAccountImporter {
    func merge(_ rows: [ImportedExecutionAccount]) throws -> (added: Int, updated: Int) {
        var existing = Dictionary(try modelContext.fetch(FetchDescriptor<ExecutionAccount>()).map { ($0.key, $0) },
                                  uniquingKeysWith: { first, _ in first })
        var added = 0, updated = 0
        for row in rows {
            let key = ArabicText.normalize(row.entity)
            if let account = existing[key] {
                if account.entity != row.entity || account.iban != row.iban || (!row.branch.isEmpty && account.branch != row.branch)
                    || (!row.swift.isEmpty && account.swift != row.swift) {
                    account.entity = row.entity
                    account.iban = row.iban
                    if !row.branch.isEmpty { account.branch = row.branch }
                    if !row.swift.isEmpty { account.swift = row.swift }
                    updated += 1
                }
            } else {
                let account = ExecutionAccount(entity: row.entity, iban: row.iban, branch: row.branch, swift: row.swift)
                modelContext.insert(account)
                existing[key] = account
                added += 1
            }
        }
        try modelContext.save()
        return (added, updated)
    }
}

/// صفحة الحسابات الدائمة داخل البيانات المحفوظة، مع البحث والنسخ.
struct ExecutionAccountsView: View {
    @Query(sort: \ExecutionAccount.entity) private var accounts: [ExecutionAccount]
    @State private var search = ""

    private var filtered: [ExecutionAccount] {
        let query = ArabicText.normalize(ArabicText.cleanQuery(search))
        guard !query.isEmpty else { return accounts }
        return accounts.filter { ArabicText.normalize($0.entity).contains(query)
            || ArabicText.normalize($0.branch).contains(query)
            || $0.iban.localizedCaseInsensitiveContains(ArabicText.latinDigits(query)) }
    }

    var body: some View {
        List {
            Section {
                if accounts.isEmpty {
                    ContentUnavailableView(tr("لم تُستورد حسابات بعد", "No accounts imported"), systemImage: "building.columns",
                                           description: Text(tr("استورد ملف IBAN من الإعدادات ← استيراد ملفات.", "Import an IBAN file from Settings › Import files.")))
                } else if filtered.isEmpty {
                    ContentUnavailableView.search(text: search)
                } else {
                    ForEach(filtered) { account in
                        VStack(alignment: .leading, spacing: 5) {
                            Text(account.entity).font(.headline)
                            if !account.branch.isEmpty { Text(account.branch).font(.subheadline).foregroundStyle(.secondary) }
                            Text(account.iban).font(.subheadline.monospaced()).textSelection(.enabled).privacySensitive()
                            if !account.swift.isEmpty { Text("SWIFT: \(account.swift)").font(.caption).foregroundStyle(.secondary) }
                        }
                        .padding(.vertical, 5)
                        .contextMenu {
                            Button(tr("نسخ IBAN", "Copy IBAN"), systemImage: "doc.on.doc") { UIPasteboard.general.string = account.iban }
                        }
                    }
                }
            } header: {
                Text(tr("\(Num.format(accounts.count)) جهة", "\(accounts.count) entities"))
            }
        }
        .adaptiveSearchable(text: $search, prompt: tr("ابحث عن جهة أو IBAN", "Search entity or IBAN"))
        .navigationTitle(tr("حسابات جهات التنفيذ", "Execution accounts"))
        .navigationBarTitleDisplayMode(.inline)
    }
}
