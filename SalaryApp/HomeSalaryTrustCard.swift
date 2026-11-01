import SwiftUI

/// الرصيد لجميع الأشهر؛ القيود غير المحسومة لا تدخل في المجموع.
struct HomeSalaryTrustCard: View {
    let action: () -> Void
    @AppStorage(SalaryTrust.settingsKey) private var stored = Data()
    @State private var currentNote = 0
    @Environment(\.colorScheme) private var colorScheme
    private var ledger: SalaryTrust.Ledger? { try? SalaryTrust.decode(stored) }
    private var notes: [String] {
        guard let ledger else { return [tr("تعذّر قراءة دفتر الأمانات", "Could not read the trust ledger")] }
        guard ledger.openingImported else { return [tr("استورد ملف الأمانات للبدء", "Import the opening trust spreadsheet to begin")] }
        let review = ledger.entries.filter(\.reviewPending).count
        let partial = ledger.entries.filter { !$0.reviewPending && $0.paid > 0 && $0.remaining > 0 }.count
        let outstanding = ledger.entries.filter { !$0.reviewPending && $0.remaining > 0 }.count
        var lines: [String] = []
        if review > 0 { lines.append(tr("\(Num.format(review)) قيد يحتاج مراجعة", "\(review) entries need review")) }
        if partial > 0 { lines.append(tr("\(Num.format(partial)) قيد مصروف جزئياً", "\(partial) entries partially paid")) }
        lines.append(tr("\(Num.format(outstanding)) قيد برصيد غير مصروف", "\(outstanding) entries with outstanding balances"))
        return lines
    }
    private var balance: String {
        guard let ledger else { return "—" }
        guard ledger.openingImported else { return "—" }
        return Money.format(Double(ledger.entries.filter { !$0.reviewPending }.reduce(Int64(0)) { $0 + $1.remaining }))
    }
    var body: some View {
        Button(action: action) {
            VStack(alignment: .leading, spacing: 7) {
                HStack(alignment: .center, spacing: 8) {
                    Label(tr("أمانات الرواتب", "Salary trust"), systemImage: "building.columns.fill")
                        .font(.subheadline.weight(.semibold)).foregroundStyle(.primary)
                    Spacer(minLength: 4)
                    VStack(alignment: .trailing, spacing: 2) {
                        Text(balance).font(.subheadline.weight(.bold)).monospacedDigit().lineLimit(1).minimumScaleFactor(0.65)
                        Text(tr("غير المصروف · كل الأشهر", "Outstanding · all months")).font(.caption2).foregroundStyle(.secondary)
                    }
                }
                Divider()
                HStack(spacing: 6) {
                    Text(notes[currentNote % notes.count]).font(.caption2).foregroundStyle(.secondary).lineLimit(2)
                    Spacer(minLength: 4)
                    if notes.count > 1 {
                        HStack(spacing: 4) {
                            ForEach(notes.indices, id: \.self) { i in Circle().fill(i == currentNote % notes.count ? Color.teal : Color.teal.opacity(0.25)).frame(width: 4, height: 4) }
                        }.accessibilityHidden(true)
                    }
                }
            }
            .padding(12)
            .background(colorScheme == .dark ? Color.teal.opacity(0.14) : Color.teal.opacity(0.08), in: RoundedRectangle(cornerRadius: 16))
            .overlay(RoundedRectangle(cornerRadius: 16).strokeBorder(Color.teal.opacity(0.23)))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityIdentifier("homeSalaryTrustCard")
        .onReceive(Timer.publish(every: 5, on: .main, in: .common).autoconnect()) { _ in
            if notes.count > 1 { withAnimation(.easeInOut(duration: 0.2)) { currentNote = (currentNote + 1) % notes.count } }
        }
    }
}
