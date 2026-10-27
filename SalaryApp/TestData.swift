import Foundation
import SwiftData

/// بيانات ملاك وهمية لاختبار المحاكي فقط (-fakeStaff 42000)، لقياس سرعة البحث الموحّد
/// بحجم يشبه الملاك الحقيقي دون رفع قاعدة البيانات الحقيقية. لا يعمل عند المستخدم.
@ModelActor
actor FakeStaffMaker {
    private static let first = ["محمد", "أحمد", "علي", "حسين", "عمر", "خالد", "مصطفى", "حسن", "يوسف", "إبراهيم",
                                "سعد", "زياد", "عبد الله", "عبد الرحمن", "مهند", "ليث", "ياسر", "حيدر", "سيف", "رعد"]
    private static let family = ["الدليمي", "العبيدي", "الجميلي", "الكبيسي", "الراوي", "العاني", "الفهداوي", "المحمدي", "النعيمي", "الهيتي"]
    private static let ranks = ["عميد", "عقيد", "مقدم", "رائد", "نقيب", "ملازم أول", "مفوض1", "مفوض3", "شرطي"]
    private static let statuses = ["مستمر بالخدمة", "مستمر بالخدمة", "مستمر بالخدمة", "متقاعد", "منقول"]
    private static let places = ["قسم النجدة", "فوج الطوارئ الأول", "مركز العمليات المشترك", "مدرسة التدريب"]

    func make(count: Int) throws {
        var rng = SystemRandomNumberGenerator()
        func pick(_ list: [String]) -> String { list.randomElement(using: &rng)! }
        // الموظفان الثابتان في اختبار الرواتب موجودان في الملاك أيضاً.
        var names = ["أحمد علي حسين الدليمي", "عمر خالد جاسم العبيدي"]
        while names.count < count {
            names.append([pick(Self.first), pick(Self.first), pick(Self.first), pick(Self.first), pick(Self.family)].joined(separator: " "))
        }
        for (i, name) in names.enumerated() {
            let rank = pick(Self.ranks), status = pick(Self.statuses), place = pick(Self.places)
            let mother = i == 0 ? "فاطمة محمد الدليمي" : "\(pick(Self.first)) \(pick(Self.family))"
            modelContext.insert(StaffMember(
                name: name, mother: mother,
                statNumber: String(100000 + i), unifiedNumber: String(9_000_000 + i),
                rank: rank, status: status, workplace: place,
                sections: [StaffSection(title: "الوظيفية", fields: [
                    StaffField(label: "الرتبة", value: rank),
                    StaffField(label: "الحالة الوظيفية", value: status),
                    StaffField(label: "القسم", value: place),
                ])]
            ))
            if i % 3000 == 2999 { try modelContext.save() }
        }
        try modelContext.save()
    }
}

enum TestData {
    /// يتأكد من بقاء عمود غير معروف مسبقاً وكل ترتيب الورقة بعد الاستيراد الوهمي.
    @MainActor
    static func checkRawPayrollIfRequested() {
        guard UserDefaults.standard.bool(forKey: "rawColumnsTest") else { return }
        var query = FetchDescriptor<Employee>(predicate: #Predicate { $0.name == "أحمد علي حسين الدليمي" })
        query.fetchLimit = 1
        let employee = try? SharedModel.container.mainContext.fetch(query).first
        let snapshot = employee?.rawColumns
        let passed = snapshot != nil && snapshot?.headers.count == snapshot?.values.count &&
            snapshot?.values(named: "حقل مستقبلي تجريبي").first == "قيمة تجريبية" &&
            snapshot?.values(named: "NOTA1").first == "" &&
            snapshot?.values(named: "الرفرنس").first == "0" &&
            snapshot?.values(named: "الاسمي").first != nil && employee?.details["الاسمي"] != nil
        LaunchOptions.writeReport("rawColumns=\(passed ? "ok" : "failed") count=\(snapshot?.headers.count ?? 0)")
    }

    @MainActor
    static func prepareIfRequested() async {
        let count = UserDefaults.standard.integer(forKey: "fakeStaff")
        guard count > 0, StaffInfo.count == 0 else { return }
        let container = SharedModel.container
        do {
            try await Task.detached {
                try await FakeStaffMaker(modelContainer: container).make(count: count)
            }.value
            StaffInfo.save(count: count, fileName: "ملاك تجريبي.accdb", statuses: ["مستمر بالخدمة", "متقاعد", "منقول"])
            StaffImportState.shared.version += 1
            LaunchOptions.writeReport("staff=\(count)")
        } catch {
            LaunchOptions.writeReport("error=\(error)")
        }
    }
}

/// تنقّل آلي داخل التطبيق لاختبار المحاكي فقط (-autopilot YES): نفس حركات المستخدم
/// (تبويب البحث، بحث، فتح نتيجة، أقسام الصفحة، رجوع، الرئيسية) بدون أداة الاختبار،
/// حتى يقيس سجل التجمّد سلاسة التطبيق نفسه فقط.
@MainActor @Observable
final class AutoPilot {
    static let shared = AutoPilot()
    var openIndex = 0
    var openRequest = 0
    var backRequest = 0
    var part = ""
    var partRequest = 0

    func run() async {
        guard UserDefaults.standard.bool(forKey: "autopilot") else { return }
        func pause(_ seconds: Double) async { try? await Task.sleep(for: .seconds(seconds)) }
        func step(_ name: String, wait: Double, _ action: () -> Void) async {
            PerfLog.mark(name)
            action()
            await pause(wait)
        }
        await pause(3)
        for round in 1...2 {
            await step("تبويب البحث \(round)", wait: 1.5) { AppRouter.shared.tab = .search }
            await step("بحث احمد علي \(round)", wait: 3) { ShortcutRouter.shared.pendingSearch = "احمد علي" }
            for i in 0..<3 {
                await step("فتح النتيجة \(i + 1)", wait: 1.5) { openIndex = i; openRequest += 1 }
                await step("قسم الأشهر", wait: 1) { part = "months"; partRequest += 1 }
                await step("قسم الراتب", wait: 1) { part = "salary"; partRequest += 1 }
                await step("رجوع", wait: 1.5) { backRequest += 1 }
            }
            await step("بحث علي حسين \(round)", wait: 3) { ShortcutRouter.shared.pendingSearch = "علي حسين" }
            await step("الرئيسية \(round)", wait: 2) { AppRouter.shared.tab = .payroll }
        }
        PerfLog.mark("انتهى")
        LaunchOptions.writeReport("autopilot=done")
    }
}
