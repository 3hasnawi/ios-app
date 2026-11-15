import SwiftUI

/// يشارك اختيار الشهرين ونتيجة المقارنة الموجودة في تدقيق الاستقطاعات.
struct DeductionEntityAlertRows: View {
    let summary: DeductionEntityAlertSummary
    var onOpen: (DeductionEntityAlertKind) -> Void

    var body: some View {
        VStack(spacing: 12) {
            ForEach(DeductionEntityAlertKind.allCases) { kind in
                let rows = summary.rows(kind)
                Button { onOpen(kind) } label: {
                    HStack(spacing: 10) {
                        Image(systemName: kind == .added ? "plus.circle" : "exclamationmark.triangle")
                            .foregroundStyle(rows.isEmpty ? Color.secondary : kind == .added ? Color.green : Color.orange)
                        VStack(alignment: .leading, spacing: 3) {
                            Text(kind.title).font(.subheadline.weight(.semibold)).foregroundStyle(.primary)
                            if !rows.isEmpty {
                                Text((kind == .missing ? tr("المبلغ السابق: ", "Previous amount: ") : tr("مبلغ الشهر: ", "This month's amount: "))
                                     + Money.format(rows.reduce(0) { $0 + $1.amount }))
                                    .font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                            }
                        }
                        Spacer(minLength: 6)
                        Text(Num.format(rows.count)).font(.title3.weight(.bold).monospacedDigit()).foregroundStyle(.primary)
                        Image(systemName: "chevron.forward").font(.caption.weight(.bold)).foregroundStyle(.tertiary)
                    }
                    .padding(.vertical, 3)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityIdentifier("entityAlert-\(kind.rawValue)")
            }
        }
        .accessibilityIdentifier("deductionEntityAlerts")
    }
}

struct DeductionEntityAlertsList: View {
    let comparison: DeductionComparison
    let summary: DeductionEntityAlertSummary
    let kind: DeductionEntityAlertKind
    @State private var search = ""
    private var rows: [DeductionEntityAlert] {
        let tokens = ArabicText.normalize(ArabicText.cleanQuery(search)).split(separator: " ").map(String.init)
        return summary.rows(kind).filter { row in tokens.allSatisfy { ArabicText.normalize(row.title).contains($0) } }
    }
    var body: some View {
        List {
            Section {
                if rows.isEmpty { Text(tr("لا توجد جهات بهذه الحالة.", "No entities with this status.")).foregroundStyle(.secondary) }
                ForEach(rows) { alert in
                    NavigationLink {
                        DeductionEntityAlertDetail(comparison: comparison, alert: alert)
                    } label: {
                        VStack(alignment: .leading, spacing: 6) {
                            Text(alert.title).font(.headline)
                            HStack {
                                Text(tr("\(Num.format(alert.people)) منتسب", "\(alert.people) employees"))
                                Spacer()
                                Text(Money.format(alert.amount)).fontWeight(.semibold).monospacedDigit()
                            }.font(.subheadline).foregroundStyle(.secondary)
                        }.padding(.vertical, 4)
                    }
                }
            } header: {
                Text(tr("\(comparison.to) مقارنة بـ \(comparison.from)", "\(comparison.to) compared with \(comparison.from)"))
            } footer: {
                if kind == .missing {
                    Text(tr("المبلغ والعدد من الشهر السابق. تبقى قيود الأشهر السابقة محفوظة.", "Amounts and counts are from the previous month. Historical entries remain saved."))
                } else {
                    Text(tr("الجهات التي لها استقطاعات في الشهر الجديد ولم يكن لها استقطاع في شهر المقارنة السابق.", "Entities with deductions this month and none in the earlier comparison month."))
                }
            }
        }
        .navigationTitle(kind.title).navigationBarTitleDisplayMode(.inline)
        .adaptiveSearchable(text: $search, prompt: tr("بحث باسم الجهة", "Search entities"))
        .accessibilityIdentifier("deductionEntityAlertsList")
    }
}

private struct DeductionEntityAlertDetail: View {
    let comparison: DeductionComparison
    let alert: DeductionEntityAlert
    @State private var search = ""
    private var month: String { alert.kind == .added ? comparison.to : comparison.from }
    private var rows: [DeductionChange] {
        let tokens = ArabicText.normalize(ArabicText.cleanQuery(search)).split(separator: " ").map(String.init)
        return comparison.entries.filter { row in
            DeductionEntityAlerts.key(row.entity) == alert.key && tokens.allSatisfy {
                ArabicText.normalize(row.name + " " + ArabicText.latinDigits(row.note)).contains($0)
            }
        }.sorted { ArabicText.alphabeticalOrder($0.name, $1.name) == .orderedAscending }
    }
    var body: some View {
        List {
            Section {
                LabeledContent(tr("الشهر", "Month"), value: month)
                LabeledContent(tr("المنتسبون", "Employees"), value: Num.format(alert.people))
                LabeledContent(tr("المبلغ", "Amount"), value: Money.format(alert.amount))
            }
            Section {
                ForEach(rows.prefix(500)) { row in
                    VStack(alignment: .leading, spacing: 5) {
                        HStack {
                            Text(row.name).fontWeight(.semibold)
                            Spacer(minLength: 8)
                            Text(Money.format(alert.kind == .added ? row.after : row.before)).monospacedDigit()
                        }.font(.subheadline)
                        if !row.note.isEmpty { Text(row.note).font(.caption).foregroundStyle(.secondary) }
                    }
                    .personActions(name: row.name, month: month,
                        copies: row.note.isEmpty ? [] : [CopyItem(label: tr("نسخ رقم الإضبارة", "Copy case number"), value: row.note)])
                }
            } header: { Text(tr("قيود الاستقطاع", "Deduction entries")) }
            footer: {
                if rows.count > 500 { Text(tr("يظهر أول ٥٠٠ قيد؛ استخدم البحث للوصول إلى البقية.", "Showing the first 500 entries. Search to find other entries.")) }
                if rows.isEmpty { Text(tr("لا توجد نتائج للبحث.", "No search results.")) }
            }
        }
        .navigationTitle(alert.title).navigationBarTitleDisplayMode(.inline)
        .adaptiveSearchable(text: $search, prompt: tr("بحث بالاسم أو الإضبارة", "Name or case number"))
    }
}
