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
print("Shared payroll column vectors passed in Swift")
