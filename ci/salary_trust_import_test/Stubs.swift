import Foundation
func tr(_ ar:String,_ en:String)->String { ar }
enum Num { static func digits(_ s:String)->String{s};static func format(_ n:Int)->String{String(n)} }
enum AppSettings {
    static let isEnglish=false
    static let languageKey="test-language", appearanceKey="test-appearance", numeralsKey="test-numerals", textSizeKey="test-size"
}
enum ImportError:Error { case cannotOpen(String) }
enum BackupError:Error { case notBackup,dataLossRisk }
struct TransferLetterCustomizationStore:Codable { static let settingsKey="test-unified";var hasUnifiedTemplates=false }
enum CustomTransferTemplate { static let settingsKey="test-custom" }
enum FundingApproval { static let settingsKey="test-funding" }
enum ContractOverrides { static let settingsKey="test-contract" }
enum BankReconciliation { static let settingsKey="test-bank" }
enum TransferRules { static let settingsKey="test-rules" }
enum TransferIssuance { static let settingsKey="test-issuance" }
enum TransferReferencePack { static let settingsKey="test-reference" }
enum TransferLetterEntityLinks { static let settingsKey="test-links" }
enum TransferHeaderFont { static let boldSettingsKey="test-bold",regularSettingsKey="test-regular" }
struct ReportSheet {
    enum Kind { case serial,text,amount }
    struct Column { let title:String;let width:Double;let kind:Kind }
    let header:String,title:String;let columns:[Column];let rows:[[String]];let amounts:[Double]
    var showTotal=true
    static let headerKey="fictional-trust-header",defaultHeader="قيادة وهمية"
}
#if os(Linux)
extension URL {
    func startAccessingSecurityScopedResource()->Bool{false}
    func stopAccessingSecurityScopedResource(){}
}
#endif
