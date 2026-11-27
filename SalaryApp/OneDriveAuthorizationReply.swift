import Foundation

/// لا يتضمن الخطأ الظاهر للمستخدم رابط الرجوع أو قيم التفويض أو وصفاً مرسلاً من الحساب.
enum OneDriveAuthorizationReplyError: Error, Equatable {
    case address
    case session
    case missingCode
    case ambiguous
    case microsoft(String)

    var message: String {
        switch self {
        case .address: return tr("عنوان الرجوع من Microsoft لا يطابق عنوان التطبيق.", "The return address does not match the registered app.")
        case .session: return tr("تعذّر مطابقة رد Microsoft مع جلسة الدخول. ابدأ تسجيل الدخول مجدداً.", "Microsoft's reply could not be matched to this sign-in. Start again.")
        case .missingCode: return tr("رجعت Microsoft إلى التطبيق دون رمز التفويض. أعد تسجيل الدخول.", "Microsoft returned without an authorization code. Sign in again.")
        case .ambiguous: return tr("رد تسجيل الدخول غير صالح. أعد تسجيل الدخول.", "Invalid sign-in response. Start sign-in again.")
        case .microsoft(let reason): return tr("تعذّر إكمال تسجيل الدخول لدى Microsoft", "Microsoft could not complete sign-in") + " (" + reason + ")."
        }
    }
}

enum OneDriveAuthorizationReply {
    static func serviceError(_ error: String, description: String?) -> OneDriveAuthorizationReplyError {
        let known = ["access_denied", "invalid_scope", "invalid_client", "unauthorized_client", "invalid_grant", "invalid_request",
                     "consent_required", "interaction_required", "login_required", "server_error", "temporarily_unavailable"]
        var reason = known.contains(error) ? error : "OAuth"
        if let description, let range = description.range(of: #"\bAADSTS[0-9]{4,10}\b"#, options: .regularExpression) {
            reason += " · " + description[range]
        }
        return .microsoft(reason)
    }

    static func code(from callback: URL, scheme: String, state: String) throws -> String {
        guard let url = URLComponents(url: callback, resolvingAgainstBaseURL: false),
              url.scheme?.lowercased() == scheme.lowercased(), url.percentEncodedHost?.lowercased() == "auth",
              url.user == nil, url.password == nil, url.port == nil,
              url.percentEncodedPath.isEmpty || url.percentEncodedPath == "/" else {
            throw OneDriveAuthorizationReplyError.address
        }
        // يُطلب response_mode=query صراحة؛ لا نخلط استجابتين من الاستعلام والجزء اللاحق للرابط.
        let responseNames = ["state", "code", "error", "error_description"]
        let query = (url.queryItems ?? []).filter { responseNames.contains($0.name) }
        let fragment = url.percentEncodedFragment.flatMap { URLComponents(string: "?" + $0) }
        let extra = (fragment?.queryItems ?? []).filter { responseNames.contains($0.name) }
        guard query.isEmpty || extra.isEmpty else { throw OneDriveAuthorizationReplyError.ambiguous }
        let items = query.isEmpty ? extra : query
        guard Set(items.map(\.name)).count == items.count else { throw OneDriveAuthorizationReplyError.ambiguous }
        func value(_ name: String) -> String? { items.first { $0.name == name }?.value }
        guard !state.isEmpty, value("state") == state else { throw OneDriveAuthorizationReplyError.session }
        if items.contains(where: { $0.name == "error" }) {
            guard let error = value("error"), !error.isEmpty, !items.contains(where: { $0.name == "code" }) else { throw OneDriveAuthorizationReplyError.ambiguous }
            throw serviceError(error, description: value("error_description"))
        }
        guard let code = value("code"), !code.isEmpty else { throw OneDriveAuthorizationReplyError.missingCode }
        return code
    }
}
