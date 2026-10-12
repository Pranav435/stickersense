import AppKit

/// Opens the sticker picker when the user types two colons (`::`) in a chat app, the way
/// Slack and Discord open their emoji pickers. Only watches for ":" — it keeps no record of
/// anything typed — and macOS hides keystrokes in password fields from it entirely.
@MainActor
final class ColonTrigger {
    /// Called with the chat app the colons were typed into.
    var onTrigger: ((NSRunningApplication) -> Void)?
    var isEnabled = true
    var scope: AutoScope = .chatApps

    private var monitor: Any?
    private var lastColon: Date?

    /// Global key monitoring needs Accessibility permission; call again once it's granted.
    func start() {
        guard monitor == nil, AXIsProcessTrusted() else { return }
        monitor = NSEvent.addGlobalMonitorForEvents(matching: .keyDown) { [weak self] event in
            MainActor.assumeIsolated { self?.handle(event) }
        }
    }

    func stop() {
        if let monitor { NSEvent.removeMonitor(monitor) }
        monitor = nil
    }

    private func handle(_ event: NSEvent) {
        guard isEnabled else { return }
        // Shortcuts (Command/Control/Option) aren't typing.
        guard event.modifierFlags.intersection([.command, .control, .option]).isEmpty,
              event.characters == ":" else {
            lastColon = nil
            return
        }
        let now = Date()
        guard let previous = lastColon, now.timeIntervalSince(previous) < 1.0 else {
            lastColon = now
            return
        }
        lastColon = nil
        guard let app = NSWorkspace.shared.frontmostApplication,
              app.bundleIdentifier != Bundle.main.bundleIdentifier,
              StickerDetector.isWatched(bundleID: app.bundleIdentifier, scope: scope),
              Self.focusIsTextInput(of: app) else { return }
        onTrigger?(app)
    }

    /// Only when typing into a text field (a chat's compose box), not e.g. a search list.
    /// One accessibility query, made only after "::" — never on ordinary keystrokes.
    static func focusIsTextInput(of app: NSRunningApplication) -> Bool {
        let element = AXUIElementCreateApplication(app.processIdentifier)
        var focused: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, kAXFocusedUIElementAttribute as CFString, &focused) == .success,
              let focused, CFGetTypeID(focused) == AXUIElementGetTypeID() else { return true }
        var role: CFTypeRef?
        AXUIElementCopyAttributeValue(focused as! AXUIElement, kAXRoleAttribute as CFString, &role)
        guard let role = role as? String else { return true }
        return [kAXTextAreaRole as String, kAXTextFieldRole as String, kAXComboBoxRole as String, "AXWebArea"].contains(role)
    }
}
