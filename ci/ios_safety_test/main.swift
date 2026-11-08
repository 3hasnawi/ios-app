import Foundation

func tr(_ ar: String, _ en: String) -> String { ar }

func expectFailure(_ expected: PayrollReplacementError, _ action: () throws -> Void) {
    do {
        try action()
        fatalError("Expected a safe rejection")
    } catch let error as PayrollReplacementError {
        precondition(error == expected, "Wrong rejection: \(error)")
    } catch {
        fatalError("Unexpected error: \(error)")
    }
}

let current = ["أيلول ٢٠٢٦", "تشرين الاول ٢٠٢٦"]
let retained = try PayrollRetention.monthsToKeep(existing: current, incoming: "تشرين الثاني ٢٠٢٦")
precondition(retained == Set(["تشرين الاول ٢٠٢٦", "تشرين الثاني ٢٠٢٦"]))
expectFailure(.olderMonth) { _ = try PayrollRetention.monthsToKeep(existing: current, incoming: "آب ٢٠٢٦") }
expectFailure(.ambiguousMonth) { _ = try PayrollRetention.monthsToKeep(existing: ["تشرين الاول 2026"], incoming: "أكتوبر 2026") }
expectFailure(.unknownMonth) { _ = try PayrollRetention.monthsToKeep(existing: current, incoming: "أيلول 99999999999999999999") }
precondition(MonthName.sortKey("أيلول 99999999999999999999") == 0)
precondition(MonthName.sortKey("أيلول ٢٠٢٦") == 202609)
precondition(ArabicText.cleanQuery("أحمد، علي.؟") == "أحمد علي")
precondition(ArabicText.cleanQuery("مديرية/فرع - الرمادي") == "مديرية/فرع - الرمادي")
precondition(ArabicText.normalize(ArabicText.cleanQuery("أَحمد، علي.")) == "احمد علي")
print("iOS payroll retention and voice-search normalization tests passed with fictional data")

for (input, expected) in [("٣٬٥٠٠", 3500.0), ("1,234.50", 1234.5), ("-12.5", -12.5), ("1.2e3", 1200.0)] {
    precondition(ArabicText.number(from: input) == expected)
}
for input in ["3500/7000", "3500 7000", "1,23", "inf", "nan", "1e999", "مبلغ 3500", "3500%"] {
    precondition(ArabicText.number(from: input) == nil, "Ambiguous amount accepted")
}
print("Strict monetary input tests passed")
