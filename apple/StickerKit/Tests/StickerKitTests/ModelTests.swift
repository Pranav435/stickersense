import CoreGraphics
import Foundation
import Testing
@testable import StickerKit

/// Runs the real on-device model. Enabled with `STICKERSENSE_MODEL_TESTS=1 swift test`
/// because it needs Apple Intelligence and takes a few seconds.
@Suite("On-device model", .enabled(if: ProcessInfo.processInfo.environment["STICKERSENSE_MODEL_TESTS"] == "1"), .serialized)
struct ModelTests {
    init() async {
        await TextReader.warmUp().value
    }

    @Test func engineIsReady() {
        #expect(StickerDescriber.status == .ready, "\(StickerDescriber.status.explanation)")
    }

    @Test func describesStaticStickerWithText() async throws {
        let describer = StickerDescriber()
        let image = Fixtures.sticker("🐶", caption: "idk lol")
        let ocr = await TextReader.read(ImagePrep.flattened(image))
        let start = Date()
        let reading = try await describer.read(frames: [image], context: .file, ocrText: ocr)
        let seconds = Date().timeIntervalSince(start)
        print("SHORT (\(String(format: "%.2f", seconds))s):", SpokenText.short(reading, includeVibe: true), "| kind:", reading.kind)
        #expect(reading.subject.lowercased().contains("dog") || reading.subject.lowercased().contains("puppy"))
        #expect(TextReader.normalized(reading.text).contains("idk"))
        #expect(!reading.animated)
    }

    @Test func describesMotion() async throws {
        let describer = StickerDescriber()
        let reading = try await describer.read(frames: Fixtures.animatedFrames(), context: .file)
        print("ANIMATED:", SpokenText.short(reading))
        #expect(reading.animated)
        #expect(reading.subject.lowercased().contains("cat"))
    }

    @Test func ignoresChatChromeInCursorCrops() async throws {
        let describer = StickerDescriber()
        let sticker = Fixtures.sticker("🎉", caption: "yay", captionColor: .black)
        let reading = try await describer.read(frames: [sticker], context: .cursorItem(screenReaderLabel: "Sticker, 4:05 PM, Read"), ocrText: "yay 4:05 PM")
        print("CURSOR:", SpokenText.short(reading))
        #expect(!reading.text.contains("4:05"))
    }

    @Test func detailedIsLonger() async throws {
        let describer = StickerDescriber()
        let image = Fixtures.sticker("😂", caption: "LMAO", captionColor: .black)
        let short = try await describer.read(frames: [image], context: .file, ocrText: "LMAO")
        let detail = try await describer.detailed(frames: [image], context: .file, ocrText: "LMAO", shortReading: short)
        print("DETAIL:", detail)
        #expect(detail.count > SpokenText.short(short).count)
    }

    @Test func readsChatScreenshotNewestFirst() async throws {
        let shot = Fixtures.chatScreenshot()
        let start = Date()
        let readings = try await StickerDescriber().readScreenshot(shot)
        print(String(format: "SCREEN (%.2fs):", Date().timeIntervalSince(start)), SpokenText.screen(readings))
        #expect(readings.count >= 2)
        #expect(readings.first.map { "\($0.subject) \($0.text)".lowercased() }?.contains("yay") == true)
        #expect(readings.last.map { "\($0.subject) \($0.text)".lowercased() }?.contains("dog") == true)
    }

    @Test func warmDescriptionsAreQuick() async throws {
        let describer = StickerDescriber()
        await describer.prewarm()
        try await Task.sleep(for: .seconds(1))
        var times: [Double] = []
        for emoji in ["🐶", "🐼", "🦊", "🐸"] {
            let image = Fixtures.sticker(emoji, caption: "hey", captionColor: .black)
            let start = Date()
            _ = try await describer.read(frames: [image], context: .file, ocrText: "hey")
            times.append(Date().timeIntervalSince(start))
            try await Task.sleep(for: .milliseconds(500)) // let the next session prewarm, as in the app
        }
        let median = times.sorted()[times.count / 2]
        print("WARM short reads:", times.map { String(format: "%.2f", $0) }.joined(separator: " "), String(format: "median %.2fs", median))
        #expect(median < 2.0)
    }

    @Test func detailAfterPrewarm() async throws {
        let describer = StickerDescriber()
        let image = Fixtures.sticker("🐼", caption: "hi", captionColor: .black)
        let short = try await describer.read(frames: [image], context: .file, ocrText: "hi")
        await describer.prewarmDetail()
        try await Task.sleep(for: .seconds(1))
        let start = Date()
        let detail = try await describer.detailed(frames: [image], context: .file, ocrText: "hi", shortReading: short)
        print(String(format: "DETAIL warm %.2fs:", Date().timeIntervalSince(start)), detail)
    }

    @Test func warmCacheAnswersInstantly() async throws {
        let library = StickerLibrary(directory: Fixtures.tempDirectory())
        let pipeline = StickerPipeline(describer: StickerDescriber(), library: library)
        let image = Fixtures.sticker("🐸", caption: "hmm", captionColor: .black)
        let first = try await pipeline.describe(frames: [image], context: .file, source: .received(app: "test"))
        let start = Date()
        let onScreen = Fixtures.onScreenCapture(of: image, scale: 0.5)
        let probe = await pipeline.probe(onScreen)
        let again = try await pipeline.describe(frames: [onScreen], probe: probe, context: .cursorItem(screenReaderLabel: nil), source: .received(app: "test"))
        let seconds = Date().timeIntervalSince(start)
        print("CACHED in \(String(format: "%.3f", seconds))s")
        #expect(again.id == first.id)
        #expect(seconds < 0.6)
    }
}
