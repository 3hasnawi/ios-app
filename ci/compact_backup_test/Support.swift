import Foundation
import SwiftData

func tr(_ ar: String, _ en: String) -> String { en }
struct ImportedEmployee { var items: [String: Double] }
struct ProgressSink { func callAsFunction(_ value: Double) {} }
enum Num { static func digits(_ s: String) -> String { s }; static func format(_ n: Int) -> String { String(n) } }
enum AppSettings {
    static let isEnglish = true
    static let languageKey = "language", appearanceKey = "appearance", numeralsKey = "appNumerals", textSizeKey = "appTextSize"
}
struct TransferLetterCustomizationStore: Codable {
    static let settingsKey = "templates"
    var hasUnifiedTemplates = false
    func validateUnifiedTemplates() throws {}
}
enum CustomTransferTemplate { static let settingsKey = "custom"; static func decode(_ data: Data) throws -> Data { data } }
enum BankReconciliation { static let settingsKey = "reconciliation"; static func decode(_ data: Data) throws -> Data { data } }

enum TransferIssuance { static let settingsKey = "issuance" }
enum TransferReferencePack { static let settingsKey = "references"; static func validateData(_ data: Data) throws -> Data { data } }
enum TransferLetterEntityLinks { static let settingsKey = "links" }
enum TransferHeaderFont { static let boldSettingsKey = "bold", regularSettingsKey = "regular" }
enum ReportSheet { static let headerKey = "report-header" }
enum RestoreSafety { static func prepare(store: URL, includeRefunds: Bool = false) throws { throw CocoaError(.featureUnsupported) } }
enum ImportCenter { static func removeCopy(_ url: URL) {} }

struct RefundSnapshot: Codable { let source: String }
struct RefundStorage {
    let root: URL
    static var local: Self { Self(root: URL(fileURLWithPath: FileManager.default.currentDirectoryPath).appendingPathComponent("test-refunds")) }
}
enum RefundPayrollSearch {
    static func relinkDraft(in storage: RefundStorage, context: ModelContext) throws {}
}
