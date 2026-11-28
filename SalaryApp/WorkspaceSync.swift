import Foundation
import SwiftUI

@_silgen_name("payroll_sync") private func payrollSync(_ input: UnsafePointer<CChar>, _ transfer: @convention(c) (Int32, Int64, Int64) -> Void) -> UnsafeMutablePointer<CChar>?
@_silgen_name("payroll_sync_free") private func payrollSyncFree(_ output: UnsafeMutablePointer<CChar>)

struct SyncConflict: Decodable, Identifiable {
    let key, ticket, local, remote: String
    var id: String { ticket }
    var label: String {
        let parts = (try? JSONDecoder().decode([String].self, from: Data(key.utf8))) ?? [key]
        return parts.dropFirst().joined(separator: " · ")
    }
}
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
    let error, file, transaction: String?
    let conflicts: [SyncConflict]?
    let apply: Bool?
}
@Observable @MainActor
final class WorkspaceSync {
    static let shared = WorkspaceSync()
    private let account = OneDriveAccount()
    private var afterSave: Task<Void, Never>?
    private let defaults = UserDefaults.standard
    var enabled = UserDefaults.standard.bool(forKey: "oneDriveSyncEnabled")
    var linked = false
    var status = tr("المزامنة متوقفة", "Sync is off")
    private(set) var transferring = false
    var busy = false
    var points: [WorkspacePoint] = []
    var historyHead = ""
    var cursor: String?
    var preview: WorkspacePreview?
    var selected: WorkspacePoint?
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
        do { try account.logout(); linked = false; points = []; preview = nil; selected = nil; historyHead = ""; cursor = nil; enable(false); conflicts = []; choices = [:]; status = tr("تم تسجيل الخروج", "Signed out") }
        catch { show(error) }
    }
    private func show(_ error: Error) { status = (error as? OneDriveAuthorizationReplyError)?.message ?? error.localizedDescription }
    func choose(_ conflict: SyncConflict, _ side: String) { choices[conflict.key] = ["ticket":conflict.ticket,"side":side] }
    static let intervals = [1,5,15,30,60,120]
    var interval = {
        let saved = UserDefaults.standard.integer(forKey:"oneDriveIntervalMinutes")
        return WorkspaceSync.intervals.contains(saved) ? saved : 15
    }()
    private var lastAttempt: Date?
    func setInterval(_ minutes: Int) {
        guard Self.intervals.contains(minutes) else { return }
        interval = minutes
        defaults.set(minutes,forKey:"oneDriveIntervalMinutes")
    }
    func rememberPoint(_ reason: String) async {
        guard linked else { return }
        let dir = URL.applicationSupportDirectory.appendingPathComponent("OneDriveWorkspace",isDirectory:true)
        do { _ = try await call(["action":"note","reason":reason,"dir":dir.path]) }
        catch { show(error) }
    }
    func localDataSaved() {
        guard enabled else { return }
        afterSave?.cancel()
        afterSave = Task {
            do { try await Task.sleep(for:.seconds(2)) } catch { return }
            await automatic()
        }
    }

    func automatic() async {
        guard enabled, conflicts.isEmpty, UIApplication.shared.applicationState == .active,
              lastAttempt.map({ Date().timeIntervalSince($0) >= Double(interval * 60) }) ?? true else { return }
        await synchronize(automatic:true)
    }
    private func mayInstall(automatic: Bool) -> Bool {
        guard UIApplication.shared.applicationState == .active, !AppLock.shared.isLocked else { return false }
        guard automatic else { return true }
        let router = AppRouter.shared
        guard (router.tab == .payroll || router.tab == .settings), !router.showReports,
              let scene = UIApplication.shared.connectedScenes.compactMap({ $0 as? UIWindowScene }).first(where: { $0.activationState == .foregroundActive }),
              let root = scene.keyWindow?.rootViewController, root.presentedViewController == nil else { return false }
        func editing(_ controller: UIViewController) -> Bool {
            if controller.viewIfLoaded?.window == nil { return false }
            if let nav = controller as? UINavigationController, nav.viewControllers.count > 1 { return true }
            return controller.presentedViewController != nil || controller.children.contains(where:editing)
        }
        return !editing(root)
    }
    func synchronize(automatic: Bool = false, action: String = "prepare", point: WorkspacePoint? = nil,
                     more: Bool = false, until: Date? = nil) async {
        let store = BackupManager.shared
        guard (enabled || (!automatic && linked)), !busy, store.workSyncAvailable else { return }
        if action == "prepare" { lastAttempt = Date() }
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
            var args: [String:Any] = ["action":action,"dir":dir.path,"token":token,"stamp":stamp,"choices":choices]
            args["resumeDeferred"] = automatic
            args["head"] = historyHead
            args["revision"] = point?.id
            if action == "history" {
                if more { args["cursor"] = cursor }
                else { points = []; cursor = nil }
                if let until { args["until"] = Int64(until.timeIntervalSince1970 / 60) * 60_000 + 59_999 }
            }
            if action == "history" || action == "preview" { preview = nil; selected = nil }
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
            if reply.status == "export" {
                status = tr("تجهيز المزامنة", "Preparing sync")
                do { exported = try await store.workSyncExport(automatic: automatic); args["local"] = exported?.path }
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
                conflicts = []
                if reply.apply == true && !mayInstall(automatic:automatic) {
                    status = tr("توجد تعديلات ستصل عند العودة إلى الرئيسية", "Updates will be applied when you return home")
                    return
                }
                status = tr("جارٍ التحقق من النسخة…", "Verifying the archive…")
                validated = try await store.workSyncValidate(URL(fileURLWithPath:path))
                guard store.workSyncStamp == stamp, store.workSyncAvailable else { throw BackupError.sourceChanged }
                args["transaction"] = transaction
                args["action"] = "publish"
                reply = try await call(args)
                guard reply.status == "committed", store.workSyncStamp == stamp else { throw BackupError.sourceChanged }
                if reply.apply == true, let validated {
                    guard mayInstall(automatic:automatic) else { throw BackupError.sourceChanged }
                    try await store.workSyncApply(validated,expected:stamp)
                }
                args["action"] = "ack"
                args["stamp"] = reply.apply == true ? store.workSyncStamp : stamp
                reply = try await call(args)
            }
            guard reply.status == "synced" else { throw AccountError("تعذّر إكمال المزامنة.") }
            conflicts = []; choices = [:]; last = Date(); defaults.set(last,forKey:"oneDriveLastSync")
            status = tr("البيانات متزامنة", "Data is up to date")
            if action == "restore" {
                preview = nil; selected = nil; points = []; cursor = nil
                status = tr("تمت الاستعادة وحُفظت نقطة للرجوع إليها", "Restored and saved a recovery point")
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
                        if stage >= 2 {
                            let label = stage == 2 ? tr("رفع التعديلات", "Uploading changes") : tr("استلام وتجميع النسخة", "Receiving and assembling the archive")
                            let completed = ByteCountFormatter.string(fromByteCount: done, countStyle: .file)
                            let size = ByteCountFormatter.string(fromByteCount: total, countStyle: .file)
                            sync.status = label + " · " + tr("\(completed) من \(size)", "\(completed) of \(size)")
                        } else if stage == 0 {
                            sync.status = tr("جارٍ إكمال المزامنة…", "Finishing sync…")
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
                    if sync.enabled {
                        Picker(tr("فحص التغييرات كل", "Check for changes every"),selection:Binding(get:{sync.interval},set:{sync.setInterval($0)})) {
                            ForEach(WorkspaceSync.intervals,id:\.self) { m in
                                Text(Num.format(m) + " " + tr("دقيقة", "min")).tag(m)
                            }
                        }
                    }
                    Button(tr("مزامنة الآن", "Sync now")) { Task { await sync.synchronize() } }
                    NavigationLink(tr("سجل المزامنة والاستعادة", "Sync history and restore")) { WorkspaceHistoryView() }
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
                    if p.id != sync.historyHead {
                        Button(tr("استعادة هذه النقطة", "Restore this point"),role:.destructive) { confirming = true }
                    }
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
                if let p = sync.selected { Task { await sync.synchronize(action:"restore",point:p) } }
            }
            Button(tr("إلغاء", "Cancel"),role:.cancel) {}
        } message: {
            Text(tr("سيُحفظ الوضع الحالي قبل الاستعادة. تنتقل النقطة المختارة إلى أجهزتك عند المزامنة.", "Your current state will be saved first. Other devices receive the restored data when they sync."))
        }
    }
}
