import Foundation

/// مكوّن تأسيسي للاختبار فقط؛ لا يُفعّل صيغة نسخ أو ترحيلاً تلقائياً.
/// تحفظ أجزاء JSON الأصلية حتى الأعمدة المكررة والحقول المستقبلية مهما كان ترتيب الخصائص.
enum SharedPayrollColumns {
    struct Parts {
        let schemaID: String
        let prefix: Data
        let before: Data
        let tail: Data
    }

    static func split(_ raw: Data, digest: (Data) -> String) -> Parts? {
        guard let object = try? JSONSerialization.jsonObject(with: raw) as? [String: Any],
              let headers = object["headers"] as? [String],
              let values = object["values"] as? [String], headers.count == values.count else { return nil }
        let bytes = [UInt8](raw)
        var index = 0
        func whitespace(_ b: UInt8) -> Bool { b == 0x20 || b == 0x09 || b == 0x0A || b == 0x0D }
        func skip(_ position: inout Int) { while position < bytes.count && whitespace(bytes[position]) { position += 1 } }
        func stringEnd(_ start: Int) -> Int? {
            var pos = start + 1; var escaped = false
            while pos < bytes.count {
                let byte = bytes[pos]; pos += 1
                if escaped { escaped = false }
                else if byte == 0x5C { escaped = true }
                else if byte == 0x22 { return pos }
            }
            return nil
        }
        skip(&index)
        guard index < bytes.count && bytes[index] == 0x7B else { return nil }
        index += 1
        var depth = 1
        while index < bytes.count {
            let byte = bytes[index]
            if byte == 0x22 {
                guard let end = stringEnd(index) else { return nil }
                var next = end; skip(&next)
                if depth == 1 && Array(bytes[index..<end]) == Array("\"headers\"".utf8) && next < bytes.count && bytes[next] == 0x3A {
                    next += 1; skip(&next)
                    guard next < bytes.count && bytes[next] == 0x5B else { return nil }
                    let start = next; next += 1
                    while next < bytes.count {
                        if bytes[next] == 0x22 {
                            guard let endString = stringEnd(next) else { return nil }
                            next = endString
                        } else if bytes[next] == 0x5D {
                            next += 1
                            let prefix = Data(bytes[start..<next])
                            let key = digest(prefix)
                            guard key.count == 64, key.unicodeScalars.allSatisfy({ (48...57).contains($0.value) || (65...70).contains($0.value) }) else { return nil }
                            return Parts(schemaID: key, prefix: prefix, before: Data(bytes[..<start]), tail: Data(bytes[next...]))
                        } else { next += 1 }
                    }
                    return nil
                }
                index = end
            } else {
                if byte == 0x7B || byte == 0x5B { depth += 1 }
                else if byte == 0x7D || byte == 0x5D { depth -= 1 }
                index += 1
            }
        }
        return nil
    }

    static func join(schemaID: String, prefix: Data, before: Data, tail: Data, digest: (Data) -> String) throws -> Data {
        guard schemaID.count == 64, digest(prefix) == schemaID else {
            throw NSError(domain: "SharedPayrollColumns", code: 1, userInfo: [NSLocalizedDescriptionKey: "بصمة أعمدة الرواتب لا تطابق المخطط المحفوظ."])
        }
        var raw = before
        raw.append(prefix)
        raw.append(tail)
        return raw
    }
}
