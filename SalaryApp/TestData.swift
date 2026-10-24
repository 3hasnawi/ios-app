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
            modelContext.insert(StaffMember(
                name: name, mother: "\(pick(Self.first)) \(pick(Self.family))",
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
