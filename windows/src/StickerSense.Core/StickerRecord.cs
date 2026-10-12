using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StickerSense.Core;

/// <summary>One sticker StickerSense has seen, imported or found. JSON field names match the Swift app.</summary>
public sealed record StickerRecord
{
    [JsonPropertyName("id")]
    [JsonConverter(typeof(UpperGuidJsonConverter))]
    public required Guid Id { get; init; }

    [JsonPropertyName("fingerprint")]
    public required Fingerprint Fingerprint { get; init; }

    /// <summary>Null until described (e.g. freshly imported WhatsApp stickers).</summary>
    [JsonPropertyName("reading")]
    public StickerReading? Reading { get; init; }

    [JsonPropertyName("detailed")]
    public string? Detailed { get; init; }

    [JsonPropertyName("ocrText")]
    public string OcrText { get; init; } = "";

    [JsonPropertyName("source")]
    public required StickerSource Source { get; init; }

    /// <summary>File name inside the library's <c>images</c> folder (only kept for collectible kinds).</summary>
    [JsonPropertyName("imageFile")]
    public string? ImageFile { get; init; }

    /// <summary>Original web address (GIPHY).</summary>
    [JsonPropertyName("remoteURL")]
    public string? RemoteUrl { get; init; }

    [JsonPropertyName("addedAt")]
    [JsonConverter(typeof(Iso8601JsonConverter))]
    public DateTimeOffset AddedAt { get; init; }

    [JsonPropertyName("lastSeenAt")]
    [JsonConverter(typeof(Iso8601JsonConverter))]
    public DateTimeOffset LastSeenAt { get; init; }

    [JsonPropertyName("timesSeen")]
    public int TimesSeen { get; init; }

    [JsonPropertyName("timesSent")]
    public int TimesSent { get; init; }

    [JsonIgnore]
    public bool IsDescribed => Reading is not null;

    [JsonIgnore]
    public bool IsCollectible => Reading?.Kind.IsCollectible() ?? true;

    /// <summary>What the screen reader reads for this row in lists.</summary>
    [JsonIgnore]
    public string ScreenReaderLabel => Reading is null
        ? $"Not described yet, from {Source.DisplayName}"
        : $"{SpokenText.ListLabel(Reading)}. From {Source.DisplayName}";
}

/// <summary>Swift's UUID encodes as uppercase text; keep the same so libraries stay interchangeable.</summary>
internal sealed class UpperGuidJsonConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Guid.Parse(reader.GetString() ?? throw new JsonException("Missing id."));

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("D").ToUpperInvariant());
}

/// <summary>
/// Swift's <c>.iso8601</c> date strategy: <c>2026-10-12T16:03:00Z</c>, no fractional seconds
/// (Swift refuses to decode dates that have them).
/// </summary>
internal sealed class Iso8601JsonConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTimeOffset.Parse(reader.GetString() ?? throw new JsonException("Missing date."), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
}
