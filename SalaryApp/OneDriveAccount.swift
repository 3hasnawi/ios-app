import AuthenticationServices
import CryptoKit
import Security
import UIKit

struct AccountError: LocalizedError {
    let errorDescription: String?
    init(_ message: String) { errorDescription = message }
}

@MainActor
final class OneDriveAccount: NSObject, ASWebAuthenticationPresentationContextProviding {
    private let client = "986d159c-3325-4480-af32-3572c0c9f3c9"
    private let scheme = "msauth.com.example.SalaryApp"
    private let authority = "https://login.microsoftonline.com/consumers/oauth2/v2.0/"
    private let scope = "https://graph.microsoft.com/Files.ReadWrite.AppFolder offline_access"
    private var token = ""
    private var expires = Date.distantPast
    private var browser: ASWebAuthenticationSession?
    private let network = URLSession(configuration: .ephemeral)
    private var window: UIWindow? {
        UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
            .first { $0.activationState == .foregroundActive }?.keyWindow
    }
    func presentationAnchor(for session: ASWebAuthenticationSession) -> ASPresentationAnchor { window ?? ASPresentationAnchor() }
    private var query: [String:Any] {
        [kSecClass as String:kSecClassGenericPassword, kSecAttrAccount as String:client,
         kSecAttrService as String:"payroll.onedrive.refresh"]
    }
    func storedToken() throws -> String? {
        var q = query
        q[kSecReturnData as String] = true
        var item: CFTypeRef?
        let result = SecItemCopyMatching(q as CFDictionary, &item)
        if result == errSecItemNotFound { return nil }
        guard result == errSecSuccess, let data = item as? Data, let value = String(data:data, encoding:.utf8) else { throw AccountError("تعذّر فتح جلسة OneDrive المحفوظة.") }
        return value
    }
    private func save(_ value: String) throws {
        let data = Data(value.utf8)
        let update = SecItemUpdate(query as CFDictionary, [kSecValueData as String:data] as CFDictionary)
        if update == errSecSuccess { return }
        var q = query
        q[kSecAttrAccessible as String] = kSecAttrAccessibleWhenUnlockedThisDeviceOnly
        q[kSecValueData as String] = data
        guard update == errSecItemNotFound, SecItemAdd(q as CFDictionary,nil) == errSecSuccess else { throw AccountError("تعذّر حفظ جلسة OneDrive.") }
    }
    func logout() throws {
        let status = SecItemDelete(query as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else { throw AccountError("تعذّر إنهاء الجلسة.") }
        token = ""
    }
        private func base64URL(_ data: Data) -> String {
            data.base64EncodedString().replacingOccurrences(of: "+", with: "-")
                .replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
        }
        private func random() throws -> String {
            var bytes = [UInt8](repeating: 0, count: 32)
            guard SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes) == errSecSuccess else { throw AccountError(tr("تعذّر بدء تسجيل الدخول.", "Could not start sign-in.")) }
            return base64URL(Data(bytes))
        }
        func login() async throws {

            let verifier = try random(), state = try random()
            let redirect = scheme + "://auth"
            let challenge = base64URL(Data(SHA256.hash(data: Data(verifier.utf8))))
            var url = URLComponents(string: authority + "authorize")!
            url.queryItems = ["client_id":client, "scope":scope,
                              "response_type":"code", "response_mode":"query", "redirect_uri":redirect, "state":state, "prompt":"select_account",
                              "code_challenge":challenge,"code_challenge_method":"S256"].map { URLQueryItem(name:$0.key,value:$0.value) }
            guard window != nil, let start = url.url else { throw AccountError(tr("تعذّر فتح شاشة Microsoft.", "Could not open Microsoft sign-in.")) }
            let callback: URL = try await withCheckedThrowingContinuation { continuation in
                let session = ASWebAuthenticationSession(url: start, callbackURLScheme: scheme) { callback, _ in
                    if let callback { continuation.resume(returning: callback) }
                    else { continuation.resume(throwing: AccountError(tr("لم يكتمل الرجوع من Microsoft إلى التطبيق.", "Sign-in did not return to the installed app."))) }
                }
                browser = session
                session.presentationContextProvider = self
                if !session.start() { continuation.resume(throwing: AccountError(tr("تعذّر فتح شاشة Microsoft.", "Could not open Microsoft sign-in."))) }
            }
            defer { browser = nil }
            let code = try OneDriveAuthorizationReply.code(from: callback, scheme: scheme, state: state)
            let fields = ["client_id":client,"grant_type":"authorization_code","redirect_uri":redirect,"code":code,"code_verifier":verifier]
            try await exchange(fields)
        }
        func access() async throws -> String {
            if !token.isEmpty, expires.timeIntervalSinceNow > 120 { return token }
            guard let refresh = try storedToken() else { throw AccountError(tr("سجّل الدخول إلى OneDrive.", "Sign in to OneDrive.")) }
            try await exchange(["client_id": client, "grant_type": "refresh_token", "refresh_token":refresh, "scope":scope])
            return token
        }
        private func exchange(_ fields: [String:String]) async throws {
            let unreserved = CharacterSet.alphanumerics.union(CharacterSet(charactersIn:"-._~"))
            var request = URLRequest(url: URL(string: authority+"token")!)
            request.httpMethod = "POST"
            request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
            request.httpBody = Data(fields.map { $0.key+"="+($0.value.addingPercentEncoding(withAllowedCharacters:unreserved) ?? "") }.joined(separator:"&").utf8)
            let (data,response) = try await network.data(for: request)
            let json = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
            if let reason = json?["error"] as? String {
                throw OneDriveAuthorizationReply.serviceError(reason, description: json?["error_description"] as? String)
            }
            guard (response as? HTTPURLResponse)?.statusCode == 200,
                  let access = json?["access_token"] as? String, !access.isEmpty else {
                throw AccountError(tr("تعذّر استلام رمز الوصول من Microsoft.", "Could not obtain the access token from Microsoft."))
            }
            if let refresh = json?["refresh_token"] as? String, !refresh.isEmpty {
                try save(refresh)
            } else if try storedToken() == nil { throw AccountError(tr("لم تسمح Microsoft بحفظ الجلسة.", "Microsoft did not return a persistent session.")) }
            expires = Date().addingTimeInterval(json?["expires_in"] as? Double ?? 3600)
            token = access
        }
}
