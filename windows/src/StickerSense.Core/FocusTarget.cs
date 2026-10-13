namespace StickerSense.Core;

/// <summary>A rectangle in physical screen pixels (top-left origin).</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public double CenterX => X + Width / 2.0;
    public double CenterY => Y + Height / 2.0;
    public long Area => (long)Math.Max(0, Width) * Math.Max(0, Height);
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>A square of <paramref name="side"/> pixels centred on a point.</summary>
    public static PixelRect Around(int x, int y, int side) => new(x - side / 2, y - side / 2, side, side);

    public PixelRect Inset(int by) =>
        Width > 2 * by && Height > 2 * by ? new PixelRect(X + by, Y + by, Width - 2 * by, Height - 2 * by) : this;

    public PixelRect Intersect(PixelRect other)
    {
        int left = Math.Max(X, other.X), top = Math.Max(Y, other.Y);
        int right = Math.Min(Right, other.Right), bottom = Math.Min(Bottom, other.Bottom);
        return right > left && bottom > top ? new PixelRect(left, top, right - left, bottom - top) : default;
    }

    public override string ToString() => $"({X}, {Y}, {Width}×{Height})";
}

/// <summary>
/// The item that has keyboard / screen-reader focus: where it is and what UI Automation says
/// about it, plus the foreground app it belongs to.
/// </summary>
public sealed record FocusTarget
{
    public required PixelRect Bounds { get; init; }

    /// <summary>The element's accessible name (often the alt text of an image).</summary>
    public string Name { get; init; } = "";

    /// <summary>Localized control type as the screen reader speaks it, e.g. "image".</summary>
    public string RoleText { get; init; } = "";

    /// <summary>UI Automation says this is an image element.</summary>
    public bool IsImage { get; init; }

    /// <summary>Foreground app's process name without ".exe", e.g. "whatsapp" or "chrome".</summary>
    public string AppName { get; init; } = "";

    /// <summary>The element belongs to StickerSense itself.</summary>
    public bool IsOwnApp { get; init; }

    /// <summary>Display scale of the monitor the item is on (1.0 = 96 DPI), to convert pixels to DIPs.</summary>
    public double DisplayScale { get; init; } = 1.0;

    /// <summary>
    /// The platform's handle for the element (a UI Automation element in the app), for the rare
    /// closer look when the label is ambiguous. Opaque to Core.
    /// </summary>
    public object? Element { get; init; }

    /// <summary>Roughly what a screen reader says for the item, e.g. "Sticker, image".</summary>
    public string Text => RoleText.Length == 0 || Name.Contains(RoleText, StringComparison.OrdinalIgnoreCase)
        ? Name
        : Name.Length == 0 ? RoleText : $"{Name}, {RoleText}";

    /// <summary>Two readings refer to the same item when they describe the same thing at the same spot.</summary>
    public bool IsSameItem(FocusTarget? other) =>
        other is not null
        && Text == other.Text
        && Math.Abs(Bounds.CenterX - other.Bounds.CenterX) < 3 && Math.Abs(Bounds.CenterY - other.Bounds.CenterY) < 3
        && Math.Abs(Bounds.Width - other.Bounds.Width) < 4 && Math.Abs(Bounds.Height - other.Bounds.Height) < 4;

    /// <summary>Same label and size, possibly scrolled to a different position.</summary>
    public bool IsLikelySameItem(FocusTarget? other) =>
        other is not null
        && Text == other.Text
        && Math.Abs(Bounds.Width - other.Bounds.Width) < 4 && Math.Abs(Bounds.Height - other.Bounds.Height) < 4;
}

/// <summary>Which apps auto-describe watches.</summary>
public enum AutoScope
{
    ChatApps,
    Everywhere,
}

public static class AutoScopes
{
    public static string Title(this AutoScope scope) => scope switch
    {
        AutoScope.ChatApps => "Chat apps and browsers",
        _ => "Every app",
    };
}

public enum StickerVerdict
{
    Sticker,
    Gif,
    NotSticker,
}

/// <summary>
/// Decides whether the focused item is a sticker or GIF worth describing automatically, from
/// what UI Automation delivered with the focus event (name, role, size), and which apps to watch.
/// </summary>
public static class StickerDetector
{
    /// <summary>Chat apps and browsers (for WhatsApp Web, Messenger, Instagram, Discord on the web…), by process name.</summary>
    public static readonly IReadOnlySet<string> ChatApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "whatsapp", "whatsapp.root", "telegram", "discord", "signal", "slack", "ms-teams", "teams", "messenger",
        "viber", "line", "kakaotalk", "wechat", "weixin", "skype", "element",
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc", "msedgewebview2",
    };

    /// <summary>Sticker or GIF according to the label, role and size (sizes in device-independent pixels).</summary>
    public static StickerVerdict Verdict(FocusTarget target)
    {
        var scale = target.DisplayScale > 0 ? target.DisplayScale : 1.0;
        return LabelClassifier.Classify(target.Text, target.Bounds.Width / scale, target.Bounds.Height / scale, target.IsImage);
    }

    /// <param name="alsoWatched">Extra process names to treat as chat apps (a test-only allowance, e.g. the self-test chat window).</param>
    public static bool IsWatched(string appName, AutoScope scope, IReadOnlyCollection<string>? alsoWatched = null) =>
        scope == AutoScope.Everywhere || ChatApps.Contains(appName)
        || (alsoWatched is not null && alsoWatched.Contains(appName, StringComparer.OrdinalIgnoreCase));
}
