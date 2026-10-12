using System.Net;
using System.Text;

namespace StickerSense.Core.Tests;

public class GiphyTests
{
    private const string SearchJson = """
        {"data":[{"id":"abc","title":"Happy Birthday GIF by Disney","alt_text":"","images":{
          "fixed_width":{"url":"https://media.giphy.com/media/abc/200w.gif"},
          "downsized":{"url":"https://media.giphy.com/media/abc/giphy-downsized.gif"}}},
          {"id":"broken","title":"x","images":{}},
          {"id":"st","title":"Thumbs Up Sticker","alt_text":"Cartoon thumbs up Sticker by Pudgy","images":{
          "fixed_width_small":{"url":"https://media.giphy.com/media/st/100w.gif"}}}]}
        """;

    [Fact]
    public void ParsesSearchResults()
    {
        var items = GiphyClient.Parse(Encoding.UTF8.GetBytes(SearchJson), GiphyKind.Gif);
        Assert.Equal(2, items.Count);
        Assert.Equal("Happy Birthday", items[0].CleanTitle);
        Assert.EndsWith("downsized.gif", items[0].OriginalUrl);
        Assert.EndsWith("200w.gif", items[0].PreviewUrl);
        Assert.Equal("Cartoon thumbs up", items[1].CleanTitle);
        Assert.Equal(items[1].PreviewUrl, items[1].OriginalUrl);
        Assert.Equal("Sticker", (items[1] with { Kind = GiphyKind.Sticker, Title = "", AltText = " Sticker" }).CleanTitle);
        Assert.Equal("https://media.giphy.com/media/st/100w.gif", items[1].SmallUrl); // fixed_width_small as the fallback
        Assert.Null(items[0].SmallUrl);
    }

    [Fact]
    public void ParsesPagination()
    {
        const string json = """
            {"data":[{"id":"a","title":"Cat GIF","images":{"fixed_width":{"url":"https://x/a.gif"},"fixed_width_downsampled":{"url":"https://x/a-small.gif"}}}],
             "pagination":{"total_count":120,"count":1,"offset":25}}
            """;
        var page = GiphyClient.ParsePage(Encoding.UTF8.GetBytes(json), GiphyKind.Gif, 25);
        Assert.Equal(26, page.NextOffset);
        Assert.True(page.HasMore);
        Assert.Equal("https://x/a-small.gif", page.Items[0].SmallUrl);
        var last = GiphyClient.ParsePage(Encoding.UTF8.GetBytes(json.Replace("120", "26")), GiphyKind.Gif, 25);
        Assert.False(last.HasMore);
        var noPagination = GiphyClient.ParsePage(Encoding.UTF8.GetBytes(SearchJson), GiphyKind.Gif, 0);
        Assert.Equal(2, noPagination.NextOffset);
        Assert.False(noPagination.HasMore);
    }

    [Fact]
    public void ResultsSummaryCountsBothSources()
    {
        Assert.Equal("3 of yours and 50 from GIPHY. First: Cat. Enter pastes it.",
            PickerSearch.ResultsSummary(3, 50, "cat", "Cat", "", "Enter pastes it."));
        Assert.Equal("Searching GIPHY…", PickerSearch.ResultsSummary(0, 0, "", null, "", "", giphyPending: true));
        Assert.Equal("2 results, GIPHY still loading. First: Dog. Hint",
            PickerSearch.ResultsSummary(2, 0, "dog", "Dog", "", "Hint", giphyPending: true));
        Assert.Equal("Nothing found for zzz. Add your key.", PickerSearch.ResultsSummary(0, 0, "zzz", null, "Add your key.", ""));
    }

    [Fact]
    public async Task MissingKeyIsExplained()
    {
        var error = await Assert.ThrowsAsync<GiphyException>(() => new GiphyClient("").SearchAsync("cat", GiphyKind.Gif));
        Assert.Equal(GiphyErrorKind.MissingKey, error.Kind);
        Assert.Equal("Add your GIPHY API key in Settings to search GIFs.", error.Message);
    }

    [Fact]
    public async Task RejectedKeyIsExplained()
    {
        using var http = new HttpClient(new StubHandler(HttpStatusCode.Unauthorized, "{}"));
        var error = await Assert.ThrowsAsync<GiphyException>(() => new GiphyClient("bad", http: http).SearchAsync("cat", GiphyKind.Sticker));
        Assert.Equal(GiphyErrorKind.InvalidKey, error.Kind);

        using var broken = new HttpClient(new StubHandler(HttpStatusCode.InternalServerError, "{}"));
        error = await Assert.ThrowsAsync<GiphyException>(() => new GiphyClient("key", http: broken).SearchAsync("cat", GiphyKind.Gif));
        Assert.Equal("GIPHY search failed (error 500).", error.Message);
    }

    [Fact]
    public async Task SearchesTheRightEndpoint()
    {
        var handler = new StubHandler(HttpStatusCode.OK, SearchJson);
        using var http = new HttpClient(handler);
        var page = await new GiphyClient("k&y", http: http).SearchAsync("happy birthday", GiphyKind.Sticker, limit: 80, offset: 50);
        Assert.Equal(2, page.Items.Count);
        Assert.All(page.Items, i => Assert.Equal(GiphyKind.Sticker, i.Kind));
        var uri = handler.LastUri!.AbsoluteUri;
        Assert.StartsWith("https://api.giphy.com/v1/stickers/search?", uri);
        Assert.Contains("api_key=k%26y", uri);
        Assert.Contains("q=happy%20birthday", uri);
        Assert.Contains("limit=50", uri); // GIPHY's maximum
        Assert.Contains("offset=50", uri);
        Assert.Contains("rating=pg-13", uri);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}

public class StickerDetectorTests
{
    private static FocusTarget Target(string name, int w = 150, int h = 150, bool image = false, string role = "", double scale = 1.0) =>
        new() { Bounds = new PixelRect(100, 100, w, h), Name = name, IsImage = image, RoleText = role, AppName = "whatsapp", DisplayScale = scale };

    [Fact]
    public void RecognisesStickerAndGifWords()
    {
        Assert.Equal(StickerVerdict.Sticker, StickerDetector.Verdict(Target("Sticker", role: "image", image: true)));
        Assert.Equal(StickerVerdict.Gif, StickerDetector.Verdict(Target("GIF")));
        Assert.Equal(StickerVerdict.Gif, StickerDetector.Verdict(Target("Animated sticker, 4:05 PM")));
        Assert.Equal(StickerVerdict.Sticker, StickerDetector.Verdict(Target("スタンプ")));
        Assert.Equal(StickerVerdict.Sticker, StickerDetector.Verdict(Target("Pegatina de un gato")));
        Assert.Equal(StickerVerdict.Sticker, StickerDetector.Verdict(Target("Mis pegatinas sticker from Ana, 15:48, Received in Family", role: "list item")));
    }

    [Fact]
    public void LongTextMessagesMentioningStickersAreNotStickers()
    {
        const string message = "Message from Ana, hey can you send me that sticker you sent yesterday it was so funny, 15:49";
        Assert.Equal(StickerVerdict.NotSticker, StickerDetector.Verdict(Target(message, w: 600, h: 60)));
        // …but an image element whose long alt text says sticker is one.
        Assert.Equal(StickerVerdict.Sticker, StickerDetector.Verdict(Target(message, image: true)));
    }

    [Fact]
    public void UnlabelledStickerSizedImagesCount()
    {
        Assert.Equal(StickerVerdict.Sticker, StickerDetector.Verdict(Target("", image: true)));
        Assert.Equal(StickerVerdict.Sticker, StickerDetector.Verdict(Target("", role: "image")));
        Assert.Equal(StickerVerdict.NotSticker, StickerDetector.Verdict(Target("", w: 600, h: 400, image: true)));  // photo
        Assert.Equal(StickerVerdict.NotSticker, StickerDetector.Verdict(Target("", w: 40, h: 40, image: true)));    // avatar
        Assert.Equal(StickerVerdict.NotSticker, StickerDetector.Verdict(Target("", w: 200, h: 90, image: true)));   // banner
        Assert.Equal(StickerVerdict.NotSticker, StickerDetector.Verdict(Target("See you at 6")));
    }

    [Fact]
    public void SizesAreMeasuredInDeviceIndependentPixels()
    {
        Assert.Equal(StickerVerdict.NotSticker, StickerDetector.Verdict(Target("", w: 320, h: 320, image: true, scale: 1.0)));
        Assert.Equal(StickerVerdict.Sticker, StickerDetector.Verdict(Target("", w: 320, h: 320, image: true, scale: 2.0)));
    }

    [Fact]
    public void WatchesChatAppsOrEverything()
    {
        Assert.True(StickerDetector.IsWatched("WhatsApp.Root", AutoScope.ChatApps));
        Assert.True(StickerDetector.IsWatched("msedge", AutoScope.ChatApps));
        Assert.False(StickerDetector.IsWatched("notepad", AutoScope.ChatApps));
        Assert.True(StickerDetector.IsWatched("notepad", AutoScope.Everywhere));
    }

    [Fact]
    public void FocusTargetsCompareBySpotAndText()
    {
        var a = Target("Sticker", role: "image");
        Assert.Equal("Sticker, image", a.Text);
        Assert.Equal("Image", Target("Image", role: "image").Text);
        Assert.True(a.IsSameItem(a with { Bounds = new PixelRect(101, 101, 151, 150) }));
        Assert.False(a.IsSameItem(a with { Bounds = new PixelRect(110, 100, 150, 150) }));
        Assert.False(a.IsSameItem(a with { Name = "GIF" }));
        Assert.False(a.IsSameItem(null));
        Assert.True(a.IsLikelySameItem(a with { Bounds = new PixelRect(100, 400, 150, 150) })); // scrolled
        Assert.False(a.IsLikelySameItem(a with { Bounds = new PixelRect(100, 100, 300, 150) }));
        Assert.Equal(new PixelRect(-10, 90, 220, 220), PixelRect.Around(100, 200, 220) with { Y = 90 });
        Assert.Equal(new PixelRect(3, 3, 94, 94), new PixelRect(0, 0, 100, 100).Inset(3));
        Assert.Equal(new PixelRect(50, 50, 50, 50), new PixelRect(0, 0, 100, 100).Intersect(new PixelRect(50, 50, 100, 100)));
    }

    [Fact]
    public void FriendlyAppNames()
    {
        Assert.Equal("WhatsApp", new StickerSource.Received("WhatsApp.Root").DisplayName);
        Assert.Equal("Google Chrome", AppNames.FriendlyName("chrome"));
        Assert.Equal("someapp", AppNames.FriendlyName("someapp"));
        Assert.Equal(SourceFilter.Received, new StickerSource.Imported().FilterTag);
        Assert.Equal("GIPHY", SourceFilter.Giphy.Title());
    }
}
