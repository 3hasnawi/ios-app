import Foundation
import Compression
import CryptoKit

nonisolated enum ContractBatchPayload {
    enum Failure: Error { case invalid }
    private static let limit = 64 * 1024 * 1024
    static func pack(_ data: Data) throws -> Data {
        guard !data.isEmpty, data.count <= limit else { throw Failure.invalid }
        var length = UInt32(data.count).bigEndian
        var result = withUnsafeBytes(of: &length) { Data($0) }
        result.append(contentsOf: SHA256.hash(data: data))
        result.append(try (data as NSData).compressed(using: .lzfse) as Data)
        return result
    }
    static func unpack(_ packed: Data) throws -> Data {
        guard packed.count > 36 else { throw Failure.invalid }
        let length = packed.prefix(4).reduce(0) { $0 * 256 + Int($1) }
        guard length > 0, length <= limit else { throw Failure.invalid }
        var result = Data(count: length + 1)
        let size = result.withUnsafeMutableBytes { dest in
            packed.withUnsafeBytes { src in
                compression_decode_buffer(dest.bindMemory(to: UInt8.self).baseAddress!, length + 1,
                    src.bindMemory(to: UInt8.self).baseAddress! + 36, packed.count - 36, nil, COMPRESSION_LZFSE)
            }
        }
        guard size == length else { throw Failure.invalid }
        result.removeLast()
        guard Data(SHA256.hash(data: result)) == packed.dropFirst(4).prefix(32) else { throw Failure.invalid }
        return result
    }
}
