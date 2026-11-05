import Foundation
func tr(_ a: String, _ b: String) -> String { a }
enum Num { static func format(_ n: Int) -> String { String(n) } }
enum MonthName { static func sortKey(_ s: String) -> Int { 202601 } }
enum WorkbookSheetNames { static func years(in text: String) -> Set<Int> { [] } }
struct SavedExecutionAccount { let entity: String; let iban: String; let branch: String; let swift: String }
enum TransferRules { enum Category { case anbar }; static func classify(_ a: SavedExecutionAccount) -> Category { .anbar } }
enum PayrollColumnSnapshot { static func encode(headers: [String], values: [String]) -> Data? { try? JSONEncoder().encode(values) } }
