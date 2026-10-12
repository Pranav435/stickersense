import AppKit
import Foundation

/// The VoiceOver cursor: where it is and what VoiceOver says about the item under it.
nonisolated struct VOCursor: Equatable, Sendable {
    /// Global screen coordinates in points, top-left origin (the same space ScreenCaptureKit uses).
    var bounds: CGRect
    var text: String

    /// Two readings refer to the same item when VoiceOver says the same thing about the same spot.
    func isSameItem(as other: VOCursor?) -> Bool {
        guard let other else { return false }
        return text == other.text
            && abs(bounds.midX - other.bounds.midX) < 3 && abs(bounds.midY - other.bounds.midY) < 3
            && abs(bounds.width - other.bounds.width) < 4 && abs(bounds.height - other.bounds.height) < 4
    }

    /// Same label and size, possibly scrolled to a different position.
    func isLikelySameItem(as other: VOCursor?) -> Bool {
        guard let other else { return false }
        return text == other.text
            && abs(bounds.width - other.bounds.width) < 4 && abs(bounds.height - other.bounds.height) < 4
    }
}

nonisolated enum VoiceOverError: Error, LocalizedError, Equatable {
    case notRunning
    /// "Allow VoiceOver to be controlled with AppleScript" is off in VoiceOver Utility.
    case appleScriptDisabled
    /// The user declined the macOS Automation prompt for controlling VoiceOver.
    case automationDenied
    case failed(String)

    var errorDescription: String? {
        switch self {
        case .notRunning:
            "VoiceOver isn't running."
        case .appleScriptDisabled:
            "VoiceOver doesn't allow AppleScript control. In VoiceOver Utility, General, turn on Allow VoiceOver to be controlled with AppleScript."
        case .automationDenied:
            "StickerSense isn't allowed to talk to VoiceOver. In System Settings, Privacy and Security, Automation, turn on VoiceOver under StickerSense."
        case .failed(let message):
            "Couldn't reach VoiceOver: \(message)"
        }
    }
}

/// Talks to VoiceOver through its AppleScript dictionary. All scripts run on one private
/// serial queue because NSAppleScript is not thread-safe.
nonisolated final class VoiceOverBridge: @unchecked Sendable {
    private let queue = DispatchQueue(label: "StickerSense.VoiceOver", qos: .userInitiated)
    private var cursorScript: NSAppleScript?
    private var boundsScript: NSAppleScript?
    private var lastPhraseScript: NSAppleScript?

    var isRunning: Bool { NSWorkspace.shared.isVoiceOverEnabled }

    /// Reads the VoiceOver cursor's rectangle and spoken text in a single Apple Event round trip (~5 ms).
    func cursor() async throws -> VOCursor {
        guard isRunning else { throw VoiceOverError.notRunning }
        return try await withCheckedThrowingContinuation { continuation in
            queue.async {
                continuation.resume(with: Result { try self.readCursor() })
            }
        }
    }

    /// Just the cursor's rectangle — VoiceOver answers this in about 1 ms, so it's what auto mode polls.
    func cursorBounds() async throws -> CGRect {
        guard isRunning else { throw VoiceOverError.notRunning }
        return try await withCheckedThrowingContinuation { continuation in
            queue.async {
                continuation.resume(with: Result {
                    if self.boundsScript == nil {
                        self.boundsScript = NSAppleScript(source: #"tell application "VoiceOver" to return bounds of vo cursor"#)
                    }
                    let list = try self.run(self.boundsScript!)
                    guard list.numberOfItems == 4 else { throw VoiceOverError.failed("unexpected reply") }
                    let v = (1...4).map { Double(list.atIndex($0)?.int32Value ?? 0) }
                    return CGRect(x: v[0], y: v[1], width: v[2] - v[0], height: v[3] - v[1])
                })
            }
        }
    }

    /// What VoiceOver last spoke (used by self-tests to confirm announcements were heard).
    func lastPhrase() async throws -> String {
        guard isRunning else { throw VoiceOverError.notRunning }
        return try await withCheckedThrowingContinuation { continuation in
            queue.async {
                continuation.resume(with: Result {
                    if self.lastPhraseScript == nil {
                        self.lastPhraseScript = NSAppleScript(source: #"tell application "VoiceOver" to return content of last phrase"#)
                    }
                    let result = try self.run(self.lastPhraseScript!)
                    return result.stringValue ?? ""
                })
            }
        }
    }

    /// Makes VoiceOver speak (and braille) text via its `output` command.
    func output(_ text: String) async throws {
        guard isRunning else { throw VoiceOverError.notRunning }
        let escaped = text.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"")
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            queue.async {
                continuation.resume(with: Result {
                    let script = NSAppleScript(source: "tell application \"VoiceOver\" to output \"\(escaped)\"")!
                    _ = try self.run(script)
                })
            }
        }
    }

    /// Fallback capture when Screen Recording isn't granted: VoiceOver screenshots the whole
    /// screen (with its cursor drawn) to the Desktop. We crop it and delete the file.
    func grabScreenshotFile() async throws -> URL {
        guard isRunning else { throw VoiceOverError.notRunning }
        return try await withCheckedThrowingContinuation { continuation in
            queue.async {
                continuation.resume(with: Result {
                    let script = NSAppleScript(source: #"tell application "VoiceOver" to grab screenshot vo cursor"#)!
                    let result = try self.run(script)
                    guard let path = result.stringValue, !path.isEmpty else { throw VoiceOverError.failed("no screenshot") }
                    return URL(fileURLWithPath: (path as NSString).expandingTildeInPath)
                })
            }
        }
    }

    // MARK: Private

    private func readCursor() throws -> VOCursor {
        if cursorScript == nil {
            cursorScript = NSAppleScript(source: """
            tell application "VoiceOver"
                tell vo cursor
                    set b to bounds
                    set t to text under cursor
                end tell
            end tell
            return {b, t}
            """)
        }
        let result = try run(cursorScript!)
        guard result.numberOfItems == 2, let boundsList = result.atIndex(1), boundsList.numberOfItems == 4 else {
            throw VoiceOverError.failed("unexpected reply")
        }
        let values = (1...4).map { Double(boundsList.atIndex($0)?.int32Value ?? 0) }
        let rect = CGRect(x: values[0], y: values[1], width: values[2] - values[0], height: values[3] - values[1])
        let text = result.atIndex(2)?.stringValue ?? ""
        return VOCursor(bounds: rect, text: text)
    }

    private func run(_ script: NSAppleScript) throws -> NSAppleEventDescriptor {
        var error: NSDictionary?
        let result = script.executeAndReturnError(&error)
        if let error {
            let number = (error[NSAppleScript.errorNumber] as? Int) ?? 0
            let message = (error[NSAppleScript.errorMessage] as? String) ?? "error \(number)"
            switch number {
            case -1743: throw VoiceOverError.automationDenied
            case -600, -609: throw VoiceOverError.notRunning
            default:
                if message.localizedCaseInsensitiveContains("applescript") || number == -10006 || number == -1708 || number == -10000 {
                    throw VoiceOverError.appleScriptDisabled
                }
                throw VoiceOverError.failed(message)
            }
        }
        return result
    }
}
