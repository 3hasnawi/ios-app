import Foundation
import SwiftUI

@_silgen_name("payroll_sync") private func payrollSync(_ input: UnsafePointer<CChar>, _ transfer: @convention(c) (Int32, Int64, Int64) -> Void) -> UnsafeMutablePointer<CChar>?
@_silgen_name("payroll_sync_free") private func payrollSyncFree(_ output: UnsafeMutablePointer<CChar>)

struct WorkspacePoint: Decodable, Identifiable {
    let id: String
    let at: Int64
    var date: Date { Date(timeIntervalSince1970: Double(at)/1000) }
}
private struct SyncReply: Decodable {
    let head: String?
    let points: [WorkspacePoint]?
    let status: String
    let error, file, transaction, warning: String?
}
@Observable @MainActor
final class WorkspaceSync {
    static let shared = WorkspaceSync()
    private let account = OneDriveAccount()
    private(set) var failed = false
    var linked = false
    var status = tr("نقل النسخ يدوي", "Manual backup transfer")
    private(set) var transferring = false
    private(set) var showingProgress = false
    private(set) var transferFraction: Double?
    var busy = false
    var points: [WorkspacePoint] = []
    var historyHead = ""
    init() {
        linked = (try? account.storedToken()) != nil
    }
    private func show(_ error: Error) {
        failed = true
        status = (error as? OneDriveAuthorizationReplyError)?.message ?? error.localizedDescription
    }
    private var workspaceDirectory: URL {
        URL.applicationSupportDirectory.appendingPathComponent("OneDriveWorkspace", isDirectory: true)
    }

    func rememberPoint(_ reason: String) async {
        guard linked else { return }
        let dir = workspaceDirectory
        do { _ = try await call(["action":"note","reason":reason,"dir":dir.path]) }
        catch { show(error) }
    }
    private var mayInstall: Bool {
        UIApplication.shared.applicationState == .active && !AppLock.shared.isLocked
    }

    func synchronize(action: String, point: WorkspacePoint? = nil,
                     reconnect: Bool = false) async {
        let store = BackupManager.shared
        guard !busy else { return }
        failed = false
        if action == "history" { points = []; historyHead = "" }
        guard store.workSyncAvailable else {
            failed = true
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
        }
        var exported: URL?, received: URL?, validated: URL?
        defer {
            if let exported { BackupFormat.removeExport(exported) }
            if let received { try? FileManager.default.removeItem(at: received) }
            if let validated { try? FileManager.default.removeItem(at: validated) }
        }
        do {
            status = tr("جارٍ الاتصال بـ OneDrive…", "Connecting to OneDrive…")
            let token = try await account.backupAccess(reconnect: reconnect)
            try Task.checkCancellation()
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
                points = reply.points ?? []
                historyHead = reply.head ?? ""
                status = points.isEmpty ? tr("لا توجد نسخة محفوظة على OneDrive.", "No backup saved on OneDrive.") : tr("اختر النسخة المراد استيرادها.", "Choose the backup to import.")
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
            if action == "backup-download" { points = [] }
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

struct OneDriveBackupView: View {
    let operation: BackupOperation
    @State private var sync = WorkspaceSync.shared
    @State private var selection: WorkspacePoint?
    @State private var confirmImport = false
    @State private var started = false

    private func start(reconnect: Bool = false) async {
        await sync.synchronize(action: operation == .upload ? "backup-upload" : "history", reconnect: reconnect)
    }

    var body: some View {
        List {
            if !sync.showingProgress {
                HStack {
                    if sync.busy { ProgressView() }
                    Text(sync.status)
                }
            }
            if operation == .restore && !sync.busy {
                ForEach(sync.points) { point in
                    Button {
                        selection = point
                        confirmImport = true
                    } label: {
                        LabeledContent(point.id == sync.historyHead ? tr("أحدث نسخة", "Latest backup") : tr("النسخة السابقة", "Previous backup")) {
                            Text(point.date.formatted(date: .abbreviated, time: .standard))
                        }
                    }
                }
            }
            if sync.failed && !sync.busy {
                Button(tr("إعادة المحاولة", "Try again")) { Task { await start() } }
                Button(tr("إعادة ربط الحساب", "Reconnect account")) { Task { await start(reconnect: true) } }
            }
        }
        .navigationTitle(operation.title + " · OneDrive")
        .task {
            guard !started else { return }
            started = true
            await start()
        }
        .alert(tr("استيراد النسخة؟", "Import backup?"), isPresented: $confirmImport, presenting: selection) { point in
            Button(tr("استيراد", "Import"), role: .destructive) {
                Task { await sync.synchronize(action: "backup-download", point: point) }
            }
            Button(tr("إلغاء", "Cancel"), role: .cancel) {}
        } message: { point in
            Text(tr("ستُستبدل بيانات هذا الجهاز، بما فيها الدفعات، ببيانات نسخة", "This device’s data, including its batches, will be replaced with the backup from") + " " + point.date.formatted(date: .abbreviated, time: .standard))
        }
    }
}
