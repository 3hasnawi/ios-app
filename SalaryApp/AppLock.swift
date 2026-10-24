import SwiftUI
import LocalAuthentication

/// قفل التطبيق بـ Face ID (أو Touch ID، أو رمز الجهاز إذا تعذّر التعرّف).
/// اختياري من الإعدادات. يُقفل عند فتح التطبيق وعند الرجوع إليه بعد المدة المختارة،
/// ويغطي البيانات في مبدّل التطبيقات.
@MainActor @Observable
final class AppLock {
    static let shared = AppLock()
    static let enabledKey = "faceIDLock"
    static let timeoutKey = "faceIDLockTimeout"

    static var isEnabled: Bool { UserDefaults.standard.bool(forKey: enabledKey) }
    /// بالثواني: 0 فوراً.
    static var timeout: Int { UserDefaults.standard.integer(forKey: timeoutKey) }
    /// لصورة شاشة القفل في اختبار المحاكي فقط (-lockPreview YES).
    private static var isPreview: Bool { UserDefaults.standard.bool(forKey: "lockPreview") }

    var isLocked: Bool
    var isAuthenticating = false
    var message: String?
    /// يُطلب Face ID تلقائياً مرة واحدة لكل قفل؛ بعد الإلغاء يكون بالزر فقط.
    private var autoPrompt: Bool
    private var backgroundedAt: Date?

    private init() {
        // -skipLock: لصورة الإعدادات في اختبار المحاكي فقط.
        let locked = (Self.isEnabled && !UserDefaults.standard.bool(forKey: "skipLock")) || Self.isPreview
        isLocked = locked
        autoPrompt = locked && !Self.isPreview
    }

    // MARK: - نوع التحقق على هذا الجهاز

    enum Kind { case faceID, touchID, opticID, passcode, none }

    static var kind: Kind {
        let context = LAContext()
        var error: NSError?
        if context.canEvaluatePolicy(.deviceOwnerAuthenticationWithBiometrics, error: &error) {
            switch context.biometryType {
            case .faceID: return .faceID
            case .touchID: return .touchID
            case .opticID: return .opticID
            default: return .passcode
            }
        }
        return context.canEvaluatePolicy(.deviceOwnerAuthentication, error: &error) ? .passcode : .none
    }

    static var methodName: String {
        switch kind {
        case .faceID: "Face ID"
        case .touchID: "Touch ID"
        case .opticID: "Optic ID"
        case .passcode, .none: tr("رمز الجهاز", "Passcode")
        }
    }

    static var systemImage: String {
        switch kind {
        case .faceID: "faceid"
        case .touchID: "touchid"
        case .opticID: "opticid"
        case .passcode, .none: "lock.fill"
        }
    }

    // MARK: - القفل والفتح

    /// تحقق عام (يُستخدم أيضاً لتأكيد تفعيل القفل من الإعدادات).
    static func authenticate(reason: String) async -> Result<Void, Error> {
        let context = LAContext()
        context.localizedCancelTitle = tr("إلغاء", "Cancel")
        context.localizedFallbackTitle = tr("استخدام رمز الجهاز", "Use Passcode")
        do {
            try await context.evaluatePolicy(.deviceOwnerAuthentication, localizedReason: reason)
            return .success(())
        } catch {
            return .failure(error)
        }
    }

    func unlock() async {
        guard isLocked, !isAuthenticating, !Self.isPreview else { return }
        guard Self.kind != .none else {
            // لا يوجد رمز قفل على الجهاز أصلاً: لا يمكن التحقق، فلا يُحبس المستخدم خارج التطبيق.
            isLocked = false
            return
        }
        isAuthenticating = true
        defer { isAuthenticating = false }
        switch await Self.authenticate(reason: tr("افتح تطبيق الرواتب", "Unlock the payroll app")) {
        case .success:
            isLocked = false
            message = nil
        case .failure(let error):
            let code = (error as? LAError)?.code
            message = (code == .userCancel || code == .appCancel || code == .systemCancel)
                ? nil
                : tr("تعذّر التحقق. حاول مرة أخرى.", "Couldn't verify. Try again.")
        }
    }

    func scenePhaseChanged(_ phase: ScenePhase) {
        guard Self.isEnabled else { return }
        switch phase {
        case .background:
            if !isLocked { backgroundedAt = Date() }
        case .active:
            if !isLocked, let since = backgroundedAt, Date().timeIntervalSince(since) >= Double(Self.timeout) {
                isLocked = true
                autoPrompt = true
            }
            backgroundedAt = nil
            if isLocked, autoPrompt {
                autoPrompt = false
                Task { await unlock() }
            }
        default:
            break
        }
    }

    /// عند إيقاف القفل من الإعدادات.
    func disable() {
        isLocked = false
        autoPrompt = false
    }
}

/// يغطي التطبيق بشاشة القفل، وبشعار فقط في مبدّل التطبيقات.
struct AppLockGate<Content: View>: View {
    @ViewBuilder let content: () -> Content
    @State private var lock = AppLock.shared
    @Environment(\.scenePhase) private var scenePhase
    @AppStorage(AppLock.enabledKey) private var enabled = false

    var body: some View {
        content()
            .overlay {
                if lock.isLocked {
                    LockScreen(lock: lock)
                        .transition(.opacity)
                } else if enabled && scenePhase != .active {
                    PrivacyCover()
                }
            }
            .animation(.easeOut(duration: 0.25), value: lock.isLocked)
            .onChange(of: scenePhase, initial: true) { _, phase in
                lock.scenePhaseChanged(phase)
            }
    }
}

private struct LockScreen: View {
    let lock: AppLock

    var body: some View {
        ZStack {
            Color(.systemBackground).ignoresSafeArea()
            Image("HomeLogo")
                .resizable()
                .scaledToFit()
                .frame(maxWidth: 320)
                .opacity(0.06)
                .accessibilityHidden(true)

            VStack(spacing: 18) {
                Spacer()
                Image(systemName: "lock.fill")
                    .font(.system(size: 30, weight: .semibold))
                    .foregroundStyle(.white)
                    .frame(width: 72, height: 72)
                    .background(Theme.salaryGradient, in: Circle())
                Text(tr("تطبيق الرواتب مقفل", "Payroll is locked"))
                    .font(.title2.bold())
                Text(tr("استخدم \(AppLock.methodName) لعرض البيانات", "Use \(AppLock.methodName) to view your data"))
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                if let message = lock.message {
                    Text(message)
                        .font(.footnote.weight(.medium))
                        .foregroundStyle(.red)
                }
                Spacer()
                Button {
                    Task { await lock.unlock() }
                } label: {
                    Label(tr("فتح بـ \(AppLock.methodName)", "Unlock with \(AppLock.methodName)"), systemImage: AppLock.systemImage)
                        .font(.headline)
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 6)
                }
                .glassButtonStyle(prominent: true)
                .buttonBorderShape(.capsule)
                .disabled(lock.isAuthenticating)
                .padding(.horizontal, 32)
                .padding(.bottom, 24)
                .accessibilityIdentifier("unlockButton")
            }
            .padding(.horizontal, 24)
        }
    }
}

private struct PrivacyCover: View {
    var body: some View {
        ZStack {
            Rectangle().fill(.regularMaterial).ignoresSafeArea()
            Image("HomeLogo")
                .resizable()
                .scaledToFit()
                .frame(width: 120)
                .opacity(0.5)
        }
    }
}
