import Foundation
func tr(_ ar:String,_ en:String)->String { ar }
enum Num { static func digits(_ s:String)->String{s};static func format(_ n:Int)->String{String(n)} }
enum AppSettings {
    static let isEnglish=false
    static let languageKey="test-language", appearanceKey="test-appearance", numeralsKey="test-numerals", textSizeKey="test-size"
}
struct ProgressSink:Sendable { func callAsFunction(_ value:Double) {} }
enum Color { case indigo,blue,orange,green,teal,gray }
struct SavedExecutionAccount { let entity:String,iban:String,branch:String,swift:String }
enum BackupError:Error { case notBackup,dataLossRisk }
struct TransferLetterCustomizationStore:Codable { static let settingsKey="test-unified";var hasUnifiedTemplates=false }
enum CustomTransferTemplate { static let settingsKey="test-custom" }
enum FundingApproval { static let settingsKey="test-funding" }
enum ContractOverrides { static let settingsKey="test-contract" }
enum BankReconciliation { static let settingsKey="test-bank" }
enum TransferRules { static let settingsKey="test-rules";enum Category { case anbar };static func classify(_ account:SavedExecutionAccount)->Category{.anbar} }
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
func autoreleasepool<T>(invoking body:()throws->T) rethrows ->T { try body() }
extension URL {
    func startAccessingSecurityScopedResource()->Bool{false}
    func stopAccessingSecurityScopedResource(){}
}
#endif

@MainActor final class BackupManager {
    static let shared=BackupManager()
    var isWorking=false, changes=0
    func markChanged(){changes += 1}
}
@MainActor final class ImportCenter { static let shared=ImportCenter();var isBusy=false }
@MainActor final class StaffImportState { static let shared=StaffImportState();var isImporting=false }
struct OfficialBook { let id:UUID }
@MainActor final class OfficialBookStore { static let shared=OfficialBookStore();var books:[OfficialBook]=[] }
enum OfficialBookFiles { static func validate(_ book:OfficialBook)throws{} }
