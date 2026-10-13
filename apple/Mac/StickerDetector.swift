import AppKit
import ApplicationServices
import StickerKit

/// Decides whether the item under the VoiceOver cursor is a sticker or GIF worth describing
/// automatically. Uses what VoiceOver says about the item, and (when Accessibility access is
/// granted) the element's role.
enum StickerDetector {
    typealias Verdict = LabelClassifier.Verdict

    /// Native chat apps.
    static let nativeChatApps: Set<String> = [
        "net.whatsapp.WhatsApp", "desktop.WhatsApp", "com.apple.MobileSMS", "ru.keepcoder.Telegram",
        "org.telegram.desktop", "com.hnc.Discord", "org.whispersystems.signal-desktop", "com.tinyspeck.slackmacgap",
        "com.facebook.archon", "com.facebook.archon.developerID", "com.microsoft.teams2", "com.viber.osx",
        "jp.naver.line.mac", "com.kakao.KakaoTalkMac", "com.tencent.xinWeChat", "com.skype.skype",
    ]

    /// Browsers, for WhatsApp Web, Messenger, Instagram, Discord on the web… Pages there are full of
    /// ordinary text, so only things VoiceOver calls an image can trigger auto mode.
    static let browsers: Set<String> = [
        "com.apple.Safari", "com.google.Chrome", "org.mozilla.firefox", "company.thebrowser.Browser",
        "com.microsoft.edgemac", "com.brave.Browser", "com.operasoftware.Opera", "com.vivaldi.Vivaldi",
    ]

    static var chatApps: Set<String> { nativeChatApps.union(browsers) }

    static func verdict(for cursor: VOCursor, axRole: String?, bundleID: String? = NSWorkspace.shared.frontmostApplication?.bundleIdentifier) -> Verdict {
        LabelClassifier.classify(label: cursor.text, size: cursor.bounds.size, isImageElement: axRole == (kAXImageRole as String),
                                 inBrowser: bundleID.map(browsers.contains) ?? false)
    }

    /// Labels that don't say what the item is, where the element's role is worth checking.
    static func labelIsAmbiguous(_ label: String) -> Bool {
        let words = label.lowercased().components(separatedBy: CharacterSet.alphanumerics.inverted).filter { !$0.isEmpty }
        return words.isEmpty || (words.count <= 6 && !Set(words).isDisjoint(with: ["image", "picture", "graphic", "photo", "sticker", "gif"]))
    }

    static func isWatched(bundleID: String?, scope: AutoScope) -> Bool {
        switch scope {
        case .everywhere: true
        case .chatApps: bundleID.map(chatApps.contains) ?? false
        }
    }

    /// Accessibility role of the element at the centre of `rect` (needs Accessibility permission).
    static func role(at rect: CGRect) -> String? {
        guard AXIsProcessTrusted() else { return nil }
        let system = AXUIElementCreateSystemWide()
        var element: AXUIElement?
        guard AXUIElementCopyElementAtPosition(system, Float(rect.midX), Float(rect.midY), &element) == .success, let element else { return nil }
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, kAXRoleAttribute as CFString, &value) == .success else { return nil }
        return value as? String
    }
}
