import Foundation

private actor Counter {
    private(set) var calls = 0
    func increment() -> Int {
        calls += 1
        return calls
    }
}

private actor Gate {
    private var waiter: CheckedContinuation<Void, Never>?
    func wait() async { await withCheckedContinuation { waiter = $0 } }
    func waitUntilStarted() async { while waiter == nil { await Task.yield() } }
    func release() { waiter?.resume(); waiter = nil }
}

@main struct MonthlyCalculationTests {
    @MainActor static func main() async throws {
        let cache = MonthlyCalculationCache<String, Int>()
        let counter = Counter()
        let gate = Gate()
        let leaving = Task {
            try await cache.value(for: "October") {
                let result = await counter.increment()
                await gate.wait()
                return result
            }
        }
        await gate.waitUntilStarted()
        leaving.cancel()
        let returning = Task { try await cache.value(for: "October") { await counter.increment() } }
        await gate.release()
        do { _ = try await leaving.value; preconditionFailure("Cancelled view received a value") } catch is CancellationError {}
        let shared = try await returning.value
        let reopened = try await cache.value(for: "October") { await counter.increment() }
        precondition(shared == 1 && reopened == 1)
        let otherMonth = try await cache.value(for: "November") { await counter.increment() }
        precondition(otherMonth == 2)

        let slow = Gate()
        let obsolete = Task { try await cache.value(for: "December") { await slow.wait(); return 99 } }
        await slow.waitUntilStarted()
        cache.invalidate()
        let refreshed = try await cache.value(for: "December") { await counter.increment() }
        await slow.release()
        do { _ = try await obsolete.value; preconditionFailure("Returned invalidated work") } catch is CancellationError {}
        let retained = try await cache.value(for: "December") { 99 }
        precondition(refreshed == 3 && retained == 3)

        let waiting = Gate()
        let previous = Task { try await cache.value(for: "October") { await waiting.wait(); return 10 } }
        await waiting.waitUntilStarted()
        cache.invalidate("October")
        let updated = try await cache.value(for: "October") { 11 }
        let untouched = try await cache.value(for: "December") { 99 }
        await waiting.release()
        do { _ = try await previous.value; preconditionFailure("Returned pre-refresh amount") } catch is CancellationError {}
        let current = try await cache.value(for: "October") { 12 }
        precondition(updated == 11 && current == 11 && untouched == 3)

        enum Failure: Error { case temporary }
        do { _ = try await cache.value(for: "retry") { throw Failure.temporary }; preconditionFailure("Lost load failure") } catch Failure.temporary {}
        let retry = try await cache.value(for: "retry") { 7 }
        precondition(retry == 7)
        _ = try await cache.value(for: "January") { 1 }
        _ = try await cache.value(for: "February") { 2 }
        let evicted = try await cache.value(for: "December") { 4 }
        precondition(evicted == 4)
        print("Monthly calculation reuse, cancellation, invalidation, retry and eviction passed")
    }
}
