import SwiftUI

struct TrustSearchSummaryView: View {
    let summary: SalaryTrustSearchSummary
    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Label(tr("أمانات رواتب", "Salary trust"), systemImage: "tray.full")
                .font(.caption.weight(.semibold))
            if summary.allPaid {
                Text(tr("الأمانات مصروفة بالكامل", "Salary trust paid in full")).font(.subheadline.weight(.semibold))
            } else {
                if summary.remaining > 0 {
                    HStack {
                        Text(tr("غير المصروف", "Outstanding")).font(.caption)
                        Spacer(minLength: 8)
                        Text(Money.format(Double(summary.remaining))).font(.headline.monospacedDigit())
                    }
                }
                if summary.review > 0 {
                    HStack {
                        Text(tr("للمراجعة", "Needs review")).font(.caption)
                        Spacer(minLength: 8)
                        Text(Money.format(Double(summary.review))).font(.subheadline.weight(.semibold).monospacedDigit())
                    }
                }
            }
            Text(tr("\(Num.format(summary.months)) شهر · المصروف: \(Money.format(Double(summary.paid)))",
                    "\(summary.months) months · paid: \(Money.format(Double(summary.paid)))"))
                .font(.caption).foregroundStyle(.secondary)
        }
        .foregroundStyle(summary.allPaid ? Color.green : Color.orange)
        .padding(12)
        .background((summary.allPaid ? Color.green : Color.orange).opacity(0.08), in: RoundedRectangle(cornerRadius: 12))
        .privacySensitive()
        .accessibilityIdentifier("searchTrustSummary")
    }
}
