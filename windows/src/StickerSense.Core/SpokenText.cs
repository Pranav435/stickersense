namespace StickerSense.Core;

/// <summary>
/// Turns readings into what the screen reader says. Kept short: the user hears this right
/// after the screen reader's own announcement of the item, so we don't repeat the obvious.
/// </summary>
public static class SpokenText
{
    public static string Short(StickerReading reading, bool includeVibe = false)
    {
        if (reading.Kind == StickerKind.Nothing)
        {
            return reading.Subject.Length == 0
                ? "No sticker or picture here."
                : $"No sticker here. {reading.Subject}.";
        }
        var parts = new List<string>();
        var prefix = KindPrefix(reading.Kind);
        if (reading.Kind == StickerKind.Text && reading.Text.Length > 0)
        {
            parts.Add($"{prefix}“{reading.Text}”.");
        }
        else
        {
            var line = prefix + reading.Subject;
            if (reading.Text.Length > 0) line += $", “{reading.Text}”";
            parts.Add(line + ".");
        }
        if (includeVibe && reading.Vibe.Length > 0)
        {
            parts.Add($"Meaning: {reading.Vibe}.");
        }
        return string.Join(" ", parts);
    }

    /// <summary>Searchable / listable one-liner used in the history and picker lists.</summary>
    public static string ListLabel(StickerReading reading)
    {
        var line = reading.Subject;
        if (reading.Text.Length > 0) line += $", “{reading.Text}”";
        if (reading.Animated) line += ", animated";
        return line;
    }

    internal static string KindPrefix(StickerKind kind) => kind switch
    {
        StickerKind.Gif => "GIF: ",
        StickerKind.Photo => "Photo: ",
        StickerKind.Text => "Text: ",
        _ => "",
    };

    /// <summary>"just now", "5 minutes ago", "yesterday", "Monday", "12 March".</summary>
    public static string RelativeTime(DateTimeOffset when, DateTimeOffset now)
    {
        var ago = now - when;
        if (ago < TimeSpan.FromMinutes(1)) return "just now";
        if (ago < TimeSpan.FromHours(1)) return Plural((int)ago.TotalMinutes, "minute") + " ago";
        var localWhen = when.ToLocalTime();
        var localNow = now.ToLocalTime();
        if (localWhen.Date == localNow.Date) return Plural((int)ago.TotalHours, "hour") + " ago";
        if (localWhen.Date == localNow.Date.AddDays(-1)) return "yesterday";
        if (ago < TimeSpan.FromDays(7)) return localWhen.ToString("dddd", System.Globalization.CultureInfo.CurrentCulture);
        return localWhen.ToString(localWhen.Year == localNow.Year ? "d MMMM" : "d MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture);
    }

    private static string Plural(int count, string unit) => $"{count} {unit}{(count == 1 ? "" : "s")}";

    /// <summary>"Hear what StickerSense can do": every shortcut, spelled out, and whether auto mode is on.</summary>
    public static string Help(string describe, string picker, string history, string autoToggle, bool colonTrigger, bool autoOn) =>
        $"StickerSense shortcuts. {describe}: describe what has focus; press it again within 3 seconds for more detail. " +
        $"{picker}: sticker picker, to find and paste a sticker. " +
        (colonTrigger ? "Or type two colons in a chat to open the picker. " : "") +
        $"{history}: history of described stickers. " +
        $"{autoToggle}: turn automatic descriptions {(autoOn ? "off" : "on")}. " +
        $"Automatic descriptions are {(autoOn ? "on" : "off")}: stickers and GIFs in chats are described when focus rests on them.";

    public static string Failure(Exception error) =>
        error is UserFacingException ? error.Message : "Couldn't describe it.";
}
