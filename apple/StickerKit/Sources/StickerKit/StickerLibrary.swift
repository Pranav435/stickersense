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
    /// Fingerprints of several frames of an animated sticker or GIF, so it's recognised whichever
    /// frame a chat happens to be showing (e.g. a GIF you sent from the picker, now playing).
    public var frameFingerprints: [Fingerprint]? = nil

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
            // Drop descriptions of blank captures ("Black rectangle") that older versions cached.
            records = Dictionary(decoded.filter { !$0.fingerprint.thumb.isUniform }.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
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
        guard !fingerprint.thumb.isUniform else { return nil }
        let ocr = TextReader.normalized(ocrText)
        var best: (record: StickerRecord, distance: Int, animated: Bool)?
        for record in records.values where record.isDescribed {
            // The main fingerprint, or — for an animation — whichever remembered frame is showing.
            var distance: Int?
            if record.fingerprint.isSameSticker(as: fingerprint) {
                distance = record.fingerprint.hammingDistance(to: fingerprint)
            } else if let frame = record.frameFingerprints?.first(where: { $0.isSameSticker(as: fingerprint) }) {
                distance = frame.hammingDistance(to: fingerprint)
            }
            guard let distance else { continue }
            let animated = (record.frameFingerprints?.count ?? 0) > 1
            // Captions of a still must agree; an animation's text changes from frame to frame.
            if !animated {
                let other = TextReader.normalized(record.ocrText)
                if !ocr.isEmpty || !other.isEmpty {
                    guard Self.textsAgree(ocr, other) else { continue }
                }
            }
            // One GIF, one description: prefer the reading made from its motion.
            let better = best == nil || (animated && !best!.animated) || (animated == best!.animated && distance < best!.distance)
            if better { best = (record, distance, animated) }
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
        // Never cache a blank capture: every blank image looks alike and would "match" forever.
        if fingerprint.thumb.isUniform {
            return StickerRecord(id: UUID(), fingerprint: fingerprint, reading: reading, detailed: nil, ocrText: ocrText, source: source,
                                 imageFile: nil, remoteURL: remoteURL, addedAt: now, lastSeenAt: now, timesSeen: 1, timesSent: 0)
        }
        if var existing = match(fingerprint, ocrText: ocrText) ?? records.values.first(where: { $0.fingerprint == fingerprint }) {
            existing.reading = replaceReading ? reading : (existing.reading ?? reading)
            existing.lastSeenAt = now
            existing.timesSeen += 1
            if existing.imageFile == nil, reading.kind.isCollectible {
                existing.imageFile = saveImage(frames, id: existing.id, animated: reading.animated)
            }
            if (existing.frameFingerprints?.count ?? 0) < frames.count, frames.count > 1 {
                existing.frameFingerprints = Self.frameFingerprints(frames)
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
            timesSent: 0,
            frameFingerprints: frames.count > 1 ? Self.frameFingerprints(frames) : nil
        )
        records[id] = record
        if frames.count > 1 { absorbStillCopies(of: id) }
        scheduleSave()
        return record
    }

    /// An animation first seen paused (a still preview) gets a still-frame description. Once it's
    /// described with its motion, fold those stale GIF copies into the animated record, and keep
    /// their preview frame so the paused preview is recognised directly next time.
    private func absorbStillCopies(of id: UUID) {
        guard var record = records[id], var frames = record.frameFingerprints, frames.count > 1 else { return }
        for other in records.values where other.id != id && (other.frameFingerprints?.count ?? 0) <= 1 && other.reading?.kind == .gif {
            guard case .received = other.source, frames.contains(where: { Self.isLooseFrameMatch($0, other.fingerprint) }) else { continue }
            record.timesSeen += other.timesSeen
            frames.append(other.fingerprint)
            records.removeValue(forKey: other.id)
            if let file = other.imageFile { try? FileManager.default.removeItem(at: imagesDirectory.appendingPathComponent(file)) }
        }
        record.frameFingerprints = Array(frames.suffix(24))
        records[id] = record
    }

    /// A single frame of a GIF StickerSense has already described with its motion — e.g. the paused
    /// preview a chat shows. Remembers the frame so the next lookup is a plain cache hit.
    public func matchAnimationFrame(_ fingerprint: Fingerprint) -> StickerRecord? {
        guard !fingerprint.thumb.isUniform else { return nil }
        var best: (record: StickerRecord, distance: Int)?
        for record in records.values where record.isDescribed && (record.reading?.kind == .gif || record.reading?.animated == true) {
            guard let frames = record.frameFingerprints, frames.count > 1 else { continue }
            for frame in frames where Self.isLooseFrameMatch(frame, fingerprint) {
                let distance = frame.hammingDistance(to: fingerprint)
                if distance < (best?.distance ?? .max) { best = (record, distance) }
            }
        }
        guard var record = best?.record else { return nil }
        record.frameFingerprints = Array(((record.frameFingerprints ?? []) + [fingerprint]).suffix(24))
        record.lastSeenAt = Date()
        record.timesSeen += 1
        records[record.id] = record
        scheduleSave()
        return record
    }

    /// Frames of one animation: compared more loosely than stills, since a chat may re-encode
    /// it and show any point in the loop.
    static func isLooseFrameMatch(_ a: Fingerprint, _ b: Fingerprint) -> Bool {
        a.hammingDistance(to: b) <= 14 && a.thumb.difference(to: b.thumb) <= 0.02
    }

    static func frameFingerprints(_ frames: [CGImage]) -> [Fingerprint] {
        ImagePrep.sampleIndices(count: frames.count, max: 16).map { Fingerprint(frames[$0]) }
    }

    /// Recognises something the user sent from the picker recently ("Your GIF" in a chat), from a
    /// single on-screen frame. Chats re-encode GIFs and overlay badges, so the comparison is looser
    /// than `match` — safe only because the candidates are the handful of items sent in the last day.
    public func matchRecentlySent(_ fingerprint: Fingerprint, within: TimeInterval = 86_400) -> StickerRecord? {
        guard !fingerprint.thumb.isUniform else { return nil }
        let cutoff = Date().addingTimeInterval(-within)
        var best: (record: StickerRecord, distance: Int)?
        for record in records.values where record.isDescribed && record.timesSent > 0 && record.lastSeenAt > cutoff {
            for stored in [record.fingerprint] + (record.frameFingerprints ?? []) {
                let distance = stored.hammingDistance(to: fingerprint)
                if distance <= 22, stored.thumb.difference(to: fingerprint.thumb) <= 0.05, distance < (best?.distance ?? .max) {
                    best = (record, distance)
                }
            }
        }
        return best?.record
    }

    /// Recognises an animated sticker or GIF from several captured frames. Each captured frame may
    /// land anywhere in the loop, and a chat may re-encode it, so single frames are compared more
    /// loosely than stills — but at least two captured frames must each match a stored frame.
    public func matchAnimated(_ captured: [Fingerprint]) -> StickerRecord? {
        let usable = captured.filter { !$0.thumb.isUniform }
        guard usable.count >= 2 else { return nil }
        var best: (record: StickerRecord, hits: Int)?
        for record in records.values where record.isDescribed {
            guard let stored = record.frameFingerprints, stored.count >= 2 else { continue }
            let hits = usable.filter { frame in stored.contains { Self.isLooseFrameMatch($0, frame) } }.count
            if hits >= 2, hits > (best?.hits ?? 0) { best = (record, hits) }
        }
        return best?.record
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

    public func setFrameFingerprints(from frames: [CGImage], for id: UUID) {
        guard frames.count > 1, var record = records[id] else { return }
        record.frameFingerprints = Self.frameFingerprints(frames)
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
