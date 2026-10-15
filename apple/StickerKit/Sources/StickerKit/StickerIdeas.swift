import Foundation
import FoundationModels

/// What to draw for a sticker search: a few picture ideas, and the words to write on them.
public struct StickerIdea: Sendable, Equatable {
    /// Words StickerSense writes on the sticker itself, since image models can't spell. Empty for none.
    public var caption: String
    /// What to draw, one short description per variation.
    public var pictures: [String]

    public init(caption: String, pictures: [String]) {
        self.caption = caption
        self.pictures = pictures
    }
}

/// Turning a picker search into prompts for the image model. The Windows app has the same rules
/// (StickerIdeas.cs), so a search makes the same kind of sticker on both.
public enum StickerIdeas {
    /// How many stickers one Generate makes.
    public static let variations = 4

    /// Added to every picture. Tried with SDXS and SD-Turbo: these words give one clean character
    /// on plain white that cuts out well. Saying "sticker" draws a round badge behind it, or a
    /// photo of a sticker on a table.
    public static let style = "cute cartoon character illustration, thick black outline, flat vibrant colors, full body, centered on a plain white background"

    /// The longest caption written on a sticker; longer ones get too small to read.
    public static let maxCaptionLength = 40

    /// The image model's prompt for one picture idea.
    public static func prompt(for picture: String) -> String {
        let picture = picture.trimmingCharacters(in: .whitespacesAndNewlines.union(CharacterSet(charactersIn: ".,")))
        return "\(picture), \(style)"
    }

    /// Words in quotes are the caption, exactly as typed: `corgi "thank you"` gives the picture
    /// "corgi" and the caption "thank you". Straight and curly quotes both work.
    public static func splitQuoted(_ query: String) -> (picture: String, caption: String?) {
        let pattern = #"["“”«»]([^"“”«»]+)["“”«»]"#
        guard let regex = try? NSRegularExpression(pattern: pattern),
              let match = regex.firstMatch(in: query, range: NSRange(query.startIndex..., in: query)),
              let whole = Range(match.range, in: query), let inner = Range(match.range(at: 1), in: query) else {
            return (query.trimmingCharacters(in: .whitespacesAndNewlines), nil)
        }
        let caption = String(query[inner]).trimmingCharacters(in: .whitespacesAndNewlines)
        var picture = query
        picture.removeSubrange(whole)
        picture = picture.split(whereSeparator: \.isWhitespace).joined(separator: " ")
        return (picture, caption.isEmpty ? nil : caption)
    }

    /// Ideas without a language model: the search is the picture, and only quoted words become a
    /// caption. A caption-only search ("\"lol\"") draws a character reacting to it.
    public static func fallback(for query: String) -> StickerIdea {
        let (picture, caption) = splitQuoted(query)
        let subject = picture.isEmpty ? "a cute cartoon character reacting to \(caption ?? query)" : picture
        return StickerIdea(caption: caption.map(clampCaption) ?? "", pictures: Array(repeating: subject, count: variations))
    }

    /// Tidies a language model's plan: a quoted caption wins; pictures are trimmed, deduplicated,
    /// stripped of requests for lettering, and topped up to `variations` with the search itself.
    public static func cleaned(caption: String, pictures: [String], query: String) -> StickerIdea {
        let (picturePart, quoted) = splitQuoted(query)
        var caption = quoted ?? caption.trimmingCharacters(in: .whitespacesAndNewlines.union(CharacterSet(charactersIn: "\"“”'")))
        if ["none", "empty", "n/a", "-", "no caption"].contains(caption.lowercased()) { caption = "" }
        var seen = Set<String>()
        var kept: [String] = []
        for picture in pictures {
            var text = picture.trimmingCharacters(in: .whitespacesAndNewlines)
            // "1. a corgi" or "- a corgi" from a numbered list.
            if let range = text.range(of: #"^(\d+[.)]|[-*•])\s*"#, options: .regularExpression) { text.removeSubrange(range) }
            text = text.trimmingCharacters(in: .whitespacesAndNewlines.union(CharacterSet(charactersIn: "\".")))
            guard !text.isEmpty, text.count <= 160, !asksForLettering(text), seen.insert(text.lowercased()).inserted else { continue }
            kept.append(text)
            if kept.count == variations { break }
        }
        let fallbackPicture = picturePart.isEmpty ? fallback(for: query).pictures[0] : picturePart
        while kept.count < variations { kept.append(kept.first ?? fallbackPicture) }
        return StickerIdea(caption: clampCaption(caption), pictures: kept)
    }

    static func clampCaption(_ caption: String) -> String {
        let flat = caption.split(whereSeparator: \.isNewline).joined(separator: " ").trimmingCharacters(in: .whitespaces)
        guard flat.count > maxCaptionLength else { return flat }
        let cut = flat.prefix(maxCaptionLength)
        return String(cut.lastIndex(of: " ").map { cut[..<$0] } ?? cut)
    }

    /// The image model would draw gibberish letters: lettering is StickerSense's job.
    static func asksForLettering(_ picture: String) -> Bool {
        let lower = picture.lowercased()
        let words = Set(lower.components(separatedBy: CharacterSet.letters.inverted))
        return !words.isDisjoint(with: ["text", "letters", "lettering", "words", "written", "caption", "banner", "message", "note"])
            || lower.contains("that says") || lower.contains("sign saying")
    }

    // MARK: The language model's part (same wording as Windows)

    public static let instructions = """
        You plan chat stickers. Someone typed a search for a sticker. You plan stickers that an image generator will draw, and the words to print on them.
        caption: the words to print on the sticker, only when the search is something people say in chat, such as a greeting, reaction, reply or wish ("thank you", "lol", "good morning", "happy birthday", "on my way"). Copy those words exactly as the search has them, changing only capital letters. Leave it empty when the search only describes a picture, such as "sleepy cat" or "pizza".
        pictures: different ideas for the picture, each a short phrase of at most 15 words. Keep the character or thing the search names in every idea, and vary its pose, expression or prop. Only when the search names nobody, choose a cute animal or character that shows its meaning and mood. Never ask for words, letters or signs in the picture.
        """

    public static func request(for query: String) -> String {
        "The search: \(query)"
    }
}

@Generable(description: "A plan for chat stickers that an image generator will draw")
struct StickerPlan {
    @Guide(description: "The search's own words to print on the sticker, copied exactly, when the search is something people say in chat; empty when the search only describes a picture")
    var caption: String

    @Guide(description: "Different picture ideas, each at most 15 words: the character or thing the search names (or, if it names none, a cute animal) in a different pose, expression or with a different prop. No words or letters in the picture.", .count(StickerIdeas.variations))
    var pictures: [String]
}

/// Plans stickers with Apple's on-device model, falling back to plain rules when it's
/// unavailable or declines.
public enum StickerIdeaWriter {
    public static func ideas(for query: String) async -> StickerIdea {
        let query = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard case .available = SystemLanguageModel.default.availability else { return StickerIdeas.fallback(for: query) }
        let session = LanguageModelSession(instructions: StickerIdeas.instructions)
        do {
            let plan = try await session.respond(
                to: StickerIdeas.request(for: query),
                generating: StickerPlan.self,
                options: GenerationOptions(temperature: 0.8, maximumResponseTokens: 220)
            ).content
            return StickerIdeas.cleaned(caption: plan.caption, pictures: plan.pictures, query: query)
        } catch {
            return StickerIdeas.fallback(for: query)
        }
    }
}
