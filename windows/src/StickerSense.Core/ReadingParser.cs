using System.Text.RegularExpressions;

namespace StickerSense.Core;

/// <summary>Reads the model's reply back into a <see cref="StickerReading"/>, tolerating small format slips.</summary>
public static partial class ReadingParser
{
    [GeneratedRegex(@"^\s*(?:[-*•>#]+\s*)?\**\s*(KIND|TYPE|SUBJECT|MOTION|TEXT|MEANING|VIBE)\s*\**\s*[:：]\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex FieldLine();

    [GeneratedRegex(@"<\|[a-z_0-9]+\|>", RegexOptions.IgnoreCase)]
    private static partial Regex SpecialToken();

    // A label run into the previous line ("KIND: emoji, SUBJECT: smiling face"). Capitals only,
    // so ordinary words such as "text:" inside a subject aren't split off.
    [GeneratedRegex(@"[\s,;]+(?=(?:KIND|TYPE|SUBJECT|MOTION|TEXT|MEANING|VIBE)\s*[:：])")]
    private static partial Regex InlineLabel();

    // "MOTION: remains still" and the like: the model saw no change, so there's nothing to say.
    [GeneratedRegex(@"^\W*(none|no (change|changes|motion|movement)|nothing( changes| moves)?|(it )?(remains?|stays?) (still|the same|unchanged|static)|static|unchanged|does(n't| not) (move|change))\b", RegexOptions.IgnoreCase)]
    private static partial Regex NoMotion();

    [GeneratedRegex(@"^(sure|certainly|okay|ok|of course)\b[,!.]?\s*(here('s| is)[^:\n]*:\s*)?", RegexOptions.IgnoreCase)]
    private static partial Regex Filler();

    private static readonly HashSet<string> NoText = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "no text", "n/a", "na", "-", "—", "empty", "nothing", "no words", "there is no text", "no visible text", "(none)",
    };

    private static readonly Dictionary<string, StickerKind> KindSynonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["animation"] = StickerKind.Gif,
        ["animated"] = StickerKind.Gif,
        ["picture"] = StickerKind.Photo,
        ["image"] = StickerKind.Photo,
        ["photograph"] = StickerKind.Photo,
        ["drawing"] = StickerKind.Illustration,
        ["cartoon"] = StickerKind.Illustration,
        ["art"] = StickerKind.Illustration,
        ["emoticon"] = StickerKind.Emoji,
        ["smiley"] = StickerKind.Emoji,
        ["none"] = StickerKind.Nothing,
        ["empty"] = StickerKind.Nothing,
        ["blank"] = StickerKind.Nothing,
    };

    /// <summary>
    /// Parses a short reading. <paramref name="ocrText"/> is the cleaned OCR text (may be empty);
    /// <paramref name="chromeLabel"/> is the screen reader label when it describes a sticker
    /// (see <see cref="LabelClassifier.ChromeLabel"/>), so the sender's name isn't taken for sticker text.
    /// </summary>
    public static StickerReading Parse(string output, bool animated, string ocrText, string? chromeLabel = null)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unlabelled = new List<string>();
        foreach (var rawLine in InlineLabel().Replace(SpecialToken().Replace(output, "\n"), "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            var match = FieldLine().Match(line);
            if (match.Success)
            {
                var key = match.Groups[1].Value.ToUpperInvariant() switch
                {
                    "TYPE" => "KIND",
                    "VIBE" => "MEANING",
                    var other => other,
                };
                fields.TryAdd(key, Unquote(match.Groups[2].Value));
            }
            else
            {
                unlabelled.Add(line);
            }
        }

        var kind = ParseKind(fields.GetValueOrDefault("KIND"));
        var subject = fields.GetValueOrDefault("SUBJECT") ?? "";
        if (subject.Length == 0 && unlabelled.Count > 0)
        {
            // The model ignored the format: use its first sentence.
            subject = FirstSentence(Filler().Replace(string.Join(" ", unlabelled), ""));
        }
        // Small models run on past the word limits the prompt sets ("waves back and forth, then
        // stops, then waves back and forth…"): keep the spoken reading short.
        subject = Limit(subject, 12);
        var motion = Limit(fields.GetValueOrDefault("MOTION") ?? "", 10);
        if (NoMotion().IsMatch(motion)) motion = "";
        if (animated) subject = JoinMotion(subject, motion);

        return Reading(kind ?? (animated ? StickerKind.Gif : StickerKind.Sticker), subject,
            fields.GetValueOrDefault("MEANING") ?? "", animated, ocrText, chromeLabel);
    }

    /// <summary>
    /// Builds the reading. The sticker's words come straight from OCR (more exact than the model,
    /// and free): chat chrome and label text removed, so nothing is ever "read" that isn't there.
    /// </summary>
    internal static StickerReading Reading(StickerKind kind, string subject, string vibe, bool animated, string ocrText, string? chromeLabel = null)
    {
        if (animated && kind == StickerKind.Photo) kind = StickerKind.Gif;
        var text = StickerText.FromOcr(ocrText, chromeLabel);
        return new StickerReading(kind, CleanSubject(subject), text, NoText.Contains(vibe.Trim('.', ' ')) ? "" : vibe.Trim().TrimEnd('.'), animated);
    }

    internal static StickerKind? ParseKind(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var word = new string(value.Trim().TakeWhile(c => char.IsLetter(c)).ToArray());
        if (StickerKinds.TryParse(word, out var kind)) return kind;
        if (KindSynonyms.TryGetValue(word, out kind)) return kind;
        return StickerKind.Sticker;
    }

    public static string CleanSubject(string subject)
    {
        var s = subject.Trim();
        foreach (var prefix in new[] { "a sticker of ", "sticker of ", "this sticker shows ", "this image shows ", "the image shows ", "an image of ", "a gif of " })
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) s = s[prefix.Length..];
        }
        s = s.TrimEnd('.');
        return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    }

    /// <summary>"Cartoon cat" + "the cat waves, then winks" → "Cartoon cat, waves, then winks".</summary>
    public static string JoinMotion(string subject, string motion)
    {
        static string Clean(string value) => value.Trim().Trim('.').Trim();
        static string Bare(string word) => word.ToLowerInvariant().Replace("'s", "").Replace("’s", "").Trim(',', '.', '!', '?', ';', ':');
        string s = Clean(subject), m = Clean(motion);
        // "is waving" reads better as "waving" after the subject.
        foreach (var helper in new[] { "is ", "are ", "it is ", "they are " })
        {
            if (m.StartsWith(helper, StringComparison.OrdinalIgnoreCase) && m.Length > helper.Length) m = m[helper.Length..];
        }
        if (m.Length == 0) return s;
        if (s.Length == 0) return m;
        // Models often repeat the subject ("Cartoon cat" + "cat's mouth opens"): drop the repeat.
        var subjectWords = s.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim(',', '.', '!', '?', ';', ':')).ToHashSet();
        var motionWords = m.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var first = Bare(motionWords[0]);
        if (first is "the" or "a" or "an" or "it" or "its" || subjectWords.Contains(first))
        {
            var rest = motionWords.Skip(1).ToList();
            if (first is "the" or "a" or "an" && rest.Count > 0 && subjectWords.Contains(Bare(rest[0]))) rest = rest.Skip(1).ToList();
            if (rest.Count > 0) m = string.Join(" ", rest);
        }
        return $"{s}, {char.ToLowerInvariant(m[0])}{m[1..]}";
    }

    /// <summary>Complete sentences in a reply so far (ended by . ! or ? and a space or line break).</summary>
    public static int CompletedSentences(string reply) => SentenceEnd().Count(reply);

    [GeneratedRegex(@"[.!?][""”']?\s")]
    private static partial Regex SentenceEnd();

    /// <summary>Words on the last (unfinished) line of a reply so far.</summary>
    public static int LastLineWords(string reply)
    {
        var start = reply.LastIndexOf('\n') + 1;
        return reply[start..].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    /// <summary>Complete (newline-ended), non-empty lines in a reply so far.</summary>
    public static int CompletedLines(string reply)
    {
        var count = 0;
        var start = 0;
        for (var i = 0; i < reply.Length; i++)
        {
            if (reply[i] != '\n') continue;
            if (!string.IsNullOrWhiteSpace(reply[start..i])) count++;
            start = i + 1;
        }
        return count;
    }

    /// <summary>Tidies a detailed description: no markdown, no special tokens, no "Sure! Here's…".</summary>
    public static string CleanDetail(string output)
    {
        var text = SpecialToken().Replace(output, " ");
        text = text.Replace("**", "").Replace("__", "");
        var lines = text.Split('\n')
            .Select(l => Regex.Replace(l.Trim(), @"^(#+|[-*•]|\d+[.)])\s+", ""))
            .Where(l => l.Length > 0);
        text = Regex.Replace(string.Join(" ", lines), @"\s{2,}", " ").Trim();
        text = Filler().Replace(text, "").Trim();
        // Chatty openings ("I can help you with that. Let's go through the image…").
        text = Preamble().Replace(text, "").Trim();
        // Cut off at the token limit: end on the last whole sentence.
        if (text.Length > 0 && !".!?\"”')".Contains(text[^1]))
        {
            var end = text.LastIndexOfAny(['.', '!', '?']);
            if (end > 0) text = text[..(end + 1)];
        }
        return text;
    }

    [GeneratedRegex(@"^((I can help( you)? with that|I'd be happy to help|Certainly|Of course|Sure)[^.!]*[.!]\s*|Let'?s (go through|look at|take a look at|describe)[^.!:]*[.!:]\s*)+", RegexOptions.IgnoreCase)]
    private static partial Regex Preamble();

    /// <summary>
    /// At most <paramref name="words"/> words: cut after the last comma within the limit when
    /// there is one, so a list of clauses ends on a whole clause.
    /// </summary>
    internal static string Limit(string text, int words)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= words) return text.Trim();
        var kept = parts.Take(words).ToList();
        var lastComma = kept.FindLastIndex(w => w.EndsWith(','));
        if (lastComma >= 1) kept = kept.Take(lastComma + 1).ToList();
        return string.Join(' ', kept).TrimEnd(',', ';', ':');
    }

    private static string Unquote(string value)
    {
        var v = value.Trim().Trim('*', '`').Trim();
        if (v.Length >= 2 && ((v[0] == '"' && v[^1] == '"') || (v[0] == '“' && v[^1] == '”') || (v[0] == '\'' && v[^1] == '\'')))
        {
            v = v[1..^1].Trim();
        }
        return v;
    }

    private static string FirstSentence(string text)
    {
        var t = text.Trim();
        var end = t.IndexOfAny(['.', '!', '?']);
        return end > 0 ? t[..end] : t;
    }
}
