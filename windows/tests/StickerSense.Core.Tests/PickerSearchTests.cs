using System.Net;
using System.Text;
using SixLabors.ImageSharp;

namespace StickerSense.Core.Tests;

public class PickerSearchTests
{
    /// <summary>Serves GIPHY search pages and small GIF files, counting concurrent downloads.</summary>
    private sealed class FakeGiphy : HttpMessageHandler
    {
        private readonly byte[] gif = ImagePrep.GifData(Fixtures.AnimatedFrames().Select(f => Fixtures.OnScreenCapture(f, 0.25)).ToList());
        private int active;
        public int MaxActive;
        public int Downloads;
        public int Total = 120;
        public List<string> Searches { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "api.giphy.com")
            {
                lock (Searches) Searches.Add(uri.PathAndQuery);
                var kind = uri.AbsolutePath.Contains("stickers") ? "s" : "g";
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                int offset = int.Parse(query["offset"]!), limit = int.Parse(query["limit"]!);
                var count = Math.Max(0, Math.Min(limit, Total - offset));
                var data = Enumerable.Range(offset, count).Select(i => new
                {
                    id = $"{kind}{i}",
                    title = $"Cat {i}",
                    images = new
                    {
                        fixed_width = new { url = $"https://media.test/{kind}{i}/200w.gif" },
                        fixed_width_downsampled = new { url = $"https://media.test/{kind}{i}/100w.gif" },
                    },
                });
                var json = System.Text.Json.JsonSerializer.Serialize(new { data, pagination = new { total_count = Total, count, offset } });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }
            var now = Interlocked.Increment(ref active);
            lock (this) MaxActive = Math.Max(MaxActive, now);
            await Task.Delay(30, cancellationToken);
            Interlocked.Decrement(ref active);
            Interlocked.Increment(ref Downloads);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(gif) };
        }
    }

    private static (PickerSearch Search, FakeDescriber Describer, FakeGiphy Giphy, StickerLibrary Library) Make(string? libraryDir = null)
    {
        var giphy = new FakeGiphy();
        var http = new HttpClient(giphy);
        var library = new StickerLibrary(libraryDir ?? Fixtures.TempDirectory());
        var describer = new FakeDescriber();
        var pipeline = new StickerPipeline(describer, library, new FakeOcr("hi!"), DescribePriority.Background);
        var search = new PickerSearch(pipeline, Fixtures.TempDirectory(), key => new GiphyClient(key, http: http), http);
        return (search, describer, giphy, library);
    }

    [Fact]
    public async Task PagesInterleaveStickersAndGifs()
    {
        var (search, _, giphy, library) = Make();
        using var _l = library;
        var page = await search.GiphyPageAsync("cat", "key");
        Assert.Equal(50, page.Items.Count);
        Assert.Equal(["s0", "g0", "s1", "g1"], page.Items.Take(4).Select(i => i.Id));
        Assert.Equal(25, page.NextGifOffset);
        Assert.Equal(25, page.NextStickerOffset);
        Assert.True(page.HasMore);
        Assert.Equal("", page.Note);

        var next = await search.GiphyPageAsync("cat", "key", page.NextGifOffset, page.NextStickerOffset);
        Assert.Equal("s25", next.Items[0].Id);
        Assert.Contains(giphy.Searches, q => q.Contains("offset=25") && q.Contains("limit=25"));

        giphy.Total = 60;
        var last = await search.GiphyPageAsync("cat", "key", 50, 50);
        Assert.Equal(20, last.Items.Count);
        Assert.False(last.HasMore);
    }

    [Fact]
    public async Task ExplainsMissingKeysAndSkipsEmptyQueries()
    {
        var (search, _, giphy, library) = Make();
        using var _l = library;
        Assert.Equal("Add your GIPHY API key in Settings to search GIFs.", (await search.GiphyPageAsync("cat", "")).Note);
        Assert.Empty((await search.GiphyPageAsync("  ", "key")).Items);
        Assert.Empty(giphy.Searches);
    }

    [Fact]
    public async Task DownloadsSixAtATime()
    {
        var (search, _, giphy, library) = Make();
        using var _l = library;
        var page = await search.GiphyPageAsync("cat", "key"); // prefetches the small renditions
        await Eventually.True(() => giphy.Downloads >= page.Items.Count, "all previews to download");
        Assert.InRange(giphy.MaxActive, 2, 6);
        // Already on disk: no new download.
        Assert.NotNull(await search.SmallFileAsync(page.Items[0]));
        Assert.Equal(page.Items.Count, giphy.Downloads);
    }

    [Fact]
    public async Task DescribesFromTheSmallRenditionAndRemembers()
    {
        var dir = Fixtures.TempDirectory();
        GiphyItem gif;
        var (search, describer, _, library) = Make(dir);
        using (library)
        using (search)
        {
            gif = (await search.GiphyPageAsync("cat", "key")).Items[0];
            var reading = await search.DescribeAsync(gif);
            Assert.NotNull(reading);
            Assert.InRange(describer.LastFrameCount, 1, 3);
            Assert.Equal("hi!", describer.LastOcr); // OCR of the first frame
            Assert.Same(reading, search.Reading(gif));
            Assert.Equal("GIPHY sticker: Shiba dog shrugging, “hi!”, animated", PickerSearch.Label(gif, reading));
            Assert.Equal(1, describer.Reads);
            await search.DescribeAsync(gif); // known: not described again
            Assert.Equal(1, describer.Reads);
        }
        Assert.True(File.Exists(Path.Combine(dir, "giphy-descriptions.json")));
        var (again, _, _, library2) = Make(dir);
        using (library2) Assert.NotNull(again.Reading(gif));
        Assert.Equal("GIF: Cat 3, from GIPHY", PickerSearch.Label(new GiphyItem("g3", GiphyKind.Gif, "Cat 3 GIF by Pets", "", "https://x", "https://y"), null));
    }

    [Fact]
    public void KeepsTheNewest3000Descriptions()
    {
        var dir = Fixtures.TempDirectory();
        var (search, _, _, library) = Make(dir);
        var start = DateTimeOffset.UtcNow.AddDays(-1);
        for (var i = 0; i < 3005; i++)
        {
            search.Remember(new GiphyItem($"g{i}", GiphyKind.Gif, "", "", "https://x", "https://y"), new StickerReading(StickerKind.Gif, $"Cat {i}"), start.AddSeconds(i));
        }
        search.SaveNow();
        library.Dispose();
        var (reloaded, _, _, library2) = Make(dir);
        using var _l = library2;
        Assert.Equal(3000, reloaded.DescriptionCount);
        Assert.Null(reloaded.Reading(new GiphyItem("g0", GiphyKind.Gif, "", "", "https://x", "https://y")));
        Assert.NotNull(reloaded.Reading(new GiphyItem("g3004", GiphyKind.Gif, "", "", "https://x", "https://y")));
    }

    [Fact]
    public async Task SendingAGiphyResultAddsItToTheLibrary()
    {
        var (search, _, _, library) = Make();
        using var _l = library;
        var gif = (await search.GiphyPageAsync("cat", "key")).Items[1];
        await search.DescribeAsync(gif);
        await search.MarkSentAsync(gif);
        var stored = library.Search("", SourceFilter.Giphy).Single();
        Assert.Equal(1, stored.TimesSent);
        Assert.Equal(new StickerSource.Giphy(gif.Id), stored.Source);
    }
}

public class OcrTimeLimitTests
{
    private sealed class SlowOcr(TimeSpan delay) : IOcrReader
    {
        public async Task<IReadOnlyList<OcrLine>> ReadLinesAsync(SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32> image, CancellationToken ct = default)
        {
            await Task.Delay(delay, ct);
            _ = image.Width; // still usable after the caller has moved on
            return [new OcrLine("late", new NormalizedBox(0, 0, 1, 1))];
        }
    }

    [Fact]
    public async Task GivesUpQuicklyWhileTheEngineWarmsUp()
    {
        var reader = new TimeLimitedOcrReader(new SlowOcr(TimeSpan.FromSeconds(2)), TimeSpan.FromMilliseconds(100));
        using var image = Fixtures.Sticker(Art.Dog, size: 64);
        var started = Environment.TickCount64;
        Assert.Empty(await reader.ReadLinesAsync(image));
        Assert.True(Environment.TickCount64 - started < 1000);
    }

    [Fact]
    public async Task ReturnsLinesInTime()
    {
        var reader = new TimeLimitedOcrReader(new SlowOcr(TimeSpan.FromMilliseconds(10)), TimeSpan.FromSeconds(1.5));
        using var image = Fixtures.Sticker(Art.Dog, size: 64);
        Assert.Equal("late", (await reader.ReadLinesAsync(image)).Single().Text);
    }
}
