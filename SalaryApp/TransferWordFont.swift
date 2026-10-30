import Foundation

/// فحص حق تضمين الخط كاملاً في مستند قابل للتعديل قبل إضافته إلى Word.
nonisolated enum TransferWordFont {
    enum Failure: LocalizedError {
        case invalidOrRestricted
        var errorDescription: String? { tr("خط الترويسة غير صالح أو لا يسمح بتضمينه في مستند قابل للتعديل.", "The header font is invalid or does not permit embedding in an editable document.") }
    }
    static func permitsEditableEmbedding(_ data: Data) -> Bool {
        data.withUnsafeBytes { (bytes: UnsafeRawBufferPointer) -> Bool in
            guard bytes.count >= 12 else { return false }
            func u16(_ i: Int) -> Int { Int(bytes[i]) << 8 | Int(bytes[i + 1]) }
            func u32(_ i: Int) -> Int { Int(bytes[i]) << 24 | Int(bytes[i + 1]) << 16 | Int(bytes[i + 2]) << 8 | Int(bytes[i + 3]) }
            let tables = u16(4)
            guard tables > 0, tables <= 512, 12 + tables * 16 <= bytes.count else { return false }
            for index in 0..<tables {
                let entry = 12 + index * 16
                guard bytes[entry] == 0x4f, bytes[entry + 1] == 0x53,
                      bytes[entry + 2] == 0x2f, bytes[entry + 3] == 0x32 else { continue }
                let offset = u32(entry + 8), length = u32(entry + 12)
                guard length >= 10, offset <= bytes.count - 10, length <= bytes.count - offset else { return false }
                let flags = u16(offset + 8)
                let embedding = flags & 0x000e
                return (embedding == 0 || embedding == 0x0008) && flags & 0x0200 == 0
            }
            return false
        }
    }
    static func validateBold(_ data: Data) throws {
        guard permitsEditableEmbedding(data) else { throw Failure.invalidOrRestricted }
        _ = try TransferHeaderFont.make(data: data, style: .bold, size: 18)
    }
}
