import Foundation

// اختبارات لينكس المستقلة تستخدم sha256sum؛ التطبيق سيستخدم CryptoKit عند ربط المكوّن.
func digest(_ data: Data) -> String {
    let process = Process()
    process.executableURL = URL(fileURLWithPath: "/usr/bin/sha256sum")
    let input = Pipe(); let output = Pipe()
    process.standardInput = input; process.standardOutput = output
    try! process.run()
    input.fileHandleForWriting.write(data)
    try! input.fileHandleForWriting.close()
    let result = output.fileHandleForReading.readDataToEndOfFile()
    process.waitUntilExit()
    precondition(process.terminationStatus == 0)
    return String(data: result, encoding: .utf8)!.split(separator: " ").first!.uppercased()
}
let samples = [
    "{\"headers\":[\"الاسم\",\"الاسم\",\"\"],\"values\":[\"أ\",\"\",\"س\"],\"future\":{\"keep\":7}}",
    "  { \"headers\" : [\"أ\\\"ب\", \"ملاحظة\"], \"values\": [\"\\u0627\", \"\"] } \n",
    "{\"headers\":[],\"values\":[]}",
    "{\"values\":[\"x\"],\"headers\":[\"a\"]}",
    "{\"future\":{\"headers\":[\"متداخل\"]},\"values\":[\"text \\\"headers\\\": [x]\"],\"headers\":[\"a\"]}",
]
for (index, sample) in samples.enumerated() {
    let original = Data(sample.utf8)
    let parts = SharedPayrollColumns.split(original, digest: digest)!
    if index == 0 { precondition(parts.schemaID == "A4760FF0B92D6CA8158C9517118F6B68BC798793BF5DF760141CBB2966A60A48") }
    let restored = try SharedPayrollColumns.join(schemaID: parts.schemaID, prefix: parts.prefix, before: parts.before, tail: parts.tail, digest: digest)
    precondition(restored == original)
    do {
        _ = try SharedPayrollColumns.join(schemaID: parts.schemaID, prefix: parts.prefix + Data([32]), before: parts.before, tail: parts.tail, digest: digest)
        fatalError("corrupted schema accepted")
    } catch { }
}
let ordered = SharedPayrollColumns.split(Data("{\"headers\":[\"a\"],\"values\":[\"x\"]}".utf8), digest: digest)!
let reordered = SharedPayrollColumns.split(Data("{\"values\":[\"x\"],\"headers\":[\"a\"]}".utf8), digest: digest)!
precondition(ordered.schemaID == reordered.schemaID)
for sample in ["not-json", "{\"headers\":[\"a\"],\"values\":[]}", "{\"headers\":[1],\"values\":[\"x\"]}"] {
    precondition(SharedPayrollColumns.split(Data(sample.utf8), digest: digest) == nil)
}
var sharedID: String?
for i in 0..<25 {
    let encodedValues = try JSONEncoder().encode(["منتسب \(i)", "", "نص \"وهمي\""])
    var raw = Data("{\"headers\":[\"الاسم\",\"الاسم\",\"ملاحظة\"],\"values\":".utf8)
    raw.append(encodedValues); raw.append(Data(",\"future\":{\"id\":\(i)}}".utf8))
    let parts = SharedPayrollColumns.split(raw, digest: digest)!
    if sharedID == nil { sharedID = parts.schemaID }
    precondition(parts.schemaID == sharedID)
    let restored = try SharedPayrollColumns.join(schemaID: parts.schemaID, prefix: parts.prefix, before: parts.before, tail: parts.tail, digest: digest)
    precondition(restored == raw)
}
var rowSchemas: [String: Data] = [:]
let encoder = JSONEncoder()
let headerData = Data("{\"headers\":[\"اسم المنتسب\",\"رقم العمل\",\"الملاحظات\",\"الملاحظات\"],\"values\":[\"وهمي\",\"123\",\"\",\"أ\"],\"future\":{\"keep\":true}}".utf8)
var samplePacked: SharedPayrollBackupRow.Packed?
for i in 0..<10 {
    let name = String(data: try encoder.encode("منتسب \(i)"), encoding: .utf8)!
    let outer = Data("{\"name\":\(name),\"rawColumns\":\"\(headerData.base64EncodedString())\",\"future\":{\"amount\":12345678901234567}}".utf8)
    let packed = try SharedPayrollBackupRow.pack(outer, schemas: &rowSchemas, digest: digest)
    precondition(packed.schemaID != nil)
    let restored = try SharedPayrollBackupRow.unpack(packed, schemas: rowSchemas, digest: digest)
    precondition(restored == outer)
    if samplePacked == nil { samplePacked = packed }
}
precondition(rowSchemas.count == 1)
let packed = samplePacked!
let damaged = SharedPayrollBackupRow.Packed(schemaID: packed.schemaID, before: packed.before, after: packed.after! + Data([32]), original: nil, rowHash: packed.rowHash)
do { _ = try SharedPayrollBackupRow.unpack(damaged, schemas: rowSchemas, digest: digest); fatalError("damaged row accepted") } catch { }
do { _ = try SharedPayrollBackupRow.unpack(packed, schemas: [:], digest: digest); fatalError("missing schema accepted") } catch { }
var corruptSchemas = rowSchemas
corruptSchemas[packed.schemaID!]!.append(32)
do { _ = try SharedPayrollBackupRow.unpack(packed, schemas: corruptSchemas, digest: digest); fatalError("corrupt schema accepted") } catch { }
let fallback = Data("{\"rawColumns\":null,\"future\":\"محفوظ\"}".utf8)
let packedFallback = try SharedPayrollBackupRow.pack(fallback, schemas: &rowSchemas, digest: digest)
precondition(packedFallback.schemaID == nil)
let unpackedFallback = try SharedPayrollBackupRow.unpack(packedFallback, schemas: rowSchemas, digest: digest)
precondition(unpackedFallback == fallback)
let escapedColumns = Data("{\"headers\":[\"࿿࿿࿿࿿࿿࿿࿿࿿࿿࿿\"],\"values\":[\"x\"]}".utf8)
let encodedEscaped = escapedColumns.base64EncodedString()
precondition(encodedEscaped.contains("/") && encodedEscaped.contains("+"))
let escapedRow = Data("{\"rawColumns\":\"\(encodedEscaped.replacingOccurrences(of: "/", with: "\\/").replacingOccurrences(of: "+", with: "\\u002B"))\",\"future\":true}".utf8)
let escapedPacked = try SharedPayrollBackupRow.pack(escapedRow, schemas: &rowSchemas, digest: digest)
precondition(escapedPacked.schemaID != nil)
let escapedRestored = try SharedPayrollBackupRow.unpack(escapedPacked, schemas: rowSchemas, digest: digest)
precondition(escapedRestored == escapedRow)
if let fixturePath = ProcessInfo.processInfo.environment["SALARY_SHARED_FIXTURE_PATH"] {
    struct Fixture: Codable {
        let Row: SharedPayrollBackupRow.Packed
        let Schemas: [String: Data]
        let Original: Data
    }
    let fixture = try JSONDecoder().decode(Fixture.self, from: Data(contentsOf: URL(fileURLWithPath: fixturePath)))
    let fromWindows = try SharedPayrollBackupRow.unpack(fixture.Row, schemas: fixture.Schemas, digest: digest)
    precondition(fromWindows == fixture.Original)
    var swiftSchemas: [String: Data] = [:]
    let fromSwift = try SharedPayrollBackupRow.pack(fixture.Original, schemas: &swiftSchemas, digest: digest)
    precondition(fromSwift.schemaID == fixture.Row.schemaID && fromSwift.before == fixture.Row.before && fromSwift.after == fixture.Row.after && fromSwift.rowHash == fixture.Row.rowHash && swiftSchemas == fixture.Schemas)
    print("C# and Swift produced identical packed rows and schemas")
}
if let fixturePath = ProcessInfo.processInfo.environment["SALARY_ESCAPED_FIXTURE_PATH"] {
    struct EscapedFixture: Codable {
        let Row: SharedPayrollBackupRow.Packed
        let Schemas: [String: Data]
        let Original: Data
    }
    let fixture = try JSONDecoder().decode(EscapedFixture.self, from: Data(contentsOf: URL(fileURLWithPath: fixturePath)))
    let restored = try SharedPayrollBackupRow.unpack(fixture.Row, schemas: fixture.Schemas, digest: digest)
    precondition(restored == fixture.Original)
    var swiftSchemas: [String: Data] = [:]
    let fromSwift = try SharedPayrollBackupRow.pack(fixture.Original, schemas: &swiftSchemas, digest: digest)
    precondition(fromSwift.schemaID == fixture.Row.schemaID && fromSwift.before == fixture.Row.before && fromSwift.after == fixture.Row.after && fromSwift.rowHash == fixture.Row.rowHash)
    print("C# and Swift preserve escaped Base64 JSON bytes exactly")
}
if let candidateDir = ProcessInfo.processInfo.environment["SALARY_CANDIDATE_FIXTURE_DIR"] {
    let folder = URL(fileURLWithPath: candidateDir, isDirectory: true)
    let schemaURL = folder.appendingPathComponent("candidate-schemas.jsonl")
    let rowURL = folder.appendingPathComponent("candidate-rows.jsonl")
    let sourceURL = folder.appendingPathComponent("source-rows.jsonl")
    let candidateSchemas = try SharedPayrollCandidateReader.schemas(from: schemaURL, digest: digest)
    var restored = Data()
    try SharedPayrollCandidateReader.rows(from: rowURL, schemas: candidateSchemas, digest: digest) { restored.append($0) }
    let originalRows = try Data(contentsOf: sourceURL)
    precondition(restored == originalRows)
    let temp = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("salary-schema-test-\(UUID().uuidString)")
    defer { try? FileManager.default.removeItem(at: temp) }
    let rawSchemas = try Data(contentsOf: schemaURL)
    try (rawSchemas + rawSchemas).write(to: temp)
    do { _ = try SharedPayrollCandidateReader.schemas(from: temp, digest: digest); fatalError("duplicate schema accepted") } catch { }
    do { try SharedPayrollCandidateReader.rows(from: rowURL, schemas: [:], digest: digest) { _ in }; fatalError("missing schema accepted") } catch { }
    print("Swift restored the C# candidate archive's employee bytes exactly")
    if let outputDir = ProcessInfo.processInfo.environment["SALARY_SWIFT_CANDIDATE_OUTPUT"] {
        struct SchemaLine: Encodable {
            let id: String
            let data: Data
            enum CodingKeys: String, CodingKey { case id = "Id", data = "Data" }
        }
        let outputFolder = URL(fileURLWithPath: outputDir, isDirectory: true)
        try FileManager.default.createDirectory(at: outputFolder, withIntermediateDirectories: true)
        var swiftSchemas: [String: Data] = [:]
        var swiftRows = Data()
        try BackupLineReader.read(sourceURL, includeNewlines: true) { line in
            let packed = try SharedPayrollBackupRow.pack(line, schemas: &swiftSchemas, digest: digest)
            swiftRows.append(try JSONEncoder().encode(packed)); swiftRows.append(10)
        }
        var encodedSchemas = Data()
        for key in swiftSchemas.keys.sorted() {
            encodedSchemas.append(try JSONEncoder().encode(SchemaLine(id: key, data: swiftSchemas[key]!)))
            encodedSchemas.append(10)
        }
        try swiftRows.write(to: outputFolder.appendingPathComponent("swift-rows.jsonl"))
        try encodedSchemas.write(to: outputFolder.appendingPathComponent("swift-schemas.jsonl"))
    }
}
print("Shared payroll column vectors passed in Swift")
