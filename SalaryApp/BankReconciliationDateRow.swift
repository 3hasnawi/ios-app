import SwiftUI
import UIKit

struct BankReconciliationDateRow: View {
    let title: String
    @Binding var date: Date
    @State private var showingCalendar = false
    @Environment(\.isEnabled) private var editable

    var body: some View {
        let value = BankReconciliation.displayDate(date)
        let row = LabeledContent(title) {
            Text(verbatim: value)
                .monospacedDigit()
                .padding(.horizontal, editable ? 10 : 0)
                .padding(.vertical, editable ? 4 : 0)
                .background { if editable { Capsule().fill(.quaternary) } }
                .fixedSize(horizontal: true, vertical: false)
                .environment(\.layoutDirection, .leftToRight)
        }
        if editable {
            Button { showingCalendar = true } label: {
                row.contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .sheet(isPresented: $showingCalendar) {
                NavigationStack {
                    ScrollView {
                        ReconciliationCalendar(date: date) { selected in
                            if BankReconciliation.day(selected) != BankReconciliation.day(date) { date = selected }
                            showingCalendar = false
                        }
                        .padding(.horizontal)
                    }
                    .navigationTitle(title)
                    .navigationBarTitleDisplayMode(.inline)
                    .toolbar {
                        ToolbarItem(placement: .cancellationAction) {
                            Button(tr("إلغاء", "Cancel")) { showingCalendar = false }
                        }
                    }
                }
                .presentationDetents([.height(440), .large])
            }
        } else {
            row
        }
    }
}

private struct ReconciliationCalendar: UIViewRepresentable {
    let date: Date
    let selected: (Date) -> Void

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    func makeUIView(context: Context) -> UICalendarView {
        let view = UICalendarView()
        let calendar = BankReconciliation.dayCalendar
        view.locale = AppSettings.locale
        view.calendar = calendar
        view.timeZone = calendar.timeZone
        let components = calendar.dateComponents([.year, .month, .day], from: date)
        let selection = UICalendarSelectionSingleDate(delegate: context.coordinator)
        view.selectionBehavior = selection
        view.setVisibleDateComponents(components, animated: false)
        selection.setSelected(components, animated: false)
        return view
    }

    func updateUIView(_ view: UICalendarView, context: Context) { context.coordinator.parent = self }

    final class Coordinator: NSObject, UICalendarSelectionSingleDateDelegate {
        var parent: ReconciliationCalendar
        init(_ parent: ReconciliationCalendar) { self.parent = parent }

        func dateSelection(_ selection: UICalendarSelectionSingleDate, didSelectDate components: DateComponents?) {
            let calendar = BankReconciliation.dayCalendar
            // قد يرسل التقويم قيمة فارغة عند لمس اليوم المحدد مرة أخرى.
            if let components {
                if let day = calendar.date(from: components) { parent.selected(day) }
            } else {
                parent.selected(parent.date)
            }
        }
    }
}
