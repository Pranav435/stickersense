import AppIntents
import Foundation

/// Something a control or shortcut asked the app to do when it opens.
enum PendingRoute: String, Sendable {
    case describeClipboard
    case openPicker
}

/// Hands routes from intents (which may run before the UI exists) to the app's UI.
@MainActor
final class AppRouter {
    static let shared = AppRouter()
    static let didChange = Notification.Name("StickerSenseRouteChanged")

    var pending: PendingRoute? {
        didSet { NotificationCenter.default.post(name: Self.didChange, object: nil) }
    }
}

/// Opens StickerSense and describes whatever sticker, GIF or picture is on the clipboard.
/// Used by the Control Center control and Back Tap (copy a sticker in WhatsApp, then trigger it).
struct DescribeCopiedStickerIntent: AppIntent {
    static let title: LocalizedStringResource = "Describe Copied Sticker"
    static let description = IntentDescription("Opens StickerSense and describes the sticker, GIF or picture you copied.")
    static let supportedModes: IntentModes = .foreground(.immediate)

    @MainActor func perform() async throws -> some IntentResult {
        AppRouter.shared.pending = .describeClipboard
        return .result()
    }
}

/// Opens StickerSense's sticker picker.
struct OpenStickerPickerIntent: AppIntent {
    static let title: LocalizedStringResource = "Open Sticker Picker"
    static let description = IntentDescription("Opens StickerSense to find a sticker or GIF to send.")
    static let supportedModes: IntentModes = .foreground(.immediate)

    @MainActor func perform() async throws -> some IntentResult {
        AppRouter.shared.pending = .openPicker
        return .result()
    }
}
