import SwiftUI

struct TrustSearchSummaryView: View {
    let summary: SalaryTrustSearchSummary
    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            if summary.allPaid {
                Label(tr("الأمانات مصروفة بالكامل", "Salary trust paid in full"), systemImage: "tray.full")
            } else if summary.remaining > 0 {
                Label(tr("أمانات غير مصروفة · \(Money.format(Double(summary.remaining)))", "Outstanding trust · \(Money.format(Double(summary.remaining)))"), systemImage: "tray.full")
            } else {
                Label(tr("أمانات الرواتب", "Salary trust"), systemImage: "tray.full")
            }
            if summary.review > 0 {
                Text(tr("للمراجعة · \(Money.format(Double(summary.review)))", "Needs review · \(Money.format(Double(summary.review)))"))
            }
        }
        .font(.caption.weight(.semibold)).monospacedDigit()
        .foregroundStyle(summary.allPaid ? Color.green : Color.primary)
        .padding(.horizontal, 8).padding(.vertical, 4)
        .background((summary.allPaid ? Color.green : Color.orange).opacity(0.12), in: RoundedRectangle(cornerRadius: 8))
        .privacySensitive()

    }
}
