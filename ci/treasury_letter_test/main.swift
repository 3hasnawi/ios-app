import Foundation
func reject(_ body: () throws -> Void) {
    do { try body(); fatalError("invalid amount accepted") } catch {}
}
let stamps = 1000.0
let schools = 600.0
let combined = try TreasuryLetterRules.total([stamps, schools])
precondition(combined == 1600)
for invalid in [0.0, -1.0, 1.5, Double.infinity, Double.nan, 9_007_199_254_740_992.0] {
    precondition(!TreasuryLetterRules.validAmount(invalid))
    reject { _ = try TreasuryLetterRules.total([invalid]) }
}
let rows = [
    ReportRow(id: 0, name: "منتسب وهمي", rank: "شرطي", workplace: "", amount: 1000, note: ""),
    ReportRow(id: 1, name: "منتسب وهمي ثان", rank: "مفوض", workplace: "", amount: 600, note: "")
]
let sum = try TreasuryLetterRules.validateRows(rows, summary: .init(count: 2, total: 1600))
precondition(sum == 1600)
reject { _ = try TreasuryLetterRules.validateRows(rows, summary: .init(count: 1, total: 1600)) }
reject { _ = try TreasuryLetterRules.validateRows(rows, summary: .init(count: 2, total: 1599)) }
let noRank = [ReportRow(id: 0, name: "منتسب وهمي", rank: "", workplace: "", amount: 1000, note: "")]
reject { _ = try TreasuryLetterRules.validateRows(noRank, summary: .init(count: 1, total: 1000)) }
let withoutRank = try TreasuryLetterRules.validateRows(noRank, summary: .init(count: 1, total: 1000), requireRank: false)
precondition(withoutRank == 1000)
print("Treasury letter amount and row checks passed")
