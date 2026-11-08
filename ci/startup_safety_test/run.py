from pathlib import Path
import subprocess,tempfile
root=Path(__file__).resolve().parents[2]
s=(root/'SalaryApp/Shortcuts.swift').read_text()
start=s.index('@MainActor @Observable\nfinal class DatabaseHealth')
end=s.index('/// ينقل',start)
boot=s[start:end]
# Recovery itself is exercised through SwiftData in the simulator; isolate the startup coordinator here.
a=boot.index('    private static func recoveredURL(')
boot=boot[:a] + '    static func recoveredURL(_ id: String) -> URL { URL(fileURLWithPath: "/fictional/" + id) }\n    static func makeRecoveredContainer(_ url: URL) throws -> ModelContainer { try ModelContainer(for: Employee.self) }\n}\n'
production='import Foundation\nimport Observation\n'+boot
stubs='''import Foundation
final class ModelContainer: @unchecked Sendable {
    static var count = 0
    init(for models: Any.Type...) throws { Self.count += 1 }
}
enum PayrollMonth {} ; enum Employee {} ; enum Deduction {} ; enum StaffMember {} ; enum ExecutionAccount {}
enum RestoreSafety {
    static var failed = false
    static func recoverIfNeeded() throws {
        Thread.sleep(forTimeInterval: 0.02)
        if !failed { failed = true; throw NSError(domain: "fictional-startup", code: 1) }
    }
}
'''
main='''import Foundation
@main struct Test {
    @MainActor static func main() async throws {
        do { try await SharedModel.ensureReady(); fatalError("Expected safe startup failure") } catch { }
        async let first = SharedModel.ensureReady()
        async let second = SharedModel.ensureReady()
        let (a,b) = try await (first,second)
        precondition(a === b && a === SharedModel.container && ModelContainer.count == 1)
        SharedModel.health.restartRequired = true
        do { try await SharedModel.ensureReady(); fatalError("Partial restore was exposed") } catch { }
        print("Startup retries, concurrent callers and recovery blocking passed")
    }
}
'''
with tempfile.TemporaryDirectory() as temp:
 p=Path(temp);(p/'Production.swift').write_text(production);(p/'Stubs.swift').write_text(stubs);(p/'Test.swift').write_text(main)
 subprocess.run(['swiftc','Production.swift','Stubs.swift','Test.swift','-o','test'],cwd=p,check=True)
 subprocess.run([str(p/'test')],cwd=p,check=True)
