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
    /// <summary>Words screen readers and chat apps use for stickers, in several languages.</summary>
    public static readonly IReadOnlySet<string> StickerWords = new HashSet<string>
    {
        "sticker", "stickers", "memoji", "animoji", "genmoji",
        "pegatina", "pegatinas", "autocollant", "autocollants", "adesivo", "adesivos", "aufkleber",
        "наклейка", "стикер", "स्टिकर", "ステッカー", "スタンプ", "贴纸", "貼圖", "스티커", "çıkartma",
    };

    public static readonly IReadOnlySet<string> GifWords = new HashSet<string> { "gif", "gifs" };

    public static readonly IReadOnlyList<string> GifPhrases = ["animated image", "animated sticker", "animated gif"];

    /// <param name="width">Width in device-independent pixels (like the Mac's points).</param>
    /// <param name="height">Height in device-independent pixels.</param>
    public static StickerVerdict Classify(string label, double width, double height, bool isImageElement)
    {
        var cleaned = Clean(label);
        var comma = cleaned.IndexOf(',');
        var firstClause = comma >= 0 ? cleaned[..comma] : cleaned;
        if (KeywordVerdict(firstClause) is { } verdict) return verdict;

        var words = Tokens(cleaned);
        // Short labels ("Sticker", "GIF, 4:05 PM") or real image elements: trust keywords anywhere.
        if ((words.Count <= 8 || isImageElement) && KeywordVerdict(cleaned) is { } anywhere) return anywhere;

        // An unlabelled picture ("image"): treat it as a sticker when it's sticker-sized —
        // roughly square, bigger than an avatar, smaller than most photos.
        if (isImageElement || (words.Count <= 8 && words.Contains("image")))
        {
            var ratio = width / Math.Max(height, 1);
            if (width is >= 80 and <= 280 && height is >= 80 and <= 280 && ratio > 0.7 && ratio < 1.4) return StickerVerdict.Sticker;
        }
        return StickerVerdict.NotSticker;
    }

    internal static StickerVerdict? KeywordVerdict(string text)
    {
        var words = Tokens(text).ToHashSet();
        if (words.Overlaps(GifWords) || GifPhrases.Any(p => text.Contains(p, StringComparison.Ordinal))) return StickerVerdict.Gif;
        if (words.Overlaps(StickerWords)) return StickerVerdict.Sticker;
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
        return words.Count == 0 || (words.Count <= 3 && words.Any(w => w is "image" or "picture" or "graphic" or "photo"));
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
