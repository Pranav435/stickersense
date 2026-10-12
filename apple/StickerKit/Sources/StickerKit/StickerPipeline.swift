import CoreGraphics
import Foundation

/// The result of a quick look at one frame: enough to answer from the cache instantly.
public struct StickerProbe: Sendable {
    public let fingerprint: Fingerprint
    public let ocrText: String
    public let cached: StickerRecord?
    /// Where the chat's own text (sender name, time) sits in the capture; paint these out with
    /// `ImagePrep.masking` before describing or saving.
    public var chromeBoxes: [CGRect] = []
}

/// Ties describing, caching and the library together. Used by the Mac app, the iOS app and
/// the iOS share extension so they all behave the same way.
public struct StickerPipeline: Sendable {
    public let describer: StickerDescriber
    public let library: StickerLibrary

    public init(describer: StickerDescriber, library: StickerLibrary) {
        self.describer = describer
        self.library = library
    }

    /// Fingerprint + OCR + cache lookup. Fast (~100 ms) — run it before capturing more frames.
    /// `screenReaderLabel` (what VoiceOver says about the item) is used to drop chat text such as
    /// the sender's name from OCR, so the same sticker from different people still hits the cache.
    public func probe(_ firstFrame: CGImage, screenReaderLabel: String? = nil) async -> StickerProbe {
        let lines = await TextReader.readLines(ImagePrep.flattened(firstFrame))
        let chrome = TextReader.chromeBoxes(in: lines, label: screenReaderLabel)
        // Fingerprint without the sender's name and time, so the same sticker matches whoever sent it.
        let fingerprint = Fingerprint(ImagePrep.masking(firstFrame, boxes: chrome))
        let text = TextReader.text(of: lines, label: screenReaderLabel)
        let cached = await library.match(fingerprint, ocrText: text)
        return StickerProbe(fingerprint: fingerprint, ocrText: text, cached: cached, chromeBoxes: chrome)
    }

    /// Describes (or reuses the cached description for) a sticker and records it.
    public func describe(
        frames: [CGImage],
        probe: StickerProbe? = nil,
        context: DescriptionContext,
        source: StickerSource,
        remoteURL: URL? = nil
    ) async throws -> StickerRecord {
        guard let first = frames.first else { throw DescribeError.failed("no image") }
        let probe = await probeIfNeeded(probe, first, context: context)
        let frames = frames.map { ImagePrep.masking($0, boxes: probe.chromeBoxes) }
        if let cached = probe.cached, cached.reading != nil {
            return await library.remember(
                frames: frames, fingerprint: probe.fingerprint, reading: cached.reading!,
                ocrText: probe.ocrText, source: source, remoteURL: remoteURL
            )
        }
        let reading = try await describer.read(frames: frames, context: context, ocrText: probe.ocrText)
        return await library.remember(
            frames: frames, fingerprint: probe.fingerprint, reading: reading,
            ocrText: probe.ocrText, source: source, remoteURL: remoteURL
        )
    }

    /// Stores a reading the caller produced itself (e.g. a speculative single-frame reading
    /// raced against motion detection). Frames should already be masked.
    @discardableResult
    public func store(frames: [CGImage], probe: StickerProbe, reading: StickerReading, source: StickerSource) async -> StickerRecord {
        await library.remember(frames: frames, fingerprint: probe.fingerprint, reading: reading, ocrText: probe.ocrText,
                               source: source, replaceReading: true)
    }

    /// Detailed description, cached on the record after the first time.
    public func detail(for record: StickerRecord, frames: [CGImage]?, context: DescriptionContext) async throws -> String {
        if let current = await library.record(record.id), let detailed = current.detailed { return detailed }
        var images = frames ?? []
        if images.isEmpty, let url = await library.imageURL(for: record) {
            images = (try? ImagePrep.frames(from: url, maxFrames: 4)) ?? []
        }
        guard !images.isEmpty else { throw DescribeError.failed("the picture is no longer available") }
        let text = try await describer.detailed(frames: images, context: context, ocrText: record.ocrText, shortReading: record.reading)
        await library.setDetailed(text, for: record.id)
        return text
    }

    /// Describes a library record that was imported without a description (WhatsApp, share sheet).
    @discardableResult
    public func describeStored(_ record: StickerRecord) async throws -> StickerRecord {
        if record.reading != nil { return record }
        guard let url = await library.imageURL(for: record) else { throw DescribeError.failed("missing file") }
        let frames = try ImagePrep.distinctFrames(ImagePrep.frames(from: url, maxFrames: 4))
        guard let first = frames.first else { throw DescribeError.failed("unreadable file") }
        let ocr = await TextReader.read(ImagePrep.flattened(first))
        let reading = try await describer.read(frames: frames, context: .file, ocrText: ocr)
        await library.setReading(reading, ocrText: ocr, for: record.id)
        return await library.record(record.id) ?? record
    }

    private func probeIfNeeded(_ probe: StickerProbe?, _ first: CGImage, context: DescriptionContext) async -> StickerProbe {
        if let probe { return probe }
        return await self.probe(first, screenReaderLabel: StickerDescriber.screenReaderLabel(in: context))
    }
}
