import AuthenticationServices
import CryptoKit
import Security
import SwiftUI
import WebKit

private struct TrialFailure: LocalizedError {
    let errorDescription: String?
    init(_ message: String) { errorDescription = message }
}

// The only allowed redirect during a data request is Graph's HTTPS download.
private final class TrialRedirects: NSObject, URLSessionTaskDelegate {
    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest,
                    completionHandler: @escaping (URLRequest?) -> Void) {
        guard task.originalRequest?.httpMethod == "GET",
              task.originalRequest?.url?.lastPathComponent == "content",
              request.url?.scheme == "https", let url = request.url else {
            completionHandler(nil)
            return
        }
        completionHandler(URLRequest(url: url))
    }
}

@MainActor
struct OneDriveTrialView: UIViewRepresentable {
    var openRefund: (Data) throws -> Void
    func makeCoordinator() -> Coordinator {
        let handler = Coordinator()
        handler.openRefund = openRefund
        return handler
    }
    func makeUIView(context: Context) -> WKWebView {
        let config = WKWebViewConfiguration()
        config.websiteDataStore = .nonPersistent()
        config.userContentController.addScriptMessageHandler(context.coordinator, contentWorld: .page, name: "trial")
        let view = WKWebView(frame: .zero, configuration: config)
        context.coordinator.view = view
        view.navigationDelegate = context.coordinator
        do {
            guard let page = Bundle.main.url(forResource: "OneDriveTrial", withExtension: "html"),
                  let script = Bundle.main.url(forResource: "OneDriveTrial", withExtension: "js") else {
                throw TrialFailure("resources")
            }
            let html = try String(contentsOf: page, encoding: .utf8)
            let engine = try String(contentsOf: script, encoding: .utf8)
            let localized = html.replacingOccurrences(of: "lang=\"ar\"", with: tr("lang=\"ar\"", "lang=\"en\""))
                .replacingOccurrences(of: "data-numbers=\"latn\"", with: "data-numbers=\"" + (AppSettings.numerals == .arabicIndic ? "arab" : "latn") + "\"")
            view.loadHTMLString(localized.replacingOccurrences(of: "/* TRIAL_ENGINE */", with: engine), baseURL: nil)
        } catch {
            view.loadHTMLString("<p dir='rtl'>تعذّر تحميل شاشة التجربة.</p>", baseURL: nil)
        }
        return view
    }
    func updateUIView(_ view: WKWebView, context: Context) {}
    static func dismantleUIView(_ view: WKWebView, coordinator: Coordinator) {
        view.configuration.userContentController.removeScriptMessageHandler(forName: "trial", contentWorld: .page)
        coordinator.browser?.cancel()
        coordinator.network.invalidateAndCancel()
    }

    @MainActor
    final class Coordinator: NSObject, WKNavigationDelegate, WKScriptMessageHandlerWithReply, ASWebAuthenticationPresentationContextProviding {
        var openRefund: ((Data) throws -> Void)?
        weak var view: WKWebView?
        var browser: ASWebAuthenticationSession?
        let network: URLSession = {
            let config = URLSessionConfiguration.ephemeral
            config.httpShouldSetCookies = false
            config.timeoutIntervalForRequest = 30
            return URLSession(configuration: config, delegate: TrialRedirects(), delegateQueue: nil)
        }()
        private let client = "986d159c-3325-4480-af32-3572c0c9f3c9"
        private let scheme = "msauth.com.example.SalaryApp"
        private let authority = "https://login.microsoftonline.com/consumers/oauth2/v2.0/"
        private let base = "https://graph.microsoft.com/v1.0/me/drive/"
        private var token = ""
        private var root = ""
        func presentationAnchor(for session: ASWebAuthenticationSession) -> ASPresentationAnchor {
            view?.window ?? ASPresentationAnchor()
        }
        func webView(_ webView: WKWebView, decidePolicyFor action: WKNavigationAction,
                     decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
            decisionHandler(action.targetFrame?.isMainFrame == true && action.request.url?.absoluteString == "about:blank" ? .allow : .cancel)
        }
        func userContentController(_ controller: WKUserContentController, didReceive message: WKScriptMessage,
                                   replyHandler: @escaping (Any?, String?) -> Void) {
            guard message.frameInfo.isMainFrame, let input = message.body as? [String: Any],
                  let action = input["action"] as? String else {
                replyHandler(nil, tr("طلب غير صالح.", "Invalid request.")); return
            }
            Task {
                do { replyHandler(try await perform(action, id: input["id"] as? String ?? "", value: input["value"] as? String ?? ""), nil) }
                catch let error as OneDriveAuthorizationReplyError { replyHandler(nil, error.message) }
                catch { replyHandler(nil, error.localizedDescription) }
            }
        }
        private func base64URL(_ data: Data) -> String {
            data.base64EncodedString().replacingOccurrences(of: "+", with: "-")
                .replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
        }
        private func random() throws -> String {
            var bytes = [UInt8](repeating: 0, count: 32)
            guard SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes) == errSecSuccess else { throw TrialFailure(tr("تعذّر بدء تسجيل الدخول.", "Could not start sign-in.")) }
            return base64URL(Data(bytes))
        }
        private func login() async throws {
            token = ""; root = ""
            let verifier = try random(), state = try random()
            let redirect = scheme + "://auth"
            let challenge = base64URL(Data(SHA256.hash(data: Data(verifier.utf8))))
            var url = URLComponents(string: authority + "authorize")!
            url.queryItems = ["client_id":client, "scope":"https://graph.microsoft.com/Files.ReadWrite.AppFolder",
                              "response_type":"code", "response_mode":"query", "redirect_uri":redirect, "state":state, "prompt":"select_account",
                              "code_challenge":challenge,"code_challenge_method":"S256"].map { URLQueryItem(name:$0.key,value:$0.value) }
            guard view?.window != nil, let start = url.url else { throw TrialFailure(tr("تعذّر فتح شاشة Microsoft.", "Could not open Microsoft sign-in.")) }
            let callback: URL = try await withCheckedThrowingContinuation { continuation in
                let session = ASWebAuthenticationSession(url: start, callbackURLScheme: scheme) { callback, _ in
                    if let callback { continuation.resume(returning: callback) }
                    else { continuation.resume(throwing: TrialFailure(tr("لم يكتمل الرجوع من Microsoft إلى التطبيق.", "Sign-in did not return to the installed app."))) }
                }
                browser = session
                session.presentationContextProvider = self
                if !session.start() { continuation.resume(throwing: TrialFailure(tr("تعذّر فتح شاشة Microsoft.", "Could not open Microsoft sign-in."))) }
            }
            defer { browser = nil }
            let code = try OneDriveAuthorizationReply.code(from: callback, scheme: scheme, state: state)
            let fields = ["client_id":client,"grant_type":"authorization_code","redirect_uri":redirect,"code":code,"code_verifier":verifier]
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
                throw TrialFailure(tr("تعذّر استلام رمز الوصول من Microsoft.", "Could not obtain the access token from Microsoft."))
            }
            token = access
        }
        private func graph(_ url: URL, data: Data? = nil) async throws -> Data {
            guard !token.isEmpty, url.scheme == "https", url.host == "graph.microsoft.com" else { throw TrialFailure(tr("أعد تسجيل الدخول.", "Sign in again.")) }
            var request = URLRequest(url:url)
            request.setValue("Bearer " + token, forHTTPHeaderField:"Authorization")
            if let data {
                request.httpMethod = "PUT"; request.httpBody = data
                request.setValue("application/json", forHTTPHeaderField:"Content-Type")
            }
            let (bytes,response) = try await network.bytes(for:request)
            let code = (response as? HTTPURLResponse)?.statusCode ?? 0
            if data != nil && code == 409 { return Data() } // Always read back and verify before acknowledgement.
            guard (200..<300).contains(code), response.expectedContentLength <= 1_000_000 else {
                throw TrialFailure(tr("طلب OneDrive (", "OneDrive request (") + String(code) + "). " + (code == 401 ? tr("أعد تسجيل الدخول.", "Sign in again.") : tr("أعد المحاولة بعد التحقق من الاتصال والصلاحية.", "Check the connection and permission, then retry.")))
            }
            var body = Data()
            for try await byte in bytes {
                guard body.count < 1_000_000 else { throw TrialFailure(tr("تجاوز الملف حجم التجربة.", "Trial file is too large.")) }
                body.append(byte)
            }
            return body
        }
        private func localFile(refunds: Bool) throws -> URL {
            let fm = FileManager.default
            var dir = try fm.url(for:.applicationSupportDirectory,in:.userDomainMask,appropriateFor:nil,create:true)
                .appendingPathComponent("OneDriveTrialV1", isDirectory:true)
            try fm.createDirectory(at:dir,withIntermediateDirectories:true)
            var policy = URLResourceValues(); policy.isExcludedFromBackup = true
            try dir.setResourceValues(policy)
            return dir.appendingPathComponent(refunds ? "refunds.json" : "state.json")
        }
        private func perform(_ action:String, id:String, value:String) async throws -> String {
            switch action {
            case "device": return tr("معرّف النسخة المثبّتة: ", "Installed bundle ID: ") + (Bundle.main.bundleIdentifier ?? "؟")
            case "load", "refundLoad":
                let file = try localFile(refunds: action == "refundLoad")
                if !FileManager.default.fileExists(atPath:file.path) { return "" }
                return try String(contentsOf:file,encoding:.utf8)
            case "store", "refundStore":
                guard value.utf8.count <= 1_000_000 else { throw TrialFailure(tr("سجل التجربة كبير.", "Test history is too large.")) }
                try Data(value.utf8).write(to:localFile(refunds: action == "refundStore"),options:[.atomic,.completeFileProtection]); return ""
            case "openRefund":
                guard value.utf8.count <= 16384, let openRefund else { throw TrialFailure(tr("تعذّر فتح الدفعة.", "Could not open the batch.")) }
                try openRefund(Data(value.utf8)); return ""
            case "login": try await login(); return ""
            case "folder":
                let data = try await graph(URL(string:base+"special/approot")!)
                guard let item = try JSONSerialization.jsonObject(with:data) as? [String:Any],
                      let rid = item["id"] as? String, let parent = item["parentReference"] as? [String:Any],
                      let drive = parent["driveId"] as? String, !drive.isEmpty, !rid.isEmpty,
                      let escaped = rid.addingPercentEncoding(withAllowedCharacters:.alphanumerics) else { throw TrialFailure(tr("تعذّر تحديد مجلد التجربة.", "Could not identify the test folder.")) }
                root = base + "items/" + escaped
                return drive + "/" + rid
            default: break
            }
            guard !root.isEmpty else { throw TrialFailure(tr("افتح مجلد التجربة أولاً.", "Open the test folder first.")) }
            if action == "list" {
                var next = URL(string:root + "/children?$select=name&$top=200")
                var names = [String](), pages = Set<URL>()
                while let url = next {
                    guard pages.insert(url).inserted, pages.count <= 100 else { throw TrialFailure(tr("تعذرت قراءة فهرس التجربة.", "Could not read the file list.")) }
                    let data = try await graph(url)
                    guard let obj = try JSONSerialization.jsonObject(with:data) as? [String:Any], let rows = obj["value"] as? [[String:Any]] else { throw TrialFailure(tr("فهرس غير صالح.", "Invalid file list.")) }
                    names += rows.compactMap { $0["name"] as? String }.filter { $0.hasPrefix("payroll-trial-v1-") || $0.hasPrefix("payroll-refund-trial-v1-") }
                    if let link = obj["@odata.nextLink"] as? String {
                        guard let page = URL(string:link) else { throw TrialFailure(tr("تعذرت قراءة الصفحة التالية.", "Could not load the next page.")) }
                        next = page
                    } else { next = nil }
                }
                return String(decoding:try JSONEncoder().encode(names),as:UTF8.self)
            }
            guard UUID(uuidString:id)?.uuidString.lowercased() == id, ["read","write","refundRead","refundWrite"].contains(action), value.utf8.count <= 16384 else { throw TrialFailure(tr("ملف تجربة غير صالح.", "Invalid test file.")) }
            let prefix = action.hasPrefix("refund") ? "payroll-refund-trial-v1-" : "payroll-trial-v1-"
            let writing = action == "write" || action == "refundWrite"
            let path = root + ":/" + prefix + id + ".json:/content"
            let url = URL(string:path + (writing ? "?@microsoft.graph.conflictBehavior=fail" : ""))!
            let data = try await graph(url,data:writing ? Data(value.utf8) : nil)
            return writing ? "" : String(decoding:data,as:UTF8.self)
        }
    }
}
