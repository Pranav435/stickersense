using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace StickerSense.Core;

/// <summary>
/// Text found on stickers. OCR (Windows.Media.Ocr in the app) is far more reliable at exact
/// spelling than a vision-language model, so its text is handed to the model as a hint — after
/// the chat-interface text around the sticker (timestamps, ticks) has been filtered out.
/// </summary>
public static partial class StickerText
{
    [GeneratedRegex(@"\b\d{1,2}[:.]\d{2}(\s?[aApP]\.?[mM]\.?)?\b")]
    private static partial Regex InlineTime();

    [GeneratedRegex("[✓✔︎]+")]
    private static partial Regex InlineTicks();

    [GeneratedRegex(@"\b(Edited|edited)\b")]
    private static partial Regex InlineEdited();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex RepeatedSpace();

    private static readonly Regex[] ChromeLines =
    [
        new(@"^\d{1,2}[:.]\d{2}(\s?[ap]\.?m\.?)?$", RegexOptions.Compiled),         // 4:05, 16:05, 4:05 PM
        new(@"^(today|yesterday)(,?\s.*)?$", RegexOptions.Compiled),
        new(@"^(edited|delivered|read|seen|sent|forwarded)(\s.*)?$", RegexOptions.Compiled),
        new("^[✓✔︎\\s]+$", RegexOptions.Compiled),
        new(@"^\d+$", RegexOptions.Compiled),                                          // reaction counters
    ];

    /// <summary>Removes timestamps and read-receipt ticks embedded inside a line of text.</summary>
    public static string StripChrome(string text)
    {
        var t = InlineTime().Replace(text, "");
        t = InlineTicks().Replace(t, "");
        t = InlineEdited().Replace(t, "");
        t = RepeatedSpace().Replace(t, " ");
        return TrimEdges(t);
    }

    /// <summary>
    /// Timestamps, read receipts and other chat-interface text that sits around a sticker inside
    /// the focused item's rectangle but isn't part of the sticker.
    /// </summary>
    public static bool IsChatChrome(string line)
    {
        var lowered = line.ToLowerInvariant();
        return ChromeLines.Any(pattern => pattern.IsMatch(lowered));
    }

    /// <summary>
    /// The sticker's own text, one line per OCR line: chat chrome stripped, and lines that are just
    /// something the screen reader label already says (sender name, chat name) dropped.
    /// Pass <paramref name="label"/> only when the label is about a sticker (<see cref="LabelClassifier.ChromeLabel"/>).
    /// </summary>
    public static string TextOf(IEnumerable<OcrLine> lines, string? label) =>
        string.Join("\n", lines.Select(l => StripChrome(l.Text)).Where(t => t.Length > 0 && !IsChatChrome(t) && !IsInLabel(t, label) && !IsFragment(t)));

    /// <summary>A stray letter or two ("V") misread from a moving caption or an edge of the picture.</summary>
    internal static bool IsFragment(string line) => line.Count(char.IsLetterOrDigit) < 2;

    /// <summary>
    /// Where the chat's own text sits around the sticker (sender name, time, ticks), to be painted
    /// out before the model sees the picture and before it is fingerprinted.
    /// </summary>
    public static IReadOnlyList<NormalizedBox> ChromeBoxes(IEnumerable<OcrLine> lines, string? label) =>
        lines.Where(line =>
        {
            var stripped = StripChrome(line.Text);
            return stripped.Length == 0 || IsChatChrome(stripped) || IsInLabel(stripped, label)
                || stripped.Length < line.Text.Trim().Length - 3; // had a timestamp in it
        }).Select(line => line.Box).ToList();

    /// <summary>True when <paramref name="text"/> is just something the screen reader already said (sender name etc.).</summary>
    public static bool IsInLabel(string text, string? label)
    {
        if (string.IsNullOrEmpty(label)) return false;
        var n = Normalized(text);
        return n.Length >= 3 && Normalized(label).Contains(n, StringComparison.Ordinal);
    }

    /// <summary>Drops OCR lines that also appear in the screen reader label.</summary>
    public static string RemovingLabelText(string ocr, string? label)
    {
        if (string.IsNullOrEmpty(label)) return ocr;
        return string.Join("\n", ocr.Split('\n').Where(line => Normalized(line).Length > 0 && !IsInLabel(line, label)));
    }

    /// <summary>
    /// The sticker's words for speaking, straight from OCR: chat chrome and label text removed,
    /// lines joined. Long text means a screenshot or document rather than a caption, so it's cut
    /// at about 160 characters with "…".
    /// </summary>
    public static string FromOcr(string ocr, string? chromeLabel = null)
    {
        var flat = string.Join(" ", ocr.Split('\n').Select(StripChrome).Where(l => l.Length > 0 && !IsChatChrome(l) && !IsInLabel(l, chromeLabel))).Trim();
        if (flat.Length <= 160) return flat;
        var cut = flat[..160];
        var space = cut.LastIndexOf(' ');
        return (space > 0 ? cut[..space] : cut) + "…";
    }

    /// <summary>Adds lines from <paramref name="more"/> that <paramref name="text"/> doesn't contain yet (OCR of later animation frames).</summary>
    public static string MergeLines(string text, string more)
    {
        var merged = text;
        foreach (var line in more.Split('\n'))
        {
            var n = Normalized(line);
            if (n.Length == 0 || Normalized(merged).Contains(n, StringComparison.Ordinal)) continue;
            merged = merged.Length == 0 ? line : merged + "\n" + line;
        }
        return merged;
    }

    /// <summary>Normalises text for comparisons (case, accents, punctuation and spacing don't matter).</summary>
    public static string Normalized(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in Fold(text))
        {
            if (IsAlphanumeric(c)) builder.Append(c);
        }
        return builder.ToString();
    }

    /// <summary>Lowercase words for search (accents and width folded).</summary>
    public static IReadOnlyList<string> Tokens(string text) => Split(Fold(text));

    private static List<string> Split(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (var c in text)
        {
            if (IsAlphanumeric(c))
            {
                current.Append(c);
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    /// <summary>Lowercase words split on anything that isn't a letter, mark or number (no accent folding).</summary>
    public static IReadOnlyList<string> Words(string text) => Split(text.ToLowerInvariant());

    /// <summary>Lowercases and removes diacritics and full/half-width differences.</summary>
    private static string Fold(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(c);
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Letters, marks and numbers — the same set as Foundation's <c>CharacterSet.alphanumerics</c>.</summary>
    private static bool IsAlphanumeric(char c)
    {
        if (char.IsLetterOrDigit(c)) return true;
        var category = CharUnicodeInfo.GetUnicodeCategory(c);
        return category is UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
            or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;
    }

    /// <summary>Trims whitespace and punctuation, but keeps sentence punctuation and quotes (! ? . " ').</summary>
    private static string TrimEdges(string text)
    {
        static bool Trimmable(char c) =>
            char.IsWhiteSpace(c) || (char.IsPunctuation(c) && c is not ('!' or '?' or '.' or '"' or '\''));
        var start = 0;
        var end = text.Length;
        while (start < end && Trimmable(text[start])) start++;
        while (end > start && Trimmable(text[end - 1])) end--;
        return text[start..end];
    }
}
