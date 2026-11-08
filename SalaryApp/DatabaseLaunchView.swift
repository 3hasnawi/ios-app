import SwiftUI
import SwiftData

/// خطأ فتح القاعدة لا ينشئ قاعدة فارغة ولا يسمح باستعمال بيانات جزئية.
struct DatabaseLaunchView: View {
    @State private var container: ModelContainer?
    @State private var error: String?
    @State private var attempt = 0
    @State private var recovering = false
    @State private var selectedBackup: URL?
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
                    Button("إعادة المحاولة") { self.error = nil; attempt += 1 }.disabled(recovering)
                    Button("استرجاع نسخة محفوظة") {
                        FilePicker.present(types: [.zip]) { urls in
                            guard let url = urls.first else { return }
                            Task { await FilePicker.waitUntilSettled(); selectedBackup = url }
                        }
                    }.disabled(recovering)
                    if recovering { ProgressView("استرجاع النسخة إلى قاعدة مستقلة…") }
                }
            } else {
                ProgressView(RestoreSafety.pending ? "استعادة نقطة الرجوع…" : "فتح البيانات…")
            }
        }
        .alert("استرجاع البيانات", isPresented: Binding(get: { selectedBackup != nil }, set: { if !$0 { selectedBackup = nil } })) {
            Button("إلغاء", role: .cancel) { selectedBackup = nil }
            Button("استرجاع") {
                guard let url = selectedBackup else { return }
                selectedBackup = nil; recovering = true
                Task {
                    defer { recovering = false }
                    do { container = try await SharedModel.recover(from: url) }
                    catch { self.error = error.localizedDescription }
                }
            }
        } message: {
            Text("سيتم إنشاء قاعدة مستقلة من النسخة المختارة. تبقى القاعدة التي تعذر فتحها محفوظة، ولا تُستخدم قاعدة فارغة للعمل.")
        }
        .task(id: attempt) {
            guard container == nil, !health.restartRequired else { return }
            #if targetEnvironment(simulator)
            if ProcessInfo.processInfo.arguments.contains("-bootstrapRecoveryTest") {
                do { LaunchOptions.writeReport(try await DatabaseRecoverySelfTest.run()) }
                catch { LaunchOptions.writeReport("bootstrapRecovery=failed \(error.localizedDescription)") }
                return
            }
            #endif
            do { container = try await SharedModel.ensureReady() }
            catch { self.error = error.localizedDescription }
        }
    }
}
