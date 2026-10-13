using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit.Abstractions;

namespace StickerSense.Core.Tests;

/// <summary>Mirrors the Mac's LabelTests.</summary>
public class LabelTests
{
    private const double W = 138, H = 208; // a WhatsApp sticker, in device-independent pixels

    [Theory]
    [InlineData("‎‎Mis pegatinas [AB12CD] sticker from Ana Example, 15:48, ‎Received in Family Group")]
    [InlineData("Sticker from Ana, 4:05 PM, Received")]
    [InlineData("Cute Cats sticker, 15:48, Sent to Family Group, Read")]
    [InlineData("Sticker")]
    [InlineData("スタンプ, 15:48")]
    public void WhatsAppStickerLabelsAreStickers(string label) =>
        Assert.Equal(StickerVerdict.Sticker, LabelClassifier.Classify(label, W, H, isImageElement: false));

    [Fact]
    public void GifLabelsAreGifs()
    {
        Assert.Equal(StickerVerdict.Gif, LabelClassifier.Classify("GIF from Ana, 15:48, Received in Family", W, H, false));
        Assert.Equal(StickerVerdict.Gif, LabelClassifier.Classify("animated image", W, H, false));
    }

    [Theory]
    [InlineData("Message from Ana Example, send me that sticker pack you used yesterday please, 15:49, Received in Family Group")]
    [InlineData("Your message, lol where did you get that gif from it is amazing, 15:50, Sent to Family Group, Read")]
    [InlineData("Message from Ana, Bye, 15:48, Received in Family Group")]
    public void TextMessagesMentioningStickersAreNot(string label) =>
        Assert.Equal(StickerVerdict.NotSticker, LabelClassifier.Classify(label, W, H, isImageElement: false));

    [Fact]
    public void UnlabelledImagesUseSize()
    {
        Assert.Equal(StickerVerdict.Sticker, LabelClassifier.Classify("image", 150, 150, true));
        Assert.Equal(StickerVerdict.NotSticker, LabelClassifier.Classify("image", 40, 40, true));   // avatar
        Assert.Equal(StickerVerdict.NotSticker, LabelClassifier.Classify("image", 400, 300, true)); // photo
    }

    [Fact]
    public void LabelTextIsRemovedFromOcr()
    {
        const string label = "Mis pegatinas sticker from Ana Example, 15:48, Received in Family Group";
        Assert.Equal("idk lol", StickerText.RemovingLabelText("Ana Example\nidk lol", label));
        Assert.Equal("", StickerText.RemovingLabelText("Ana Example", label));
        Assert.Equal("good morning", StickerText.RemovingLabelText("good morning", null));
        Assert.True(StickerText.IsInLabel("Ana Example", label));
        Assert.False(StickerText.IsInLabel("idk", label));
    }

    [Fact]
    public void OnlyStickerLabelsCountAsChatChrome()
    {
        // On a sticker, the label's words are chrome; on a text message the label IS the message.
        Assert.NotNull(LabelClassifier.ChromeLabel("Cute pack sticker from Ana Example, 15:48, Received in Family Group"));
        Assert.Null(LabelClassifier.ChromeLabel("Message from Ana Example, hey, 15:49"));
        Assert.Null(LabelClassifier.ChromeLabel(null));
    }

    [Fact]
    public void AmbiguousLabelsAreRecognised()
    {
        Assert.True(LabelClassifier.IsAmbiguous(""));
        Assert.True(LabelClassifier.IsAmbiguous("image"));
        Assert.True(LabelClassifier.IsAmbiguous("Picture, group"));
        Assert.False(LabelClassifier.IsAmbiguous("Message from Sam, lunch?"));
        // "Sticker" on something that isn't an image (on a web page): worth looking inside it for one.
        Assert.True(LabelClassifier.IsAmbiguous("Sticker"));
        Assert.False(LabelClassifier.IsAmbiguous("Sticker pack from Ana Example sent yesterday afternoon"));
    }

    [Fact]
    public void TextAboutStickersIsNotASticker()
    {
        // Real lines a screen reader read from this project's README on GitHub.
        (string Label, double Width, double Height)[] readme =
        [
            ("Someone sends you a sticker. Your screen reader, ever the diplomat, says \"sticker\". Thanks.", 700, 90),
            ("StickerSense describes stickers, GIFs and pictures out loud, in any app, for people who use a screen reader.", 700, 60),
            ("A sticker it hasn't seen before: about 1.2 seconds.", 500, 24),
            ("A sticker, $3 link", 110, 22),
            ("A GIF, $5 link", 90, 22),
            ("Nobody has sent a sticker yet, so the first name here could be yours.", 300, 120),
        ];
        foreach (var (label, width, height) in readme)
        {
            Assert.Equal(StickerVerdict.NotSticker, LabelClassifier.Classify(label, width, height, isImageElement: false, inBrowser: true));
            Assert.Equal(StickerVerdict.NotSticker, LabelClassifier.Classify(label, width, height, isImageElement: false, inBrowser: false));
        }
    }

    [Fact]
    public void WebStickersNeedImageSemantics()
    {
        Assert.Equal(StickerVerdict.Sticker, LabelClassifier.Classify("Sticker, image", 160, 160, isImageElement: false, inBrowser: true));
        Assert.Equal(StickerVerdict.Gif, LabelClassifier.Classify("GIF", 160, 160, isImageElement: true, inBrowser: true));
        Assert.Equal(StickerVerdict.NotSticker, LabelClassifier.Classify("Sticker", 160, 160, isImageElement: false, inBrowser: true));
        // Process names decide what's a browser.
        var web = new FocusTarget { Name = "Sticker", Bounds = new PixelRect(0, 0, 160, 160), AppName = "msedge" };
        Assert.Equal(StickerVerdict.NotSticker, StickerDetector.Verdict(web));
        Assert.Equal(StickerVerdict.Sticker, StickerDetector.Verdict(web with { AppName = "whatsapp" }));
        Assert.Equal(StickerVerdict.Sticker, StickerDetector.Verdict(web with { IsImage = true }));
    }

    [Fact]
    public void TextShapedRegionsAreNeverStickers()
    {
        Assert.False(LabelClassifier.IsStickerShaped(700, 60));
        Assert.False(LabelClassifier.IsStickerShaped(110, 22));
        Assert.True(LabelClassifier.IsStickerShaped(138, 208));
        Assert.True(LabelClassifier.IsStickerShaped(300, 170));
        Assert.Equal(StickerVerdict.NotSticker, LabelClassifier.Classify("Sticker", 700, 60, isImageElement: true));
    }

    [Fact]
    public void DirectionMarksAreStripped() =>
        Assert.Equal("ana sticker", LabelClassifier.Clean("⁦Ana⁩ ‏Sticker‬"));
}

/// <summary>Mirrors the Mac's MaskingTests, with OCR lines from the fixture instead of live OCR.</summary>
public class MaskingTests(ITestOutputHelper output)
{
    private const string AnaLabel = "Cute pack sticker from Ana Example, 15:48, Received in Family Group";

    [Fact]
    public void SenderNameAndTimeArePaintedOut()
    {
        var (capture, lines) = Fixtures.GroupChatCapture("Ana Example", Art.Cat, "hey");
        using var _ = capture;
        var boxes = StickerText.ChromeBoxes(lines, AnaLabel);
        Assert.Equal(2, boxes.Count); // name and time
        Assert.Equal("hey", StickerText.TextOf(lines, AnaLabel));

        using var masked = ImagePrep.Masking(capture, boxes);
        foreach (var box in boxes)
        {
            Assert.True(IsPlain(masked, box, Fixtures.ChatBeige), $"text still visible in {box}");
            Assert.False(IsPlain(capture, box, Fixtures.ChatBeige), "the fixture should have drawn text there");
        }
        var caption = lines.Single(l => l.Text == "hey").Box;
        Assert.False(IsPlain(masked, caption, Fixtures.ChatBeige), "the sticker's own text must stay");
    }

    [Fact]
    public void NoBoxesMeansAnUnchangedCopy()
    {
        using var sticker = Fixtures.Sticker(Art.Dog);
        using var copy = ImagePrep.Masking(sticker, []);
        Assert.NotSame(sticker, copy);
        Assert.Equal(sticker[100, 300], copy[100, 300]);
    }

    [Fact]
    public async Task SameStickerFromDifferentSendersHitsTheCache()
    {
        using var library = new StickerLibrary(Fixtures.TempDirectory());
        var (fromAna, anaLines) = Fixtures.GroupChatCapture("Ana Example", Art.Cat, "hey");
        var (fromRaj, rajLines) = Fixtures.GroupChatCapture("Raj Sample", Art.Cat, "hey");
        using var a = fromAna;
        using var b = fromRaj;
        IReadOnlyList<OcrLine> current = anaLines;
        var pipeline = new StickerPipeline(new FakeDescriber(), library, new FakeOcr(_ => current));

        var probeA = await pipeline.ProbeAsync(fromAna, new DescriptionContext.ScreenItem("Pack sticker from Ana Example, 15:48"));
        Assert.Equal("hey", probeA.OcrText);
        library.Remember([fromAna], probeA.Fingerprint, new StickerReading(StickerKind.Sticker, "Cat", "hey"), probeA.OcrText, new StickerSource.Received("test"));

        current = rajLines;
        var probeB = await pipeline.ProbeAsync(fromRaj, new DescriptionContext.ScreenItem("Pack sticker from Raj Sample, 15:48"));
        output.WriteLine($"distance {probeA.Fingerprint.HammingDistance(probeB.Fingerprint)}, colour {probeA.Fingerprint.Thumb.Difference(probeB.Fingerprint.Thumb):0.0000}");
        Assert.NotNull(probeB.Cached);
    }

    [Fact]
    public async Task TextMessagesKeepTheirWords()
    {
        // The label of a text message is the message: its words aren't chrome and nothing is painted out.
        var (capture, lines) = Fixtures.GroupChatCapture("Ana Example", Art.Cat, "hey");
        using var _ = capture;
        var pipeline = new StickerPipeline(new FakeDescriber(), new StickerLibrary(Fixtures.TempDirectory()), new FakeOcr(lines));
        var probe = await pipeline.ProbeAsync(capture, new DescriptionContext.ScreenItem("Message from Ana Example, hey, 15:48"));
        Assert.Single(probe.ChromeBoxes); // just the time
        Assert.Equal("Ana Example\nhey", probe.OcrText);
    }

    private static bool IsPlain(Image<Rgba32> image, NormalizedBox box, Rgba32 background)
    {
        int left = (int)(box.X * image.Width), top = (int)(box.Y * image.Height);
        int right = (int)Math.Ceiling(box.Right * image.Width), bottom = (int)Math.Ceiling(box.Bottom * image.Height);
        for (var y = top; y < Math.Min(bottom, image.Height); y++)
        {
            for (var x = left; x < Math.Min(right, image.Width); x++)
            {
                var p = image[x, y];
                if (Math.Abs(p.R - background.R) + Math.Abs(p.G - background.G) + Math.Abs(p.B - background.B) > 12) return false;
            }
        }
        return true;
    }
}

public class BackgroundIndexerTests
{
    private sealed class Machine
    {
        public volatile bool PowerSaving;
        private long idleTicks = TimeSpan.FromSeconds(200).Ticks;
        public TimeSpan SinceLastInput => TimeSpan.FromTicks(Interlocked.Read(ref idleTicks));
        public void Idle(TimeSpan by) => Interlocked.Exchange(ref idleTicks, by.Ticks);
    }

    private static (StickerLibrary Library, StickerRecord Record) LibraryWithImport()
    {
        var dir = Fixtures.TempDirectory();
        var library = new StickerLibrary(dir);
        var file = Path.Combine(dir, "imported.png");
        using (var image = Fixtures.Sticker(Art.Frog, "hmm")) File.WriteAllBytes(file, ImagePrep.PngData(image));
        return (library, library.ImportFile(file, new StickerSource.WhatsApp("imported.png"))!);
    }

    private static BackgroundIndexer Indexer(StickerPipeline pipeline, Machine machine, Recorder log) =>
        new(pipeline, () => machine.SinceLastInput, () => machine.PowerSaving, log)
        {
            CheckInterval = TimeSpan.FromMilliseconds(20),
            InputPoll = TimeSpan.FromMilliseconds(10),
            Pause = TimeSpan.FromMilliseconds(10),
        };

    [Fact]
    public async Task WaitsUntilTheComputerIsIdle()
    {
        var (library, record) = LibraryWithImport();
        using var _ = library;
        var describer = new FakeDescriber();
        var machine = new Machine();
        machine.Idle(TimeSpan.FromSeconds(30)); // typed something half a minute ago
        var run = Indexer(new StickerPipeline(describer, library, new NullOcrReader(), DescribePriority.Background), machine, new Recorder()).RunAsync(CancellationToken.None);
        await Task.Delay(200);
        Assert.Equal(0, describer.Reads);

        machine.Idle(TimeSpan.FromSeconds(95));
        await run.WaitAsync(TimeSpan.FromSeconds(5)); // finishes when everything is described
        Assert.Equal(1, describer.Reads);
        Assert.NotNull(library.Record(record.Id)!.Reading);
    }

    [Fact]
    public async Task NeverRunsOnBatterySaver()
    {
        var (library, _) = LibraryWithImport();
        using var __ = library;
        var describer = new FakeDescriber();
        var machine = new Machine { PowerSaving = true };
        using var stop = new CancellationTokenSource();
        var run = Indexer(new StickerPipeline(describer, library, new NullOcrReader(), DescribePriority.Background), machine, new Recorder()).RunAsync(stop.Token);
        await Task.Delay(200);
        Assert.Equal(0, describer.Reads);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task StopsTheMomentTheUserIsBack()
    {
        var (library, record) = LibraryWithImport();
        using var _ = library;
        var describer = new FakeDescriber { Hold = new TaskCompletionSource() };
        var machine = new Machine();
        var log = new Recorder();
        var run = Indexer(new StickerPipeline(describer, library, new NullOcrReader(), DescribePriority.Background), machine, log).RunAsync(CancellationToken.None);
        await Eventually.True(() => describer.ReadStarted, "background describing to start");

        machine.Idle(TimeSpan.Zero); // a key press
        await Eventually.True(() => log.Log.Any(l => l.Contains("paused")), "describing to stop");
        Assert.Null(library.Record(record.Id)!.Reading); // abandoned, not marked as failed

        // Idle again later: it picks the sticker up again.
        describer.Hold.SetResult();
        machine.Idle(TimeSpan.FromMinutes(5));
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(library.Record(record.Id)!.Reading);
    }
}

/// <summary>Mirrors the Mac's BlankCaptureTests.</summary>
public class BlankCaptureTests
{
    [Fact]
    public void FlatImagesAreBlankAndNeverCached()
    {
        using var black = new Image<Rgba32>(300, 120, new Rgba32(0, 0, 0));
        Assert.True(ImagePrep.IsBlank(black));
        using var sticker = Fixtures.Sticker(Art.Dog, caption: "idk");
        Assert.False(ImagePrep.IsBlank(sticker));

        var dir = Fixtures.TempDirectory();
        using (var library = new StickerLibrary(dir))
        {
            var blank = new Fingerprint(black);
            library.Remember([black], blank, new StickerReading(StickerKind.Illustration, "Black rectangle"), "", new StickerSource.Received("x"));
            Assert.Equal(0, library.Count);
            Assert.Null(library.Match(blank, ""));
        }
    }

    [Fact]
    public void BlankRecordsCachedByOlderVersionsAreDropped()
    {
        var dir = Fixtures.TempDirectory();
        using var black = new Image<Rgba32>(200, 200, new Rgba32(0, 0, 0));
        using var sticker = Fixtures.Sticker(Art.Dog, caption: "idk");
        var blankRecord = new StickerRecord
        {
            Id = Guid.NewGuid(),
            Fingerprint = new Fingerprint(black),
            Reading = new StickerReading(StickerKind.Illustration, "Black rectangle"),
            OcrText = "",
            Source = new StickerSource.Received("whatsapp"),
            AddedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
            TimesSeen = 3,
        };
        using (var library = new StickerLibrary(dir))
        {
            library.Remember([sticker], new Fingerprint(sticker), new StickerReading(StickerKind.Sticker, "Dog shrugging"), "idk", new StickerSource.Received("whatsapp"));
        }
        // An older version wrote a blank record into the file.
        var path = Path.Combine(dir, "library.json");
        var records = System.Text.Json.JsonSerializer.Deserialize<List<StickerRecord>>(File.ReadAllBytes(path), StickerLibrary.JsonOptions)!;
        records.Add(blankRecord);
        File.WriteAllBytes(path, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(records, StickerLibrary.JsonOptions));
        using (var reloaded = new StickerLibrary(dir))
        {
            Assert.Equal(1, reloaded.Count);
            Assert.Null(reloaded.Record(blankRecord.Id));
        }
    }
}
