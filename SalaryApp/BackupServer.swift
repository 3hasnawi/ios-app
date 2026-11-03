import SwiftUI
import Security
import CryptoKit
import Network

/// تحفظ كلمة الوصول في سلسلة المفاتيح، ولا تدخل في النسخة الاحتياطية.
nonisolated enum BackupServerConfig {
    static let enabledKey = "backupServerEnabled"
    static let addressKey = "backupServerAddress"
    static let pendingKey = "backupServerFirstPending"
    static let seenKey = "backupServerSeen"
    static let conflictKey = "backupServerNewer"
    static let firstChoiceKey = "backupServerFirstChoice"
    static let notifyKey = "backupServerNotify"
    static let notifiedKey = "backupServerNotified"
    static let seenUploadKey = "backupServerSeenUploadID"
    private static let deviceKey = "backupServerDeviceID"
    static var deviceID: String {
        let defaults = UserDefaults.standard
        if let raw = defaults.string(forKey: deviceKey), let id = UUID(uuidString: raw) { return id.uuidString.lowercased() }
        let id = UUID().uuidString.lowercased()
        defaults.set(id, forKey: deviceKey)
        return id
    }
    static var seen: Date? {
        guard let value = UserDefaults.standard.string(forKey: seenKey) else { return nil }
        return BackupServerUpload.parseDate(value)
    }
    static func remember(_ date: Date, uploadID: String? = nil) {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        UserDefaults.standard.set(formatter.string(from: date), forKey: seenKey)
        if let uploadID, UUID(uuidString: uploadID) != nil { UserDefaults.standard.set(uploadID.lowercased(), forKey: seenUploadKey) }
        else { UserDefaults.standard.removeObject(forKey: seenUploadKey) }
    }
    private static let account = "salary-backup-server-token"
    static var enabled: Bool { UserDefaults.standard.bool(forKey: enabledKey) }
    static var address: String { UserDefaults.standard.string(forKey: addressKey) ?? "" }
    static var token: String {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrAccount as String: account,
                                    kSecReturnData as String: true, kSecMatchLimit as String: kSecMatchLimitOne]
        var result: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess, let data = result as? Data else { return "" }
        return String(data: data, encoding: .utf8) ?? ""
    }
    static func saveToken(_ value: String) -> Bool {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrAccount as String: account]
        let data = Data(value.utf8)
        let status = SecItemUpdate(query as CFDictionary, [kSecValueData as String: data] as CFDictionary)
        if status == errSecSuccess { return true }
        guard status == errSecItemNotFound else { return false }
        var item = query
        item[kSecValueData as String] = data
        item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        return SecItemAdd(item as CFDictionary, nil) == errSecSuccess
    }
    static func url(_ address: String) -> URL? {
        guard let parts = URLComponents(string: address.trimmingCharacters(in: .whitespacesAndNewlines)),
              let host = parts.host, parts.user == nil, parts.password == nil,
              parts.query == nil, parts.fragment == nil, parts.path.isEmpty || parts.path == "/",
              let ip = IPv4Address(host) else { return nil }
        let bytes = Array(ip.rawValue)
        guard parts.scheme?.lowercased() == "http", bytes[0] == 10 ||
            (bytes[0] == 172 && (16...31).contains(Int(bytes[1]))) ||
            (bytes[0] == 192 && bytes[1] == 168) else { return nil }
        return parts.url
    }
    static var configured: Bool { url(address) != nil && token.utf8.count >= 32 }
    static var ready: Bool { enabled && configured }
}

nonisolated struct BackupServerStatus {
    let count: Int
    let size: Int64
    let free: Int64
    let latest: Date?
    var uploadID: String? = nil
    var deviceID: String? = nil
    var deviceName: String? = nil
    var backupFormatGuard = false
    var newer: Bool {
        guard let latest else { return false }
        if let uploadID { return uploadID != UserDefaults.standard.string(forKey: BackupServerConfig.seenUploadKey) }
        guard let seen = BackupServerConfig.seen else { return true }
        return latest.timeIntervalSince(seen) > 2
    }
    var description: String {
        guard let latest else { return tr("لا توجد نسخة بعد", "No backup yet") }
        let source = deviceName ?? tr("جهاز غير معروف", "Unknown device")
        return "\(latest.formatted(date: .abbreviated, time: .shortened)) · \(source) (\(ByteCountFormatter.string(fromByteCount: size, countStyle: .file)))"
    }
}

nonisolated enum BackupServerUpload {
    static func parseDate(_ value: String) -> Date? {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = f.date(from: value) { return date }
        f.formatOptions = [.withInternetDateTime]
        return f.date(from: value)
    }
    static func failure(_ code: Int, fallback: String) -> NSError {
        let reason: String
        switch code {
        case 401: reason = tr("رمز الوصول غير صحيح؛ انسخه من إعدادات الإضافة في Home Assistant.", "Incorrect access token; copy it from the Home Assistant add-on settings.")
        case 400: reason = tr("رفض السيرفر النسخة: الملف غير مكتمل أو نسخة السيرفر فيها أشهر أو بيانات غير موجودة هنا؛ استرجع نسخة السيرفر أولاً.", "The server rejected the backup: the file is incomplete or the server has months or data missing here. Restore the server backup first.")
        case 409: reason = tr("السيرفر مضبوط على مزامنة التعديلات التي لا يدعمها هذا الإصدار.", "The server is configured for edit synchronization, which this version does not support.")
        default: reason = fallback
        }
        return NSError(domain: "BackupServer", code: code, userInfo: [NSLocalizedDescriptionKey: reason])
    }
    static func status(base: URL, token: String) async throws -> BackupServerStatus {
        var request = URLRequest(url: base.appendingPathComponent("v1/status"))
        request.setValue("Bearer " + token, forHTTPHeaderField: "Authorization")
        request.cachePolicy = .reloadIgnoringLocalCacheData
        request.timeoutInterval = 10
        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw URLError(.badServerResponse) }
        guard http.statusCode == 200 else { throw failure(http.statusCode, fallback: tr("تعذّر قراءة حالة السيرفر.", "Could not read server status.")) }
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              object["service"] as? String == "salary-backup",
              let count = (object["backup_count"] as? NSNumber)?.intValue, count >= 0,
              let size = (object["backup_bytes"] as? NSNumber)?.int64Value, size >= 0,
              let free = (object["disk_free"] as? NSNumber)?.int64Value, free >= 0,
              let backups = object["backups"] as? [[String: Any]] else {
            throw failure(0, fallback: tr("رد السيرفر غير مفهوم أو العنوان لا يخص سيرفر النسخ.", "Invalid response or address is not a backup server."))
        }
        let latest: Date?
        if count > 0 {
            guard let raw = (backups.first?["uploaded_at"] ?? backups.first?["created_at"]) as? String, let date = parseDate(raw) else {
                throw failure(0, fallback: tr("تعذّر قراءة تاريخ نسخة السيرفر؛ لم يُرفع شيء.", "Could not read the server backup date; nothing was uploaded."))
            }
            latest = date
        } else {
            guard backups.isEmpty else { throw failure(0, fallback: tr("حالة السيرفر غير متسقة.", "Inconsistent server status.")) }
            latest = nil
        }
        let first = backups.first
        let rawID = first?["upload_id"] as? String
        let uploadID = rawID.flatMap { UUID(uuidString: $0)?.uuidString.lowercased() }
        let device = first?["device"] as? [String: Any]
        let deviceID = (device?["id"] as? String).flatMap { UUID(uuidString: $0)?.uuidString.lowercased() }
        let name = device?["name"] as? String
        let deviceName = deviceID != nil && name?.isEmpty == false && (name?.count ?? 0) <= 128 ? name : nil
        return BackupServerStatus(count: count, size: size, free: free, latest: latest, uploadID: uploadID, deviceID: deviceID, deviceName: deviceName, backupFormatGuard: object["backup_format_guard"] as? Bool == true)
    }

    /// لا نضغط مئات الميغابايتات إذا لم يكن السيرفر قابلاً للوصول؛ أي رد HTTP يكفي.
    static func checkReachability(base: URL) async throws {
        var request = URLRequest(url: base)
        request.httpMethod = "HEAD"
        request.cachePolicy = .reloadIgnoringLocalCacheData
        request.timeoutInterval = 8
        do {
            let (_, response) = try await URLSession.shared.data(for: request)
            guard response is HTTPURLResponse else { throw URLError(.badServerResponse) }
        } catch {
            if Task.isCancelled { throw CancellationError() }
            throw NSError(domain: "BackupServer", code: 2, userInfo: [NSLocalizedDescriptionKey:
                tr("السيرفر غير متاح حالياً؛ ستُعاد محاولة النسخ تلقائياً عند توفر الاتصال.", "The server is currently unreachable; backup will be retried automatically when connectivity is available.")])
        }
    }

    static func sha256(_ file: URL) throws -> String {
        let input = try FileHandle(forReadingFrom: file)
        defer { try? input.close() }
        var hash = SHA256()
        while let bytes = try input.read(upToCount: 1024 * 1024), !bytes.isEmpty { hash.update(data: bytes) }
        return hash.finalize().map { String(format: "%02x", $0) }.joined()
    }
    static func upload(file: URL, base: URL, token: String, deviceID: String, deviceName: String) async throws -> (size: Int64, uploadID: String?, uploaded: Date?) {
        let digest = try await Task.detached(priority: .utility) { try sha256(file) }.value
        var request = URLRequest(url: base.appendingPathComponent("v1/backups"))
        request.httpMethod = "POST"
        request.setValue("Bearer " + token, forHTTPHeaderField: "Authorization")
        request.setValue(digest, forHTTPHeaderField: "X-Content-SHA256")
        request.setValue(deviceID, forHTTPHeaderField: "X-Backup-Device-ID")
        request.setValue(Data(deviceName.utf8).base64EncodedString(), forHTTPHeaderField: "X-Backup-Device-Name")
        request.setValue("ios", forHTTPHeaderField: "X-Backup-Device-Platform")
        request.setValue("application/zip", forHTTPHeaderField: "Content-Type")
        request.timeoutInterval = 600
        let (data, response) = try await URLSession.shared.upload(for: request, fromFile: file)
        guard let http = response as? HTTPURLResponse else { throw URLError(.badServerResponse) }
        guard http.statusCode == 201 else {
            throw failure(http.statusCode, fallback: tr("لم يؤكد السيرفر حفظ النسخة؛ تحقق من العنوان ورمز الوصول.", "Server did not confirm the backup; check the address and token."))
        }
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              object["sha256"] as? String == digest,
              let size = (object["size"] as? NSNumber)?.int64Value, size > 0 else {
            throw NSError(domain: "BackupServer", code: 1, userInfo: [NSLocalizedDescriptionKey: tr("لم يؤكد السيرفر حفظ النسخة؛ تحقق من العنوان ورمز الوصول.", "Server did not confirm the backup; check the address and token.")])
        }
        let uploadID = (object["upload_id"] as? String).flatMap { UUID(uuidString: $0)?.uuidString.lowercased() }
        let uploaded = (object["uploaded_at"] as? String).flatMap(parseDate)
        return (size, uploadID, uploaded)
    }
}

struct BackupServerSettings: View {
    @Environment(\.dismiss) private var dismiss
    @State private var address = BackupServerConfig.address
    @State private var enabled = BackupServerConfig.enabled
    @State private var token = BackupServerConfig.token
    @State private var error = false
    @AppStorage(BackupServerConfig.notifyKey) private var notifyServer = true
    @State private var testing = false
    @State private var testResult: String?
    var body: some View {
        NavigationStack {
            Form {
                Section {
                    TextField("http://192.168.1.10:8765", text: $address)
                        .keyboardType(.URL).textInputAutocapitalization(.never).autocorrectionDisabled()
                        .environment(\.layoutDirection, .leftToRight)
                    SecureField(tr("رمز الوصول", "Access token"), text: $token)
                        .textInputAutocapitalization(.never).autocorrectionDisabled()
                        .environment(\.layoutDirection, .leftToRight)
                    Toggle(tr("تفعيل النسخ إلى السيرفر", "Back up to server"), isOn: $enabled)
                    Toggle(tr("إشعار عند وجود نسخة أحدث", "Notify about newer backups"), isOn: $notifyServer)
                        .onChange(of: notifyServer) { _, enabled in
                            BackupBackground.schedule()
                            if enabled { Task { _ = await AuditNotifications.requestAuthorization() } }
                        }
                    Button {
                        Task {
                            guard let base = BackupServerConfig.url(address), token.utf8.count >= 32,
                                  token.unicodeScalars.allSatisfy({ $0.isASCII && !$0.properties.isWhitespace }) else {
                                testResult = tr("تحقق من العنوان ورمز الوصول.", "Check the address and access token."); return
                            }
                            testing = true
                            defer { testing = false }
                            do {
                                try await BackupServerUpload.checkReachability(base: base)
                                let result = try await BackupServerUpload.status(base: base, token: token)
                                testResult = tr("✓ متصل بسيرفر النسخ · آخر نسخة: \(result.description) · المساحة المتاحة: \(ByteCountFormatter.string(fromByteCount: result.free, countStyle: .file))", "✓ Connected · Latest: \(result.description) · Free: \(ByteCountFormatter.string(fromByteCount: result.free, countStyle: .file))")
                            } catch { testResult = error.localizedDescription }
                        }
                    } label: {
                        HStack { Text(tr("اختبار الاتصال", "Test connection")); Spacer(); if testing { ProgressView() } }
                    }.disabled(testing)
                    if let testResult { Text(testResult).font(.caption).foregroundStyle(.secondary) }
                } header: {
                    Text(tr("السيرفر المحلي", "Local server"))
                } footer: {
                    Text(tr("الاتصال المحلي HTTP غير مشفّر. قد يتأخر إشعار النسخة الأحدث لأن iOS يحدد أوقات الفحص في الخلفية.", "Local HTTP is unencrypted. New-backup notifications may be delayed because iOS controls background checks."))
                }
            }
            .navigationTitle(tr("سيرفر النسخ الاحتياطي", "Backup server"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement: .confirmationAction) {
                Button(tr("تم", "Done")) {
                    let previousToken = BackupServerConfig.token
                    guard (!enabled || (BackupServerConfig.url(address) != nil && token.utf8.count >= 32 && token.unicodeScalars.allSatisfy { $0.isASCII && !$0.properties.isWhitespace })), BackupServerConfig.saveToken(token) else { error = true; return }
                    if BackupServerConfig.address != address || previousToken != token {
                        UserDefaults.standard.removeObject(forKey: BackupServerConfig.seenKey)
                        UserDefaults.standard.removeObject(forKey: BackupServerConfig.seenUploadKey)
                        UserDefaults.standard.removeObject(forKey: BackupServerConfig.conflictKey)
                        UserDefaults.standard.removeObject(forKey: BackupServerConfig.firstChoiceKey)
                        UserDefaults.standard.removeObject(forKey: BackupServerConfig.notifiedKey)
                    }
                    if enabled && (!BackupServerConfig.enabled || BackupServerConfig.address != address || previousToken != token) {
                        UserDefaults.standard.set(true, forKey: BackupServerConfig.pendingKey)
                        UserDefaults.standard.removeObject(forKey: "autoBackupLastError")
                        UserDefaults.standard.removeObject(forKey: "autoBackupLastErrorAt")
                    }
                    UserDefaults.standard.set(address, forKey: BackupServerConfig.addressKey)
                    UserDefaults.standard.set(enabled, forKey: BackupServerConfig.enabledKey)
                    BackupBackground.schedule()
                    if enabled && notifyServer { Task { _ = await AuditNotifications.requestAuthorization() } }
                    dismiss()
                }
            } }
            .alert(tr("تحقق من البيانات", "Check settings"), isPresented: $error) {
                Button(tr("حسناً", "OK"), role: .cancel) {}
            } message: { Text(tr("أدخل عنوان IP محلياً ورمز وصول لا يقل عن 32 حرفاً.", "Enter a local IP address and a token of at least 32 characters.")) }
        }
    }
}
