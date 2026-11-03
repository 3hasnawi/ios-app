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

/// كاتب تجريبي فقط؛ لا يغيّر نسخة المستخدم ولا يُستخدم في واجهة النسخ.
enum SharedPayrollCandidateWriter {
    private struct SchemaLine: Encodable {
        let id: String
        let data: Data
        enum CodingKeys: String, CodingKey { case id = "Id", data = "Data" }
    }

    static func write(source: URL, rows: URL, schemas: URL, digest: (Data) -> String) throws {
        let paths = [source, rows, schemas].map { $0.standardizedFileURL.path }
        guard Set(paths).count == 3 else { throw CocoaError(.fileWriteFileExists) }
        let fm = FileManager.default
        var madeRows = false, madeSchemas = false
        do {
            try Data().write(to: rows, options: .withoutOverwriting)
            madeRows = true
            try Data().write(to: schemas, options: .withoutOverwriting)
            madeSchemas = true
            let rowFile = try FileHandle(forWritingTo: rows)
            defer { try? rowFile.close() }
            let schemaFile = try FileHandle(forWritingTo: schemas)
            defer { try? schemaFile.close() }
            let encoder = JSONEncoder()
            var definitions: [String: Data] = [:]
            var schemaBytes = 0
            try BackupLineReader.read(source, includeNewlines: true, maxLineBytes: 8_000_000) { line in
                let previousCount = definitions.count
                let packed = try SharedPayrollBackupRow.pack(line, schemas: &definitions, digest: digest)
                if definitions.count != previousCount, let id = packed.schemaID, let definition = definitions[id] {
                    guard definitions.count <= 100_000, definition.count <= 128 * 1024 * 1024 - schemaBytes else {
                        throw CocoaError(.fileWriteOutOfSpace)
                    }
                    schemaBytes += definition.count
                    var encoded = try encoder.encode(SchemaLine(id: id, data: definition))
                    encoded.append(10)
                    try schemaFile.write(contentsOf: encoded)
                }
                var encoded = try encoder.encode(packed)
                guard encoded.count < 16 * 1024 * 1024 else { throw CocoaError(.fileWriteOutOfSpace) }
                encoded.append(10)
                try rowFile.write(contentsOf: encoded)
            }
        } catch {
            if madeRows { try? fm.removeItem(at: rows) }
            if madeSchemas { try? fm.removeItem(at: schemas) }
            throw error
        }
    }
}
