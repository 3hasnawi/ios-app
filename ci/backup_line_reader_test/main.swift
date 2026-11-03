import Foundation

struct Row: Codable, Equatable { var text: String; var number: Int }
enum TestError: Error { case stop }
let file = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("salary-lines-\(UUID().uuidString).jsonl")
defer { try? FileManager.default.removeItem(at: file) }
let expected = [Row(text: "منتسب أول", number: 1), Row(text: String(repeating: "رواتب", count: 25_000), number: 2), Row(text: "أخير \"وهمي\"", number: 3)]
let encoder = JSONEncoder()
var input = Data([10, 10])
input.append(try encoder.encode(expected[0])); input.append(Data([13, 10, 10]))
input.append(try encoder.encode(expected[1])); input.append(10)
input.append(try encoder.encode(expected[2])) // no trailing newline
try input.write(to: file)
for chunkSize in [1, 2, 17, 1024, 65_536, input.count + 1] {
    var actual: [Row] = []
    var copied = Data()
    try BackupLineReader.read(file, chunkSize: chunkSize, onChunk: { copied.append($0) }) { line in
        actual.append(try JSONDecoder().decode(Row.self, from: line))
    }
    precondition(actual == expected, "decoded rows differ at chunk size \(chunkSize)")
    precondition(copied == input, "chunk hash input differs")
}
var calls = 0
do {
    try BackupLineReader.read(file, chunkSize: 7) { _ in calls += 1; throw TestError.stop }
    fatalError("callback error ignored")
} catch TestError.stop { precondition(calls == 1) }
try Data().write(to: file)
var empty = 0
try BackupLineReader.read(file) { _ in empty += 1 }
precondition(empty == 0)
try Data([123, 34, 120, 34, 58, 0xff, 125, 10]).write(to: file)
do {
    try BackupLineReader.read(file, chunkSize: 2) { line in _ = try JSONDecoder().decode(Row.self, from: line) }
    fatalError("invalid record accepted")
} catch is DecodingError { }
print("Backup line reader tests passed with fictional data")

// اختبار اختياري: ملف وهمي أكبر من 2.27 GB، لا يختبر SwiftData أو جهاز iPhone.
if CommandLine.arguments.contains("--large") {
    let sample = Row(text: String(repeating: "راتب وهمي ", count: 1_000), number: 7)
    var line = try encoder.encode(sample); line.append(10)
    let totalRows = 2_300_000_000 / line.count + 1
    _ = FileManager.default.createFile(atPath: file.path, contents: nil)
    let output = try FileHandle(forWritingTo: file)
    try output.truncate(atOffset: 0)
    var batch = Data()
    for _ in 0..<128 { batch.append(line) }
    for _ in 0..<(totalRows / 128) { try output.write(contentsOf: batch) }
    for _ in 0..<(totalRows % 128) { try output.write(contentsOf: line) }
    try output.close()
    var count = 0
    try BackupLineReader.read(file) { data in
        let row = try JSONDecoder().decode(Row.self, from: data)
        guard row.number == 7, row.text == sample.text else { fatalError("row changed") }
        count += 1
    }
    precondition(count == totalRows)
    let memory = (try? String(contentsOfFile: "/proc/self/status", encoding: .utf8))?.split(separator: "\n").first { $0.hasPrefix("VmHWM:") } ?? "memory unavailable"
    print("Large fictional JSONL passed: \(count) rows, \(totalRows * line.count) bytes, \(memory)")
}
