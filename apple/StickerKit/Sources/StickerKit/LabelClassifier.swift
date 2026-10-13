import CoreGraphics
import Foundation

/// Decides from a screen reader's label (and the item's size) whether an item is a sticker or
/// GIF worth describing automatically.
///
/// Chat apps put the item type first: WhatsApp says "Mis pegatinas sticker from Ana, 15:48,
/// Received in Family"; a text message reads "Message from Ana, send me that sticker, 15:49".
/// So a keyword counts when it's in the label's first clause, or anywhere in a short label.
public enum LabelClassifier {
    public enum Verdict: Equatable, Sendable {
        case sticker
        case gif
        case notSticker
    }

    /// The words chat apps use for a sticker *item*, in several languages. Singular only:
    /// "stickers" or "GIFs" in a label is someone writing about stickers, not a sticker.
    public static let stickerWords: Set<String> = [
        "sticker", "memoji", "animoji", "genmoji",
        "pegatina", "autocollant", "adesivo", "aufkleber", "наклейка", "стикер", "स्टिकर", "ステッカー", "スタンプ",
        "贴纸", "貼圖", "스티커", "çıkartma",
    ]

    public static let gifWords: Set<String> = ["gif"]
    public static let gifPhrases = ["animated image", "animated sticker", "animated gif"]

    /// - Parameter inBrowser: web pages are full of text *about* stickers (this project's README,
    ///   for a start), so there a keyword only counts on something VoiceOver calls an image.
    public static func classify(label: String, size: CGSize, isImageElement: Bool, inBrowser: Bool = false) -> Verdict {
        // Text lines and tiny controls are never stickers, whatever they say.
        guard isStickerShaped(size) else { return .notSticker }
        let cleaned = clean(label)
        let words = tokens(cleaned)
        let saysImage = isImageElement || words.suffix(2).contains("image")
        if inBrowser, !saysImage { return .notSticker }

        let firstClause = cleaned.split(separator: ",", maxSplits: 1).first.map(String.init) ?? cleaned
        // Long enough for "<pack name> sticker from <sender>"; prose is ruled out separately.
        if !isProse(firstClause), tokens(firstClause).count <= 14, let verdict = keywordVerdict(firstClause) { return verdict }

        // Short labels ("Sticker", "GIF, 4:05 PM") or real image elements: trust keywords anywhere.
        if (words.count <= 8 && !isProse(cleaned)) || saysImage, let verdict = keywordVerdict(cleaned) { return verdict }

        // An unlabelled picture ("image"): treat it as a sticker when it's sticker-sized —
        // roughly square, bigger than an avatar, smaller than most photos.
        if saysImage, words.count <= 8 {
            let ratio = size.width / max(size.height, 1)
            if (80...280).contains(size.width), (80...280).contains(size.height), ratio > 0.7, ratio < 1.4 {
                return .sticker
            }
        }
        return .notSticker
    }

    /// Stickers and GIFs are roughly square-ish blocks; lines of text are long and thin.
    public static func isStickerShaped(_ size: CGSize) -> Bool {
        let shortSide = min(size.width, size.height), longSide = max(size.width, size.height)
        return shortSide >= 40 && longSide / max(shortSide, 1) <= 3
    }

    /// A sentence ("Someone sends you a sticker. Your screen reader…") rather than an item label.
    static func isProse(_ text: String) -> Bool {
        text.range(of: #"[.!?]\s+\S"#, options: .regularExpression) != nil
    }

    /// Words that make "sticker" part of a sentence ("send me a sticker") rather than an item type.
    static let proseDeterminers: Set<String> = ["a", "an", "the", "this", "that", "my", "your", "his", "her", "their", "our", "one", "any", "some", "every"]

    static func keywordVerdict(_ text: String) -> Verdict? {
        let list = tokens(text)
        func itemWord(_ vocabulary: Set<String>) -> Bool {
            list.indices.contains { i in
                guard vocabulary.contains(list[i]) else { return false }
                guard i > 0 else { return true }
                // "Your GIF, 14:19, Sent to…" is how WhatsApp labels your own GIF: a possessive
                // opening the label is fine; mid-sentence ("send me your sticker") it's prose.
                if i == 1, ["your", "my"].contains(list[0]) { return true }
                return !proseDeterminers.contains(list[i - 1])
            }
        }
        if itemWord(gifWords) || gifPhrases.contains(where: text.contains) { return .gif }
        if itemWord(stickerWords) { return .sticker }
        // Scripts without spaces between words (Japanese, Chinese): substring match.
        if stickerWords.contains(where: { word in !word.unicodeScalars.allSatisfy(\.isASCII) && text.contains(word) }) { return .sticker }
        return nil
    }

    /// Lowercased, with the invisible direction marks WhatsApp sprinkles into labels removed.
    static func clean(_ label: String) -> String {
        let marks = CharacterSet(charactersIn: "\u{200E}\u{200F}\u{202A}\u{202B}\u{202C}\u{202D}\u{202E}\u{2066}\u{2067}\u{2068}\u{2069}")
        return String(String.UnicodeScalarView(label.unicodeScalars.filter { !marks.contains($0) })).lowercased()
    }

    static func tokens(_ text: String) -> [String] {
        text.components(separatedBy: CharacterSet.alphanumerics.inverted).filter { !$0.isEmpty }
    }
}
