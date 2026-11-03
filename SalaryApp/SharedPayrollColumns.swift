import Foundation

/// مكوّن تأسيسي للاختبار فقط؛ لا يُفعّل صيغة نسخ أو ترحيلاً تلقائياً.
/// تحفظ البادئة واللاحقة بايتات JSON الأصلية حتى الأعمدة المكررة والحقول المستقبلية.
enum SharedPayrollColumns {
    struct Parts {
        let schemaID: String
        let prefix: Data
        let tail: Data
    }

    static func split(_ raw: Data, digest: (Data) -> String) -> Parts? {
        guard let object = try? JSONSerialization.jsonObject(with: raw) as? [String: Any],
              let headers = object["headers"] as? [String],
              let values = object["values"] as? [String], headers.count == values.count else { return nil }
        let bytes = [UInt8](raw)
        var index = 0
        func whitespace(_ b: UInt8) -> Bool { b == 0x20 || b == 0x09 || b == 0x0A || b == 0x0D }
        func skip() { while index < bytes.count && whitespace(bytes[index]) { index += 1 } }
        func take(_ byte: UInt8) -> Bool { guard index < bytes.count && bytes[index] == byte else { return false }; index += 1; return true }
        skip()
        guard take(0x7B) else { return nil } // {
        skip()
        let property = Array("\"headers\"".utf8)
        guard index + property.count <= bytes.count && Array(bytes[index..<(index + property.count)]) == property else { return nil }
        index += property.count; skip()
        guard take(0x3A) else { return nil } // :
        skip()
        guard take(0x5B) else { return nil } // [
        var inString = false
        var escaped = false
        var foundEnd = false
        while index < bytes.count {
            let byte = bytes[index]; index += 1
            if inString {
                if escaped { escaped = false }
                else if byte == 0x5C { escaped = true }
                else if byte == 0x22 { inString = false }
            } else if byte == 0x22 { inString = true }
            else if byte == 0x5D { foundEnd = true; break }
        }
        guard foundEnd else { return nil }
        let prefix = Data(bytes[..<index])
        let tail = Data(bytes[index...])
        let key = digest(prefix)
        guard key.count == 64, key.unicodeScalars.allSatisfy({ (48...57).contains($0.value) || (65...70).contains($0.value) }) else { return nil }
        return Parts(schemaID: key, prefix: prefix, tail: tail)
    }

    static func join(schemaID: String, prefix: Data, tail: Data, digest: (Data) -> String) throws -> Data {
        guard schemaID.count == 64, digest(prefix) == schemaID else {
            throw NSError(domain: "SharedPayrollColumns", code: 1, userInfo: [NSLocalizedDescriptionKey: "بصمة أعمدة الرواتب لا تطابق المخطط المحفوظ."])
        }
        var raw = prefix
        raw.append(tail)
        return raw
    }
}
