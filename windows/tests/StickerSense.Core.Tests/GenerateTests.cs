using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace StickerSense.Core.Tests;

public class GenerateTests
{
    // MARK: Ideas

    [Fact]
    public void QuotedWordsAreTheCaption()
    {
        Assert.Equal(("corgi", "thank you"), StickerIdeas.SplitQuoted("corgi \"thank you\""));
        Assert.Equal(("cake with candles", "Happy birthday"), StickerIdeas.SplitQuoted("“Happy birthday” cake with candles"));
        Assert.Equal(("sleepy cat", (string?)null), StickerIdeas.SplitQuoted("sleepy cat"));
    }

    [Fact]
    public void FallbackDrawsTheSearch()
    {
        var idea = StickerIdeas.Fallback("sleepy cat");
        Assert.Equal("", idea.Caption);
        Assert.Equal(Enumerable.Repeat("sleepy cat", StickerIdeas.Variations), idea.Pictures);
        var quotedOnly = StickerIdeas.Fallback("\"lol\"");
        Assert.Equal("lol", quotedOnly.Caption);
        Assert.Contains("lol", quotedOnly.Pictures[0]);
    }

    [Fact]
    public void CleansTheModelsPlan()
    {
        var idea = StickerIdeas.Cleaned("\"Thank you\"",
            ["1. a corgi bowing politely.", "2) a corgi bowing politely", "a corgi holding a sign that says thanks", "- a cat with flowers", ""],
            "corgi saying thank you");
        Assert.Equal("Thank you", idea.Caption);
        Assert.Equal(["a corgi bowing politely", "a cat with flowers", "a corgi bowing politely", "a corgi bowing politely"], idea.Pictures);
    }

    [Fact]
    public void QuotedCaptionBeatsTheModel()
    {
        var idea = StickerIdeas.Cleaned("Thanks!", ["a corgi"], "corgi \"ty <3\"");
        Assert.Equal("ty <3", idea.Caption);
        Assert.Equal(StickerIdeas.Variations, idea.Pictures.Count);
    }

    [Fact]
    public void ReadsTheLanguageModelsLines()
    {
        var reply = "CAPTION: Thank you\n1. a corgi bowing\n2. a corgi holding flowers\n3. a corgi waving\n4. a corgi with a heart\n";
        var idea = StickerIdeas.ParseLines(reply, "corgi saying thank you");
        Assert.Equal("Thank you", idea.Caption);
        Assert.Equal(["a corgi bowing", "a corgi holding flowers", "a corgi waving", "a corgi with a heart"], idea.Pictures);

        var noCaption = StickerIdeas.ParseLines("CAPTION:\n1. a sleepy panda\n2. a panda yawning", "sleepy panda");
        Assert.Equal("", noCaption.Caption);
        Assert.Equal(StickerIdeas.Variations, noCaption.Pictures.Count);
        Assert.Equal("", StickerIdeas.ParseLines("CAPTION: <words to print>\n1. a cat", "cat").Caption);
    }

    [Fact]
    public void ACaptionThatOnlyNamesWhatsDrawnIsDropped()
    {
        // What Qwen3-VL 2B answered for "angry cat".
        var angry = StickerIdeas.ParseLines("CAPTION: angry cat\n1. an angry cat growling\n2. an angry cat with a fist raised\n3. an angry cat standing on hind legs\n4. an angry cat biting its tail", "angry cat");
        Assert.Equal("", angry.Caption);
        // Phrases people say keep theirs, even when a picture mentions a word of it.
        var birthday = StickerIdeas.ParseLines("CAPTION: happy birthday\n1. a birthday cake with candles lit\n2. a puppy in a party hat\n3. a bear with a balloon\n4. a cat with a present", "happy birthday");
        Assert.Equal("happy birthday", birthday.Caption);
        Assert.Equal("ty", StickerIdeas.Cleaned("x", ["a cat", "a cat", "a cat"], "cat \"ty\"").Caption);
    }

    [Fact]
    public void PeopleNobodyAskedForBecomeAnimals()
    {
        // What Qwen3-VL 2B answered for "good morning".
        var idea = StickerIdeas.ParseLines("CAPTION: good morning\n1. a person with a bright smile holding a rose\n2. a man wearing a green jacket standing near a window\n3. a sleepy owl with a coffee cup\n4. a woman with glasses sitting by a bookshelf", "good morning");
        Assert.Equal("good morning", idea.Caption);
        Assert.Equal(["a cute cat feeling good morning", "a cute dog feeling good morning", "a sleepy owl with a coffee cup", "a cute bunny feeling good morning"], idea.Pictures);
        // Asked for: kept.
        Assert.Equal("a girl dancing in the rain", StickerIdeas.Cleaned("", ["a girl dancing in the rain"], "girl dancing").Pictures[0]);
    }

    [Fact]
    public void QuotedWordsInPicturesAreDropped() =>
        Assert.True(StickerIdeas.AsksForLettering("a gift box with a card reading “Happy Birthday”"));

    [Fact]
    public void LongCaptionsAreCutAtAWord()
    {
        var caption = StickerIdeas.ClampCaption("this is a really very long caption that goes on and on forever");
        Assert.True(caption.Length <= StickerIdeas.MaxCaptionLength);
        Assert.StartsWith(caption, "this is a really very long caption that goes on");
        Assert.False(caption.EndsWith(' '));
    }

    [Fact]
    public void LetteringIsLeftToStickerSense()
    {
        Assert.True(StickerIdeas.AsksForLettering("a banner reading hello"));
        Assert.True(StickerIdeas.AsksForLettering("a dog with the words good boy"));
        Assert.False(StickerIdeas.AsksForLettering("a cat texting on a phone"));
    }

    [Fact]
    public void SameRulesAsTheMac()
    {
        // The two apps must make the same stickers from the same search.
        Assert.Equal("a happy corgi, " + StickerIdeas.Style, StickerIdeas.Prompt(" a happy corgi. "));
        Assert.Equal("cute cartoon character illustration, thick black outline, flat vibrant colors, full body, centered on a plain white background", StickerIdeas.Style);
        Assert.Equal(4, StickerIdeas.Variations);
    }

    [Fact]
    public void MacUsesTheSameWords()
    {
        // StickerIdeas.swift must give the image model and the language model the same words.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "apple", "StickerKit", "Sources", "StickerKit", "StickerIdeas.swift"))) dir = dir.Parent;
        if (dir is null) return; // not run from a checkout
        var swift = File.ReadAllText(Path.Combine(dir.FullName, "apple", "StickerKit", "Sources", "StickerKit", "StickerIdeas.swift"));
        Assert.Contains($"public static let style = \"{StickerIdeas.Style}\"", swift);
        // Swift's multi-line string has the instructions word for word, a line each.
        foreach (var line in StickerIdeas.Instructions.Split('\n')) Assert.Contains(line, swift);
    }

    [Fact]
    public void ExamplesTeachTheReplyForm()
    {
        var request = StickerIdeas.LinesRequest("corgi");
        Assert.EndsWith("The search: corgi", request);
        Assert.Contains("The search: sleepy cat\nCAPTION: none\n1. a sleepy cat yawning", request);
        Assert.Contains("CAPTION: good morning\n1.", request);
        // The examples read back as plans.
        foreach (var (query, caption, pictures) in StickerIdeas.Examples)
        {
            var idea = StickerIdeas.ParseLines($"CAPTION: {(caption.Length > 0 ? caption : "none")}\n" + string.Join("\n", pictures.Select((p, i) => $"{i + 1}. {p}")), query);
            Assert.Equal(caption, idea.Caption);
            Assert.Equal(pictures, idea.Pictures);
        }
    }

    [Fact]
    public async Task PlansWithTheModelAndFallsBackWithoutIt()
    {
        var model = new FakeCompleter(" thank you\n1. a corgi bowing\n2. a corgi waving\n3. a corgi with flowers\n4. a corgi winking");
        var idea = await StickerIdeaWriter.IdeasAsync("corgi saying thank you", model);
        Assert.Equal("thank you", idea.Caption);
        Assert.Equal("a corgi waving", idea.Pictures[1]);
        Assert.Equal(StickerIdeas.Instructions, model.LastInstructions);
        Assert.Equal(StickerIdeas.ReplyLines, model.LastStopAfterLines);

        var broken = new FakeCompleter(null);
        var fallback = await StickerIdeaWriter.IdeasAsync("corgi \"ty\"", broken);
        Assert.Equal("ty", fallback.Caption);
        Assert.Equal("corgi", fallback.Pictures[0]);
    }

    // MARK: Art

    [Fact]
    public void DistanceTransformIsEuclidean()
    {
        var inside = new bool[7 * 7];
        inside[3 * 7 + 3] = true;
        var d = StickerArt.DistanceToInside(inside, 7, 7);
        Assert.Equal(0, d[3 * 7 + 3]);
        Assert.Equal(3, d[3 * 7 + 6], 9);
        Assert.Equal(Math.Sqrt(18), d[0], 9);
    }

    [Fact]
    public void ComposesASquareStickerWithBorderAndCaption()
    {
        using var subject = Disc(300, Color.FromRgb(230, 50, 50));
        using var art = StickerArt.Compose(subject, "Thank you");
        Assert.Equal(StickerArt.Size, art.Width);
        Assert.Equal(StickerArt.Size, art.Height);
        Assert.Equal(0, art[0, 0].A);
        Assert.Equal(0, art[511, 511].A);
        // Centre column, top down: transparent, then the white border, then the red subject.
        var column = Enumerable.Range(0, 512).Select(y => art[256, y]).ToList();
        var firstVisible = column.FindIndex(p => p.A > 200);
        Assert.True(firstVisible > 0);
        Assert.True(column[firstVisible + 2].R > 230 && column[firstVisible + 2].G > 230, "white border above the subject");
        Assert.Contains(column, p => p.R > 200 && p.G < 80);
        // Dark lettering in the lower part.
        var letters = 0;
        for (var y = 330; y < 500; y++) for (var x = 60; x < 452; x++) if (art[x, y] is { A: > 250, R: < 60, G: < 60 }) letters++;
        Assert.True(letters > 800, $"caption letters ({letters})");
        Assert.Equal(0, art[256, 3].A);
    }

    [Fact]
    public void NoCaptionCentresTheSubject()
    {
        using var subject = Disc(200, Color.FromRgb(50, 100, 230));
        using var art = StickerArt.Compose(subject, "");
        var rows = Enumerable.Range(0, 512).Where(y => art[256, y].A > 128).ToList();
        Assert.InRange((rows.First() + rows.Last()) / 2, 254, 258);
    }

    [Fact]
    public void LongCaptionsBreakIntoTwoLines()
    {
        var lines = StickerArt.CaptionLines("good morning sunshine my friend", 480, 170, 1);
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.True(l.Width <= 480));
        Assert.Single(StickerArt.CaptionLines("lol", 480, 170, 1));
    }

    [Fact]
    public void PlainWhiteBackgroundIsCutAwayWithoutAModel()
    {
        using var picture = new Image<Rgba32>(128, 128, Color.White.ToPixel<Rgba32>());
        picture.Mutate(c => c.Fill(Color.FromRgb(240, 120, 20), new SixLabors.ImageSharp.Drawing.EllipsePolygon(64, 64, 40)));
        var mask = StickerArt.PlainBackgroundMask(picture);
        Assert.NotNull(mask);
        Assert.Equal(0, mask[2 * 128 + 2]);
        Assert.Equal(1, mask[64 * 128 + 64]);
        using var cut = StickerArt.ApplyMask(picture, mask);
        Assert.Equal(0, cut[2, 2].A);
        Assert.Equal(255, cut[64, 64].A);

        // A busy edge isn't a plain background: no mask.
        using var noisy = new Image<Rgba32>(64, 64);
        noisy.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++) row[x] = new Rgba32((byte)(x * 37 % 256), (byte)(y * 53 % 256), (byte)((x + y) * 11 % 256), 255);
            }
        });
        Assert.Null(StickerArt.PlainBackgroundMask(noisy));
    }

    // MARK: Library

    [Fact]
    public void GeneratedSourceRoundTripsInTheSharedFormat()
    {
        var json = JsonSerializer.Serialize<StickerSource>(new StickerSource.Generated("corgi saying thank you"));
        Assert.Equal("{\"generated\":{\"prompt\":\"corgi saying thank you\"}}", json);
        Assert.Equal(new StickerSource.Generated("corgi saying thank you"), JsonSerializer.Deserialize<StickerSource>(json));
        Assert.Equal(SourceFilter.Generated, new StickerSource.Generated("x").FilterTag);
        Assert.Equal("Made by you", new StickerSource.Generated("x").Origin);
        Assert.Equal("From WhatsApp", new StickerSource.WhatsApp("a").Origin);
        Assert.Equal("Made by you", SourceFilter.Generated.Title());
    }

    [Fact]
    public async Task PastedGeneratedStickerJoinsTheLibrary()
    {
        var dir = Fixtures.TempDirectory();
        var library = new StickerLibrary(dir);
        var describer = new FakeDescriber { Reading = new StickerReading(StickerKind.Illustration, "Orange ball") };
        using var search = new PickerSearch(new StickerPipeline(describer, library, new NullOcrReader()), Path.Combine(dir, "dl"));
        using var subject = Disc(300, Color.FromRgb(240, 150, 20));
        using var art = StickerArt.Compose(subject, "yay");
        var file = Path.Combine(dir, "made.png");
        await File.WriteAllBytesAsync(file, ImagePrep.PngData(art));
        var sticker = new GeneratedSticker(Guid.NewGuid(), "orange ball saying yay", "yay", "an orange ball", file, 1);
        Assert.Equal("New sticker 1: an orange ball, “yay”, still being described", sticker.Label);

        var reading = await search.DescribeAsync(sticker);
        Assert.NotNull(reading);
        Assert.Equal("yay", reading.Text);
        Assert.Equal("yay", describer.LastOcr);
        sticker = sticker with { Reading = reading };
        Assert.Equal("New sticker 1: Orange ball, “yay”", sticker.Label);

        await search.MarkSentAsync(sticker);
        var found = library.Search("orange ball", SourceFilter.Generated).Single();
        Assert.Equal(1, found.TimesSent);
        Assert.Equal("Orange ball, “yay”. Made by you", found.ScreenReaderLabel);
        // The search that made it finds it again.
        Assert.Single(library.Search("saying"));
    }

    // MARK: Making

    [Fact]
    public async Task FactoryDrawsCutsOutAndSaves()
    {
        var generator = new FakeGenerator();
        var factory = new StickerFactory(generator, masker: null);
        var folder = Fixtures.TempDirectory();
        var sticker = await factory.MakeAsync("a happy orange", "yay", "orange yay", 3, folder);
        Assert.Equal("a happy orange, " + StickerIdeas.Style, generator.LastPrompt);
        Assert.Equal(3, sticker.Number);
        Assert.Equal("yay", sticker.Caption);
        Assert.True(File.Exists(sticker.FilePath));
        using var made = Image.Load<Rgba32>(sticker.FilePath);
        Assert.Equal(512, made.Width);
        // The white background was cut away (no cut-out model: the plain-background fallback).
        Assert.Equal(0, made[4, 4].A);
        Assert.True(made[256, 200].A > 250);
    }

    [Fact]
    public async Task FactoryUsesTheCutOutModelsMask()
    {
        var masker = new FakeMasker();
        var factory = new StickerFactory(new FakeGenerator(), masker);
        var sticker = await factory.MakeAsync("an orange", "", "orange", 1, Fixtures.TempDirectory());
        Assert.Equal(1, masker.Calls);
        using var made = Image.Load<Rgba32>(sticker.FilePath);
        // The fake mask keeps only the left half of the picture: the sticker is taller than wide.
        var columns = Enumerable.Range(0, 512).Where(x => made[x, 256].A > 128).ToList();
        Assert.True(columns.Count < 400);
    }

    [Fact]
    public async Task DownloadsAndChecksTheFiles()
    {
        var good = new byte[300_000];
        Random.Shared.NextBytes(good);
        var handler = new FakeFiles(good);
        var downloader = new HuggingFaceDownloader(new HttpClient(handler));
        var folder = Fixtures.TempDirectory();
        var file = new ModelFile("model.bin", "https://example.test/model.bin", good.Length, Convert.ToHexString(SHA256.HashData(good)).ToLowerInvariant());
        var target = Path.Combine(folder, "sticker", "model.bin");
        var reports = new List<DownloadProgress>();
        await downloader.DownloadFilesAsync([(file, target)], new SyncProgress(reports.Add));
        Assert.Equal(good, await File.ReadAllBytesAsync(target));
        Assert.Equal(100, reports.Last().Percent);

        // Already there: nothing downloaded again.
        await downloader.DownloadFilesAsync([(file, target)], null);
        Assert.Equal(1, handler.Requests);

        // A corrupted download is refused and removed.
        var bad = file with { Name = "bad.bin", Sha256 = new string('0', 64) };
        var badTarget = Path.Combine(folder, "sticker", "bad.bin");
        await Assert.ThrowsAsync<ModelDownloadException>(() => downloader.DownloadFilesAsync([(bad, badTarget)], null));
        Assert.False(File.Exists(badTarget));
    }

    [Fact]
    public void CatalogPointsAtPinnedFiles()
    {
        foreach (var model in Enum.GetValues<StickerModel>())
        {
            var needed = StickerMakerCatalog.Needed(model, "/models");
            Assert.Contains(needed, n => n.File == StickerMakerCatalog.CutOutModel);
            Assert.All(needed, n => Assert.Matches("^[0-9a-f]{64}$", n.File.Sha256));
            Assert.All(StickerMakerCatalog.Files(model), f => Assert.Contains("/resolve/", f.Url));
            Assert.False(StickerMakerCatalog.IsInstalled(model, Fixtures.TempDirectory()));
        }
        Assert.Contains("--steps", StickerMakerCatalog.Arguments(StickerModel.Fast, "/models"));
        Assert.Contains("--taesd", StickerMakerCatalog.Arguments(StickerModel.Better, "/models"));
        Assert.Contains("Stability AI", StickerMakerCatalog.Credit(StickerModel.Better));
    }

    // MARK: Helpers

    private static Image<Rgba32> Disc(int size, Color color)
    {
        var image = new Image<Rgba32>(size, size);
        image.Mutate(c => c.Fill(color, new SixLabors.ImageSharp.Drawing.EllipsePolygon(size / 2f, size / 2f, size / 2f - 20)));
        return image;
    }

    private sealed class FakeGenerator : IImageGenerator
    {
        public string LastPrompt = "";

        public Task<Image<Rgba32>> GenerateAsync(string prompt, int seed, CancellationToken ct)
        {
            LastPrompt = prompt;
            var image = new Image<Rgba32>(512, 512, Color.White.ToPixel<Rgba32>());
            image.Mutate(c => c.Fill(Color.FromRgb(250, 140, 30), new SixLabors.ImageSharp.Drawing.EllipsePolygon(256, 256, 150)));
            return Task.FromResult(image);
        }
    }

    private sealed class FakeMasker : ISubjectMasker
    {
        public int Calls;

        public Task<float[]?> MaskAsync(Image<Rgba32> image, CancellationToken ct)
        {
            Calls++;
            var mask = new float[image.Width * image.Height];
            for (var y = 0; y < image.Height; y++) for (var x = 0; x < image.Width / 2; x++) mask[y * image.Width + x] = image[x, y].B < 200 ? 1 : 0;
            return Task.FromResult<float[]?>(mask);
        }
    }

    private sealed class FakeCompleter(string? reply) : ITextCompleter
    {
        public string LastInstructions = "";
        public int LastStopAfterLines;

        public Task<string> CompleteAsync(string instructions, string prompt, string replyStart, int maxNewTokens, int stopAfterLines, CancellationToken ct)
        {
            LastInstructions = instructions;
            LastStopAfterLines = stopAfterLines;
            return reply is null ? Task.FromException<string>(new InvalidOperationException("no model")) : Task.FromResult(reply);
        }
    }

    private sealed class FakeFiles(byte[] content) : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
        }
    }

    private sealed class SyncProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }
}
