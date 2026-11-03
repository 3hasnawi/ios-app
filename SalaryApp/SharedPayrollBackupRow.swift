import Foundation

/// تمثيل تجريبي غير مفعّل يحافظ على سطر JSON الأصلي وبصمته؛ لا يغيّر تنسيق النسخ الحالية.
enum SharedPayrollBackupRow {
    private static func validDigest(_ value: String) -> Bool {
        value.count == 64 && value.unicodeScalars.allSatisfy { (48...57).contains($0.value) || (65...70).contains($0.value) }
    }
    struct Packed: Codable {
        let schemaID: String?
        let before: Data?
        let after: Data?
        let original: Data?
        let rowHash: String
        enum CodingKeys: String, CodingKey {
            case schemaID = "SchemaId", before = "Before", after = "After", original = "Original", rowHash = "RowHash"
        }
    }

    static func pack(_ line: Data, schemas: inout [String: Data], digest: (Data) -> String) throws -> Packed {
        let rowHash = digest(line)
        guard validDigest(rowHash) else { throw NSError(domain: "SharedPayrollBackupRow", code: 5) }
        func fallback() -> Packed { Packed(schemaID: nil, before: nil, after: nil, original: line, rowHash: rowHash) }
        guard let object = try? JSONSerialization.jsonObject(with: line) as? [String: Any],
              let base64 = object["rawColumns"] as? String,
              let raw = Data(base64Encoded: base64), raw.base64EncodedString() == base64,
              let columns = SharedPayrollColumns.split(raw, digest: digest) else { return fallback() }
        let bytes = [UInt8](line)
        func whitespace(_ byte: UInt8) -> Bool { byte == 0x20 || byte == 0x09 || byte == 0x0A || byte == 0x0D }
        func stringEnd(_ start: Int) -> Int? {
            var index = start + 1; var escaped = false
            while index < bytes.count {
                let byte = bytes[index]; index += 1
                if escaped { escaped = false }
                else if byte == 0x5C { escaped = true }
                else if byte == 0x22 { return index }
            }
            return nil
        }
        var index = 0; var depth = 0; var found: (Int, Int)?
        while index < bytes.count {
            let byte = bytes[index]
            if byte == 0x22 {
                guard let end = stringEnd(index) else { return fallback() }
                var next = end
                while next < bytes.count && whitespace(bytes[next]) { next += 1 }
                if depth == 1 && next < bytes.count && bytes[next] == 0x3A {
                    let key = Array(bytes[index..<end])
                    // مفاتيح JSON المهربة غير متوقعة: أعد السجل الأصلي كاملاً.
                    if key.contains(0x5C) { return fallback() }
                    if key == Array("\"rawColumns\"".utf8) {
                        guard found == nil else { return fallback() }
                        next += 1
                        while next < bytes.count && whitespace(bytes[next]) { next += 1 }
                        guard next < bytes.count && bytes[next] == 0x22, let endValue = stringEnd(next) else { return fallback() }
                        found = (next, endValue)
                    }
                }
                index = end
            } else {
                if byte == 0x7B || byte == 0x5B { depth += 1 }
                else if byte == 0x7D || byte == 0x5D { depth -= 1 }
                index += 1
            }
        }
        guard let (tokenStart, tokenEnd) = found,
              let offsets = offsetsForBase64(Array(bytes[tokenStart..<tokenEnd]), decoded: base64) else { return fallback() }
        let start = ((columns.before.count + 2) / 3) * 3
        let end = ((columns.before.count + columns.prefix.count) / 3) * 3
        guard end - start >= 12 else { return fallback() }
        let first = start / 3 * 4; let last = end / 3 * 4
        guard last <= base64.utf8.count else { return fallback() }
        let begin = tokenStart + offsets[first]; let finish = tokenStart + offsets[last]
        let schema = Data(bytes[begin..<finish]); let schemaID = digest(schema)
        guard validDigest(schemaID) else { throw NSError(domain: "SharedPayrollBackupRow", code: 5) }
        if let existing = schemas[schemaID], existing != schema {
            throw NSError(domain: "SharedPayrollBackupRow", code: 1, userInfo: [NSLocalizedDescriptionKey: "تعارضت بصمة مخطط أعمدة الرواتب."])
        }
        schemas[schemaID] = schema
        return Packed(schemaID: schemaID, before: Data(bytes[..<begin]), after: Data(bytes[finish...]), original: nil, rowHash: rowHash)
    }

    private static func offsetsForBase64(_ token: [UInt8], decoded: String) -> [Int]? {
        let expected = Array(decoded.utf8)
        guard token.count >= 2, token.first == 0x22, token.last == 0x22 else { return nil }
        var offsets = [Int](); offsets.reserveCapacity(expected.count + 1)
        var pos = 1
        for expectedByte in expected {
            offsets.append(pos)
            guard pos < token.count - 1 else { return nil }
            var character = Int(token[pos]); pos += 1
            if character == 0x5C {
                guard pos < token.count - 1 else { return nil }
                character = Int(token[pos]); pos += 1
                if character == 0x75 {
                    guard pos + 4 <= token.count - 1 else { return nil }
                    var value = 0
                    for _ in 0..<4 {
                        let hex = token[pos]; pos += 1
                        let digit: Int
                        if (48...57).contains(hex) { digit = Int(hex - 48) }
                        else if (65...70).contains(hex) { digit = Int(hex - 65) + 10 }
                        else if (97...102).contains(hex) { digit = Int(hex - 97) + 10 }
                        else { return nil }
                        value = value * 16 + digit
                    }
                    character = value
                } else if character != 0x2F { return nil }
            }
            guard character == Int(expectedByte) else { return nil }
        }
        offsets.append(pos)
        return pos == token.count - 1 ? offsets : nil
    }

    static func unpack(_ row: Packed, schemas: [String: Data], digest: (Data) -> String) throws -> Data {
        guard validDigest(row.rowHash) else { throw NSError(domain: "SharedPayrollBackupRow", code: 5) }
        let result: Data
        if let schemaID = row.schemaID {
            guard validDigest(schemaID), row.original == nil, let before = row.before, let after = row.after,
                  let schema = schemas[schemaID], digest(schema) == schemaID else {
                throw NSError(domain: "SharedPayrollBackupRow", code: 2, userInfo: [NSLocalizedDescriptionKey: "مخطط أعمدة الرواتب مفقود أو تالف."])
            }
            var joined = before; joined.append(schema); joined.append(after); result = joined
        } else {
            guard let original = row.original, row.before == nil, row.after == nil else {
                throw NSError(domain: "SharedPayrollBackupRow", code: 3, userInfo: [NSLocalizedDescriptionKey: "سجل الرواتب الأصلي غير صالح."])
            }
            result = original
        }
        guard digest(result) == row.rowHash else {
            throw NSError(domain: "SharedPayrollBackupRow", code: 4, userInfo: [NSLocalizedDescriptionKey: "سجل الرواتب لا يطابق بصمته."])
        }
        return result
    }
}
