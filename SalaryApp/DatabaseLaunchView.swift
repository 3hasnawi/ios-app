import SwiftUI
import SwiftData

/// خطأ فتح القاعدة لا ينشئ قاعدة فارغة ولا يسمح باستعمال بيانات جزئية.
struct DatabaseLaunchView: View {
    @State private var container: ModelContainer?
    @State private var error: String?
    @State private var attempt = 0
    @State private var health = SharedModel.health
    var body: some View {
        Group {
            if health.restartRequired {
                ContentUnavailableView {
                    Label("توقّف الاسترجاع قبل اكتماله", systemImage: "arrow.counterclockwise.circle")
                } description: {
                    Text("حُفظت نقطة رجوع محلية. أغلق التطبيق بالكامل وافتحه لإعادة بياناتك السابقة قبل متابعة العمل.")
                }
            } else if let container {
                RootView().modelContainer(container)
            } else if let error {
                ContentUnavailableView {
                    Label("تعذّر فتح البيانات", systemImage: "externaldrive.badge.exclamationmark")
                } description: {
                    Text("لم تُستبدل قاعدة البيانات بقاعدة فارغة. تحقق من مساحة الجهاز ثم أعد المحاولة.\n" + error)
                } actions: {
                    Button("إعادة المحاولة") { self.error = nil; attempt += 1 }
                }
            } else {
                ProgressView(RestoreSafety.pending ? "استعادة نقطة الرجوع…" : "فتح البيانات…")
            }
        }
        .task(id: attempt) {
            guard container == nil, !health.restartRequired else { return }
            do { container = try await SharedModel.ensureReady() }
            catch { self.error = error.localizedDescription }
        }
    }
}
