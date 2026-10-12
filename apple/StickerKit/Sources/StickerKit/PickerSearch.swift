import CoreGraphics
import Foundation

/// One row in the sticker picker.
public enum PickerItem: Identifiable, Hashable, Sendable {
    case library(StickerRecord)
    case giphy(GiphyItem)
    /// "More GIPHY results" — the last row while GIPHY has more pages.
    case loadMore

    public var id: String {
        switch self {
        case .library(let r): "lib-\(r.id.uuidString)"
        case .giphy(let g): "gif-\(g.kind.rawValue)-\(g.id)"
        case .loadMore: "load-more"
        }
    }

    public var giphy: GiphyItem? {
        if case .giphy(let g) = self { return g }
        return nil
    }
}

/// The picker's search, shared by the Mac and iOS apps. The user's own stickers come back
/// instantly from the local library; GIPHY stickers and GIFs come in pages of 50 (25 of
/// each, interleaved). GIPHY results are described on device — GIPHY's own titles ("GIF by
/// Disney") rarely say what's in the picture — and those descriptions are remembered on disk,
/// so a popular GIF is only ever described once.
public actor PickerSearch {
    public let library: StickerLibrary
    private let describer: StickerDescriber
    private var downloads: [String: URL] = [:]
    private var inFlightDownloads: [String: Task<URL?, Never>] = [:]
    private var readings: [String: CachedReading] = [:]
    private var saveTask: Task<Void, Never>?
    private let cacheURL: URL

    struct CachedReading: Codable {
        var reading: StickerReading
        var at: Date
    }

    public init(library: StickerLibrary, describer: StickerDescriber) {
        self.library = library
        self.describer = describer
        cacheURL = library.directory.appendingPathComponent("giphy-descriptions.json")
        if let data = try? Data(contentsOf: cacheURL),
           let decoded = try? JSONDecoder.iso.decode([String: CachedReading].self, from: data) {
            readings = decoded
        }
    }

    // MARK: Searching

    /// The user's own stickers — local and instant.
    public func libraryResults(_ query: String, filter: SourceFilter) async -> [PickerItem] {
        await library.search(query.trimmingCharacters(in: .whitespacesAndNewlines), filter: filter, limit: 300).map { .library($0) }
    }

    public struct GiphyResults: Sendable {
        public var items: [PickerItem]
        public var nextGifOffset: Int
        public var nextStickerOffset: Int
        public var hasMore: Bool
        /// Something to tell the user (missing key, network error), or empty.
        public var note: String
    }

    /// One page of GIPHY results: up to `perKind` stickers and `perKind` GIFs, interleaved.
    public func giphyPage(_ query: String, apiKey: String, gifOffset: Int = 0, stickerOffset: Int = 0, perKind: Int = 25) async -> GiphyResults {
        let query = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !query.isEmpty else { return GiphyResults(items: [], nextGifOffset: 0, nextStickerOffset: 0, hasMore: false, note: "") }
        guard !apiKey.isEmpty else {
            return GiphyResults(items: [], nextGifOffset: 0, nextStickerOffset: 0, hasMore: false, note: GiphyError.missingKey.errorDescription ?? "")
        }
        let client = GiphyClient(apiKey: apiKey)
        async let gifs = try? client.search(query, kind: .gif, limit: perKind, offset: gifOffset)
        async let stickers = try? client.search(query, kind: .sticker, limit: perKind, offset: stickerOffset)
        let (g, s) = await (gifs, stickers)
        if g == nil, s == nil {
            // Ask once more to get a useful error message.
            do { _ = try await client.search(query, kind: .gif, limit: 1) } catch {
                return GiphyResults(items: [], nextGifOffset: gifOffset, nextStickerOffset: stickerOffset, hasMore: false,
                                    note: (error as? LocalizedError)?.errorDescription ?? "GIPHY search failed.")
            }
        }
        let gifItems = g?.items ?? [], stickerItems = s?.items ?? []
        var items: [PickerItem] = []
        for i in 0..<max(gifItems.count, stickerItems.count) {
            if i < stickerItems.count { items.append(.giphy(stickerItems[i])) }
            if i < gifItems.count { items.append(.giphy(gifItems[i])) }
        }
        let results = GiphyResults(
            items: items,
            nextGifOffset: g?.nextOffset ?? gifOffset,
            nextStickerOffset: s?.nextOffset ?? stickerOffset,
            hasMore: (g?.hasMore ?? false) || (s?.hasMore ?? false),
            note: ""
        )
        // Start fetching the small renditions now, so describing them doesn't wait on the network.
        prefetch(items.compactMap(\.giphy))
        return results
    }

    // MARK: Descriptions

    /// The on-device description of a GIPHY result, if it has one.
    public func reading(for item: PickerItem) -> StickerReading? {
        readings[item.id]?.reading
    }

    public func readings(for items: [PickerItem]) -> [String: StickerReading] {
        var out: [String: StickerReading] = [:]
        for item in items where item.giphy != nil {
            if let r = readings[item.id]?.reading { out[item.id] = r }
        }
        return out
    }

    /// Describes a GIPHY result on device (remembered on disk).
    public func describe(_ gif: GiphyItem) async -> StickerReading? {
        let id = PickerItem.giphy(gif).id
        if let existing = readings[id] { return existing.reading }
        guard let url = await download(gif.smallURL ?? gif.previewURL, name: "small-\(gif.id).gif"),
              let frames = try? ImagePrep.distinctFrames(ImagePrep.frames(from: url, maxFrames: 3)),
              let first = frames.first else { return nil }
        let ocr = await TextReader.read(ImagePrep.flattened(first))
        guard let reading = try? await describer.read(frames: frames, context: .file, ocrText: ocr) else { return nil }
        readings[id] = CachedReading(reading: reading, at: Date())
        scheduleSave()
        return reading
    }

    public func detail(_ gif: GiphyItem) async throws -> String {
        guard let url = await download(gif.previewURL, name: "preview-\(gif.id).gif") else { throw DescribeError.failed("download failed") }
        let frames = try ImagePrep.distinctFrames(ImagePrep.frames(from: url, maxFrames: 3))
        let ocr = await TextReader.read(ImagePrep.flattened(frames[0]))
        return try await describer.detailed(frames: frames, context: .file, ocrText: ocr, shortReading: readings[PickerItem.giphy(gif).id]?.reading)
    }

    // MARK: Files

    /// A local file for sending or copying the item.
    public func fileURL(for item: PickerItem) async -> URL? {
        switch item {
        case .library(let record): return await library.imageURL(for: record)
        case .giphy(let gif): return await download(gif.originalURL, name: "\(gif.id).gif")
        case .loadMore: return nil
        }
    }

    /// Records that the user sent an item, adding GIPHY results to their library.
    public func markSent(_ item: PickerItem) async {
        switch item {
        case .library(let record):
            await library.markSent(record.id)
        case .giphy(let gif):
            guard let reading = readings[item.id]?.reading,
                  let url = await download(gif.originalURL, name: "\(gif.id).gif"),
                  let frames = try? ImagePrep.frames(from: url, maxFrames: 8), let first = frames.first else { return }
            let record = await library.remember(frames: frames, fingerprint: Fingerprint(first), reading: reading, ocrText: "", source: .giphy(id: gif.id), remoteURL: gif.originalURL)
            await library.markSent(record.id)
        case .loadMore:
            break
        }
    }

    /// Downloads small renditions six at a time.
    public func prefetch(_ gifs: [GiphyItem]) {
        for gif in gifs.prefix(60) {
            let url = gif.smallURL ?? gif.previewURL
            let name = "small-\(gif.id).gif"
            Task { _ = await self.download(url, name: name) }
        }
    }

    private var activeDownloads = 0
    private var waiters: [CheckedContinuation<Void, Never>] = []

    private func download(_ url: URL, name: String) async -> URL? {
        if let cached = downloads[name], FileManager.default.fileExists(atPath: cached.path) { return cached }
        if let running = inFlightDownloads[name] { return await running.value }
        let task = Task<URL?, Never> {
            await self.acquireSlot()
            defer { self.releaseSlot() }
            let dir = FileManager.default.temporaryDirectory.appendingPathComponent("StickerSense-GIPHY", isDirectory: true)
            try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            let destination = dir.appendingPathComponent(name)
            if FileManager.default.fileExists(atPath: destination.path) { return destination }
            guard let (data, response) = try? await URLSession.shared.data(from: url),
                  (response as? HTTPURLResponse)?.statusCode == 200,
                  (try? data.write(to: destination)) != nil else { return nil }
            return destination
        }
        inFlightDownloads[name] = task
        let result = await task.value
        inFlightDownloads[name] = nil
        if let result { downloads[name] = result }
        return result
    }

    private func acquireSlot() async {
        if activeDownloads < 6 {
            activeDownloads += 1
            return
        }
        await withCheckedContinuation { waiters.append($0) }
    }

    private func releaseSlot() {
        if waiters.isEmpty {
            activeDownloads -= 1
        } else {
            waiters.removeFirst().resume()
        }
    }

    private func scheduleSave() {
        saveTask?.cancel()
        saveTask = Task {
            try? await Task.sleep(for: .seconds(1))
            guard !Task.isCancelled else { return }
            self.saveNow()
        }
    }

    private func saveNow() {
        // Keep the most recent 3000 descriptions.
        if readings.count > 3000 {
            let keep = readings.sorted { $0.value.at > $1.value.at }.prefix(3000)
            readings = Dictionary(uniqueKeysWithValues: keep.map { ($0.key, $0.value) })
        }
        guard let data = try? JSONEncoder.iso.encode(readings) else { return }
        try? data.write(to: cacheURL, options: .atomic)
    }
}

extension JSONEncoder {
    static let iso: JSONEncoder = {
        let e = JSONEncoder()
        e.dateEncodingStrategy = .iso8601
        return e
    }()
}

extension JSONDecoder {
    static let iso: JSONDecoder = {
        let d = JSONDecoder()
        d.dateDecodingStrategy = .iso8601
        return d
    }()
}

public extension PickerItem {
    /// What VoiceOver reads for this row.
    func accessibilityLabel(reading: StickerReading?) -> String {
        switch self {
        case .library(let record):
            return record.accessibilityLabel
        case .giphy(let gif):
            let kind = gif.kind == .sticker ? "GIPHY sticker" : "GIF"
            if let reading { return "\(kind): \(SpokenText.listLabel(reading))" }
            return "\(kind): \(gif.cleanTitle), from GIPHY"
        case .loadMore:
            return "More GIPHY results"
        }
    }

    /// Short status line announced after a search.
    static func resultsSummary(libraryCount: Int, giphyCount: Int, query: String, firstLabel: String?, note: String, sendHint: String, giphyPending: Bool = false) -> String {
        let suffix = note.isEmpty ? "" : " " + note
        let total = libraryCount + giphyCount
        if total == 0 {
            if giphyPending { return "Searching GIPHY…" }
            return query.isEmpty
                ? "No stickers yet. Stickers you hear described, and imported stickers, appear here." + suffix
                : "Nothing found for \(query)." + suffix
        }
        var counts: String
        if giphyCount > 0, libraryCount > 0 {
            counts = "\(libraryCount) of yours and \(giphyCount) from GIPHY"
        } else if giphyCount > 0 {
            counts = "\(giphyCount) from GIPHY"
        } else {
            counts = "\(libraryCount) result\(libraryCount == 1 ? "" : "s")"
        }
        if giphyPending { counts += ", GIPHY still loading" }
        return "\(counts). First: \(firstLabel ?? ""). \(sendHint)" + suffix
    }
}
