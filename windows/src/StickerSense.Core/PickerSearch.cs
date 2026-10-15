using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace StickerSense.Core;

/// <summary>
/// The picker's search. The user's own stickers come back instantly from the local library;
/// GIPHY stickers and GIFs come in pages of 50 (25 of each, interleaved). GIPHY results are
/// described on device — GIPHY's own titles ("GIF by Disney") rarely say what's in the picture —
/// and those descriptions are kept on disk (giphy-descriptions.json), so a popular GIF is only
/// ever described once. Thread-safe.
/// </summary>
public sealed class PickerSearch : IDisposable
{
    public const int PerKind = 25;
    private const int KeptDescriptions = 3000;

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly StickerPipeline pipeline;
    private readonly Func<string, GiphyClient> clientFor;
    private readonly HttpClient http;
    private readonly string downloadDirectory;
    private readonly string cachePath;
    private readonly object gate = new();
    private readonly Dictionary<string, CachedReading> readings;
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> downloads = new();
    private readonly SemaphoreSlim downloadSlots = new(6, 6);
    private readonly ITimer saveTimer;

    /// <param name="pipeline">Background-priority pipeline used to describe GIPHY results.</param>
    /// <param name="clientFor">Makes a GIPHY client for an API key (tests pass a fake HTTP handler).</param>
    /// <param name="http">Downloads previews and files.</param>
    public PickerSearch(StickerPipeline pipeline, string downloadDirectory, Func<string, GiphyClient>? clientFor = null, HttpClient? http = null, TimeProvider? clock = null)
    {
        this.pipeline = pipeline;
        this.downloadDirectory = downloadDirectory;
        this.http = http ?? SharedHttp;
        this.clientFor = clientFor ?? (key => new GiphyClient(key, http: this.http));
        cachePath = Path.Combine(pipeline.Library.Directory, "giphy-descriptions.json");
        readings = Load(cachePath);
        saveTimer = (clock ?? TimeProvider.System).CreateTimer(_ => SaveNow(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public StickerLibrary Library => pipeline.Library;

    private sealed record CachedReading(
        [property: JsonPropertyName("reading")] StickerReading Reading,
        [property: JsonPropertyName("at")][property: JsonConverter(typeof(Iso8601JsonConverter))] DateTimeOffset At);

    // MARK: Searching

    /// <summary>The user's own stickers — local and instant.</summary>
    public IReadOnlyList<StickerRecord> LibraryResults(string query, SourceFilter filter) =>
        filter == SourceFilter.Giphy ? [] : Library.Search(query.Trim(), filter, limit: 300);

    public sealed record GiphyResults(IReadOnlyList<GiphyItem> Items, int NextGifOffset, int NextStickerOffset, bool HasMore, string Note);

    /// <summary>One page of GIPHY results: up to <paramref name="perKind"/> stickers and as many GIFs, interleaved.</summary>
    public async Task<GiphyResults> GiphyPageAsync(string query, string apiKey, int gifOffset = 0, int stickerOffset = 0, int perKind = PerKind, CancellationToken ct = default)
    {
        query = query.Trim();
        if (query.Length == 0) return new GiphyResults([], 0, 0, false, "");
        if (string.IsNullOrWhiteSpace(apiKey)) return new GiphyResults([], 0, 0, false, new GiphyException(GiphyErrorKind.MissingKey).Message);

        var client = clientFor(apiKey);
        var gifTask = TrySearch(client, query, GiphyKind.Gif, perKind, gifOffset, ct);
        var stickerTask = TrySearch(client, query, GiphyKind.Sticker, perKind, stickerOffset, ct);
        var (gifs, gifError) = await gifTask.ConfigureAwait(false);
        var (stickers, stickerError) = await stickerTask.ConfigureAwait(false);
        if (gifs is null && stickers is null)
        {
            var error = gifError ?? stickerError;
            return new GiphyResults([], gifOffset, stickerOffset, false, error is GiphyException ? error.Message : "GIPHY search failed.");
        }

        var g = gifs?.Items ?? [];
        var s = stickers?.Items ?? [];
        var items = new List<GiphyItem>();
        for (var i = 0; i < Math.Max(g.Count, s.Count); i++)
        {
            if (i < s.Count) items.Add(s[i]);
            if (i < g.Count) items.Add(g[i]);
        }
        // Start fetching the small renditions now, so describing them doesn't wait on the network.
        Prefetch(items);
        return new GiphyResults(items, gifs?.NextOffset ?? gifOffset, stickers?.NextOffset ?? stickerOffset,
            (gifs?.HasMore ?? false) || (stickers?.HasMore ?? false), "");
    }

    private static async Task<(GiphyPage? Page, Exception? Error)> TrySearch(GiphyClient client, string query, GiphyKind kind, int limit, int offset, CancellationToken ct)
    {
        try
        {
            return (await client.SearchAsync(query, kind, limit, offset, ct).ConfigureAwait(false), null);
        }
        catch (Exception e) when (e is GiphyException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (null, e);
        }
    }

    // MARK: Descriptions

    /// <summary>The on-device description of a GIPHY result, if it has one.</summary>
    public StickerReading? Reading(GiphyItem gif)
    {
        lock (gate) return readings.GetValueOrDefault(gif.Key)?.Reading;
    }

    /// <summary>
    /// Describes a GIPHY result on device from its small rendition (at most 3 frames, OCR on the
    /// first), and remembers the description on disk. Null when it couldn't be described.
    /// </summary>
    public async Task<StickerReading?> DescribeAsync(GiphyItem gif, CancellationToken ct = default)
    {
        if (Reading(gif) is { } known) return known;
        var path = await SmallFileAsync(gif).ConfigureAwait(false);
        if (path is null) return null;
        List<Image<Rgba32>>? frames = null;
        try
        {
            frames = await Task.Run(() => LoadDistinct(path, 3), ct).ConfigureAwait(false);
            var ocr = await OcrFirstFrameAsync(frames[0], ct).ConfigureAwait(false);
            var reading = await pipeline.Describer.ReadAsync(frames, DescriptionContext.File, ocr, pipeline.Priority, ct).ConfigureAwait(false);
            lock (gate) readings[gif.Key] = new CachedReading(reading, DateTimeOffset.UtcNow);
            saveTimer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
            return reading;
        }
        catch (Exception e) when (e is DescribeException or ImagePrepException)
        {
            return null;
        }
        finally
        {
            if (frames is not null) foreach (var frame in frames) frame.Dispose();
        }
    }

    /// <summary>A detailed description of a GIPHY result, from its larger preview.</summary>
    public async Task<string> DetailAsync(GiphyItem gif, IStickerDescriber describer, CancellationToken ct = default)
    {
        var path = await DownloadAsync(gif.PreviewUrl, $"preview-{gif.Id}.gif").ConfigureAwait(false)
            ?? throw DescribeException.Failed("the download failed");
        List<Image<Rgba32>> frames;
        try
        {
            frames = await Task.Run(() => LoadDistinct(path, 3), ct).ConfigureAwait(false);
        }
        catch (ImagePrepException)
        {
            throw DescribeException.Failed("GIPHY sent an unreadable file");
        }
        try
        {
            var ocr = await OcrFirstFrameAsync(frames[0], ct).ConfigureAwait(false);
            return await describer.DetailedAsync(frames, DescriptionContext.File, ocr, Reading(gif), DescribePriority.Foreground, ct).ConfigureAwait(false);
        }
        finally
        {
            foreach (var frame in frames) frame.Dispose();
        }
    }

    private async Task<string> OcrFirstFrameAsync(Image<Rgba32> frame, CancellationToken ct)
    {
        using var flat = ImagePrep.Flattened(frame);
        return StickerText.TextOf(await pipeline.Ocr.ReadLinesAsync(flat, ct).ConfigureAwait(false), null);
    }

    private static List<Image<Rgba32>> LoadDistinct(string path, int maxFrames)
    {
        var frames = ImagePrep.LoadFrames(path, maxFrames);
        var kept = ImagePrep.DistinctFrames(frames);
        foreach (var frame in frames.Except(kept)) frame.Dispose();
        return kept;
    }

    // MARK: Files

    /// <summary>The small rendition on disk (for describing and thumbnails).</summary>
    public Task<string?> SmallFileAsync(GiphyItem gif) => DownloadAsync(gif.SmallUrl ?? gif.PreviewUrl, $"small-{gif.Id}.gif");

    /// <summary>The full GIF on disk, for sending or copying.</summary>
    public Task<string?> OriginalFileAsync(GiphyItem gif) => DownloadAsync(gif.OriginalUrl, $"{gif.Id}.gif");

    /// <summary>Records that the user sent a GIPHY result, adding it (and its description) to their library.</summary>
    public async Task MarkSentAsync(GiphyItem gif)
    {
        if (Reading(gif) is not { } reading) return;
        var path = await OriginalFileAsync(gif).ConfigureAwait(false);
        if (path is null) return;
        await Task.Run(() =>
        {
            try
            {
                // Up to 16 frames, so the GIF is recognised later from whichever frame the chat shows.
                var frames = ImagePrep.LoadFrames(path, maxFrames: 16);
                try
                {
                    var stored = Library.Remember(frames, new Fingerprint(frames[0]), reading, "", new StickerSource.Giphy(gif.Id), gif.OriginalUrl);
                    Library.MarkSent(stored.Id);
                }
                finally
                {
                    foreach (var frame in frames) frame.Dispose();
                }
            }
            catch (ImagePrepException)
            {
            }
        }).ConfigureAwait(false);
    }

    // MARK: Generated stickers

    /// <summary>
    /// Describes a sticker just made, like any other: what the describer sees, plus the caption
    /// StickerSense wrote on it (exact, so it isn't left to OCR). Null when it couldn't be described.
    /// </summary>
    public async Task<StickerReading?> DescribeAsync(GeneratedSticker sticker, CancellationToken ct = default)
    {
        List<Image<Rgba32>>? frames = null;
        try
        {
            frames = await Task.Run(() => ImagePrep.LoadFrames(sticker.FilePath, 1), ct).ConfigureAwait(false);
            var reading = await pipeline.Describer.ReadAsync(frames, DescriptionContext.File, sticker.Caption, DescribePriority.Foreground, ct).ConfigureAwait(false);
            return reading with { Text = sticker.Caption };
        }
        catch (Exception e) when (e is DescribeException or ImagePrepException or IOException)
        {
            return null;
        }
        finally
        {
            if (frames is not null) foreach (var frame in frames) frame.Dispose();
        }
    }

    public async Task<string> DetailAsync(GeneratedSticker sticker, IStickerDescriber describer, CancellationToken ct = default)
    {
        List<Image<Rgba32>> frames;
        try
        {
            frames = await Task.Run(() => ImagePrep.LoadFrames(sticker.FilePath, 1), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is ImagePrepException or IOException)
        {
            throw DescribeException.Failed("the sticker is no longer available");
        }
        try
        {
            return await describer.DetailedAsync(frames, DescriptionContext.File, sticker.Caption, sticker.Reading, DescribePriority.Foreground, ct).ConfigureAwait(false);
        }
        finally
        {
            foreach (var frame in frames) frame.Dispose();
        }
    }

    /// <summary>Records that the user sent a sticker they made, adding it to their library.</summary>
    public Task MarkSentAsync(GeneratedSticker sticker) => Task.Run(() =>
    {
        try
        {
            var frames = ImagePrep.LoadFrames(sticker.FilePath, 1);
            try
            {
                var reading = sticker.Reading ?? new StickerReading(StickerKind.Sticker, sticker.Picture, sticker.Caption);
                // It's a sticker whatever the describer thought, so the library keeps its picture.
                if (!reading.Kind.IsCollectible()) reading = reading with { Kind = StickerKind.Sticker };
                var stored = Library.Remember(frames, new Fingerprint(frames[0]), reading, sticker.Caption, new StickerSource.Generated(sticker.Query));
                Library.MarkSent(stored.Id);
            }
            finally
            {
                foreach (var frame in frames) frame.Dispose();
            }
        }
        catch (Exception e) when (e is ImagePrepException or IOException)
        {
        }
    });

    /// <summary>Downloads the small renditions of a page in the background, six at a time.</summary>
    public void Prefetch(IEnumerable<GiphyItem> gifs)
    {
        foreach (var gif in gifs.Take(60)) _ = SmallFileAsync(gif);
    }

    /// <summary>One download per file (concurrent callers share it), at most six at once.</summary>
    private Task<string?> DownloadAsync(string url, string name)
    {
        var lazy = downloads.GetOrAdd(name, _ => new Lazy<Task<string?>>(() => FetchAsync(url, name)));
        var task = lazy.Value;
        // A failed download may be retried later.
        _ = task.ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully || t.Result is null) downloads.TryRemove(new KeyValuePair<string, Lazy<Task<string?>>>(name, lazy));
        }, TaskScheduler.Default);
        return task;
    }

    private async Task<string?> FetchAsync(string url, string name)
    {
        var destination = Path.Combine(downloadDirectory, name);
        if (File.Exists(destination)) return destination;
        await downloadSlots.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(downloadDirectory);
            var bytes = await http.GetByteArrayAsync(url).ConfigureAwait(false);
            var temp = destination + ".part";
            await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);
            File.Move(temp, destination, overwrite: true);
            return destination;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            downloadSlots.Release();
        }
    }

    // MARK: Labels

    /// <summary>What the screen reader reads for a GIPHY row.</summary>
    public static string Label(GiphyItem gif, StickerReading? reading)
    {
        var kind = gif.Kind == GiphyKind.Sticker ? "GIPHY sticker" : "GIF";
        return reading is not null ? $"{kind}: {SpokenText.ListLabel(reading)}" : $"{kind}: {gif.CleanTitle}, from GIPHY";
    }

    public const string LoadMoreLabel = "More GIPHY results";

    /// <summary>
    /// The status line announced after a search, e.g. "3 of yours and 50 from GIPHY. First: Cat.
    /// Press Enter to paste the first one…".
    /// </summary>
    public static string ResultsSummary(int libraryCount, int giphyCount, string query, string? firstLabel, string note, string sendHint, bool giphyPending = false)
    {
        var suffix = note.Length == 0 ? "" : " " + note;
        var total = libraryCount + giphyCount;
        if (total == 0)
        {
            if (giphyPending) return "Searching GIPHY…";
            return query.Trim().Length == 0
                ? "No stickers yet. Stickers you hear described, and imported stickers, appear here." + suffix
                : $"Nothing found for {query.Trim()}." + suffix;
        }
        var counts = giphyCount > 0 && libraryCount > 0 ? $"{libraryCount} of yours and {giphyCount} from GIPHY"
            : giphyCount > 0 ? $"{giphyCount} from GIPHY"
            : $"{libraryCount} result{(libraryCount == 1 ? "" : "s")}";
        if (giphyPending) counts += ", GIPHY still loading";
        return $"{counts}. First: {firstLabel ?? ""}. {sendHint}" + suffix;
    }

    // MARK: Persistence

    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static Dictionary<string, CachedReading> Load(string path)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<Dictionary<string, CachedReading>>(File.ReadAllBytes(path), JsonOptions) ?? [];
        }
        catch (Exception e) when (e is JsonException or IOException or NotSupportedException or FormatException)
        {
        }
        return [];
    }

    /// <summary>Writes the descriptions, keeping the newest 3000.</summary>
    public void SaveNow()
    {
        Dictionary<string, CachedReading> snapshot;
        lock (gate)
        {
            if (readings.Count > KeptDescriptions)
            {
                foreach (var old in readings.OrderByDescending(r => r.Value.At).Skip(KeptDescriptions).Select(r => r.Key).ToList()) readings.Remove(old);
            }
            snapshot = new Dictionary<string, CachedReading>(readings);
        }
        try
        {
            var temp = cachePath + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions));
            File.Move(temp, cachePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal void Remember(GiphyItem gif, StickerReading reading, DateTimeOffset at)
    {
        lock (gate) readings[gif.Key] = new CachedReading(reading, at);
    }

    internal int DescriptionCount
    {
        get { lock (gate) return readings.Count; }
    }

    public void Dispose()
    {
        saveTimer.Dispose();
        SaveNow();
    }
}
