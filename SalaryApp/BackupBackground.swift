import Foundation
import BackgroundTasks

/// يطلب iOS وقت تشغيل دوري في الخلفية؛ موعد التنفيذ الفعلي يحدده النظام.
enum BackupBackground {
    private static let identifier = "com.example.SalaryApp.periodicBackup"
    private static let scheduleKey = "backupSchedule"
    private static let fileKey = "autoBackupFile"
    private static let lastKey = "autoBackupLast"

    static func register() {
        BGTaskScheduler.shared.register(forTaskWithIdentifier: identifier, using: nil) { task in
            guard let task = task as? BGProcessingTask else {
                task.setTaskCompleted(success: false)
                return
            }
            let work = Task { @MainActor in
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
        let raw = UserDefaults.standard.string(forKey: scheduleKey) ?? ""
        guard let interval = BackupSchedule(rawValue: raw)?.interval,
              UserDefaults.standard.data(forKey: fileKey) != nil else { return }
        let request = BGProcessingTaskRequest(identifier: identifier)
        request.requiresNetworkConnectivity = true
        request.requiresExternalPower = false
        let last = UserDefaults.standard.object(forKey: lastKey) as? Date
        request.earliestBeginDate = max(last?.addingTimeInterval(interval) ?? .now,
                                        Date().addingTimeInterval(15 * 60))
        try? BGTaskScheduler.shared.submit(request)
    }
}
