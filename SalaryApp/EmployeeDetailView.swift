import SwiftUI
import SwiftData

struct EmployeeDetailView: View {
    let employee: Employee
    @Query private var deductions: [Deduction]

    init(employee: Employee) {
        self.employee = employee
        let key = employee.matchKey
        _deductions = Query(filter: #Predicate { $0.matchKey == key }, sort: \Deduction.title)
    }

    private var total: Double {
        deductions.reduce(0) { $0 + ($1.amount ?? 0) }
    }

    var body: some View {
        List {
            Section {
                VStack(spacing: 6) {
                    Text("صافي الراتب")
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                    Text(Money.format(employee.netSalary))
                        .font(.system(size: 36, weight: .bold, design: .rounded))
                        .monospacedDigit()
                }
                .frame(maxWidth: .infinity)
                .padding(.vertical, 8)
            }

            Section("المعلومات") {
                LabeledContent("الاسم", value: employee.name)
                LabeledContent("الرتبة", value: employee.rank.orDash)
                LabeledContent("محل العمل", value: employee.workplace.orDash)
            }

            Section("الاستقطاعات") {
                if deductions.isEmpty {
                    Text("لا توجد استقطاعات باسم هذا الموظف")
                        .foregroundStyle(.secondary)
                } else {
                    ForEach(deductions) { deduction in
                        LabeledContent {
                            Text(Money.format(deduction.amount)).monospacedDigit()
                        } label: {
                            Text(deduction.title.orDash)
                            if !deduction.note.isEmpty {
                                Text(deduction.note)
                            }
                        }
                    }
                    LabeledContent("مجموع الاستقطاعات") {
                        Text(Money.format(total)).bold()
                    }
                }
            }
        }
        .navigationTitle(employee.name)
        .navigationBarTitleDisplayMode(.inline)
    }
}
