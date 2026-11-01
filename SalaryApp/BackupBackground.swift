import Foundation
import BackgroundTasks

/// يطلب iOS وقت تشغيل دوري في الخلفية؛ موعد التنفيذ الفعلي يحدده النظام.
enum BackupBackground {
    private static let identifier = "com.example.SalaryApp.periodicBackup"
    private static let scheduleKey = "backupSchedule"
    private static let fileKey = "autoBackupFile"
    private static let folderKey = "autoBackupFolder"
    private static let lastKey = "autoBackupLast"

    static func register() {
        BGTaskScheduler.shared.register(forTaskWithIdentifier: identifier, using: nil) { task in
            guard let task = task as? BGProcessingTask else {
                task.setTaskCompleted(success: false)
                return
            }
            let work = Task { @MainActor in
                await BackupManager.shared.checkServerNotification()
                await BackupManager.shared.autoBackupIfNeeded()
                schedule()
                if !Task.isCancelled { task.setTaskCompleted(success: true) }
            }
            task.expirationHandler = {
                work.cancel()
                task.setTaskCompleted(success: false)
            }
        }
    }

    static func schedule() {
        BGTaskScheduler.shared.cancel(taskRequestWithIdentifier: identifier)
        guard !BackupManager.manualOnly else { return }
        let raw = UserDefaults.standard.string(forKey: scheduleKey) ?? ""
        let interval = BackupSchedule(rawValue: raw)?.interval
        let hasDestination = BackupServerConfig.ready || UserDefaults.standard.data(forKey: fileKey) != nil || UserDefaults.standard.data(forKey: folderKey) != nil
        let monitorServer = BackupServerConfig.ready && UserDefaults.standard.bool(forKey: BackupServerConfig.notifyKey)
        guard (interval != nil && hasDestination) || monitorServer else { return }
        let request = BGProcessingTaskRequest(identifier: identifier)
        request.requiresNetworkConnectivity = true
        request.requiresExternalPower = false
        let last = UserDefaults.standard.object(forKey: lastKey) as? Date
        let backupDue = interval.map { last?.addingTimeInterval($0) ?? .now }
        let monitorDue = monitorServer ? Date().addingTimeInterval(15 * 60) : nil
        let due = [backupDue, monitorDue].compactMap { $0 }.min() ?? .now
        request.earliestBeginDate = max(due, Date().addingTimeInterval(15 * 60))
        try? BGTaskScheduler.shared.submit(request)
    }
}
