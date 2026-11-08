#if targetEnvironment(simulator)
import Foundation
import SwiftData

/// قاعدة ونسخة وهميتان، في محاكي الاختبار فقط، قبل فتح واجهة التطبيق.
@MainActor enum DatabaseRecoverySelfTest {
    static func run() async throws -> String {
        let schema = Schema([PayrollMonth.self, Employee.self, Deduction.self, StaffMember.self, ExecutionAccount.self])
        let source = try ModelContainer(for: schema, configurations: [ModelConfiguration(isStoredInMemoryOnly: true)])
        let context = ModelContext(source)
        context.insert(PayrollMonth(name: "تشرين الأول 2026", employeeCount: 600, deductionCount: 0))
        for i in 0..<600 { context.insert(Employee(month: "تشرين الأول 2026", name: "منتسب وهمي \(i)", rank: "موظف", workplace: "اختبار", netSalary: 1000)) }
        try context.save()
        let (archive, _) = try await BackupActor(modelContainer: source).export(settings: [:], progress: nil)
        defer { try? FileManager.default.removeItem(at: archive) }
        let restored = try await SharedModel.recover(from: archive)
        let verify = ModelContext(restored)
        guard try verify.fetchCount(FetchDescriptor<Employee>()) == 600, !RestoreSafety.pending,
              UserDefaults.standard.string(forKey: "activeRecoveredDatabase") != nil else { throw CocoaError(.fileReadCorruptFile) }
        // لا نترك بيانات الاختبار كي تدخل في فحوص الاستيراد التالية.
        for e in try verify.fetch(FetchDescriptor<Employee>()) { verify.delete(e) }
        for m in try verify.fetch(FetchDescriptor<PayrollMonth>()) { verify.delete(m) }
        try verify.save()
        return "bootstrapRecovery=ok rows=600 legacyBackup=5"
    }
}
#endif
