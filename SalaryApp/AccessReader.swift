import Foundation

/// قارئ لملفات Microsoft Access (.accdb و.mdb بصيغة Jet4/ACE)، مكتوب من وصف صيغة الملف المنشور.
/// يقرأ تعريفات الجداول والصفوف: النصوص والأرقام والتواريخ والقيم المنطقية والمذكرات (Memo).
/// يعمل على بايتات الملف مباشرة (الملف يُفتح بالربط بالذاكرة فلا يُحمّل كاملاً).
nonisolated struct AccessReader {
    static let pageSize = 4096

    enum ReadError: LocalizedError {
        case notAccess
        case tableMissing(String)
        var errorDescription: String? {
            switch self {
            case .notAccess: tr("الملف ليس قاعدة بيانات Access صالحة.", "This isn't a valid Access database.")
            case .tableMissing(let name): tr("لم يُعثر على جدول \"\(name)\" في قاعدة البيانات.", "Table \"\(name)\" wasn't found.")
            }
        }
    }

    enum ColumnType: UInt8 {
        case bool = 1, byte = 2, int = 3, long = 4, money = 5, float = 6, double = 7, date = 8
        case binary = 9, text = 10, ole = 11, memo = 12, guid = 15, numeric = 16
    }

    struct Column {
        let name: String
        let type: UInt8
        let number: Int
        let varIndex: Int
        let isFixed: Bool
        let fixedOffset: Int
        let length: Int
    }

    struct Table {
        let rowCount: Int
        let columns: [Column]
    }

    enum Value {
        case text(String)
        case int(Int)
        case double(Double)
        case date(Date)
        case bool(Bool)
    }

    private let bytes: UnsafeRawBufferPointer
    let pageCount: Int
    /// صفحات البيانات لكل جدول (رقم صفحة تعريف الجدول ← صفحاته).
    private let dataPages: [Int: [Int]]

    /// `bytes` يجب أن تبقى صالحة طوال استخدام القارئ (داخل withUnsafeBytes).
    init(bytes: UnsafeRawBufferPointer) throws {
        guard bytes.count >= Self.pageSize * 3, bytes[0] == 0, bytes[1] == 1 else { throw ReadError.notAccess }
        // صيغة Jet3 القديمة (Access 97) صفحاتها 2048 ولا تُدعم.
        guard bytes[0x14] >= 1 else { throw ReadError.notAccess }
        self.bytes = bytes
        pageCount = bytes.count / Self.pageSize
        var pages: [Int: [Int]] = [:]
        for n in 0..<pageCount {
            let base = n * Self.pageSize
            if bytes[base] == 1 {
                let owner = Int(Self.u32(bytes, base + 4))
                pages[owner, default: []].append(n)
            }
        }
        dataPages = pages
    }

    // MARK: - قراءة الأعداد

    private static func u16(_ b: UnsafeRawBufferPointer, _ o: Int) -> Int {
        Int(b[o]) | Int(b[o + 1]) << 8
    }

    private static func u32(_ b: UnsafeRawBufferPointer, _ o: Int) -> UInt32 {
        UInt32(b[o]) | UInt32(b[o + 1]) << 8 | UInt32(b[o + 2]) << 16 | UInt32(b[o + 3]) << 24
    }

    private static func u16(_ b: [UInt8], _ o: Int) -> Int {
        Int(b[o]) | Int(b[o + 1]) << 8
    }

    private static func u32(_ b: [UInt8], _ o: Int) -> UInt32 {
        UInt32(b[o]) | UInt32(b[o + 1]) << 8 | UInt32(b[o + 2]) << 16 | UInt32(b[o + 3]) << 24
    }

    private func page(_ n: Int) -> UnsafeRawBufferPointer {
        UnsafeRawBufferPointer(rebasing: bytes[(n * Self.pageSize)..<((n + 1) * Self.pageSize)])
    }

    // MARK: - تعريف الجدول

    func table(at tdefPage: Int) throws -> Table {
        var p = page(tdefPage)
        guard p[0] == 2 else { throw ReadError.notAccess }
        var b = [UInt8](p)
        var next = Int(Self.u32(p, 4))
        var guardCount = 0
        while next != 0, next < pageCount, guardCount < 1000 {
            p = page(next)
            b += p[8...]
            next = Int(Self.u32(p, 4))
            guardCount += 1
        }

        let rowCount = Int(Self.u32(b, 0x10))
        let columnCount = Self.u16(b, 0x2d)
        let realIndexCount = Int(Self.u32(b, 0x33))
        var off = 0x3f + realIndexCount * 12

        var raw: [(type: UInt8, num: Int, varIdx: Int, flags: UInt8, fixedOff: Int, len: Int)] = []
        for _ in 0..<columnCount {
            raw.append((
                type: b[off],
                num: Self.u16(b, off + 5),
                varIdx: Self.u16(b, off + 7),
                flags: b[off + 15],
                fixedOff: Self.u16(b, off + 21),
                len: Self.u16(b, off + 23)
            ))
            off += 25
        }
        var columns: [Column] = []
        for r in raw {
            let length = Self.u16(b, off)
            let nameBytes = Array(b[(off + 2)..<(off + 2 + length)])
            let name = String(bytes: nameBytes, encoding: .utf16LittleEndian) ?? ""
            off += 2 + length
            columns.append(Column(
                name: name, type: r.type, number: r.num, varIndex: r.varIdx,
                isFixed: r.flags & 0x01 != 0, fixedOffset: r.fixedOff, length: r.len
            ))
        }
        return Table(rowCount: rowCount, columns: columns)
    }

    // MARK: - الصفوف

    /// بايتات الصف رقم `index` في الصفحة `pageNumber`، مع أعلام الصف (محذوف / مؤشر لصف آخر).
    private func rowSlice(page pageNumber: Int, index: Int) -> (UnsafeRawBufferPointer, Int)? {
        guard pageNumber > 0, pageNumber < pageCount else { return nil }
        let p = page(pageNumber)
        let count = Self.u16(p, 12)
        guard index < count else { return nil }
        let startRaw = Self.u16(p, 14 + index * 2)
        let start = startRaw & 0x1FFF
        let end = index == 0 ? Self.pageSize : Self.u16(p, 14 + (index - 1) * 2) & 0x1FFF
        guard start < end, end <= Self.pageSize else { return nil }
        return (UnsafeRawBufferPointer(rebasing: p[start..<end]), startRaw)
    }

    /// يمر على كل صفوف الجدول ويستدعي `body` بقيم الأعمدة (الفارغة غير موجودة في القاموس).
    func forEachRow(of tdefPage: Int, table: Table, _ body: ([String: Value]) -> Void) {
        for pageNumber in dataPages[tdefPage] ?? [] {
            let p = page(pageNumber)
            let count = Self.u16(p, 12)
            for i in 0..<count {
                guard let slice = rowSlice(page: pageNumber, index: i) else { continue }
                var row = slice.0
                let flags = slice.1
                if flags & 0x8000 != 0 { continue }        // محذوف
                if flags & 0x4000 != 0 {                   // الصف في صفحة أخرى
                    guard row.count >= 4 else { continue }
                    let target = Int(row[1]) | Int(row[2]) << 8 | Int(row[3]) << 16
                    guard let moved = rowSlice(page: target, index: Int(row[0])) else { continue }
                    row = moved.0
                }
                if let values = parseRow(row, table: table) { body(values) }
            }
        }
    }

    private func parseRow(_ r: UnsafeRawBufferPointer, table: Table) -> [String: Value]? {
        guard r.count >= 4 else { return nil }
        let columnCount = Self.u16(r, 0)
        let maskSize = (columnCount + 7) / 8
        let last = r.count - 1
        guard r.count > maskSize + 3 else { return nil }
        let maskStart = r.count - maskSize
        let varCount = Self.u16(r, last - maskSize - 1)
        let tableStart = last - maskSize - 3 - varCount * 2
        guard tableStart >= 2 else { return nil }
        var varOffsets: [Int] = []
        varOffsets.reserveCapacity(varCount + 1)
        for i in 0...varCount {
            varOffsets.append(Self.u16(r, last - maskSize - 3 - i * 2))
        }

        var out: [String: Value] = [:]
        for c in table.columns {
            let n = c.number
            let present = n < columnCount && (r[maskStart + n / 8] >> UInt8(n % 8)) & 1 == 1
            if c.type == ColumnType.bool.rawValue {
                if n < columnCount { out[c.name] = .bool(present) }
                continue
            }
            guard present else { continue }
            let slice: UnsafeRawBufferPointer
            if c.isFixed {
                let s = 2 + c.fixedOffset
                guard s + c.length <= r.count else { continue }
                slice = UnsafeRawBufferPointer(rebasing: r[s..<(s + c.length)])
            } else {
                guard c.varIndex < varCount else { continue }
                let s = varOffsets[c.varIndex]
                let e = varOffsets[c.varIndex + 1]
                guard s <= e, e <= r.count else { continue }
                slice = UnsafeRawBufferPointer(rebasing: r[s..<e])
            }
            if let v = value(of: c, slice) { out[c.name] = v }
        }
        return out
    }

    private func value(of c: Column, _ v: UnsafeRawBufferPointer) -> Value? {
        switch ColumnType(rawValue: c.type) {
        case .byte:
            return v.count >= 1 ? .int(Int(v[0])) : nil
        case .int:
            return v.count >= 2 ? .int(Int(Int16(bitPattern: UInt16(Self.u16(v, 0))))) : nil
        case .long:
            return v.count >= 4 ? .int(Int(Int32(bitPattern: Self.u32(v, 0)))) : nil
        case .money:
            guard v.count >= 8 else { return nil }
            return .double(Double(Int64(bitPattern: UInt64(Self.u32(v, 0)) | UInt64(Self.u32(v, 4)) << 32)) / 10000)
        case .float:
            guard v.count >= 4 else { return nil }
            return .double(Double(Float(bitPattern: Self.u32(v, 0))))
        case .double:
            guard v.count >= 8 else { return nil }
            return .double(Double(bitPattern: UInt64(Self.u32(v, 0)) | UInt64(Self.u32(v, 4)) << 32))
        case .date:
            guard v.count >= 8 else { return nil }
            let days = Double(bitPattern: UInt64(Self.u32(v, 0)) | UInt64(Self.u32(v, 4)) << 32)
            guard days.isFinite, abs(days) < 200_000 else { return nil }
            // تواريخ Access: أيام منذ 1899-12-30.
            return .date(Date(timeIntervalSince1970: (days - 25569) * 86400))
        case .text:
            return .text(Self.decodeText(v))
        case .memo:
            return memo(v).map(Value.text)
        default:
            return nil
        }
    }

    private func memo(_ v: UnsafeRawBufferPointer) -> String? {
        guard v.count >= 12 else { return nil }
        let header = Self.u32(v, 0)
        let flag = header >> 24
        let length = Int(header & 0xFFFFFF)
        if flag & 0x80 != 0 {
            let end = min(12 + length, v.count)
            return Self.decodeText(UnsafeRawBufferPointer(rebasing: v[12..<end]))
        }
        var rowIndex = Int(v[4])
        var pageNumber = Int(v[5]) | Int(v[6]) << 8 | Int(v[7]) << 16
        if flag & 0x40 != 0 {
            guard let raw = rowSlice(page: pageNumber, index: rowIndex)?.0 else { return nil }
            let end = min(length, raw.count)
            return Self.decodeText(UnsafeRawBufferPointer(rebasing: raw[0..<end]))
        }
        var data: [UInt8] = []
        var hops = 0
        while pageNumber != 0, data.count < length, hops < 10_000 {
            guard let raw = rowSlice(page: pageNumber, index: rowIndex)?.0, raw.count >= 4 else { break }
            let nextRow = Int(raw[0])
            let nextPage = Int(raw[1]) | Int(raw[2]) << 8 | Int(raw[3]) << 16
            data += raw[4...]
            rowIndex = nextRow
            pageNumber = nextPage
            hops += 1
        }
        return data.withUnsafeBytes { Self.decodeText(UnsafeRawBufferPointer(rebasing: $0[0..<min(length, data.count)])) }
    }

    /// نص Access: UTF-16، أو "مضغوط" إذا بدأ بـ FF FE (حرف إنجليزي ببايت واحد، والصفر يبدّل الوضع).
    static func decodeText(_ v: UnsafeRawBufferPointer) -> String {
        if v.count >= 2, v[0] == 0xFF, v[1] == 0xFE {
            var units: [UInt16] = []
            units.reserveCapacity(v.count)
            var compressed = true
            var i = 2
            while i < v.count {
                if v[i] == 0 {
                    compressed.toggle()
                    i += 1
                    continue
                }
                if compressed {
                    units.append(UInt16(v[i]))
                    i += 1
                } else {
                    guard i + 1 < v.count else { break }
                    units.append(UInt16(v[i]) | UInt16(v[i + 1]) << 8)
                    i += 2
                }
            }
            return String(decoding: units, as: UTF16.self)
        }
        var units: [UInt16] = []
        units.reserveCapacity(v.count / 2)
        var i = 0
        while i + 1 < v.count {
            units.append(UInt16(v[i]) | UInt16(v[i + 1]) << 8)
            i += 2
        }
        return String(decoding: units, as: UTF16.self)
    }

    // MARK: - قائمة الجداول

    /// جداول المستخدم من جدول النظام MSysObjects (صفحة 2): الاسم ← صفحة تعريف الجدول.
    func userTables() throws -> [String: Int] {
        let system = try table(at: 2)
        var result: [String: Int] = [:]
        forEachRow(of: 2, table: system) { row in
            guard case .int(let type)? = row["Type"], type == 1,
                  case .text(let name)? = row["Name"], !name.hasPrefix("MSys"),
                  case .int(let id)? = row["Id"] else { return }
            if case .int(let flags)? = row["Flags"], UInt32(truncatingIfNeeded: flags) & 0x80000002 != 0 { return }
            result[name] = id & 0x00FF_FFFF
        }
        return result
    }
}
