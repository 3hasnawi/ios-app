import SwiftUI

extension WorkReport {
    var title: String {
        switch self {
        case .reports: tr("التقارير", "Reports")
        case .transferFiles: tr("ملفات التحويل", "Transfer files")
        case .deductionAudit: tr("تدقيق الاستقطاعات", "Deduction audit")
        case .salaryAudit: tr("تدقيق الرواتب", "Payroll audit")
        case .salaryTrust: tr("أمانات الرواتب", "Salary trust")
        case .fundingExpenses: tr("المصروفات والتمويل", "Expenses and funding")
        case .employeeDeductions: tr("كشف استقطاعات موظف", "Employee deduction statement")
        case .payrollItems: tr("بنود الرواتب", "Payroll items")
        case .refunds: tr("إعادة المبالغ", "Employee refunds")
        case .commandDeductions: tr("كشف استقطاعات القيادة", "Command deductions statement")
        case .entities: tr("جهات التنفيذ", "Execution entities")
        case .food: tr("شركة عراق الجوال", "Iraq Mobile")
        case .fund: tr("صندوق الشهداء", "Martyrs fund")
        case .health: tr("الرعاية الصحية", "Health care")
        }
    }
    var icon: String {
        switch self {
        case .reports: "doc.text"
        case .transferFiles: "arrow.left.arrow.right"
        case .deductionAudit: "checkmark.shield"
        case .salaryAudit: "checklist"
        case .salaryTrust: "tray.full"
        case .fundingExpenses: "list.bullet.rectangle.portrait"
        case .employeeDeductions: "person.text.rectangle"
        case .payrollItems: "list.bullet.rectangle"
        case .refunds: "arrow.uturn.backward.circle"
        case .commandDeductions: "tablecells"
        case .entities: "building.columns"
        case .food: "iphone"
        case .fund: "star"
        case .health: "cross.case"
        }
    }
    func group(month: String, summary: [String: PayrollItems.Summary]) -> ReportGroup? {
        let applies: (String) -> Bool
        switch self {
        case .food: applies = FoodContractReport.applies(to:)
        case .fund: applies = FundReport.applies(to:)
        case .health: applies = HealthCareReport.applies(to:)
        default: return nil
        }
        let matches = summary.keys.sorted().filter(applies)
        guard let name = matches.max(by: { summary[$0]!.total < summary[$1]!.total }), let item = summary[name] else { return nil }
        return ReportGroup(kind: .payroll, month: month, title: name, count: item.count, total: item.total, byEntity: false)
    }
}

enum PinnedEntities {
    static let key = EntityRenamePlan.pinnedKey
    static func read(_ data: Data) -> [String] { (try? JSONDecoder().decode([String].self, from: data)) ?? [] }
    static func toggle(_ title: String, in data: Data) -> Data {
        var names = read(data)
        if names.contains(title) { names.removeAll { $0 == title } } else { names.append(title) }
        return (try? JSONEncoder().encode(names)) ?? data
    }
}
