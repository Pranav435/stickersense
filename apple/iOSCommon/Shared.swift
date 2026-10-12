import AVFoundation
import StickerKit
import UIKit

/// The App Group the app, share extension and intents use to share one sticker library.
nonisolated enum SharedStore {
    static let appGroup = "group.com.pranavsavla.StickerSense"
    static let library = StickerLibrary(directory: StickerLibrary.defaultDirectory(appGroup: appGroup))
    static var defaults: UserDefaults { UserDefaults(suiteName: appGroup) ?? .standard }
}

/// Preferences shared between the app and its extensions.
nonisolated struct MobileSettings {
    private let defaults = SharedStore.defaults

    var includeMeaning: Bool {
        get { defaults.bool(forKey: "includeMeaning") }
        nonmutating set { defaults.set(newValue, forKey: "includeMeaning") }
    }

    var allowPrivateCloud: Bool {
        get { defaults.bool(forKey: "allowPrivateCloud") }
        nonmutating set { defaults.set(newValue, forKey: "allowPrivateCloud") }
    }

    var giphyKey: String {
        get { Keychain.string(for: Keychain.giphyAccount) ?? "" }
        nonmutating set { Keychain.set(newValue.trimmingCharacters(in: .whitespacesAndNewlines), for: Keychain.giphyAccount) }
    }

    var describerOptions: DescriberOptions {
        DescriberOptions(allowPrivateCloudForDetail: allowPrivateCloud, includeMeaning: includeMeaning)
    }
}

/// Speaks through VoiceOver when it's on (so it's also brailled), otherwise with the
/// system voice using the user's spoken-content settings.
@MainActor
enum Speaker {
    private static let synthesizer = AVSpeechSynthesizer()

    static func say(_ text: String, queue: Bool = false) {
        guard !text.isEmpty else { return }
        if UIAccessibility.isVoiceOverRunning {
            let announcement = NSAttributedString(string: text, attributes: [.accessibilitySpeechQueueAnnouncement: queue])
            UIAccessibility.post(notification: .announcement, argument: announcement)
        } else {
            if !queue, synthesizer.isSpeaking { synthesizer.stopSpeaking(at: .immediate) }
            let utterance = AVSpeechUtterance(string: text)
            utterance.prefersAssistiveTechnologySettings = true
            synthesizer.speak(utterance)
        }
    }
}

enum StickerClipboard {
    /// Copies a sticker so it can be pasted into any chat. GIFs stay animated; WebP becomes PNG.
    static func copy(fileURL: URL) -> Bool {
        guard let data = try? Data(contentsOf: fileURL) else { return false }
        if fileURL.pathExtension.lowercased() == "gif" {
            UIPasteboard.general.setData(data, forPasteboardType: "com.compuserve.gif")
        } else if let image = UIImage(data: data) {
            UIPasteboard.general.image = image
        } else {
            return false
        }
        return true
    }

    /// The image on the clipboard, as raw data (keeps GIF animation).
    static func imageData() -> Data? {
        let pasteboard = UIPasteboard.general
        for type in ["com.compuserve.gif", "org.webmproject.webp", "public.png", "public.jpeg", "public.heic"] {
            if let data = pasteboard.data(forPasteboardType: type) { return data }
        }
        return pasteboard.image?.pngData()
    }
}
