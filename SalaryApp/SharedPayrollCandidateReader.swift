import Foundation

/// قارئ تجريبي لملفات الصفوف بعد استخراجها؛ غير مربوط باسترجاع التطبيق أو السيرفر.
enum SharedPayrollCandidateReader {
    private struct SchemaLine: Decodable {
        let id: String
        let data: Data
        enum CodingKeys: String, CodingKey { case id = "Id", data = "Data" }
    }
    private static let maxSchemas = 100_000
    private static let maxSchemaBytes = 128 * 1024 * 1024
    private static let maxLineBytes = 16 * 1024 * 1024
    private static func invalid(_ message: String) -> NSError {
        NSError(domain: "SharedPayrollCandidateReader", code: 1, userInfo: [NSLocalizedDescriptionKey: message])
    }
    static func schemas(from url: URL, digest: (Data) -> String) throws -> [String: Data] {
        var result: [String: Data] = [:]
        var total = 0
        let decoder = JSONDecoder()
        try BackupLineReader.read(url, maxLineBytes: maxLineBytes) { line in
            let schema = try decoder.decode(SchemaLine.self, from: line)
            guard schema.id.count == 64, digest(schema.data) == schema.id, result[schema.id] == nil,
                  result.count < maxSchemas, schema.data.count <= maxSchemaBytes - total else {
                throw invalid("مخطط أعمدة مكرر أو تالف أو كبير جداً.")
            }
            total += schema.data.count
            result[schema.id] = schema.data
        }
        return result
    }
    static func rows(from url: URL, schemas: [String: Data], digest: (Data) -> String,
                     onRow: (Data) throws -> Void) throws {
        let decoder = JSONDecoder()
        try BackupLineReader.read(url, maxLineBytes: maxLineBytes) { line in
            let row = try decoder.decode(SharedPayrollBackupRow.Packed.self, from: line)
            try onRow(SharedPayrollBackupRow.unpack(row, schemas: schemas, digest: digest))
        }
    }
}
