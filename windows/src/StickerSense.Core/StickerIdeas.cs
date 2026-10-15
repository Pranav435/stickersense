using System.Text.RegularExpressions;

namespace StickerSense.Core;

/// <summary>What to draw for a sticker search: a few picture ideas, and the words to write on them.</summary>
/// <param name="Caption">Words StickerSense writes on the sticker itself, since image models can't spell. Empty for none.</param>
/// <param name="Pictures">What to draw, one short description per variation.</param>
public sealed record StickerIdea(string Caption, IReadOnlyList<string> Pictures);

/// <summary>
/// Turning a picker search into prompts for the image model. The Mac app has the same rules
/// (StickerIdeas.swift), so a search makes the same kind of sticker on both.
/// </summary>
public static partial class StickerIdeas
{
    /// <summary>How many stickers one Generate makes.</summary>
    public const int Variations = 4;

    /// <summary>
    /// Added to every picture. Tried with SDXS and SD-Turbo: these words give one clean character
    /// on plain white that cuts out well. Saying "sticker" draws a round badge behind it, or a
    /// photo of a sticker on a table.
    /// </summary>
    public const string Style = "cute cartoon character illustration, thick black outline, flat vibrant colors, full body, centered on a plain white background";

    /// <summary>The longest caption written on a sticker; longer ones get too small to read.</summary>
    public const int MaxCaptionLength = 40;

    /// <summary>The image model's prompt for one picture idea.</summary>
    public static string Prompt(string picture) => $"{picture.Trim().Trim('.', ',').Trim()}, {Style}";

    [GeneratedRegex("[\"“”«»]([^\"“”«»]+)[\"“”«»]")]
    private static partial Regex QuotedRegex();

    [GeneratedRegex(@"^(\d+[.)]|[-*•])\s*")]
    private static partial Regex ListMarkerRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesRegex();

    /// <summary>
    /// Words in quotes are the caption, exactly as typed: <c>corgi "thank you"</c> gives the picture
    /// "corgi" and the caption "thank you". Straight and curly quotes both work.
    /// </summary>
    public static (string Picture, string? Caption) SplitQuoted(string query)
    {
        var match = QuotedRegex().Match(query);
        if (!match.Success) return (query.Trim(), null);
        var caption = match.Groups[1].Value.Trim();
        var picture = SpacesRegex().Replace(query.Remove(match.Index, match.Length), " ").Trim();
        return (picture, caption.Length == 0 ? null : caption);
    }

    /// <summary>
    /// Ideas without a language model: the search is the picture, and only quoted words become a
    /// caption. A caption-only search ("\"lol\"") draws a character reacting to it.
    /// </summary>
    public static StickerIdea Fallback(string query)
    {
        var (picture, caption) = SplitQuoted(query);
        var subject = picture.Length == 0 ? $"a cute cartoon character reacting to {caption ?? query}" : picture;
        return new StickerIdea(caption is null ? "" : ClampCaption(caption), Enumerable.Repeat(subject, Variations).ToList());
    }

    /// <summary>
    /// Tidies a language model's plan: a quoted caption wins; pictures are trimmed, deduplicated,
    /// stripped of requests for lettering, and topped up to <see cref="Variations"/> with the search itself.
    /// </summary>
    public static StickerIdea Cleaned(string caption, IEnumerable<string> pictures, string query)
    {
        var (picturePart, quoted) = SplitQuoted(query);
        var finalCaption = quoted ?? caption.Trim().Trim('"', '“', '”', '\'').Trim();
        if (finalCaption.ToLowerInvariant() is "none" or "empty" or "n/a" or "-" or "no caption" or "nothing") finalCaption = "";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>();
        foreach (var picture in pictures)
        {
            var text = ListMarkerRegex().Replace(picture.Trim(), "").Trim().Trim('"', '.').Trim();
            if (text.Length == 0 || text.Length > 160 || AsksForLettering(text) || !seen.Add(text)) continue;
            kept.Add(text);
            if (kept.Count == Variations) break;
        }
        var fallbackPicture = picturePart.Length == 0 ? Fallback(query).Pictures[0] : picturePart;
        while (kept.Count < Variations) kept.Add(kept.Count > 0 ? kept[0] : fallbackPicture);
        return new StickerIdea(ClampCaption(finalCaption), kept);
    }

    internal static string ClampCaption(string caption)
    {
        var flat = string.Join(" ", caption.Split('\n', '\r').Select(l => l.Trim()).Where(l => l.Length > 0));
        if (flat.Length <= MaxCaptionLength) return flat;
        var cut = flat[..MaxCaptionLength];
        var space = cut.LastIndexOf(' ');
        return space > 0 ? cut[..space] : cut;
    }

    private static readonly HashSet<string> LetteringWords = ["text", "letters", "lettering", "words", "written", "caption", "banner", "message", "note"];

    /// <summary>The image model would draw gibberish letters: lettering is StickerSense's job.</summary>
    internal static bool AsksForLettering(string picture)
    {
        var lower = picture.ToLowerInvariant();
        var words = lower.Split(lower.Where(c => !char.IsLetter(c)).Distinct().ToArray(), StringSplitOptions.RemoveEmptyEntries);
        return words.Any(LetteringWords.Contains) || lower.Contains("that says", StringComparison.Ordinal) || lower.Contains("sign saying", StringComparison.Ordinal);
    }

    // MARK: The language model's part (same wording as the Mac)

    public const string Instructions =
        "You plan chat stickers. Someone typed a search for a sticker. You plan stickers that an image generator will draw, and the words to print on them.\n" +
        "caption: the words to print on the sticker, only when the search is something people say in chat, such as a greeting, reaction, reply or wish (\"thank you\", \"lol\", \"good morning\", \"happy birthday\", \"on my way\"). Copy those words exactly as the search has them, changing only capital letters. Leave it empty when the search only describes a picture, such as \"sleepy cat\" or \"pizza\".\n" +
        "pictures: different ideas for the picture, each a short phrase of at most 15 words. Keep the character or thing the search names in every idea, and vary its pose, expression or prop. Only when the search names nobody, choose a cute animal or character that shows its meaning and mood. Never ask for words, letters or signs in the picture.";

    public static string Request(string query) => $"The search: {query}";

    /// <summary>
    /// For models without structured output (Windows): the reply's form. The reply is begun with
    /// <see cref="ReplyStart"/> so a small model follows it.
    /// </summary>
    public static string LinesRequest(string query) =>
        Request(query) + "\n\nReply in exactly this form, with nothing after the caption when there is none:\n" +
        $"CAPTION: <words to print>\n{string.Join("\n", Enumerable.Range(1, Variations).Select(n => $"{n}. <picture idea>"))}";

    public const string ReplyStart = "CAPTION:";

    /// <summary>The lines a full reply has: the caption and one per picture.</summary>
    public const int ReplyLines = Variations + 1;

    /// <summary>Reads a reply in the <see cref="LinesRequest"/> form (starting with <see cref="ReplyStart"/>).</summary>
    public static StickerIdea ParseLines(string reply, string query)
    {
        var caption = "";
        var pictures = new List<string>();
        foreach (var raw in reply.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("CAPTION:", StringComparison.OrdinalIgnoreCase))
            {
                caption = line["CAPTION:".Length..].Trim();
                if (caption.StartsWith('<')) caption = "";
            }
            else if (ListMarkerRegex().IsMatch(line))
            {
                pictures.Add(line);
            }
        }
        return Cleaned(caption, pictures, query);
    }
}
