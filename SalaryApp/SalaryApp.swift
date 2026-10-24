import SwiftUI
import SwiftData

@main
struct SalaryApp: App {
    var body: some Scene {
        WindowGroup {
            ContentView()
                .environment(\.layoutDirection, .rightToLeft)
        }
        .modelContainer(for: [Employee.self, Deduction.self])
    }
}
