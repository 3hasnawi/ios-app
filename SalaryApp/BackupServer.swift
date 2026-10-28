import SwiftUI
import Security
import CryptoKit
import Network

/// تحفظ كلمة الوصول في سلسلة المفاتيح، ولا تدخل في النسخة الاحتياطية.
nonisolated enum BackupServerConfig {
    static let enabledKey = "backupServerEnabled"
    static let addressKey = "backupServerAddress"
    static let pendingKey = "backupServerFirstPending"
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
    static var ready: Bool { enabled && url(address) != nil && token.utf8.count >= 32 }
}

nonisolated enum BackupServerUpload {
    static func sha256(_ file: URL) throws -> String {
        let input = try FileHandle(forReadingFrom: file)
        defer { try? input.close() }
        var hash = SHA256()
        while let bytes = try input.read(upToCount: 1024 * 1024), !bytes.isEmpty { hash.update(data: bytes) }
        return hash.finalize().map { String(format: "%02x", $0) }.joined()
    }
    static func upload(file: URL, base: URL, token: String) async throws -> Int64 {
        let digest = try await Task.detached(priority: .utility) { try sha256(file) }.value
        var request = URLRequest(url: base.appendingPathComponent("v1/backups"))
        request.httpMethod = "POST"
        request.setValue("Bearer " + token, forHTTPHeaderField: "Authorization")
        request.setValue(digest, forHTTPHeaderField: "X-Content-SHA256")
        request.setValue("application/zip", forHTTPHeaderField: "Content-Type")
        request.timeoutInterval = 600
        let (data, response) = try await URLSession.shared.upload(for: request, fromFile: file)
        guard let http = response as? HTTPURLResponse, http.statusCode == 201,
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              object["sha256"] as? String == digest,
              let size = (object["size"] as? NSNumber)?.int64Value, size > 0 else {
            throw NSError(domain: "BackupServer", code: 1, userInfo: [NSLocalizedDescriptionKey: tr("لم يؤكد السيرفر حفظ النسخة؛ تحقق من العنوان ورمز الوصول.", "Server did not confirm the backup; check the address and token.")])
        }
        return size
    }
}

struct BackupServerSettings: View {
    @Environment(\.dismiss) private var dismiss
    @State private var address = BackupServerConfig.address
    @State private var enabled = BackupServerConfig.enabled
    @State private var token = BackupServerConfig.token
    @State private var error = false
    var body: some View {
        NavigationStack {
            Form {
                Section(tr("السيرفر المحلي", "Local server")) {
                    TextField("http://192.168.1.10:8765", text: $address)
                        .keyboardType(.URL).textInputAutocapitalization(.never).autocorrectionDisabled()
                        .environment(\.layoutDirection, .leftToRight)
                    SecureField(tr("رمز الوصول", "Access token"), text: $token)
                        .textInputAutocapitalization(.never).autocorrectionDisabled()
                        .environment(\.layoutDirection, .leftToRight)
                    Toggle(tr("تفعيل النسخ إلى السيرفر", "Back up to server"), isOn: $enabled)
                } footer: {
                    Text(tr("الاتصال المحلي HTTP غير مشفّر. استخدمه على شبكة موثوقة فقط؛ اختر نسخاً دورياً أو بعد الاستيراد من صفحة النسخ.", "Local HTTP is unencrypted. Use only on a trusted network; choose a schedule or after-import backup on the backup page."))
                }
            }
            .navigationTitle(tr("سيرفر النسخ الاحتياطي", "Backup server"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement: .confirmationAction) {
                Button(tr("تم", "Done")) {
                    let previousToken = BackupServerConfig.token
                    guard (!enabled || (BackupServerConfig.url(address) != nil && token.utf8.count >= 32 && token.unicodeScalars.allSatisfy { $0.isASCII && !$0.properties.isWhitespace })), BackupServerConfig.saveToken(token) else { error = true; return }
                    if enabled && (!BackupServerConfig.enabled || BackupServerConfig.address != address || previousToken != token) {
                        UserDefaults.standard.set(true, forKey: BackupServerConfig.pendingKey)
                    }
                    UserDefaults.standard.set(address, forKey: BackupServerConfig.addressKey)
                    UserDefaults.standard.set(enabled, forKey: BackupServerConfig.enabledKey)
                    BackupBackground.schedule()
                    dismiss()
                }
            } }
            .alert(tr("تحقق من البيانات", "Check settings"), isPresented: $error) {
                Button(tr("حسناً", "OK"), role: .cancel) {}
            } message: { Text(tr("أدخل عنوان IP محلياً ورمز وصول لا يقل عن 32 حرفاً.", "Enter a local IP address and a token of at least 32 characters.")) }
        }
    }
}
