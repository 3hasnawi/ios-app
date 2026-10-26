import Foundation
import CryptoKit
import Security

enum ServerBackupError: LocalizedError {
    case invalidAddress, missingToken, http(Int), invalidResponse, checksum

    var errorDescription: String? {
        switch self {
        case .invalidAddress: tr("استخدم عنوان HTTPS صالحاً للسيرفر.", "Enter a valid HTTPS server address.")
        case .missingToken: tr("أدخل رمز الجهاز من صفحة الإضافة.", "Enter this device's token from the add-on.")
        case .http(let status): tr("رفض السيرفر الطلب (\(status)).", "The server rejected the request (\(status)).")
        case .invalidResponse: tr("رد السيرفر غير صالح.", "Invalid server response.")
        case .checksum: tr("بصمة النسخة المستلمة لا تطابق الأصل.", "The downloaded backup checksum does not match.")
        }
    }
}

/// ملف النسخة ذاته يُرفع ويُنزّل؛ الرمز محفوظ في Keychain ولا يدخل النسخة الاحتياطية.
@MainActor
enum ServerBackup {
    static let addressKey = "backupServerAddress"
    static let readOnlyKey = "backupServerReadOnly"
    private static let service = "com.example.SalaryApp.backup-server"
    private static let account = "device-token"

    static func baseURL(_ value: String) -> URL? {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard var components = URLComponents(string: trimmed), components.scheme?.lowercased() == "https",
              let host = components.host, !host.isEmpty, components.user == nil, components.password == nil,
              components.query == nil, components.fragment == nil else { return nil }
        while components.path.hasSuffix("/") { components.path.removeLast() }
        return components.url
    }

    static var configured: Bool {
        baseURL(UserDefaults.standard.string(forKey: addressKey) ?? "") != nil && token() != nil
    }

    static var writable: Bool { configured && !UserDefaults.standard.bool(forKey: readOnlyKey) }

    static func token() -> String? {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
                                    kSecAttrService as String: service, kSecAttrAccount as String: account,
                                    kSecReturnData as String: true, kSecMatchLimit as String: kSecMatchLimitOne]
        var result: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess,
              let data = result as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    static func save(address: String, token value: String, readOnly: Bool) throws {
        guard let url = baseURL(address) else { throw ServerBackupError.invalidAddress }
        let clean = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !clean.isEmpty else { throw ServerBackupError.missingToken }
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
                                    kSecAttrService as String: service, kSecAttrAccount as String: account]
        let attrs: [String: Any] = [kSecValueData as String: Data(clean.utf8),
                                    kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly]
        let existing = SecItemCopyMatching(query as CFDictionary, nil)
        let status = existing == errSecSuccess
            ? SecItemUpdate(query as CFDictionary, attrs as CFDictionary)
            : SecItemAdd(query.merging(attrs) { _, new in new } as CFDictionary, nil)
        guard status == errSecSuccess else { throw NSError(domain: NSOSStatusErrorDomain, code: Int(status)) }
        UserDefaults.standard.set(url.absoluteString, forKey: addressKey)
        UserDefaults.standard.set(readOnly, forKey: readOnlyKey)
    }

    static func remove() {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
                                    kSecAttrService as String: service, kSecAttrAccount as String: account]
        SecItemDelete(query as CFDictionary)
        UserDefaults.standard.removeObject(forKey: addressKey)
        UserDefaults.standard.removeObject(forKey: readOnlyKey)
    }

    private static func request(method: String) throws -> URLRequest {
        guard let base = baseURL(UserDefaults.standard.string(forKey: addressKey) ?? "") else {
            throw ServerBackupError.invalidAddress
        }
        guard let token = token() else { throw ServerBackupError.missingToken }
        var request = URLRequest(url: base.appendingPathComponent("v1/backup"))
        request.httpMethod = method
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        request.setValue("application/zip", forHTTPHeaderField: "Accept")
        request.timeoutInterval = 600
        return request
    }

    nonisolated private static func sha256(_ file: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: file)
        defer { try? handle.close() }
        var hash = SHA256()
        while true {
            let part = try handle.read(upToCount: 1_048_576) ?? Data()
            if part.isEmpty { break }
            hash.update(data: part)
        }
        return hash.finalize().map { String(format: "%02x", $0) }.joined()
    }

    static func upload(_ file: URL) async throws -> Int64 {
        var request = try request(method: "PUT")
        request.setValue("application/zip", forHTTPHeaderField: "Content-Type")
        let (body, response) = try await URLSession.shared.upload(for: request, fromFile: file)
        guard let response = response as? HTTPURLResponse else { throw ServerBackupError.invalidResponse }
        guard response.statusCode == 200 else { throw ServerBackupError.http(response.statusCode) }
        let result = try JSONDecoder().decode(UploadResult.self, from: body)
        let localHash = try await Task.detached(priority: .utility) { try ServerBackup.sha256(file) }.value
        guard result.sha256 == localHash else { throw ServerBackupError.checksum }
        return result.size
    }

    static func download() async throws -> URL {
        let (temporary, response) = try await URLSession.shared.download(for: request(method: "GET"))
        guard let response = response as? HTTPURLResponse else { throw ServerBackupError.invalidResponse }
        guard response.statusCode == 200 else { throw ServerBackupError.http(response.statusCode) }
        guard let etag = response.value(forHTTPHeaderField: "ETag")?.trimmingCharacters(in: CharacterSet(charactersIn: "\"")),
              etag.count == 64 else { throw ServerBackupError.invalidResponse }
        let file = FileManager.default.temporaryDirectory.appendingPathComponent("backup-download-\(UUID().uuidString).zip")
        try FileManager.default.moveItem(at: temporary, to: file)
        do {
            let hash = try await Task.detached(priority: .utility) { try ServerBackup.sha256(file) }.value
            guard hash == etag else { throw ServerBackupError.checksum }
            return file
        } catch {
            try? FileManager.default.removeItem(at: file)
            throw error
        }
    }

    private struct UploadResult: Decodable {
        var sha256: String
        var size: Int64
    }
}
