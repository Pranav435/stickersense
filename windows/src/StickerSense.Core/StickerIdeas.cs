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
        var queryWords = Words(query);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>();
        var modelsOwn = new List<string>();
        var animal = 0;
        foreach (var picture in pictures)
        {
            var text = ListMarkerRegex().Replace(picture.Trim(), "").Trim().Trim('"', '.').Trim();
            if (text.Length == 0 || text.Length > 160 || AsksForLettering(text)) continue;
            // People nobody asked for: small models draw "a man in a blue shirt" for "good morning".
            var standIn = Words(text).Any(PeopleWords.Contains) && !queryWords.Any(PeopleWords.Contains);
            if (standIn) text = $"a cute {Animals[animal++ % Animals.Length]} {Mood(finalCaption.Length > 0 ? finalCaption : picturePart)}";
            if (!seen.Add(text)) continue;
            kept.Add(text);
            if (!standIn) modelsOwn.Add(text);
            if (kept.Count == Variations) break;
        }
        // A caption that only repeats a search naming what's drawn ("angry cat") isn't something said.
        if (quoted is null && finalCaption.Length > 0 && Words(finalCaption).SetEquals(queryWords)
            && queryWords.Any(w => w.Length > 2 && modelsOwn.Count(k => Words(k).Contains(w)) >= 3))
        {
            finalCaption = "";
        }
        var fallbackPicture = picturePart.Length == 0 ? Fallback(query).Pictures[0] : picturePart;
        while (kept.Count < Variations) kept.Add(kept.Count > 0 ? kept[0] : fallbackPicture);
        return new StickerIdea(ClampCaption(finalCaption), kept);
    }

    private static readonly HashSet<string> PeopleWords =
        ["person", "people", "man", "men", "woman", "women", "child", "children", "kid", "kids", "girl", "girls", "boy", "boys", "guy", "lady", "friends", "human", "humans"];

    private static readonly string[] Animals = ["cat", "dog", "bunny", "bear", "frog", "panda"];

    /// <summary>What a stand-in animal does: "saying good morning" draws gibberish, so it shows the mood instead.</summary>
    private static string Mood(string words) => words.Length == 0 ? "smiling" : $"feeling {words}";

    private static HashSet<string> Words(string text) =>
        text.ToLowerInvariant().Split(text.ToLowerInvariant().Where(c => !char.IsLetterOrDigit(c)).Distinct().ToArray(), StringSplitOptions.RemoveEmptyEntries).ToHashSet();

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
        return words.Any(LetteringWords.Contains) || lower.Contains("that says", StringComparison.Ordinal) || lower.Contains("sign saying", StringComparison.Ordinal)
            || picture.IndexOfAny(['"', '“', '”', '«', '»']) >= 0;
    }

    // MARK: The language model's part (same wording as the Mac)

    public const string Instructions =
        "You plan chat stickers. Someone typed a search for a sticker. You plan stickers that an image generator will draw, and the words to print on them.\n" +
        "caption: the words to print on the sticker, only when the search is something people say in chat, such as a greeting, reaction, reply or wish (\"thank you\", \"lol\", \"good morning\", \"happy birthday\", \"on my way\"). Copy those words exactly as the search has them, changing only capital letters. Leave it empty when the search only describes a picture, such as \"sleepy cat\" or \"pizza\".\n" +
        "pictures: different ideas for the picture, each a short phrase of at most 10 words: one cute character or thing and what it is doing, with at most one small prop, and no place, scene or background. Keep the character or thing the search names in every idea, and vary its pose, expression or prop. Only when the search names nobody, choose a cute animal that shows the meaning and mood of the search. Never ask for words, letters or signs in the picture.";

    public static string Request(string query) => $"The search: {query}";

    /// <summary>
    /// For models without structured output (Windows): the reply's form. The reply is begun with
    /// <see cref="ReplyStart"/> so a small model follows it.
    /// </summary>
    public static string LinesRequest(string query) =>
        "Reply in exactly the form of these examples. CAPTION is none unless the search is something people say.\n\n" +
        string.Join("\n\n", Examples.Select(e => $"{Request(e.Query)}\nCAPTION: {(e.Caption.Length > 0 ? e.Caption : "none")}\n" +
            string.Join("\n", e.Pictures.Select((p, i) => $"{i + 1}. {p}")))) +
        $"\n\nNow your turn.\n{Request(query)}";

    /// <summary>
    /// Worked examples for small models (Qwen3-VL 2B follows examples much better than rules):
    /// pictures without a caption ("none" is easier for them than an empty line), a phrase with
    /// one, and a reaction with no character named. Tried on Qwen3-VL 2B with sampling as in the app.
    /// </summary>
    internal static readonly IReadOnlyList<(string Query, string Caption, string[] Pictures)> Examples =
    [
        ("sleepy cat", "", ["a sleepy cat yawning", "a cat curled up asleep", "a drowsy cat hugging a pillow", "a cat in a nightcap rubbing its eyes"]),
        ("dog saying good morning", "good morning", ["a happy dog stretching", "a dog holding a coffee mug", "a dog waving one paw", "a smiling dog with a sun hat"]),
        ("pizza", "", ["a happy pizza slice with a face", "a pizza slice waving", "a cheesy pizza slice winking", "a round pizza dancing"]),
        ("lol", "lol", ["a cat laughing so hard it cries", "a frog rolling over laughing", "a dog giggling behind its paw", "a hamster holding its belly laughing"]),
    ];

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
