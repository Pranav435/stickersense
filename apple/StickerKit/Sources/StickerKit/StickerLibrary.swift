import CoreGraphics
import Foundation

/// One sticker StickerSense has seen, imported or found.
public struct StickerRecord: Codable, Identifiable, Sendable, Hashable {
    public var id: UUID
    public var fingerprint: Fingerprint
    /// `nil` until described (e.g. freshly imported WhatsApp stickers).
    public var reading: StickerReading?
    public var detailed: String?
    public var ocrText: String
    public var source: StickerSource
    /// File name inside the library's `images` folder (only kept for collectible kinds).
    public var imageFile: String?
    /// Original web address (GIPHY).
    public var remoteURL: URL?
    public var addedAt: Date
    public var lastSeenAt: Date
    public var timesSeen: Int
    public var timesSent: Int

    public var isDescribed: Bool { reading != nil }
    public var isCollectible: Bool { reading?.kind.isCollectible ?? true }

    /// What VoiceOver reads for this row in lists.
    public var accessibilityLabel: String {
        guard let reading else { return "Not described yet, from \(source.displayName)" }
        return "\(SpokenText.listLabel(reading)). From \(source.displayName)"
    }
}

/// Persistent store of described stickers. Doubles as the description cache — a sticker
/// seen before is spoken instantly — and as the searchable library behind the picker.
public actor StickerLibrary {
    public nonisolated let directory: URL
    public nonisolated var imagesDirectory: URL { directory.appendingPathComponent("images", isDirectory: true) }
    private var fileURL: URL { directory.appendingPathComponent("library.json") }

    private var records: [UUID: StickerRecord] = [:]
    private var saveTask: Task<Void, Never>?
    private var changeHandlers: [@Sendable () -> Void] = []

    public init(directory: URL) {
        self.directory = directory
        try? FileManager.default.createDirectory(at: directory.appendingPathComponent("images", isDirectory: true), withIntermediateDirectories: true)
        if let data = try? Data(contentsOf: directory.appendingPathComponent("library.json")),
           let decoded = try? Self.decoder.decode([StickerRecord].self, from: data) {
            records = Dictionary(decoded.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        }
    }

    /// Default location: `~/Library/Application Support/StickerSense` (or an App Group container on iOS).
    public static func defaultDirectory(appGroup: String? = nil) -> URL {
        if let appGroup, let container = FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: appGroup) {
            return container.appendingPathComponent("Library/StickerSense", isDirectory: true)
        }
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return base.appendingPathComponent("StickerSense", isDirectory: true)
    }

    public func onChange(_ handler: @escaping @Sendable () -> Void) {
        changeHandlers.append(handler)
    }

    public var count: Int { records.count }

    public func record(_ id: UUID) -> StickerRecord? { records[id] }

    public func imageURL(for record: StickerRecord) -> URL? {
        record.imageFile.map { imagesDirectory.appendingPathComponent($0) }
    }

    // MARK: Cache lookups

    /// Finds a previously described sticker that looks the same. When both have OCR text,
    /// the text must agree too — sticker packs often reuse one drawing with different words.
    public func match(_ fingerprint: Fingerprint, ocrText: String) -> StickerRecord? {
        let ocr = TextReader.normalized(ocrText)
        var best: (record: StickerRecord, distance: Int)?
        for record in records.values where record.isDescribed && record.fingerprint.isSameSticker(as: fingerprint) {
            let other = TextReader.normalized(record.ocrText)
            if !ocr.isEmpty || !other.isEmpty {
                guard Self.textsAgree(ocr, other) else { continue }
            }
            let distance = record.fingerprint.hammingDistance(to: fingerprint)
            if best == nil || distance < best!.distance { best = (record, distance) }
        }
        return best?.record
    }

    static func textsAgree(_ a: String, _ b: String) -> Bool {
        if a == b { return true }
        if a.isEmpty || b.isEmpty { return false }
        // Allow small OCR wobble (a dropped or misread character or two).
        let shorter = min(a.count, b.count), longer = max(a.count, b.count)
        if longer - shorter > 2 { return false }
        return levenshtein(a, b) <= max(1, longer / 8)
    }

    static func levenshtein(_ a: String, _ b: String) -> Int {
        let a = Array(a), b = Array(b)
        if a.isEmpty { return b.count }
        if b.isEmpty { return a.count }
        var prev = Array(0...b.count)
        for i in 1...a.count {
            var cur = [Int](repeating: 0, count: b.count + 1)
            cur[0] = i
            for j in 1...b.count {
                cur[j] = min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1))
            }
            prev = cur
        }
        return prev[b.count]
    }

    // MARK: Writing

    /// Remembers a described sticker (or refreshes an existing one) and returns the stored record.
    @discardableResult
    public func remember(
        frames: [CGImage],
        fingerprint: Fingerprint,
        reading: StickerReading,
        ocrText: String,
        source: StickerSource,
        remoteURL: URL? = nil,
        replaceReading: Bool = false
    ) -> StickerRecord {
        let now = Date()
        if var existing = match(fingerprint, ocrText: ocrText) ?? records.values.first(where: { $0.fingerprint == fingerprint }) {
            existing.reading = replaceReading ? reading : (existing.reading ?? reading)
            existing.lastSeenAt = now
            existing.timesSeen += 1
            if existing.imageFile == nil, reading.kind.isCollectible {
                existing.imageFile = saveImage(frames, id: existing.id, animated: reading.animated)
            }
            records[existing.id] = existing
            scheduleSave()
            return existing
        }
        let id = UUID()
        let record = StickerRecord(
            id: id,
            fingerprint: fingerprint,
            reading: reading,
            detailed: nil,
            ocrText: ocrText,
            source: source,
            imageFile: reading.kind.isCollectible ? saveImage(frames, id: id, animated: reading.animated) : nil,
            remoteURL: remoteURL,
            addedAt: now,
            lastSeenAt: now,
            timesSeen: 1,
            timesSent: 0
        )
        records[id] = record
        scheduleSave()
        return record
    }

    /// Adds an image file without describing it yet (bulk imports describe in the background).
    /// Returns `nil` when an identical sticker is already in the library.
    @discardableResult
    public func importFile(_ url: URL, source: StickerSource) -> StickerRecord? {
        guard let frames = try? ImagePrep.frames(from: url, maxFrames: 4), let first = frames.first else { return nil }
        let fingerprint = Fingerprint(first)
        if records.values.contains(where: { $0.fingerprint.hammingDistance(to: fingerprint) <= 2 && $0.fingerprint.thumb.difference(to: fingerprint.thumb) < 0.02 }) {
            return nil
        }
        let id = UUID()
        let ext = url.pathExtension.isEmpty ? "png" : url.pathExtension.lowercased()
        let fileName = "\(id.uuidString).\(ext)"
        do {
            try FileManager.default.copyItem(at: url, to: imagesDirectory.appendingPathComponent(fileName))
        } catch {
            return nil
        }
        let now = Date()
        let record = StickerRecord(
            id: id, fingerprint: fingerprint, reading: nil, detailed: nil, ocrText: "", source: source,
            imageFile: fileName, remoteURL: nil, addedAt: now, lastSeenAt: now, timesSeen: 0, timesSent: 0
        )
        records[id] = record
        scheduleSave()
        return record
    }

    public func setReading(_ reading: StickerReading, ocrText: String, for id: UUID) {
        guard var record = records[id] else { return }
        record.reading = reading
        record.ocrText = ocrText
        records[id] = record
        scheduleSave()
    }

    public func setDetailed(_ text: String, for id: UUID) {
        guard var record = records[id] else { return }
        record.detailed = text
        records[id] = record
        scheduleSave()
    }

    public func markSent(_ id: UUID) {
        guard var record = records[id] else { return }
        record.timesSent += 1
        record.lastSeenAt = Date()
        records[id] = record
        scheduleSave()
    }

    public func delete(_ id: UUID) {
        guard let record = records.removeValue(forKey: id) else { return }
        if let file = record.imageFile { try? FileManager.default.removeItem(at: imagesDirectory.appendingPathComponent(file)) }
        scheduleSave()
    }

    public func undescribed(limit: Int) -> [StickerRecord] {
        Array(records.values.filter { !$0.isDescribed }.sorted { $0.addedAt > $1.addedAt }.prefix(limit))
    }

    // MARK: Reading

    /// Everything described recently, newest first — the History window.
    public func history(limit: Int = 200) -> [StickerRecord] {
        Array(records.values.filter { record in
            guard record.isDescribed, record.timesSeen > 0 else { return false }
            switch record.source {
            case .received, .imported: return true
            case .whatsApp, .giphy: return false
            }
        }.sorted { $0.lastSeenAt > $1.lastSeenAt }.prefix(limit))
    }

    /// Picker search across descriptions, sticker text and meaning.
    public func search(_ query: String, filter: SourceFilter = .all, limit: Int = 200) -> [StickerRecord] {
        let candidates = records.values.filter { record in
            guard record.isCollectible, record.imageFile != nil else { return false }
            return filter == .all || record.source.filterTag == filter
        }
        let tokens = Self.tokens(query)
        guard !tokens.isEmpty else {
            return Array(candidates.sorted(by: Self.recencyOrder).prefix(limit))
        }
        let scored: [(StickerRecord, Double)] = candidates.compactMap { record in
            let score = Self.score(record, tokens: tokens)
            return score > 0 ? (record, score) : nil
        }
        return Array(scored.sorted { $0.1 != $1.1 ? $0.1 > $1.1 : Self.recencyOrder($0.0, $1.0) }.prefix(limit).map(\.0))
    }

    static func recencyOrder(_ a: StickerRecord, _ b: StickerRecord) -> Bool {
        if a.timesSent != b.timesSent, max(a.timesSent, b.timesSent) >= 3 { return a.timesSent > b.timesSent }
        return a.lastSeenAt > b.lastSeenAt
    }

    static func tokens(_ text: String) -> [String] {
        text.lowercased()
            .folding(options: [.diacriticInsensitive, .widthInsensitive], locale: nil)
            .components(separatedBy: CharacterSet.alphanumerics.inverted)
            .filter { !$0.isEmpty }
    }

    static func score(_ record: StickerRecord, tokens queryTokens: [String]) -> Double {
        let fields: [(String, Double)] = [
            (record.reading?.text ?? "", 3),
            (record.ocrText, 2),
            (record.reading?.subject ?? "", 2),
            (record.reading?.vibe ?? "", 2),
            (record.detailed ?? "", 1),
            (record.reading?.kind.rawValue ?? "", 1),
        ]
        let fieldWords = fields.map { (Set(tokens($0.0)), $0.1) }
        var total = 0.0
        for token in queryTokens {
            var best = 0.0
            for (words, weight) in fieldWords {
                if words.contains(token) { best = max(best, weight * 1.0) }
                else if words.contains(where: { $0.hasPrefix(token) }) { best = max(best, weight * 0.8) }
                else if token.count >= 4, words.contains(where: { $0.contains(token) }) { best = max(best, weight * 0.5) }
            }
            if best == 0 { return 0 } // every word of the query must match somewhere
            total += best
        }
        return total + min(Double(record.timesSent), 5) * 0.1
    }

    // MARK: Persistence

    private func saveImage(_ frames: [CGImage], id: UUID, animated: Bool) -> String? {
        guard let first = frames.first else { return nil }
        do {
            if animated, frames.count > 1 {
                let name = "\(id.uuidString).gif"
                try ImagePrep.gifData(frames, frameDelay: 0.3).write(to: imagesDirectory.appendingPathComponent(name))
                return name
            }
            let name = "\(id.uuidString).png"
            try ImagePrep.pngData(first).write(to: imagesDirectory.appendingPathComponent(name))
            return name
        } catch {
            return nil
        }
    }

    private func scheduleSave() {
        saveTask?.cancel()
        saveTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(400))
            guard !Task.isCancelled else { return }
            await self?.saveNow()
        }
        for handler in changeHandlers { handler() }
    }

    public func saveNow() {
        let snapshot = Array(records.values)
        guard let data = try? Self.encoder.encode(snapshot) else { return }
        try? data.write(to: fileURL, options: .atomic)
    }

    static let encoder: JSONEncoder = {
        let e = JSONEncoder()
        e.dateEncodingStrategy = .iso8601
        return e
    }()

    static let decoder: JSONDecoder = {
        let d = JSONDecoder()
        d.dateDecodingStrategy = .iso8601
        return d
    }()
}
