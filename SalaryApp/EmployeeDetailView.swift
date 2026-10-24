import SwiftUI
import SwiftData

struct EmployeeDetailView: View {
    let employee: Employee
    @Query private var deductions: [Deduction]
    @State private var showShare = false

    init(employee: Employee) {
        self.employee = employee
        let key = employee.matchKey
        let month = employee.month
        _deductions = Query(filter: #Predicate { $0.month == month && $0.matchKey == key }, sort: \Deduction.title)
    }

    var body: some View {
        List {
            Section {
                SalaryHeader(employee: employee)
                    .listRowInsets(EdgeInsets())
                    .listRowBackground(Color.clear)
            }

            Section(tr("المعلومات", "Details")) {
                InfoRow(title: tr("الرتبة", "Rank"), value: employee.rank.orDash, systemImage: "star.fill", color: .orange)
                InfoRow(title: tr("محل العمل", "Workplace"), value: employee.workplace.orDash, systemImage: "building.2.fill", color: .teal)
                InfoRow(title: tr("الشهر", "Month"), value: employee.month.orDash, systemImage: "calendar", color: .purple)
            }

            Section {
                if deductions.isEmpty {
                    Label(tr("لا توجد استقطاعات باسم هذا الموظف", "No deductions for this employee"), systemImage: "checkmark.seal.fill")
                        .foregroundStyle(.secondary)
                } else {
                    ForEach(deductions) { deduction in
                        LabeledContent {
                            Text(Money.format(deduction.amount)).monospacedDigit()
                        } label: {
                            Label {
                                Text(deduction.title.orDash)
                                if !deduction.note.isEmpty {
                                    Text(deduction.note)
                                }
                            } icon: {
                                Image(systemName: "minus.circle.fill").foregroundStyle(.red)
                            }
                        }
                    }
                    LabeledContent {
                        Text(Money.format(deductions.total))
                            .bold()
                            .monospacedDigit()
                            .foregroundStyle(.red)
                    } label: {
                        Text(tr("مجموع الاستقطاعات", "Total deductions")).bold()
                    }
                }
            } header: {
                HStack {
                    Text(tr("الاستقطاعات", "Deductions"))
                    Spacer()
                    if !deductions.isEmpty {
                        Text(Num.format(deductions.count))
                    }
                }
            }
        }
        .listSectionSpacing(.compact)
        .navigationTitle(employee.name)
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Button(tr("مشاركة", "Share"), systemImage: "square.and.arrow.up") {
                    showShare = true
                }
            }
        }
        .sheet(isPresented: $showShare) {
            ShareOptionsSheet(employee: employee)
        }
        .task {
            if LaunchOptions.share { showShare = true }
        }
    }
}

private struct InfoRow: View {
    let title: String
    let value: String
    let systemImage: String
    let color: Color

    var body: some View {
        LabeledContent {
            Text(value)
        } label: {
            Label {
                Text(title)
            } icon: {
                Image(systemName: systemImage)
                    .font(.footnote.weight(.semibold))
                    .foregroundStyle(.white)
                    .frame(width: 28, height: 28)
                    .background(color.gradient, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
            }
        }
    }
}
