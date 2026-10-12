import Foundation

/// Turns readings into what the screen reader says. Kept short: the user hears this
/// right after VoiceOver's own "Sticker" announcement, so we don't repeat the obvious.
public enum SpokenText {
    public static func short(_ reading: StickerReading, includeVibe: Bool = false) -> String {
        if reading.kind == .nothing {
            return reading.subject.isEmpty
                ? "No sticker or picture here."
                : "No sticker here. \(reading.subject)."
        }
        var parts: [String] = []
        let prefix = kindPrefix(reading.kind)
        if reading.kind == .text, !reading.text.isEmpty {
            parts.append("\(prefix)“\(reading.text)”.")
        } else {
            var line = prefix + reading.subject
            if !reading.text.isEmpty { line += ", “\(reading.text)”" }
            parts.append(line + ".")
        }
        if includeVibe, !reading.vibe.isEmpty {
            parts.append("Meaning: \(reading.vibe).")
        }
        return parts.joined(separator: " ")
    }

    /// Searchable / listable one-liner used in the history and picker lists.
    public static func listLabel(_ reading: StickerReading) -> String {
        var line = reading.subject
        if !reading.text.isEmpty { line += ", “\(reading.text)”" }
        if reading.animated { line += ", animated" }
        return line
    }

    static func kindPrefix(_ kind: StickerKind) -> String {
        switch kind {
        case .sticker, .emoji, .illustration, .meme, .nothing: ""
        case .gif: "GIF: "
        case .photo: "Photo: "
        case .text: "Text: "
        }
    }

    /// Several stickers from one screenshot, newest first.
    public static func screen(_ readings: [StickerReading], includeVibe: Bool = false) -> String {
        guard let newest = readings.first else { return "No stickers or pictures on screen." }
        var parts = ["\(readings.count == 1 ? "" : "Newest: ")\(short(newest, includeVibe: includeVibe))"]
        for (index, reading) in readings.dropFirst().enumerated() {
            parts.append("\(index == 0 ? "Before that" : "Then"): \(short(reading, includeVibe: includeVibe))")
        }
        return parts.joined(separator: " ")
    }

    public static func failure(_ error: any Error) -> String {
        (error as? LocalizedError)?.errorDescription ?? "Couldn't describe it."
    }
}
