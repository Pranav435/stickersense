import AppKit
import Carbon.HIToolbox

/// A global keyboard shortcut.
struct KeyCombo: Codable, Hashable, Sendable {
    var keyCode: UInt32
    var modifiers: NSEvent.ModifierFlags.RawValue

    var modifierFlags: NSEvent.ModifierFlags { NSEvent.ModifierFlags(rawValue: modifiers) }

    static func optionShiftCommand(_ letter: String) -> KeyCombo {
        KeyCombo(keyCode: Self.keyCode(for: letter) ?? UInt32(kVK_ANSI_D), modifiers: NSEvent.ModifierFlags([.option, .shift, .command]).rawValue)
    }

    /// Spoken/visible form, e.g. "Option Shift Command D".
    var spokenName: String {
        var parts: [String] = []
        let flags = modifierFlags
        if flags.contains(.control) { parts.append("Control") }
        if flags.contains(.option) { parts.append("Option") }
        if flags.contains(.shift) { parts.append("Shift") }
        if flags.contains(.command) { parts.append("Command") }
        parts.append(Self.letter(for: keyCode) ?? "key \(keyCode)")
        return parts.joined(separator: " ")
    }

    /// Compact form for menus, e.g. "⌥⇧⌘D".
    var symbolName: String {
        var s = ""
        let flags = modifierFlags
        if flags.contains(.control) { s += "⌃" }
        if flags.contains(.option) { s += "⌥" }
        if flags.contains(.shift) { s += "⇧" }
        if flags.contains(.command) { s += "⌘" }
        return s + (Self.letter(for: keyCode) ?? "?")
    }

    var carbonModifiers: UInt32 {
        var m: UInt32 = 0
        let flags = modifierFlags
        if flags.contains(.command) { m |= UInt32(cmdKey) }
        if flags.contains(.option) { m |= UInt32(optionKey) }
        if flags.contains(.shift) { m |= UInt32(shiftKey) }
        if flags.contains(.control) { m |= UInt32(controlKey) }
        return m
    }

    static let letters: [(String, Int)] = [
        ("A", kVK_ANSI_A), ("B", kVK_ANSI_B), ("C", kVK_ANSI_C), ("D", kVK_ANSI_D), ("E", kVK_ANSI_E), ("F", kVK_ANSI_F),
        ("G", kVK_ANSI_G), ("H", kVK_ANSI_H), ("I", kVK_ANSI_I), ("J", kVK_ANSI_J), ("K", kVK_ANSI_K), ("L", kVK_ANSI_L),
        ("M", kVK_ANSI_M), ("N", kVK_ANSI_N), ("O", kVK_ANSI_O), ("P", kVK_ANSI_P), ("Q", kVK_ANSI_Q), ("R", kVK_ANSI_R),
        ("S", kVK_ANSI_S), ("T", kVK_ANSI_T), ("U", kVK_ANSI_U), ("V", kVK_ANSI_V), ("W", kVK_ANSI_W), ("X", kVK_ANSI_X),
        ("Y", kVK_ANSI_Y), ("Z", kVK_ANSI_Z), ("0", kVK_ANSI_0), ("1", kVK_ANSI_1), ("2", kVK_ANSI_2), ("3", kVK_ANSI_3),
        ("4", kVK_ANSI_4), ("5", kVK_ANSI_5), ("6", kVK_ANSI_6), ("7", kVK_ANSI_7), ("8", kVK_ANSI_8), ("9", kVK_ANSI_9),
    ]

    static func keyCode(for letter: String) -> UInt32? {
        letters.first { $0.0 == letter.uppercased() }.map { UInt32($0.1) }
    }

    static func letter(for keyCode: UInt32) -> String? {
        letters.first { UInt32($0.1) == keyCode }?.0
    }
}

/// Registers system-wide hotkeys with Carbon (works without Accessibility permission,
/// and the key press is swallowed so it doesn't type into the chat).
@MainActor
final class HotkeyCenter {
    private var refs: [UInt32: EventHotKeyRef] = [:]
    private var handlers: [UInt32: () -> Void] = [:]
    private var eventHandler: EventHandlerRef?
    private var nextID: UInt32 = 1

    init() {
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        let callback: EventHandlerUPP = { _, event, userData in
            guard let event, let userData else { return noErr }
            var hotKeyID = EventHotKeyID()
            GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID), nil,
                              MemoryLayout<EventHotKeyID>.size, nil, &hotKeyID)
            let center = Unmanaged<HotkeyCenter>.fromOpaque(userData).takeUnretainedValue()
            let id = hotKeyID.id
            MainActor.assumeIsolated { center.handlers[id]?() }
            return noErr
        }
        InstallEventHandler(GetApplicationEventTarget(), callback, 1, &spec, Unmanaged.passUnretained(self).toOpaque(), &eventHandler)
    }

    /// Returns false if another app already owns the combination.
    @discardableResult
    func register(_ combo: KeyCombo, handler: @escaping () -> Void) -> Bool {
        let id = nextID
        nextID += 1
        var ref: EventHotKeyRef?
        let status = RegisterEventHotKey(combo.keyCode, combo.carbonModifiers, EventHotKeyID(signature: OSType(0x53_54_4B_53), id: id), GetApplicationEventTarget(), 0, &ref)
        guard status == noErr, let ref else { return false }
        refs[id] = ref
        handlers[id] = handler
        return true
    }

    func unregisterAll() {
        for ref in refs.values { UnregisterEventHotKey(ref) }
        refs.removeAll()
        handlers.removeAll()
    }
}
