import Foundation

let keys = ["صندوق الشهداء", "النشاط الرياضي", "الرعاية الصحية"]
var november = CommandDeductionTotals()
try november.add(raw: .init(headers: keys, values: ["٥٬٠٠٠", "4000", "3500"]), details: [:], items: [:])
try november.add(raw: .init(headers: keys.reversed(), values: ["6850", "", "2000"]), details: [:], items: [:])
let novemberTotals = try november.amounts()
precondition(novemberTotals == [.fund: 7000, .sports: 4000, .health: 10350]) // includes irregular health amount
var october = CommandDeductionTotals()
try october.add(raw: nil, details: ["رعاية صحية": "3500", "نشاط رياضي": "0"],
                items: ["صندوق شهداء الشرطة": 4000, "رعاية صحية": 3500, "نشاط صحي": 9000])
let octoberTotals = try october.amounts()
precondition(octoberTotals == [.fund: 4000, .sports: 0, .health: 3500]) // separate month, no double counting
var absent = CommandDeductionTotals()
do { _ = try absent.amounts(); fatalError("Empty payroll accepted") } catch TransferLetterFigureError.missingPayroll {}
try absent.add(raw: nil, details: [:], items: ["الرعاية الصحية": 3500])
do { _ = try absent.amounts(); fatalError("Missing columns reported as zero") } catch TransferLetterFigureError.missingColumns {}
for invalid in ["-٥", "3,5", "3500/7000", "0.5"] {
    do {
        try absent.add(raw: .init(headers: keys, values: ["5000", "4000", invalid]), details: [:], items: [:])
        fatalError("Invalid amount accepted")
    } catch TransferLetterFigureError.invalidAmount {}
}
print("Actual monthly totals, missing payroll, aliases, duplicates, irregular health and invalid amounts passed")
