import SwiftUI
import SwiftData
import UIKit

/// النموذج السابق يبقى ضمن مخطط SwiftData حتى تفتح قواعد بيانات الإصدارات المثبّتة.
@Model
final class ExecutionAccount {
    #Index<ExecutionAccount>([\.key])

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

/// عدد الجهات صغير؛ تخزينها كبيانات واحدة يتجنب خلل قراءة النموذج من SwiftData.
nonisolated struct SavedExecutionAccount: Codable, Sendable, Identifiable {
    let entity: String
    let iban: String
    let branch: String
    let swift: String

    var key: String { ArabicText.normalize(entity) }
    var id: String { key }
}

nonisolated enum ExecutionAccountStore {
    static let storageKey = "executionAccountsData"

    static func decode(_ data: Data) throws -> [SavedExecutionAccount] {
        if data.isEmpty { return [] }
        return try JSONDecoder().decode([SavedExecutionAccount].self, from: data)
    }

    static func load() throws -> [SavedExecutionAccount] {
        try decode(UserDefaults.standard.data(forKey: storageKey) ?? Data())
    }

    static func encode(_ rows: [SavedExecutionAccount]) throws -> Data {
        try JSONEncoder().encode(rows)
    }

    static func saveEncoded(_ data: Data) {
        UserDefaults.standard.set(data, forKey: storageKey)
    }

    static func merge(_ rows: [ImportedExecutionAccount]) throws -> (added: Int, updated: Int) {
        var saved = try load()
        var positions = Dictionary(uniqueKeysWithValues: saved.enumerated().map { ($0.element.key, $0.offset) })
        var added = 0, updated = 0
        for row in rows {
            let key = ArabicText.normalize(row.entity)
            if let index = positions[key] {
                let previous = saved[index]
                let next = SavedExecutionAccount(entity: row.entity, iban: row.iban,
                    branch: row.branch.isEmpty ? previous.branch : row.branch,
                    swift: row.swift.isEmpty ? previous.swift : row.swift)
                if previous.entity != next.entity || previous.iban != next.iban || previous.branch != next.branch || previous.swift != next.swift {
                    saved[index] = next
                    updated += 1
                }
            } else {
                positions[key] = saved.count
                saved.append(SavedExecutionAccount(entity: row.entity, iban: row.iban, branch: row.branch, swift: row.swift))
                added += 1
            }
        }
        saveEncoded(try encode(saved))
        return (added, updated)
    }
}

/// صفحة الحسابات الدائمة داخل البيانات المحفوظة، مع البحث والنسخ.
struct ExecutionAccountsView: View {
    @AppStorage(ExecutionAccountStore.storageKey) private var accountData = Data()
    @State private var search = ""

    private var accounts: [SavedExecutionAccount] {
        ((try? ExecutionAccountStore.decode(accountData)) ?? [])
            .sorted { $0.entity.localizedStandardCompare($1.entity) == .orderedAscending }
    }

    private var filtered: [SavedExecutionAccount] {
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
