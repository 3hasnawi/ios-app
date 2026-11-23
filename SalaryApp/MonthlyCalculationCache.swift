import Foundation

/// A screen leaving the navigation stack does not cancel work shared by other screens.
@MainActor
final class MonthlyCalculationCache<Key: Hashable, Value: Sendable> {
    private struct Entry {
        let id = UUID()
        let work: Task<Value, Error>
    }
    private var entries: [Key: Entry] = [:]
    private var order: [Key] = []

    func value(for key: Key, load: @escaping @Sendable () async throws -> Value) async throws -> Value {
        try Task.checkCancellation()
        let entry: Entry
        if let cached = entries[key] {
            entry = cached
            order.removeAll { $0 == key }
        } else {
            if order.count == 3 {
                entries.removeValue(forKey: order.removeFirst())?.work.cancel()
            }
            entry = Entry(work: Task.detached(priority: .userInitiated, operation: load))
            entries[key] = entry
        }
        order.append(key)
        let result = await entry.work.result
        guard entries[key]?.id == entry.id else {
            if case .failure(let error) = result { throw error }
            throw CancellationError()
        }
        if case .failure = result {
            entries[key] = nil
            order.removeAll { $0 == key }
        }
        try Task.checkCancellation()
        return try result.get()
    }

    func invalidate() {
        for entry in entries.values { entry.work.cancel() }
        entries.removeAll()
        order.removeAll()
    }
}
