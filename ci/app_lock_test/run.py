"""Run the production lock state transitions with a deterministic device authenticator."""
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[2]
source = (root / "SalaryApp/AppLock.swift").read_text()
unlock = source[source.index("    func unlock() async"):source.index("/// يغطي التطبيق")]
fixture = """
import Foundation
enum ScenePhase { case active, background, inactive }
struct Animation { static func easeOut(duration: Double) -> Animation { Animation() } }
func withAnimation(_ animation: Animation, _ body: () -> Void) { body() }
func tr(_ a: String, _ b: String) -> String { b }
struct LAError: Error {
    enum Code { case userCancel, appCancel, systemCancel, passcodeNotSet }
    let code: Code
}
@MainActor final class AppLock {
    static var timeout = 0
    static let isPreview = false, isEnabled = true
    static var evaluate: () async -> Result<Void, Error> = { .failure(LAError(code: .userCancel)) }
    static func authenticate(reason: String) async -> Result<Void, Error> { await evaluate() }
    var backgroundedAt: ContinuousClock.Instant?
    var authenticationGeneration = 0
    var isLocked = true, isAuthenticating = false, autoPrompt = false
    var message: String?
"""
tests = """
@main struct Tests {
    @MainActor static func main() async {
        let lock = AppLock()
        for code: LAError.Code in [.passcodeNotSet, .userCancel, .systemCancel, .appCancel] {
            AppLock.evaluate = { .failure(LAError(code: code)) }
            await lock.unlock()
            precondition(lock.isLocked && !lock.isAuthenticating)
        }
        AppLock.evaluate = { lock.scenePhaseChanged(.background); return .success(()) }
        await lock.unlock()
        precondition(lock.isLocked)
        AppLock.evaluate = { .success(()) }
        await lock.unlock()
        precondition(!lock.isLocked)
        AppLock.timeout = 900
        lock.scenePhaseChanged(.background)
        lock.scenePhaseChanged(.active)
        precondition(!lock.isLocked)
        AppLock.timeout = 0
        lock.scenePhaseChanged(.background)
        lock.scenePhaseChanged(.active)
        precondition(lock.isLocked)
        print("Unavailable credentials, cancellation, stale authentication, successful unlock and lock timeouts verified")
    }
}
"""
with tempfile.TemporaryDirectory() as temp:
    path = Path(temp)
    (path / "Test.swift").write_text(fixture + unlock + tests)
    subprocess.run(["swiftc", "-parse-as-library", "Test.swift", "-o", "test"], cwd=path, check=True)
    subprocess.run([str(path / "test")], cwd=path, check=True)
