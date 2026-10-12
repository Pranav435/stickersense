import AppIntents
import ImageIO
import StickerKit
import UniformTypeIdentifiers

/// "Describe Stickers": the heart of the iOS version. iOS doesn't let apps see the screen or
/// the VoiceOver cursor, so the user builds a two-step shortcut — Take Screenshot, then this —
/// and runs it with Back Tap or the Action button from any chat.
struct DescribeStickersIntent: AppIntent {
    static let title: LocalizedStringResource = "Describe Stickers"
    static let description = IntentDescription(
        "Describes the stickers and pictures in a screenshot, newest first. Put it after Take Screenshot and run it with Back Tap or the Action button.",
        categoryName: "Stickers"
    )

    @Parameter(title: "Screenshot", supportedContentTypes: [.image])
    var screenshot: IntentFile

    static var parameterSummary: some ParameterSummary {
        Summary("Describe stickers in \(\.$screenshot)")
    }

    func perform() async throws -> some IntentResult & ReturnsValue<String> & ProvidesDialog {
        let text: String
        do {
            guard let source = CGImageSourceCreateWithData(screenshot.data as CFData, nil),
                  let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else {
                throw DescribeError.failed("that isn't an image")
            }
            let readings = try await StickerDescriber(options: MobileSettings().describerOptions).readScreenshot(image)
            text = SpokenText.screen(readings, includeVibe: MobileSettings().includeMeaning)
        } catch {
            text = SpokenText.failure(error)
        }
        return .result(value: text, dialog: IntentDialog(stringLiteral: text))
    }
}

/// Describes one sticker image (e.g. passed from another shortcut action).
struct DescribeStickerImageIntent: AppIntent {
    static let title: LocalizedStringResource = "Describe Sticker Image"
    static let description = IntentDescription("Describes one sticker, GIF or picture.", categoryName: "Stickers")

    @Parameter(title: "Image", supportedContentTypes: [.image])
    var image: IntentFile

    @Parameter(title: "Detailed", default: false)
    var detailed: Bool

    func perform() async throws -> some IntentResult & ReturnsValue<String> & ProvidesDialog {
        let text: String
        do {
            let frames = try ImagePrep.distinctFrames(ImagePrep.frames(from: image.data, maxFrames: 4))
            let settings = MobileSettings()
            let pipeline = StickerPipeline(describer: StickerDescriber(options: settings.describerOptions), library: SharedStore.library)
            let record = try await pipeline.describe(frames: frames, context: .file, source: .imported)
            if detailed {
                text = try await pipeline.detail(for: record, frames: frames, context: .file)
            } else {
                text = record.reading.map { SpokenText.short($0, includeVibe: settings.includeMeaning) } ?? ""
            }
        } catch {
            text = SpokenText.failure(error)
        }
        return .result(value: text, dialog: IntentDialog(stringLiteral: text))
    }
}

struct StickerSenseShortcuts: AppShortcutsProvider {
    static var appShortcuts: [AppShortcut] {
        AppShortcut(
            intent: DescribeCopiedStickerIntent(),
            phrases: ["Describe copied sticker with \(.applicationName)", "\(.applicationName) describe copied sticker"],
            shortTitle: "Describe Copied Sticker",
            systemImageName: "text.below.photo"
        )
        AppShortcut(
            intent: OpenStickerPickerIntent(),
            phrases: ["Open \(.applicationName) picker", "Find a sticker with \(.applicationName)"],
            shortTitle: "Sticker Picker",
            systemImageName: "face.smiling"
        )
    }
}
