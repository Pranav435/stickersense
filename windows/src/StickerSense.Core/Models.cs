using System.Text.Json;
using System.Text.Json.Serialization;

namespace StickerSense.Core;

/// <summary>What the model decided the described image is.</summary>
[JsonConverter(typeof(StickerKindJsonConverter))]
public enum StickerKind
{
    Sticker,
    Gif,
    Emoji,
    Meme,
    Photo,
    Illustration,
    Text,
    Nothing,
}

public static class StickerKinds
{
    public static IReadOnlyList<StickerKind> All { get; } = Enum.GetValues<StickerKind>();

    /// <summary>Kinds worth remembering in the sticker library so they can be found and re-sent later.</summary>
    public static bool IsCollectible(this StickerKind kind) => kind switch
    {
        StickerKind.Sticker or StickerKind.Gif or StickerKind.Emoji or StickerKind.Meme or StickerKind.Illustration => true,
        _ => false,
    };

    /// <summary>The lowercase name used in JSON and prompts ("sticker", "gif"…), same as the Swift raw values.</summary>
    public static string RawValue(this StickerKind kind) => kind.ToString().ToLowerInvariant();

    public static bool TryParse(string? value, out StickerKind kind)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(candidate.RawValue(), value?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                kind = candidate;
                return true;
            }
        }
        kind = StickerKind.Sticker;
        return false;
    }
}

internal sealed class StickerKindJsonConverter : JsonConverter<StickerKind>
{
    public override StickerKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        StickerKinds.TryParse(reader.GetString(), out var kind);
        return kind;
    }

    public override void Write(Utf8JsonWriter writer, StickerKind value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.RawValue());
}

/// <summary>A structured, short reading of one sticker / GIF / image.</summary>
public sealed record StickerReading
{
    public StickerReading(StickerKind kind, string subject, string text = "", string vibe = "", bool animated = false)
    {
        Kind = kind;
        Subject = subject;
        Text = text;
        Vibe = vibe;
        Animated = animated;
    }

    [JsonPropertyName("kind")]
    public StickerKind Kind { get; init; }

    /// <summary>What it shows in a few words; for animations, what happens.</summary>
    [JsonPropertyName("subject")]
    public string Subject { get; init; }

    /// <summary>Text written on the sticker, verbatim. Empty when there is none.</summary>
    [JsonPropertyName("text")]
    public string Text { get; init; }

    /// <summary>What someone means by sending it ("I don't know, whatever").</summary>
    [JsonPropertyName("vibe")]
    public string Vibe { get; init; }

    [JsonPropertyName("animated")]
    public bool Animated { get; init; }
}

/// <summary>Where a library sticker came from. Serialised like Swift's enum with associated values.</summary>
[JsonConverter(typeof(StickerSourceJsonConverter))]
public abstract record StickerSource
{
    private StickerSource() { }

    /// <summary>Seen on screen in a chat app (process name).</summary>
    public sealed record Received(string App) : StickerSource;

    /// <summary>Imported from the local WhatsApp Desktop data.</summary>
    public sealed record WhatsApp(string RelativePath) : StickerSource;

    /// <summary>A GIPHY GIF or sticker.</summary>
    public sealed record Giphy(string Id) : StickerSource;

    /// <summary>Added by hand (or shared in on another platform).</summary>
    public sealed record Imported : StickerSource;

    public string DisplayName => this switch
    {
        Received r => AppNames.FriendlyName(r.App),
        WhatsApp => "WhatsApp",
        Giphy => "GIPHY",
        _ => "Imported",
    };

    public SourceFilter FilterTag => this switch
    {
        WhatsApp => SourceFilter.WhatsApp,
        Giphy => SourceFilter.Giphy,
        _ => SourceFilter.Received,
    };
}

internal sealed class StickerSourceJsonConverter : JsonConverter<StickerSource>
{
    public override StickerSource Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Expected a sticker source object.");
        foreach (var property in root.EnumerateObject())
        {
            string Field(string name) =>
                property.Value.ValueKind == JsonValueKind.Object && property.Value.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
            switch (property.Name)
            {
                case "received": return new StickerSource.Received(Field("app"));
                case "whatsApp": return new StickerSource.WhatsApp(Field("relativePath"));
                case "giphy": return new StickerSource.Giphy(Field("id"));
                case "imported": return new StickerSource.Imported();
            }
        }
        return new StickerSource.Imported();
    }

    public override void Write(Utf8JsonWriter writer, StickerSource value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case StickerSource.Received r:
                writer.WriteStartObject("received");
                writer.WriteString("app", r.App);
                break;
            case StickerSource.WhatsApp w:
                writer.WriteStartObject("whatsApp");
                writer.WriteString("relativePath", w.RelativePath);
                break;
            case StickerSource.Giphy g:
                writer.WriteStartObject("giphy");
                writer.WriteString("id", g.Id);
                break;
            default:
                writer.WriteStartObject("imported");
                break;
        }
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}

public enum SourceFilter
{
    All,
    Received,
    WhatsApp,
    Giphy,
}

public static class SourceFilters
{
    public static string Title(this SourceFilter filter) => filter switch
    {
        SourceFilter.All => "All",
        SourceFilter.Received => "Received",
        SourceFilter.WhatsApp => "WhatsApp",
        _ => "GIPHY",
    };
}

/// <summary>Friendly names for the chat apps and browsers StickerSense sees stickers in.</summary>
public static class AppNames
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        // Windows process names (without .exe).
        ["whatsapp"] = "WhatsApp",
        ["whatsapp.root"] = "WhatsApp",
        ["telegram"] = "Telegram",
        ["discord"] = "Discord",
        ["signal"] = "Signal",
        ["slack"] = "Slack",
        ["ms-teams"] = "Microsoft Teams",
        ["teams"] = "Microsoft Teams",
        ["messenger"] = "Messenger",
        ["viber"] = "Viber",
        ["line"] = "LINE",
        ["kakaotalk"] = "KakaoTalk",
        ["wechat"] = "WeChat",
        ["weixin"] = "WeChat",
        ["skype"] = "Skype",
        ["element"] = "Element",
        ["chrome"] = "Google Chrome",
        ["msedge"] = "Microsoft Edge",
        ["firefox"] = "Firefox",
        ["brave"] = "Brave",
        ["opera"] = "Opera",
        ["vivaldi"] = "Vivaldi",
        ["arc"] = "Arc",
        // Bundle identifiers, so a library brought over from the Mac version reads nicely.
        ["net.whatsapp.WhatsApp"] = "WhatsApp",
        ["desktop.WhatsApp"] = "WhatsApp",
        ["com.apple.MobileSMS"] = "Messages",
        ["ru.keepcoder.Telegram"] = "Telegram",
        ["org.telegram.desktop"] = "Telegram",
        ["com.hnc.Discord"] = "Discord",
        ["org.whispersystems.signal-desktop"] = "Signal",
        ["com.tinyspeck.slackmacgap"] = "Slack",
        ["com.facebook.archon"] = "Messenger",
    };

    public static string FriendlyName(string processOrBundle) =>
        Known.TryGetValue(processOrBundle, out var name) ? name : processOrBundle;
}
