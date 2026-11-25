precondition(WorkReport.pins(saved: nil, legacyHidden: "") == WorkReport.statements)
precondition(WorkReport.pins(saved: nil, legacyHidden: "food|health") == [.entities, .fund, .commandDeductions])
precondition(WorkReport.pins(saved: nil, legacyHidden: "entities|food|fund|health") == [.commandDeductions])
precondition(WorkReport.pins(saved: "", legacyHidden: "") == [])
precondition(WorkReport.pins(saved: "salaryAudit|unknown|transferFiles|salaryAudit", legacyHidden: "")
             == [.salaryAudit, .transferFiles])
let expanded: [WorkReport] = [.salaryTrust, .fundingExpenses, .employeeDeductions, .payrollItems, .refunds]
precondition(WorkReport.sections == [.reports, .transferFiles, .deductionAudit, .salaryAudit] + expanded)
let selected = [.fund, .salaryAudit, .commandDeductions] + expanded
precondition(WorkReport.pins(saved: selected.map(\.rawValue).joined(separator: "|"), legacyHidden: "") == selected)
precondition(Set(WorkReport.sections + WorkReport.statements) == Set(WorkReport.allCases))
