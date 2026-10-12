import AppKit
import Carbon.HIToolbox
import UniformTypeIdentifiers

/// Puts a sticker or GIF into the chat the user came from: copies it to the clipboard,
/// switches back to that app and presses Command-V. Never presses Return — the user
/// decides whether to send.
@MainActor
enum Paster {
    enum Outcome {
        case pasted(appName: String)
        case copiedOnly(reason: String)
    }

    static func copy(fileURL: URL) {
        let data = (try? Data(contentsOf: fileURL)) ?? Data()
        let type = UTType(filenameExtension: fileURL.pathExtension) ?? .png
        let item = NSPasteboardItem()
        if type.conforms(to: .gif) {
            item.setData(data, forType: NSPasteboard.PasteboardType(UTType.gif.identifier))
        } else if let image = NSImage(data: data), let tiff = image.tiffRepresentation,
                  let png = NSBitmapImageRep(data: tiff)?.representation(using: .png, properties: [:]) {
            // WebP and friends become PNG so every chat app accepts them.
            item.setData(png, forType: .png)
            item.setData(tiff, forType: .tiff)
        }
        item.setString(fileURL.absoluteString, forType: .fileURL)
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.writeObjects([item])
    }

    /// - Parameter replacing: characters to delete first — the "::" that opened the picker.
    static func paste(fileURL: URL, into app: NSRunningApplication?, replacing: Int = 0) async -> Outcome {
        copy(fileURL: fileURL)
        guard let app, !app.isTerminated, app.bundleIdentifier != Bundle.main.bundleIdentifier else {
            return .copiedOnly(reason: "Copied. Paste it into your chat with Command V.")
        }
        guard AXIsProcessTrusted() else {
            return .copiedOnly(reason: "Copied. Paste it with Command V. To paste automatically, give StickerSense Accessibility permission.")
        }
        NSApp.yieldActivation(to: app)
        app.activate()
        // Wait for the chat app to come to the front before pressing Command-V.
        for _ in 0..<40 {
            if NSWorkspace.shared.frontmostApplication?.processIdentifier == app.processIdentifier { break }
            try? await Task.sleep(for: .milliseconds(25))
        }
        try? await Task.sleep(for: .milliseconds(150))
        for _ in 0..<replacing {
            press(CGKeyCode(kVK_Delete))
            try? await Task.sleep(for: .milliseconds(20))
        }
        pressCommandV()
        return .pasted(appName: app.localizedName ?? "the chat")
    }

    private static func press(_ key: CGKeyCode) {
        let source = CGEventSource(stateID: .combinedSessionState)
        CGEvent(keyboardEventSource: source, virtualKey: key, keyDown: true)?.post(tap: .cghidEventTap)
        CGEvent(keyboardEventSource: source, virtualKey: key, keyDown: false)?.post(tap: .cghidEventTap)
    }

    private static func pressCommandV() {
        let source = CGEventSource(stateID: .combinedSessionState)
        let down = CGEvent(keyboardEventSource: source, virtualKey: CGKeyCode(kVK_ANSI_V), keyDown: true)
        let up = CGEvent(keyboardEventSource: source, virtualKey: CGKeyCode(kVK_ANSI_V), keyDown: false)
        down?.flags = .maskCommand
        up?.flags = .maskCommand
        down?.post(tap: .cghidEventTap)
        up?.post(tap: .cghidEventTap)
    }
}
