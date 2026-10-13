using System.Text;

namespace StickerSense.Core;

/// <summary>
/// Decides from a screen reader label (and the item's size) whether an item is a sticker or GIF
/// worth describing automatically.
/// </summary>
/// <remarks>
/// Chat apps put the item type first: WhatsApp says "Mis pegatinas sticker from Ana, 15:48,
/// Received in Family"; a text message reads "Message from Ana, send me that sticker, 15:49".
/// So a keyword counts when it's in the label's first clause, or anywhere in a short label.
/// </remarks>
public static class LabelClassifier
{
    /// <summary>
    /// The words chat apps use for a sticker <em>item</em>, in several languages. Singular only:
    /// "stickers" or "GIFs" in a label is someone writing about stickers, not a sticker.
    /// </summary>
    public static readonly IReadOnlySet<string> StickerWords = new HashSet<string>
    {
        "sticker", "memoji", "animoji", "genmoji",
        "pegatina", "autocollant", "adesivo", "aufkleber", "наклейка", "стикер", "स्टिकर", "ステッカー", "スタンプ",
        "贴纸", "貼圖", "스티커", "çıkartma",
    };

    public static readonly IReadOnlySet<string> GifWords = new HashSet<string> { "gif" };

    public static readonly IReadOnlyList<string> GifPhrases = ["animated image", "animated sticker", "animated gif"];

    /// <summary>Words that make "sticker" part of a sentence ("send me a sticker") rather than an item type.</summary>
    internal static readonly IReadOnlySet<string> ProseDeterminers = new HashSet<string>
    {
        "a", "an", "the", "this", "that", "my", "your", "his", "her", "their", "our", "one", "any", "some", "every",
    };

    /// <param name="width">Width in device-independent pixels (like the Mac's points).</param>
    /// <param name="height">Height in device-independent pixels.</param>
    /// <param name="inBrowser">
    /// Web pages are full of text <em>about</em> stickers (this project's README, for a start), so
    /// there a keyword only counts on something the screen reader calls an image.
    /// </param>
    public static StickerVerdict Classify(string label, double width, double height, bool isImageElement, bool inBrowser = false)
    {
        // Text lines and tiny controls are never stickers, whatever they say.
        if (!IsStickerShaped(width, height)) return StickerVerdict.NotSticker;
        var cleaned = Clean(label);
        var words = Tokens(cleaned);
        var saysImage = isImageElement || words.TakeLast(2).Contains("image");
        if (inBrowser && !saysImage) return StickerVerdict.NotSticker;

        var comma = cleaned.IndexOf(',');
        var firstClause = comma >= 0 ? cleaned[..comma] : cleaned;
        // Long enough for "<pack name> sticker from <sender>"; prose is ruled out separately.
        if (!IsProse(firstClause) && Tokens(firstClause).Count <= 14 && KeywordVerdict(firstClause) is { } verdict) return verdict;

        // Short labels ("Sticker", "GIF, 4:05 PM") or real image elements: trust keywords anywhere.
        if (((words.Count <= 8 && !IsProse(cleaned)) || saysImage) && KeywordVerdict(cleaned) is { } anywhere) return anywhere;

        // An unlabelled picture ("image"): treat it as a sticker when it's sticker-sized —
        // roughly square, bigger than an avatar, smaller than most photos.
        if (saysImage && words.Count <= 8)
        {
            var ratio = width / Math.Max(height, 1);
            if (width is >= 80 and <= 280 && height is >= 80 and <= 280 && ratio > 0.7 && ratio < 1.4) return StickerVerdict.Sticker;
        }
        return StickerVerdict.NotSticker;
    }

    /// <summary>Stickers and GIFs are roughly square-ish blocks; lines of text are long and thin.</summary>
    public static bool IsStickerShaped(double width, double height)
    {
        double shortSide = Math.Min(width, height), longSide = Math.Max(width, height);
        return shortSide >= 40 && longSide / Math.Max(shortSide, 1) <= 3;
    }

    /// <summary>A sentence ("Someone sends you a sticker. Your screen reader…") rather than an item label.</summary>
    internal static bool IsProse(string text) => System.Text.RegularExpressions.Regex.IsMatch(text, @"[.!?]\s+\S");

    internal static StickerVerdict? KeywordVerdict(string text)
    {
        var list = Tokens(text);
        bool ItemWord(IReadOnlySet<string> vocabulary) => Enumerable.Range(0, list.Count).Any(i =>
        {
            if (!vocabulary.Contains(list[i])) return false;
            if (i == 0) return true;
            // "Your GIF, 14:19, Sent to…" is how WhatsApp labels your own GIF: a possessive opening
            // the label is fine; mid-sentence ("send me your sticker") it's prose.
            if (i == 1 && list[0] is "your" or "my") return true;
            return !ProseDeterminers.Contains(list[i - 1]);
        });
        if (ItemWord(GifWords) || GifPhrases.Any(p => text.Contains(p, StringComparison.Ordinal))) return StickerVerdict.Gif;
        if (ItemWord(StickerWords)) return StickerVerdict.Sticker;
        // Scripts without spaces between words (Japanese, Chinese): substring match.
        if (StickerWords.Any(w => !w.All(char.IsAscii) && text.Contains(w, StringComparison.Ordinal))) return StickerVerdict.Sticker;
        return null;
    }

    /// <summary>
    /// The label, when it says the item is a sticker or GIF — then everything in it (sender name,
    /// time, chat name) is chat chrome to remove from OCR. On a text message the label is the
    /// message itself, so it must not be treated as chrome: returns null.
    /// </summary>
    public static string? ChromeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var cleaned = Clean(label);
        var comma = cleaned.IndexOf(',');
        var says = KeywordVerdict(comma >= 0 ? cleaned[..comma] : cleaned) is not null
            || (Tokens(cleaned).Count <= 8 && KeywordVerdict(cleaned) is not null);
        return says ? label : null;
    }

    /// <summary>Labels that don't say what the item is, where a closer look at the element is worth it.</summary>
    public static bool IsAmbiguous(string label)
    {
        var words = Tokens(Clean(label));
        return words.Count == 0 || (words.Count <= 6 && words.Any(w => w is "image" or "picture" or "graphic" or "photo" or "sticker" or "gif"));
    }

    /// <summary>Lowercased, with the invisible direction marks WhatsApp sprinkles into labels removed.</summary>
    internal static string Clean(string label)
    {
        var builder = new StringBuilder(label.Length);
        foreach (var c in label)
        {
            if (c is '‎' or '‏' or (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩')) continue;
            builder.Append(c);
        }
        return builder.ToString().ToLowerInvariant();
    }

    internal static IReadOnlyList<string> Tokens(string text) => StickerText.Words(text);
}
