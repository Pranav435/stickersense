import Foundation

/// What the model decided the described image is.
public enum StickerKind: String, Codable, Sendable, CaseIterable {
    case sticker, gif, emoji, meme, photo, illustration, text, nothing

    /// Kinds worth remembering in the sticker library so they can be found and re-sent later.
    public var isCollectible: Bool {
        switch self {
        case .sticker, .gif, .emoji, .meme, .illustration: true
        case .photo, .text, .nothing: false
        }
    }
}

/// A structured, short reading of one sticker / GIF / image.
public struct StickerReading: Codable, Sendable, Hashable {
    public var kind: StickerKind
    /// What it shows in a few words; for animations, what happens.
    public var subject: String
    /// Text written on the sticker, verbatim. Empty when there is none.
    public var text: String
    /// What someone means by sending it ("I don't know, whatever").
    public var vibe: String
    public var animated: Bool

    public init(kind: StickerKind, subject: String, text: String = "", vibe: String = "", animated: Bool = false) {
        self.kind = kind
        self.subject = subject
        self.text = text
        self.vibe = vibe
        self.animated = animated
    }
}

/// Where a library sticker came from.
public enum StickerSource: Codable, Sendable, Hashable {
    /// Seen on screen in a chat app (bundle identifier or app name).
    case received(app: String)
    /// Imported from the local WhatsApp app data.
    case whatsApp(relativePath: String)
    /// A GIPHY GIF or sticker.
    case giphy(id: String)
    /// Shared into the app (iOS share extension) or added by hand.
    case imported
    /// Made on this device from a picker search (`prompt` is what was typed).
    case generated(prompt: String)

    public var displayName: String {
        switch self {
        case .received(let app): AppNames.friendlyName(for: app)
        case .whatsApp: "WhatsApp"
        case .giphy: "GIPHY"
        case .imported: "Imported"
        case .generated: "Made by you"
        }
    }

    public var filterTag: SourceFilter {
        switch self {
        case .received: .received
        case .whatsApp: .whatsApp
        case .giphy: .giphy
        case .imported: .received
        case .generated: .generated
        }
    }

    /// How a list row ends: "From WhatsApp", or "Made by you".
    public var origin: String {
        if case .generated = self { return "Made by you" }
        return "From \(displayName)"
    }

    /// Words a picker search should also match: what a made sticker was asked for.
    public var searchText: String {
        if case .generated(let prompt) = self { return prompt }
        return ""
    }
}

public enum SourceFilter: String, CaseIterable, Sendable, Identifiable {
    case all, received, whatsApp, giphy, generated
    public var id: String { rawValue }
    public var title: String {
        switch self {
        case .all: "All"
        case .received: "Received"
        case .whatsApp: "WhatsApp"
        case .giphy: "GIPHY"
        case .generated: "Made by you"
        }
    }
}

public enum AppNames {
    static let known: [String: String] = [
        "net.whatsapp.WhatsApp": "WhatsApp",
        "desktop.WhatsApp": "WhatsApp",
        "com.apple.MobileSMS": "Messages",
        "com.apple.iChat": "Messages",
        "ru.keepcoder.Telegram": "Telegram",
        "org.telegram.desktop": "Telegram",
        "com.hnc.Discord": "Discord",
        "org.whispersystems.signal-desktop": "Signal",
        "com.tinyspeck.slackmacgap": "Slack",
        "com.facebook.archon": "Messenger",
        "com.facebook.archon.developerID": "Messenger",
    ]

    public static func friendlyName(for bundleOrName: String) -> String {
        known[bundleOrName] ?? bundleOrName
    }
}
