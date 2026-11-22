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
    private static var isPreview: Bool {
        #if targetEnvironment(simulator)
        UserDefaults.standard.bool(forKey: "lockPreview")
        #else
        false
        #endif
    }

    var isLocked: Bool
    var isAuthenticating = false
    var message: String?
    /// يُطلب Face ID تلقائياً مرة واحدة لكل قفل؛ بعد الإلغاء يكون بالزر فقط.
    private var autoPrompt: Bool
    private var authenticationGeneration = 0
    private var backgroundedAt: ContinuousClock.Instant?
    var pendingForegroundCheck: Bool { backgroundedAt != nil }

    private init() {
        // -skipLock: لصورة الإعدادات في اختبار المحاكي فقط.
        #if targetEnvironment(simulator)
        let locked = (Self.isEnabled && !UserDefaults.standard.bool(forKey: "skipLock")) || Self.isPreview
        #else
        let locked = Self.isEnabled
        #endif
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
        let generation = authenticationGeneration
        isAuthenticating = true
        defer { isAuthenticating = false }
        switch await Self.authenticate(reason: tr("افتح تطبيق الرواتب", "Unlock the payroll app")) {
        case .success:
            guard generation == authenticationGeneration else { return }
            withAnimation(.easeOut(duration: 0.2)) { isLocked = false }
            message = nil
        case .failure(let error):
            let code = (error as? LAError)?.code
            if code == .passcodeNotSet {
                message = tr("فعّل رمز قفل الآيفون من إعدادات الجهاز ثم أعد المحاولة.", "Set a passcode in iPhone Settings, then try again.")
                return
            }
            message = (code == .userCancel || code == .appCancel || code == .systemCancel)
                ? nil
                : tr("تعذّر التحقق. حاول مرة أخرى.", "Couldn't verify. Try again.")
        }
    }

    func scenePhaseChanged(_ phase: ScenePhase) {
        guard Self.isEnabled else { return }
        switch phase {
        case .background:
            authenticationGeneration &+= 1
            if !isLocked { backgroundedAt = .now }
        case .active:
            if !isLocked, let since = backgroundedAt, since.duration(to: .now) >= .seconds(max(0, Self.timeout)) {
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
        backgroundedAt = nil
        isLocked = false
        autoPrompt = false
    }
}

/// يغطي التطبيق بشاشة القفل، وبشعار فقط في مبدّل التطبيقات.
struct AppLockGate<Content: View>: View {
    @ViewBuilder let content: () -> Content
    @State private var lock = AppLock.shared
    @Environment(\.scenePhase) private var scenePhase
    @Environment(\.layoutDirection) private var layoutDirection
    @Environment(\.locale) private var locale
    @Environment(\.dynamicTypeSize) private var dynamicTypeSize
    @Environment(\.colorScheme) private var colorScheme
    @AppStorage(AppLock.enabledKey) private var enabled = false

    var body: some View {
        let needsPrivacyCover = enabled && (scenePhase != .active || lock.pendingForegroundCheck)
        let covered = lock.isLocked || needsPrivacyCover
        content()
            .accessibilityHidden(covered)
            .allowsHitTesting(!covered)
            .overlay {
                if covered { Color(.systemBackground).ignoresSafeArea() }
            }
            .background {
                SceneLockShield(enabled: enabled, mode: needsPrivacyCover ? .privacy : lock.isLocked ? .lock : .hidden,
                                layoutDirection: layoutDirection, locale: locale, dynamicTypeSize: dynamicTypeSize,
                                colorScheme: colorScheme)
                    .frame(width: 0, height: 0)
            }
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
            Color(.systemBackground).ignoresSafeArea()
            Image("HomeLogo")
                .resizable()
                .scaledToFit()
                .frame(width: 120)
                .opacity(0.5)
        }
    }
}

/// نافذة خاصة بالمشهد تغطي أيضاً المعاينات ونوافذ التحرير المعروضة فوق جذر SwiftUI.
private struct SceneLockShield: UIViewRepresentable {
    var enabled: Bool
    var mode: ShieldMode
    var layoutDirection: LayoutDirection
    var locale: Locale
    var dynamicTypeSize: DynamicTypeSize
    var colorScheme: ColorScheme

    enum ShieldMode { case hidden, privacy, lock }

    func makeUIView(context: Context) -> Anchor { Anchor() }
    func updateUIView(_ view: Anchor, context: Context) {
        view.settings = self
        view.refresh()
    }
    static func dismantleUIView(_ view: Anchor, coordinator: ()) { view.detach() }

    final class Anchor: UIView {
        var settings: SceneLockShield?
        private var shield: UIWindow?
        private weak var previousKey: UIWindow?
        private var visibleMode = ShieldMode.hidden

        override func didMoveToWindow() {
            super.didMoveToWindow()
            NotificationCenter.default.removeObserver(self)
            guard let scene = window?.windowScene else { detach(); return }
            NotificationCenter.default.addObserver(self, selector: #selector(willDeactivate),
                                                   name: UIScene.willDeactivateNotification, object: scene)
            refresh()
        }

        @objc private func willDeactivate() {
            if settings?.enabled == true { show(.privacy) }
        }

        func refresh() {
            guard let settings, let scene = window?.windowScene else { return }
            let inactive = settings.enabled && scene.activationState != .foregroundActive
            show(inactive ? .privacy : settings.mode)
        }

        func detach() {
            NotificationCenter.default.removeObserver(self)
            hide()
        }

        private func hide() {
            let restoreKey = shield?.isKeyWindow == true
            shield?.isHidden = true
            if restoreKey, let previousKey, !previousKey.isHidden { previousKey.makeKey() }
            shield = nil
            previousKey = nil
            visibleMode = .hidden
        }

        private func show(_ mode: ShieldMode) {
            guard mode != visibleMode else { return }
            guard mode != .hidden else { hide(); return }
            guard let settings, let scene = window?.windowScene else { return }
            let cover = shield ?? UIWindow(windowScene: scene)
            if shield == nil {
                previousKey = scene.keyWindow
                cover.windowLevel = .alert + 1
                cover.backgroundColor = .systemBackground
                shield = cover
            }
            let content: AnyView
            switch mode {
            case .lock: content = AnyView(LockScreen(lock: AppLock.shared))
            case .privacy: content = AnyView(PrivacyCover())
            case .hidden: return
            }
            let controller = UIHostingController(rootView: content
                .environment(\.layoutDirection, settings.layoutDirection)
                .environment(\.locale, settings.locale)
                .dynamicTypeSize(settings.dynamicTypeSize))
            controller.view.accessibilityViewIsModal = true
            cover.overrideUserInterfaceStyle = settings.colorScheme == .dark ? .dark : .light
            cover.rootViewController = controller
            visibleMode = mode
            if mode == .lock { cover.makeKeyAndVisible() }
            else { cover.isHidden = false }
        }
    }
}
