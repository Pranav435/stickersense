import Foundation

/// Waiting for "whichever comes first" without being held hostage by the slow side.
/// (A task group won't return until every child finishes, so it can't implement a timeout
/// around work that ignores cancellation, like Vision's first text recognition.)
public enum Race {
    /// The task's value, or `nil` if it isn't finished within `timeout`. The task keeps running.
    public static func value<T: Sendable>(of task: Task<T, Never>, timeout: Duration) async -> T? {
        let once = Once()
        return await withCheckedContinuation { (continuation: CheckedContinuation<T?, Never>) in
            Task {
                let value = await task.value
                if once.claim() { continuation.resume(returning: value) }
            }
            Task {
                try? await Task.sleep(for: timeout)
                if once.claim() { continuation.resume(returning: nil) }
            }
        }
    }

    private final class Once: @unchecked Sendable {
        private let lock = NSLock()
        private var done = false

        func claim() -> Bool {
            lock.lock()
            defer { lock.unlock() }
            if done { return false }
            done = true
            return true
        }
    }
}
