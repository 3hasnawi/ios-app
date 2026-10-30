import Foundation

/// روابط المستخدم تشترك في مخزن روابط الرواتب لتدخل النسخ الاحتياطية ولقطات التصدير الموجودة.
nonisolated enum TransferAccountLinks {
    static let prefix = "deduction|"
    static func sourceKey(_ title: String) -> String { prefix + ArabicText.normalize(title) }
    static func decode(_ data: Data) throws -> [String: String] {
        if data.isEmpty { return [:] }
        return try JSONDecoder().decode([String: String].self, from: data)
    }
    static func selectedKey(title: String, links: [String: String]) -> String {
        links[sourceKey(title)] ?? ArabicText.normalize(title)
    }
    static func resolve(title: String, accounts: [String: SavedExecutionAccount], links: [String: String]) -> SavedExecutionAccount? {
        accounts[selectedKey(title: title, links: links)]
    }
    static func authorizes(title: String, account: SavedExecutionAccount, data: Data) -> Bool {
        guard let links = try? decode(data) else { return false }
        return !ArabicText.normalize(title).isEmpty && selectedKey(title: title, links: links) == account.key
    }
    static func allowsDirectAnbar(title: String, data: Data) -> Bool {
        guard TransferRules.isAnbarBankSource(title), let links = try? decode(data) else { return false }
        return links[sourceKey(title)] == nil
    }
}
