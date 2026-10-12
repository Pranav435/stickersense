using System.Globalization;
using System.Net;
using System.Text.Json;

namespace StickerSense.Core;

public enum GiphyKind
{
    Gif,
    Sticker,
}

/// <param name="PreviewUrl">Animated preview (~200 px wide).</param>
/// <param name="OriginalUrl">Full-size animated GIF for sending.</param>
/// <param name="SmallUrl">Smallest animated rendition (~100 px), for describing and thumbnails: quicker to download.</param>
public sealed record GiphyItem(string Id, GiphyKind Kind, string Title, string AltText, string PreviewUrl, string OriginalUrl, string? SmallUrl = null)
{
    /// <summary>The picker's id for this result, e.g. "gif-Sticker-abc".</summary>
    public string Key => $"gif-{Kind}-{Id}";

    /// <summary>Best text GIPHY gives us, cleaned of the "GIF by …" boilerplate.</summary>
    public string CleanTitle
    {
        get
        {
            var t = AltText.Length == 0 ? Title : AltText;
            foreach (var marker in new[] { " GIF by ", " Sticker by ", " GIF", " Sticker" })
            {
                var index = t.IndexOf(marker, StringComparison.Ordinal);
                if (index >= 0) t = t[..index];
            }
            t = t.Trim();
            return t.Length == 0 ? (Kind == GiphyKind.Sticker ? "Sticker" : "GIF") : t;
        }
    }
}

/// <summary>One page of search results.</summary>
public sealed record GiphyPage(IReadOnlyList<GiphyItem> Items, int NextOffset, int TotalCount)
{
    public bool HasMore => NextOffset < TotalCount && Items.Count > 0;
}

public enum GiphyErrorKind
{
    MissingKey,
    InvalidKey,
    BadResponse,
}

public sealed class GiphyException : UserFacingException
{
    public GiphyException(GiphyErrorKind kind, int statusCode = 0, Exception? inner = null)
        : base(MessageFor(kind, statusCode), inner)
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    public GiphyErrorKind Kind { get; }
    public int StatusCode { get; }

    private static string MessageFor(GiphyErrorKind kind, int statusCode) => kind switch
    {
        GiphyErrorKind.MissingKey => "Add your GIPHY API key in Settings to search GIFs.",
        GiphyErrorKind.InvalidKey => "GIPHY didn't accept the API key. Check it in Settings.",
        _ => $"GIPHY search failed (error {statusCode}).",
    };
}

/// <summary>Minimal GIPHY search client (https://developers.giphy.com/docs/api/endpoint#search).</summary>
public sealed class GiphyClient
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly HttpClient http;

    public GiphyClient(string apiKey, string rating = "pg-13", HttpClient? http = null)
    {
        ApiKey = apiKey;
        Rating = rating;
        this.http = http ?? SharedHttp;
    }

    public string ApiKey { get; }
    public string Rating { get; }

    /// <summary>One page of results. GIPHY allows up to 50 per request; <paramref name="offset"/> pages through the rest.</summary>
    public async Task<GiphyPage> SearchAsync(string query, GiphyKind kind, int limit = 25, int offset = 0, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ApiKey)) throw new GiphyException(GiphyErrorKind.MissingKey);
        using var response = await http.GetAsync(SearchUri(query, kind, limit, offset), ct).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new GiphyException(GiphyErrorKind.InvalidKey, status);
        if (response.StatusCode != HttpStatusCode.OK) throw new GiphyException(GiphyErrorKind.BadResponse, status);
        var data = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        try
        {
            return ParsePage(data, kind, offset);
        }
        catch (JsonException e)
        {
            throw new GiphyException(GiphyErrorKind.BadResponse, status, e);
        }
    }

    internal Uri SearchUri(string query, GiphyKind kind, int limit, int offset = 0)
    {
        var endpoint = kind == GiphyKind.Gif ? "gifs" : "stickers";
        var language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        if (language.Length != 2) language = "en";
        var parameters = new[]
        {
            ("api_key", ApiKey),
            ("q", query),
            ("limit", Math.Clamp(limit, 1, 50).ToString(CultureInfo.InvariantCulture)),
            ("offset", Math.Max(0, offset).ToString(CultureInfo.InvariantCulture)),
            ("rating", Rating),
            ("lang", language),
        };
        var queryString = string.Join("&", parameters.Select(p => $"{p.Item1}={Uri.EscapeDataString(p.Item2)}"));
        return new Uri($"https://api.giphy.com/v1/{endpoint}/search?{queryString}");
    }

    /// <summary>Items plus GIPHY's <c>pagination</c> (total_count, count) as the next offset.</summary>
    internal static GiphyPage ParsePage(ReadOnlySpan<byte> json, GiphyKind kind, int offset)
    {
        var reader = new Utf8JsonReader(json);
        using var doc = JsonDocument.ParseValue(ref reader);
        var items = ParseItems(doc.RootElement, kind);
        int? count = null, total = null;
        if (doc.RootElement.TryGetProperty("pagination", out var pagination) && pagination.ValueKind == JsonValueKind.Object)
        {
            if (pagination.TryGetProperty("count", out var c) && c.TryGetInt32(out var countValue)) count = countValue;
            if (pagination.TryGetProperty("total_count", out var t) && t.TryGetInt32(out var totalValue)) total = totalValue;
        }
        var received = count ?? items.Count;
        return new GiphyPage(items, offset + received, total ?? offset + received);
    }

    internal static IReadOnlyList<GiphyItem> Parse(ReadOnlySpan<byte> json, GiphyKind kind)
    {
        var reader = new Utf8JsonReader(json);
        using var doc = JsonDocument.ParseValue(ref reader);
        return ParseItems(doc.RootElement, kind);
    }

    private static IReadOnlyList<GiphyItem> ParseItems(JsonElement root, GiphyKind kind)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("GIPHY response has no data array.");
        }
        var items = new List<GiphyItem>();
        foreach (var entry in data.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var id = String(entry, "id");
            if (string.IsNullOrEmpty(id)) continue;
            entry.TryGetProperty("images", out var images);
            var preview = Rendition(images, "fixed_width") ?? Rendition(images, "fixed_width_small") ?? Rendition(images, "downsized");
            var original = Rendition(images, "downsized") ?? Rendition(images, "original") ?? preview;
            if (!IsHttpUrl(preview) || !IsHttpUrl(original)) continue;
            var small = Rendition(images, "fixed_width_downsampled") ?? Rendition(images, "fixed_width_small");
            items.Add(new GiphyItem(id, kind, String(entry, "title") ?? "", String(entry, "alt_text") ?? "", preview!, original!,
                IsHttpUrl(small) ? small : null));
        }
        return items;
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Rendition(JsonElement images, string name) =>
        images.ValueKind == JsonValueKind.Object && images.TryGetProperty(name, out var rendition) ? String(rendition, "url") : null;

    private static bool IsHttpUrl(string? url) =>
        url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}
