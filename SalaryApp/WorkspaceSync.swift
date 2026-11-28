import Foundation
import SwiftUI

@_silgen_name("payroll_sync") private func payrollSync(_ input: UnsafePointer<CChar>, _ transfer: @convention(c) (Int32, Int64, Int64) -> Void) -> UnsafeMutablePointer<CChar>?
@_silgen_name("payroll_sync_free") private func payrollSyncFree(_ output: UnsafeMutablePointer<CChar>)

struct WorkspacePoint: Decodable, Identifiable {
    let id, kind: String
    let notes: [String]?
    let at: Int64
    var date: Date { Date(timeIntervalSince1970:Double(at)/1000) }
    var label: String {
        if let notes, !notes.isEmpty {
            return notes.map { $0 == "import" ? tr("قبل الاستيراد", "Before import") : tr("قبل الحذف", "Before deletion") }.joined(separator:" · ")
        }
        switch kind {
        case "backup": return tr("نسخة كاملة", "Full backup")
        case "beforeRestore": return tr("قبل الاستعادة", "Before restore")
        case "restore": return tr("بعد الاستعادة", "Restored")
        default: return tr("مزامنة", "Sync")
        }
    }
}
struct WorkspacePreview: Decodable {
    let staff, employees, refunds: Int
    let net, deductions: Double
}
private struct SyncReply: Decodable {
    let head, cursor: String?
    let points: [WorkspacePoint]?
    let preview: WorkspacePreview?
    let status: String
    let error, file, transaction, warning: String?
}
@Observable @MainActor
final class WorkspaceSync {
    static let shared = WorkspaceSync()
    private let account = OneDriveAccount()
    private let defaults = UserDefaults.standard
    private(set) var pendingUpload = false
    var linked = false
    var status = tr("نقل النسخ يدوي", "Manual backup transfer")
    private(set) var transferring = false
    private(set) var showingProgress = false
    private(set) var transferFraction: Double?
    var busy = false
    var points: [WorkspacePoint] = []
    var historyHead = ""
    var cursor: String?
    var preview: WorkspacePreview?
    var selected: WorkspacePoint?
    var last = UserDefaults.standard.object(forKey: "oneDriveLastSync") as? Date
    init() {
        linked = (try? account.storedToken()) != nil
    }
    func signIn() async {
        guard !busy else { return }
        busy = true
        defer { busy = false }
        do {
            try await account.login()
            linked = true
            refreshPendingUpload()
            status = tr("الحساب مرتبط", "Account connected")
        } catch { show(error) }
    }
    func signOut() {
        guard !busy else { return }
        do { try account.logout(); linked = false; points = []; preview = nil; selected = nil; historyHead = ""; cursor = nil; pendingUpload = false; status = tr("تم تسجيل الخروج", "Signed out") }
        catch { show(error) }
    }
    private func show(_ error: Error) { status = (error as? OneDriveAuthorizationReplyError)?.message ?? error.localizedDescription }
    private var workspaceDirectory: URL {
        URL.applicationSupportDirectory.appendingPathComponent("OneDriveWorkspace", isDirectory: true)
    }

    // نقرأ الإقرار المحلي نفسه الذي يستخدمه محرك الدمج، بما فيه إقرارات الإصدارات السابقة.
    func refreshPendingUpload() {
        guard linked else { pendingUpload = false; return }
        struct Checkpoint: Decodable { let Stamp: String }
        let file = workspaceDirectory.appendingPathComponent("checkpoint.json")
        if let data = try? Data(contentsOf: file), let checkpoint = try? JSONDecoder().decode(Checkpoint.self, from: data),
           !checkpoint.Stamp.isEmpty {
            pendingUpload = checkpoint.Stamp != BackupManager.shared.workSyncStamp
        } else {
            pendingUpload = BackupManager.shared.hasWorkspaceChangesBeforeFirstSync
        }
    }

    func rememberPoint(_ reason: String) async {
        guard linked else { return }
        let dir = workspaceDirectory
        do { _ = try await call(["action":"note","reason":reason,"dir":dir.path]) }
        catch { show(error) }
    }
    func localDataSaved() {
        if linked { pendingUpload = true }
    }

    private var mayInstall: Bool {
        UIApplication.shared.applicationState == .active && !AppLock.shared.isLocked
    }

    func synchronize(action: String, point: WorkspacePoint? = nil,
                     more: Bool = false, until: Date? = nil) async {
        let store = BackupManager.shared
        guard linked, !busy else { return }
        guard store.workSyncAvailable else {
            status = tr("انتظر انتهاء العملية الحالية ثم أعد المحاولة", "Wait for the current operation to finish, then try again")
            return
        }
        busy = true
        transferFraction = nil
        showingProgress = action == "backup-upload" || action == "backup-download"
        defer {
            showingProgress = false
            transferFraction = nil
            busy = false
            refreshPendingUpload()
        }
        var exported: URL?, received: URL?, validated: URL?
        defer {
            if let exported { BackupFormat.removeExport(exported) }
            if let received { try? FileManager.default.removeItem(at: received) }
            if let validated { try? FileManager.default.removeItem(at: validated) }
        }
        do {
            status = tr("جارٍ الاتصال بـ OneDrive…", "Connecting to OneDrive…")
            let token = try await account.access()
            linked = true
            let stamp = store.workSyncStamp
            var dir = workspaceDirectory
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories:true, attributes:[.protectionKey:FileProtectionType.completeUntilFirstUserAuthentication])
            var policy = URLResourceValues()
            policy.isExcludedFromBackup = true
            try dir.setResourceValues(policy)
            var args: [String:Any] = ["action":action,"dir":dir.path,"token":token,"stamp":stamp,"recentBackups":true]
            args["head"] = historyHead
            args["revision"] = point?.id
            if action == "history" {
                if more { args["cursor"] = cursor }
                else { points = []; cursor = nil }
                if let until { args["until"] = Int64(until.timeIntervalSince1970 / 60) * 60_000 + 59_999 }
            }
            if action == "history" || action == "preview" { preview = nil; selected = nil }
            if action == "backup-upload" {
                status = tr("تجهيز النسخة الاحتياطية الكاملة…", "Preparing full backup…")
                let file = try await store.workSyncExport()
                exported = file
                status = tr("التحقق من النسخة قبل الرفع…", "Verifying backup before upload…")
                validated = try await store.workSyncValidate(file)
                guard store.workSyncStamp == stamp, mayInstall else { throw BackupError.sourceChanged }
                args["local"] = exported?.path
            }
            var reply = try await call(args)
            if reply.status == "history" {
                points.append(contentsOf:reply.points ?? []); cursor = reply.cursor; historyHead = reply.head ?? ""
                status = points.isEmpty ? tr("لا توجد نقاط مزامنة لهذه الفترة", "No sync points in this period") : tr("سجل المزامنة", "Sync history")
                return
            }
            if reply.status == "preview" {
                preview = reply.preview; selected = point; status = tr("معاينة نقطة المزامنة", "Sync point preview")
                return
            }
            if action == "backup-download", reply.status == "downloaded", let file = reply.file, let transaction = reply.transaction {
                let url = URL(fileURLWithPath: file)
                received = url
                status = tr("التحقق من النسخة المستلمة…", "Verifying downloaded backup…")
                let folder = try await store.workSyncValidate(url)
                validated = folder
                guard mayInstall, store.workSyncStamp == stamp else { throw BackupError.sourceChanged }
                status = tr("استيراد بيانات النسخة…", "Importing backup data…")
                let installed = try await store.workSyncApply(folder, expected: stamp)
                reply = try await call(["action":"backup-ack","dir":dir.path,"stamp":installed,"transaction":transaction])
            }
            guard (action == "backup-upload" && reply.status == "uploaded") ||
                    (action == "backup-download" && reply.status == "imported") else {
                throw AccountError(tr("تعذّر إكمال نقل النسخة", "Could not complete backup transfer"))
            }
            last = Date(); defaults.set(last,forKey:"oneDriveLastSync")
            if reply.warning != nil {
                status = tr("اكتمل الرفع، وتعذّر إكمال حذف النسخ الأقدم. ستُعاد محاولة التنظيف عند الرفع التالي.", "Uploaded. Cleanup of older backups will be retried on the next upload.")
            } else {
                status = action == "backup-upload" ? tr("اكتمل رفع النسخة الكاملة", "Full backup uploaded") :
                    tr("اكتمل استيراد النسخة على هذا الجهاز", "Backup imported on this device")
            }
        } catch { show(error) }
    }
    private func call(_ args: [String:Any]) async throws -> SyncReply {
        let request = try JSONSerialization.data(withJSONObject:args)
        let bytes = try await Task.detached(priority:.utility) {
            try String(decoding:request,as:UTF8.self).withCString { input -> Data in
                let result = payrollSync(input) { stage, done, total in
                    DispatchQueue.main.async {
                        let sync = WorkspaceSync.shared
                        sync.transferring = stage != 0
                        sync.transferFraction = stage >= 2 && total > 0 ? min(1, max(0, Double(done) / Double(total))) : nil
                        if stage >= 2 {
                            let label = stage == 2 ? tr("رفع النسخة", "Uploading backup") : tr("تنزيل النسخة", "Downloading backup")
                            let completed = ByteCountFormatter.string(fromByteCount: done, countStyle: .file)
                            let size = ByteCountFormatter.string(fromByteCount: total, countStyle: .file)
                            sync.status = label + " · " + tr("\(completed) من \(size)", "\(completed) of \(size)")
                        } else if stage == 0 {
                            sync.status = tr("جارٍ إكمال النقل…", "Finishing sync…")
                        }
                    }
                }
                guard let result else { throw AccountError("تعذّر تشغيل المزامنة.") }
                defer { payrollSyncFree(result) }
                return Data(bytes:result,count:strlen(result))
            }
        }.value
        let reply = try JSONDecoder().decode(SyncReply.self,from:bytes)
        if let error = reply.error { throw AccountError(error) }
        return reply
    }
}

struct WorkspaceSyncProgress: View {
    let sync: WorkspaceSync

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(sync.status).font(.subheadline.weight(.semibold))
            ProgressView(value: sync.transferFraction)
                .progressViewStyle(.linear)
                .accessibilityLabel(tr("تقدم المزامنة", "Sync progress"))
        }
        .padding(.horizontal)
        .padding(.vertical, 8)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.regularMaterial)
    }
}

struct WorkspaceSyncSettings: View {
    @State private var sync = WorkspaceSync.shared
    @State private var confirmImport = false
    var body: some View {
        Form {
            Section("OneDrive") {
                if sync.linked {
                    Button { Task { await sync.synchronize(action:"backup-upload") } } label: {
                        Label(tr("رفع نسخة كاملة", "Upload full backup"), systemImage:"arrow.up.doc")
                    }
                    Button { confirmImport = true } label: {
                        Label(tr("استيراد أحدث نسخة", "Import latest backup"), systemImage:"arrow.down.doc")
                    }
                    if !sync.busy && sync.pendingUpload {
                        Label(tr("تعديلات محلية بانتظار الرفع", "Local changes pending upload"), systemImage: "arrow.up.circle")
                    }
                    if let last = sync.last {
                        LabeledContent(tr("آخر نقل ناجح", "Last successful transfer")) { Text(last.formatted()) }
                    }
                    NavigationLink(tr("آخر نسختين والاستعادة", "Last two backups and restore")) { WorkspaceHistoryView() }
                    Button(tr("تسجيل الخروج", "Sign out"),role:.destructive) { sync.signOut() }
                }
                Button(sync.linked ? tr("إعادة ربط الحساب", "Reconnect account") : tr("تسجيل الدخول إلى Microsoft", "Sign in to Microsoft")) { Task { await sync.signIn() } }
            }.disabled(sync.busy)
            Section(tr("حالة النقل", "Transfer status")) {
                HStack { if sync.busy { ProgressView() }; Text(sync.status) }
            }
        }
        .alert(tr("استيراد أحدث نسخة؟", "Import the latest backup?"), isPresented:$confirmImport) {
            Button(tr("استيراد", "Import"), role:.destructive) { Task { await sync.synchronize(action:"backup-download") } }
            Button(tr("إلغاء", "Cancel"),role:.cancel) {}
        } message: {
            Text(tr("بعد التحقق من الملف ستُستبدل بيانات هذا الجهاز ببيانات النسخة الموجودة على OneDrive، بما فيها الدفعات.", "After verification, the OneDrive backup will replace this device’s data, including its batches."))
        }
        .onAppear { sync.refreshPendingUpload() }
        .navigationTitle(tr("النسخ عبر OneDrive", "OneDrive backups"))
    }
}

private struct WorkspaceHistoryView: View {
    @State private var sync = WorkspaceSync.shared
    @State private var until = Date()
    @State private var filter = false
    @State private var activeUntil: Date?
    @State private var confirming = false
    var body: some View {
        List {
            Section {
                Toggle(tr("حتى تاريخ ووقت محددين", "Up to a date and time"),isOn:$filter)
                if filter { DatePicker(tr("حتى", "Until"),selection:$until) }
                Button(tr("عرض النقاط", "Show sync points")) {
                    activeUntil = filter ? until : nil
                    Task { await sync.synchronize(action:"history",until:activeUntil) }
                }
                Text(sync.status).font(.footnote)
                if sync.busy { ProgressView() }
            }
            if let p = sync.selected, let v = sync.preview {
                Section(tr("معاينة نقطة المزامنة", "Sync point preview")) {
                    Text(p.date.formatted(date:.abbreviated,time:.standard))
                    LabeledContent(tr("عدد الملاك", "Staff"),value:Num.format(v.staff))
                    LabeledContent(tr("قيود الرواتب", "Payroll records"),value:Num.format(v.employees))
                    LabeledContent(tr("دفعات الإعادة", "Refund batches"),value:Num.format(v.refunds))
                    LabeledContent(tr("مجموع صافي الرواتب", "Total net salaries"),value:Money.format(v.net))
                    LabeledContent(tr("مجموع الاستقطاعات", "Total deductions"),value:Money.format(v.deductions))
                    Button(tr("استعادة هذه النقطة", "Restore this point"),role:.destructive) { confirming = true }
                }
            }
            Section(tr("نقاط المزامنة", "Sync points")) {
                ForEach(sync.points) { p in
                    Button {
                        Task { await sync.synchronize(action:"preview",point:p) }
                    } label: {
                        LabeledContent(p.date.formatted(date:.abbreviated,time:.standard),
                                       value:p.id == sync.historyHead ? tr("الحالية", "Current") + " · " + p.label : p.label)
                    }
                }
                if sync.cursor != nil {
                    Button(tr("عرض الأقدم", "Show older")) {
                        Task { await sync.synchronize(action:"history",more:true,until:activeUntil) }
                    }
                }
            }
        }
        .disabled(sync.busy)
        .navigationTitle(tr("سجل المزامنة", "Sync history"))
        .task { await sync.synchronize(action:"history") }
        .alert(tr("استعادة النقطة المحددة؟", "Restore the selected point?"),isPresented:$confirming) {
            Button(tr("استعادة", "Restore"),role:.destructive) {
                if let p = sync.selected { Task { await sync.synchronize(action:"backup-download",point:p) } }
            }
            Button(tr("إلغاء", "Cancel"),role:.cancel) {}
        } message: {
            Text(tr("ستُستبدل بيانات هذا الجهاز ببيانات النقطة المختارة.", "The selected backup will replace the data on this device."))
        }
    }
}
