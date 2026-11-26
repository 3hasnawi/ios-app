import Foundation
import SwiftData

@Model final class Employee {
    var name: String
    init(name: String) { self.name = name }
}

@main struct CheckIdentity {
    @MainActor static func main() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        let config = ModelConfiguration(url: dir.appendingPathComponent("fixture.sqlite"))
        let container = try ModelContainer(for: Employee.self, configurations: config)
        let row = Employee(name: "Synthetic")
        container.mainContext.insert(row)
        try container.mainContext.save()
        let data = try JSONEncoder().encode(row.persistentModelID)
        print("Synthetic persistent identifier", String(decoding: data, as: UTF8.self))
        let decoded = try JSONDecoder().decode(PersistentIdentifier.self, from: data)
        precondition(decoded == row.persistentModelID)
    }
}
