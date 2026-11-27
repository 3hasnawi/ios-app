import Foundation
import SwiftUI

@_silgen_name("payroll_sync") private func payrollSync(_ input: UnsafePointer<CChar>) -> UnsafeMutablePointer<CChar>?
@_silgen_name("payroll_sync_free") private func payrollSyncFree(_ output: UnsafeMutablePointer<CChar>)

struct SyncConflict: Decodable, Identifiable {
    let key, ticket, local, remote: String
    var id: String { ticket }
    var label: String {
        let parts = (try? JSONDecoder().decode([String].self, from: Data(key.utf8))) ?? [key]
        return parts.dropFirst().joined(separator: " · ")
    }
}
private struct SyncReply: Decodable {
    let status: String
    let error, file, transaction: String?
    let conflicts: [SyncConflict]?
    let apply: Bool?
}
@Observable @MainActor
final class WorkspaceSync {
    static let shared = WorkspaceSync()
    private let account = OneDriveAccount()
    private let defaults = UserDefaults.standard
    var enabled = UserDefaults.standard.bool(forKey: "oneDriveSyncEnabled")
    var linked = false
    var status = tr("المزامنة متوقفة", "Sync is off")
    var busy = false
    var conflicts: [SyncConflict] = []
    private(set) var choices: [String:[String:String]] = [:]
    var last = UserDefaults.standard.object(forKey: "oneDriveLastSync") as? Date
    init() {
        linked = (try? account.storedToken()) != nil
        if linked { status = enabled ? tr("بانتظار المزامنة", "Waiting to sync") : tr("المزامنة متوقفة", "Sync is off") }
    }
    func enable(_ value: Bool) { enabled = value; defaults.set(value,forKey:"oneDriveSyncEnabled") }
    func signIn() async {
        guard !busy else { return }
        busy = true
        do { try await account.login(); linked = true; enable(true); busy = false; await synchronize() }
        catch { busy = false; show(error) }
    }
    func signOut() {
        guard !busy else { return }
        do { try account.logout(); linked = false; enable(false); conflicts = []; choices = [:]; status = tr("تم تسجيل الخروج", "Signed out") }
        catch { show(error) }
    }
    private func show(_ error: Error) { status = (error as? OneDriveAuthorizationReplyError)?.message ?? error.localizedDescription }
    func choose(_ conflict: SyncConflict, _ side: String) { choices[conflict.key] = ["ticket":conflict.ticket,"side":side] }
    func automatic() async {
        guard enabled, conflicts.isEmpty, UIApplication.shared.applicationState == .active else { return }
        await synchronize()
    }
    func synchronize() async {
        let store = BackupManager.shared
        guard enabled, !busy, store.workSyncAvailable else { return }
        busy = true
        defer { busy = false }
        var exported: URL?, validated: URL?
        defer {
            if let exported { BackupFormat.removeExport(exported) }
            if let validated { try? FileManager.default.removeItem(at: validated) }
        }
        do {
            status = tr("جارٍ مزامنة البيانات…", "Syncing data…")
            let token = try await account.access()
            linked = true
            let stamp = store.workSyncStamp
            var dir = URL.applicationSupportDirectory.appendingPathComponent("OneDriveWorkspace",isDirectory:true)
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories:true, attributes:[.protectionKey:FileProtectionType.completeUntilFirstUserAuthentication])
            var policy = URLResourceValues()
            policy.isExcludedFromBackup = true
            try dir.setResourceValues(policy)
            var args: [String:Any] = ["action":"prepare","dir":dir.path,"token":token,"stamp":stamp,"choices":choices]
            var reply = try await call(args)
            if reply.status == "export" {
                do { exported = try await store.workSyncExport(); args["local"] = exported?.path }
                catch BackupError.emptyData { args["empty"] = true }
                guard store.workSyncStamp == stamp else { throw BackupError.sourceChanged }
                reply = try await call(args)
            }
            if reply.status == "conflict" {
                conflicts = reply.conflicts ?? []
                choices = choices.filter { entry in !conflicts.contains { $0.key == entry.key && $0.ticket != entry.value["ticket"] } }
                status = tr("راجع القيود المختلفة", "Review conflicting records")
                return
            }
            if reply.status == "waiting" { status = tr("بانتظار بيانات للمزامنة", "Waiting for data"); return }
            if reply.status == "ready", let path = reply.file, let transaction = reply.transaction {
                validated = try await store.workSyncValidate(URL(fileURLWithPath:path))
                guard store.workSyncStamp == stamp, store.workSyncAvailable else { throw BackupError.sourceChanged }
                args["transaction"] = transaction
                args["action"] = "publish"
                reply = try await call(args)
                guard reply.status == "committed", store.workSyncStamp == stamp else { throw BackupError.sourceChanged }
                if reply.apply == true, let validated { try await store.workSyncApply(validated,expected:stamp) }
                args["action"] = "ack"
                args["stamp"] = store.workSyncStamp
                reply = try await call(args)
            }
            guard reply.status == "synced" else { throw AccountError("تعذّر إكمال المزامنة.") }
            conflicts = []; choices = [:]; last = Date(); defaults.set(last,forKey:"oneDriveLastSync")
            status = tr("البيانات متزامنة", "Data is up to date")
        } catch { show(error) }
    }
    private func call(_ args: [String:Any]) async throws -> SyncReply {
        let request = try JSONSerialization.data(withJSONObject:args)
        let bytes = try await Task.detached(priority:.utility) {
            try String(decoding:request,as:UTF8.self).withCString { input -> Data in
                guard let result = payrollSync(input) else { throw AccountError("تعذّر تشغيل المزامنة.") }
                defer { payrollSyncFree(result) }
                return Data(bytes:result,count:strlen(result))
            }
        }.value
        let reply = try JSONDecoder().decode(SyncReply.self,from:bytes)
        if let error = reply.error { throw AccountError(error) }
        return reply
    }
}

struct WorkspaceSyncSettings: View {
    @State private var sync = WorkspaceSync.shared
    var body: some View {
        Form {
            Section("OneDrive") {
                if sync.linked {
                    Toggle(tr("المزامنة التلقائية", "Automatic sync"),isOn:Binding(get:{sync.enabled},set:{ value in
                        sync.enable(value)
                        if value { Task { await sync.synchronize() } }
                    }))
                    Button(tr("مزامنة الآن", "Sync now")) { Task { await sync.synchronize() } }.disabled(!sync.enabled)
                    Button(tr("تسجيل الخروج", "Sign out"),role:.destructive) { sync.signOut() }
                }
                Button(sync.linked ? tr("إعادة ربط الحساب", "Reconnect account") : tr("تسجيل الدخول إلى Microsoft", "Sign in to Microsoft")) { Task { await sync.signIn() } }
            }.disabled(sync.busy)
            Section(tr("حالة المزامنة", "Sync status")) {
                HStack { if sync.busy { ProgressView() }; Text(sync.status) }
                if let last = sync.last { LabeledContent(tr("آخر مزامنة", "Last sync")) { Text(last.formatted()) } }
            }
            if !sync.conflicts.isEmpty {
                Section(tr("القيود المتعارضة", "Conflicting records")) {
                    ForEach(sync.conflicts) { row in
                        VStack(alignment:.leading,spacing:8) {
                            Text(row.label).font(.headline)
                            ForEach(["local","remote"],id:\.self) { side in
                                Button {
                                    sync.choose(row,side)
                                } label: {
                                    HStack {
                                        Image(systemName:sync.choices[row.key]?["side"] == side ? "checkmark.circle.fill" : "circle")
                                        Text((side == "local" ? tr("هذا الجهاز", "This device") : "OneDrive") + " · " + (side == "local" ? row.local : row.remote))
                                    }
                                }.buttonStyle(.borderless)
                            }
                        }
                    }
                    Button(tr("حفظ الاختيارات والمزامنة", "Apply selections and sync")) { Task { await sync.synchronize() } }
                        .disabled(sync.busy || !sync.conflicts.allSatisfy { sync.choices[$0.key]?["ticket"] == $0.ticket })
                }
            }
        }
        .navigationTitle(tr("مزامنة OneDrive", "OneDrive sync"))
    }
}
