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

    /// Words screen readers and chat apps use for stickers, in several languages.
    public static let stickerWords: Set<String> = [
        "sticker", "stickers", "memoji", "animoji", "genmoji",
        "pegatina", "pegatinas", "autocollant", "autocollants", "adesivo", "adesivos", "aufkleber",
        "наклейка", "стикер", "स्टिकर", "ステッカー", "スタンプ", "贴纸", "貼圖", "스티커", "çıkartma",
    ]

    public static let gifWords: Set<String> = ["gif", "gifs"]
    public static let gifPhrases = ["animated image", "animated sticker", "animated gif"]

    public static func classify(label: String, size: CGSize, isImageElement: Bool) -> Verdict {
        let cleaned = clean(label)
        let firstClause = cleaned.split(separator: ",", maxSplits: 1).first.map(String.init) ?? cleaned
        if let verdict = keywordVerdict(firstClause) { return verdict }

        let words = tokens(cleaned)
        // Short labels ("Sticker", "GIF, 4:05 PM") or real image elements: trust keywords anywhere.
        if words.count <= 8 || isImageElement, let verdict = keywordVerdict(cleaned) { return verdict }

        // An unlabelled picture ("image"): treat it as a sticker when it's sticker-sized —
        // roughly square, bigger than an avatar, smaller than most photos.
        if isImageElement || (words.count <= 8 && words.contains("image")) {
            let ratio = size.width / max(size.height, 1)
            if (80...280).contains(size.width), (80...280).contains(size.height), ratio > 0.7, ratio < 1.4 {
                return .sticker
            }
        }
        return .notSticker
    }

    static func keywordVerdict(_ text: String) -> Verdict? {
        let words = Set(tokens(text))
        if !words.isDisjoint(with: gifWords) || gifPhrases.contains(where: text.contains) { return .gif }
        if !words.isDisjoint(with: stickerWords) { return .sticker }
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
