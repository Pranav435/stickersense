import AppKit
import ApplicationServices
import Carbon.HIToolbox
import StickerKit

/// Sends a picture as a real sticker in WhatsApp for Mac. WhatsApp turns anything pasted into a
/// photo (tried: PNG, WebP and GIF data and files, Apple's sticker pasteboard type, Genmoji), so
/// StickerSense uses WhatsApp's own sticker maker instead, through Accessibility: Emoji picker,
/// Create sticker, the file (a transparent WebP, which keeps its see-through background), "Cutout
/// option 1" (the picture as it is, caption and border included), Send. WhatsApp sends a
/// sticker straight away, as when it's tapped in its own sticker tray.
///
/// It relies on WhatsApp's English labels; when any step isn't found it cancels what it opened
/// and the caller pastes the picture the usual way.
@MainActor
enum WhatsAppSticker {
    static let bundleIdentifier = "net.whatsapp.WhatsApp"

    static func isWhatsApp(_ app: NSRunningApplication?) -> Bool {
        app?.bundleIdentifier == bundleIdentifier
    }

    /// Goes as a sticker: one still frame that's see-through around its subject (stickers made
    /// in the picker and most received or WhatsApp stickers). Photos, memes and GIFs paste as before.
    static func qualifies(_ fileURL: URL) -> Bool {
        guard let frames = try? ImagePrep.frames(from: fileURL, maxFrames: 2), frames.count == 1, let image = frames.first else { return false }
        return hasTransparency(image)
    }

    static func hasTransparency(_ image: CGImage) -> Bool {
        let side = 64
        guard let ctx = CGContext(data: nil, width: side, height: side, bitsPerComponent: 8, bytesPerRow: side * 4,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue),
              let data = ctx.data else { return false }
        ctx.clear(CGRect(x: 0, y: 0, width: side, height: side))
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: side, height: side))
        let pixels = data.bindMemory(to: UInt8.self, capacity: side * side * 4)
        var clear = 0
        for i in 0..<(side * side) where pixels[i * 4 + 3] < 16 { clear += 1 }
        return clear > side * side / 20 // at least 5% see-through
    }

    enum Outcome: Equatable {
        case sent
        /// `typedRemoved`: the "::" was already deleted, so a fallback paste mustn't delete more.
        case failed(String, typedRemoved: Bool)
    }

    /// Sends `fileURL` as a sticker in the WhatsApp chat that's open, after deleting `replacing`
    /// typed characters (the "::" that opened the picker).
    static func send(_ fileURL: URL, in app: NSRunningApplication, replacing: Int) async -> Outcome {
        guard AXIsProcessTrusted() else { return .failed("StickerSense needs Accessibility permission", typedRemoved: false) }
        guard let webp = makeWebP(from: fileURL) else { return .failed("the sticker couldn't be prepared", typedRemoved: false) }
        let wa = Driver(app)

        NSApp.yieldActivation(to: app)
        app.activate()
        for _ in 0..<40 where NSWorkspace.shared.frontmostApplication?.processIdentifier != app.processIdentifier {
            try? await Task.sleep(for: .milliseconds(25))
        }
        guard NSWorkspace.shared.frontmostApplication?.processIdentifier == app.processIdentifier else {
            return .failed("WhatsApp didn't come to the front", typedRemoved: false)
        }
        try? await Task.sleep(for: .milliseconds(200))
        for _ in 0..<replacing {
            Keys.press(CGKeyCode(kVK_Delete))
            try? await Task.sleep(for: .milliseconds(20))
        }

        guard await wa.press("Emoji picker"), await wa.press("Create sticker") else {
            await wa.cleanUp()
            return .failed("WhatsApp's sticker maker wasn't found", typedRemoved: true)
        }
        // The Open panel: go straight to the file.
        guard await wa.waitFor("Open", timeout: .seconds(5)) else {
            await wa.cleanUp()
            return .failed("WhatsApp didn't ask for a picture", typedRemoved: true)
        }
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.setString(webp.path, forType: .string)
        Keys.press(CGKeyCode(kVK_ANSI_G), flags: [.maskCommand, .maskShift])
        try? await Task.sleep(for: .milliseconds(700))
        Keys.press(CGKeyCode(kVK_ANSI_V), flags: .maskCommand)
        try? await Task.sleep(for: .milliseconds(400))
        Keys.press(CGKeyCode(kVK_Return))
        try? await Task.sleep(for: .milliseconds(900))
        Keys.press(CGKeyCode(kVK_Return))

        // The sticker editor: "Cutout option 1" keeps the sticker whole (option 2 is WhatsApp's own
        // cut-out, which drops the caption). Wait for the editor to settle, or the choice is undone.
        guard await wa.waitFor("Cutout option 2", timeout: .seconds(8)) else {
            await wa.cleanUp()
            return .failed("WhatsApp's sticker editor didn't open", typedRemoved: true)
        }
        // WhatsApp works out its own cut-out first; choosing before it's done gets a flattened sticker.
        try? await Task.sleep(for: .milliseconds(3500))
        var whole = false
        for _ in 0..<4 where !whole {
            try? await Task.sleep(for: .milliseconds(300))
            _ = await wa.press("Cutout option 1", timeout: .seconds(1))
            try? await Task.sleep(for: .milliseconds(300))
            whole = wa.isSelected("Cutout option 1")
        }
        // WhatsApp prepares the whole picture after the choice; Send before that sends it flattened
        // onto a white square (seen at 0.3 s; 2 s was fine).
        try? await Task.sleep(for: .milliseconds(2500))
        guard whole, await wa.press("Send") else {
            await wa.cleanUp()
            return .failed("the sticker editor didn't respond", typedRemoved: true)
        }
        // Sent once the editor closes. Once Send is pressed, it's never pasted again as well.
        if await !wa.waitForGone("Cutout option 1", timeout: .seconds(4)) {
            NSLog("StickerSense: WhatsApp's sticker editor stayed open after Send")
        }
        Paster.copy(fileURL: fileURL) // as after any paste: the sticker is on the clipboard
        return .sent
    }

    /// WhatsApp's sticker format: 512 pixels square, transparent WebP of at most 100 KB.
    private static func makeWebP(from fileURL: URL) -> URL? {
        guard let image = try? ImagePrep.frames(from: fileURL, maxFrames: 1).first else { return nil }
        let side = 512
        guard let ctx = CGContext(data: nil, width: side, height: side, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        let fit = min(Double(side) / Double(image.width), Double(side) / Double(image.height))
        let w = Double(image.width) * fit, h = Double(image.height) * fit
        ctx.interpolationQuality = .high
        ctx.draw(image, in: CGRect(x: (Double(side) - w) / 2, y: (Double(side) - h) / 2, width: w, height: h))
        guard let square = ctx.makeImage(), let data = try? ImagePrep.webpData(square) else { return nil }
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("StickerSense-WhatsApp", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let url = dir.appendingPathComponent("sticker-\(UUID().uuidString.prefix(8)).webp")
        return (try? data.write(to: url)) != nil ? url : nil
    }

    /// Finds and presses WhatsApp's controls by their accessibility labels.
    @MainActor
    private struct Driver {
        let element: AXUIElement

        init(_ app: NSRunningApplication) {
            element = AXUIElementCreateApplication(app.processIdentifier)
        }

        private func attribute(_ e: AXUIElement, _ name: String) -> AnyObject? {
            var value: AnyObject?
            AXUIElementCopyAttributeValue(e, name as CFString, &value)
            return value
        }

        /// Title and description, without WhatsApp's left-to-right marks.
        private func labels(_ e: AXUIElement) -> [String] {
            [attribute(e, kAXTitleAttribute), attribute(e, kAXDescriptionAttribute)]
                .compactMap { ($0 as? String)?.replacingOccurrences(of: "\u{200e}", with: "") }
        }

        /// Buttons in WhatsApp's windows and panels (the Open panel included), not its menu bar.
        private func buttons() -> [AXUIElement] {
            var found: [AXUIElement] = []
            func walk(_ e: AXUIElement, depth: Int) {
                guard depth < 40 else { return }
                if (attribute(e, kAXRoleAttribute) as? String) == kAXButtonRole { found.append(e) }
                for child in (attribute(e, kAXChildrenAttribute) as? [AXUIElement]) ?? [] { walk(child, depth: depth + 1) }
            }
            for child in (attribute(element, kAXChildrenAttribute) as? [AXUIElement]) ?? []
            where (attribute(child, kAXRoleAttribute) as? String) != kAXMenuBarRole {
                walk(child, depth: 0)
            }
            return found
        }

        /// "Cutout option 1" also matches "Cutout option 1, Selected".
        private func button(_ name: String) -> AXUIElement? {
            buttons().first { labels($0).contains { $0 == name || $0.hasPrefix(name + ",") } }
        }

        func isSelected(_ name: String) -> Bool {
            button(name).map { labels($0).contains { $0.hasPrefix(name + ",") && $0.contains("Selected") } } ?? false
        }

        func waitFor(_ name: String, timeout: Duration = .seconds(3)) async -> Bool {
            let end = ContinuousClock.now + timeout
            while ContinuousClock.now < end {
                if button(name) != nil { return true }
                try? await Task.sleep(for: .milliseconds(100))
            }
            return false
        }

        func waitForGone(_ name: String, timeout: Duration) async -> Bool {
            let end = ContinuousClock.now + timeout
            while ContinuousClock.now < end {
                if button(name) == nil { return true }
                try? await Task.sleep(for: .milliseconds(100))
            }
            return false
        }

        func press(_ name: String, timeout: Duration = .seconds(3)) async -> Bool {
            guard await waitFor(name, timeout: timeout), let target = button(name) else { return false }
            return AXUIElementPerformAction(target, kAXPressAction as CFString) == .success
        }

        /// Closes whatever this opened (the Open panel, the sticker editor, the emoji picker).
        func cleanUp() async {
            for _ in 0..<3 {
                if let cancel = button("Cancel") {
                    AXUIElementPerformAction(cancel, kAXPressAction as CFString)
                    try? await Task.sleep(for: .milliseconds(300))
                } else {
                    Keys.press(CGKeyCode(kVK_Escape))
                    try? await Task.sleep(for: .milliseconds(200))
                    return
                }
            }
        }
    }
}

/// Key presses for driving another app (needs Accessibility permission).
enum Keys {
    static func press(_ key: CGKeyCode, flags: CGEventFlags = []) {
        let source = CGEventSource(stateID: .combinedSessionState)
        let down = CGEvent(keyboardEventSource: source, virtualKey: key, keyDown: true)
        let up = CGEvent(keyboardEventSource: source, virtualKey: key, keyDown: false)
        down?.flags = flags
        up?.flags = flags
        down?.post(tap: .cghidEventTap)
        up?.post(tap: .cghidEventTap)
    }
}
