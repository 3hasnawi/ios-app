import Foundation

@main struct Test {
    static func row(_ person: String, _ entity: String, _ amount: Double?, _ note: String = "") -> DeductionSnapshot {
        DeductionSnapshot(name: person, entity: entity, note: note, amount: amount)
    }
    static func compare(_ old: [DeductionSnapshot], _ new: [DeductionSnapshot]) throws -> DeductionComparison {
        try DeductionReview.compare(from: "أيلول 2026", to: "تشرين الأول 2026", old: old, new: new)
    }
    static func main() throws {
        let old = [row("منتسب وهمي أ", "جهة مستمرة", 100),
                   row("منتسب وهمي ب", "جهة غائبة", 200, "1"), row("منتسب وهمي ب", "جهة غائبة", 50, "2"),
                   row("منتسب وهمي ج", "جهة غائبة", 300), row("منتسب وهمي د", "جهة ألف", 400, "١٢"),
                   row("منتسب وهمي هـ", "جهة بصفر", 0)]
        let new = [row("منتسب وهمي أ", "جهة مستمرة", 150),
                   row("منتسب وهمي و", "جهة جديدة", 250, "3"), row("منتسب وهمي و", "جهة جديدة", 50, "4"),
                   row("منتسب وهمي ز", "جهة جديدة", 100), row("منتسب وهمي د", "جهه الف", 400, "12"),
                   row("منتسب وهمي هـ", "جهة بصفر", 0)]
        let comparison = try compare(old, new)
        let summary = try DeductionEntityAlerts.summarize(comparison)
        precondition(summary.rows(.added).count == 1 && summary.rows(.missing).count == 1)
        let added = summary.rows(.added)[0], missing = summary.rows(.missing)[0]
        precondition(added.title == "جهة جديدة" && added.people == 2 && added.amount == 400)
        precondition(missing.title == "جهة غائبة" && missing.people == 2 && missing.amount == 550)
        precondition(comparison.rows(.changed).count == 1 && comparison.rows(.started).count == 3 && comparison.rows(.stopped).count == 3)
        // تبدل الموظفين والإضبارة لا يعني دخول جهة مستمرة أو غيابها.
        let continued = try compare([row("وهمي قديم", "جهة مستمرة", 100, "10")], [row("وهمي جديد", "جهة مستمرة", 120, "11")])
        let continuedSummary = try DeductionEntityAlerts.summarize(continued)
        precondition(continuedSummary.alerts.isEmpty)
        let zero = try compare([row("وهمي", "جهة مستمرة", 100)], [row("وهمي", "جهة مستمرة", 100), row("وهمي", "جهة صفرية", 0)])
        let zeroSummary = try DeductionEntityAlerts.summarize(zero)
        precondition(zeroSummary.alerts.isEmpty)
        // الفرع جهة مستقلة؛ لا مطابقة تقريبية قد تخفي جهة جديدة.
        let branch = try compare([row("وهمي", "جهة مستقلة", 100)], [row("وهمي", "جهة مستقلة / فرع وهمي", 100)])
        let branchSummary = try DeductionEntityAlerts.summarize(branch)
        precondition(branchSummary.alerts.count == 2)
        // توحيد التسمية في كل الأشهر يزيل تنبيهات الاختلاف في الاسم فقط.
        let unified = try compare([row("وهمي", "جهة موحدة", 100)], [row("وهمي", "جهة موحدة", 100)])
        let unifiedSummary = try DeductionEntityAlerts.summarize(unified)
        precondition(unifiedSummary.alerts.isEmpty)
        let reverse = try compare(new, old)
        let reversedSummary = try DeductionEntityAlerts.summarize(reverse)
        precondition(reversedSummary.rows(.added)[0].amount == 550 && reversedSummary.rows(.missing)[0].amount == 400)
        for (a, b) in [([], new), (old, []), ([], [])] as [([DeductionSnapshot], [DeductionSnapshot])] {
            do { _ = try DeductionEntityAlerts.summarize(compare(a, b)); fatalError("Empty import marked as all entities missing") }
            catch is DeductionEntityAlerts.Incomplete { }
        }
        for bad in [Double?.none, -1, .nan, .infinity] {
            do { _ = try compare(old, [row("وهمي", "جهة", bad)]); fatalError("Invalid amount accepted") }
            catch is DeductionReview.InvalidData { }
        }
        // يتجاوز الاختبار حجم الدفعة ويعد الشخص مرة واحدة ولو كان له عدة إضبارات.
        let many = (0..<1400).map { row("وهمي \($0 / 2)", "جهة كبيرة جديدة", Double($0), "\($0)") }
        let large = try DeductionEntityAlerts.summarize(compare([row("قديم", "جهة سابقة", 1)], many))
        precondition(large.rows(.added)[0].people == 700 && large.rows(.added)[0].amount == Double((0..<1400).reduce(0, +)))
        let months = [DeductionAlertMonth(name: "تشرين الثاني 2026", order: 202611, deductions: 10),
                      DeductionAlertMonth(name: "تشرين الأول 2026", order: 202610, deductions: 0),
                      DeductionAlertMonth(name: "أيلول 2026", order: 202609, deductions: 10),
                      DeductionAlertMonth(name: "آب 2026", order: 202608, deductions: 10)]
        precondition(DeductionEntityAlerts.previousMonth(selected: "تشرين الثاني 2026", months: months) == "أيلول 2026")
        precondition(DeductionEntityAlerts.previousMonth(selected: "أيلول 2026", months: months) == "آب 2026")
        precondition(DeductionEntityAlerts.previousMonth(selected: "تشرين الأول 2026", months: months) == nil)
        precondition(DeductionEntityAlerts.previousMonth(selected: "آب 2026", months: months) == nil)
        precondition(DeductionEntityAlerts.previousMonth(selected: "غير موجود", months: months) == nil)
        let ambiguous = months + [DeductionAlertMonth(name: "ايلول 2026", order: 202609, deductions: 10)]
        precondition(DeductionEntityAlerts.previousMonth(selected: "تشرين الثاني 2026", months: ambiguous) == nil)
        precondition(DeductionEntityAlerts.previousMonth(selected: "أيلول 2026", months: ambiguous) == nil)
        print("Deduction entity alerts: added/missing groups, distinct people, totals, branches, renaming, empty/invalid import rejection, reverse comparison and previous deduction month selection passed")
    }
}
