using System.Globalization;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace StickerSense.Core.Tests;

public class PipelineTests
{
    [Fact]
    public async Task ProbeUsesOcrAndTheCache()
    {
        using var library = new StickerLibrary(Fixtures.TempDirectory());
        var describer = new FakeDescriber();
        var pipeline = new StickerPipeline(describer, library, new FakeOcr("idk"));
        using var image = Fixtures.Sticker(Art.Dog, "idk");

        var probe = await pipeline.ProbeAsync(image, DescriptionContext.File);
        Assert.Null(probe.Cached);
        Assert.Equal("idk", probe.OcrText);
        var record = await pipeline.DescribeAsync([image], probe, DescriptionContext.File, new StickerSource.Imported());
        Assert.Equal("idk", describer.LastOcr);

        using var smaller = Fixtures.OnScreenCapture(image, 0.5);
        var again = await pipeline.ProbeAsync(smaller, DescriptionContext.File);
        Assert.Equal(record.Id, again.Cached?.Id);
        await pipeline.DescribeAsync([smaller], again, DescriptionContext.File, new StickerSource.Imported());
        Assert.Equal(1, describer.Reads);
    }

    [Fact]
    public async Task DetailIsCachedOnTheRecord()
    {
        using var library = new StickerLibrary(Fixtures.TempDirectory());
        var describer = new FakeDescriber();
        var pipeline = new StickerPipeline(describer, library, new NullOcrReader());
        using var image = Fixtures.Sticker(Art.Cake);
        var record = await pipeline.DescribeAsync([image], null, DescriptionContext.File, new StickerSource.Imported());

        // No frames passed: the stored image is used.
        Assert.Equal(describer.Detail, await pipeline.DetailAsync(record, null, DescriptionContext.File));
        Assert.Equal(describer.Detail, await pipeline.DetailAsync(record, null, DescriptionContext.File));
        Assert.Equal(1, describer.Details);
        Assert.Equal(describer.Detail, library.Record(record.Id)?.Detailed);
    }

    [Fact]
    public async Task ReadsTextOnEveryFrameOfAnAnimation()
    {
        using var library = new StickerLibrary(Fixtures.TempDirectory());
        var describer = new FakeDescriber();
        var calls = 0;
        // The words appear partway through: "hi" on the first frame, "bye" later.
        var ocr = new FakeOcr(_ => Interlocked.Increment(ref calls) == 1
            ? [new OcrLine("hi", new NormalizedBox(0.3, 0.8, 0.2, 0.1))]
            : [new OcrLine("Hi!", new NormalizedBox(0.3, 0.8, 0.2, 0.1)), new OcrLine("bye", new NormalizedBox(0.3, 0.9, 0.2, 0.1))]);
        var pipeline = new StickerPipeline(describer, library, ocr);
        await pipeline.DescribeAsync(Fixtures.AnimatedFrames(), null, DescriptionContext.File, new StickerSource.Imported());
        Assert.Equal("hi\nbye", describer.LastOcr);
        Assert.Equal(3, ocr.Reads);
    }

    [Fact]
    public async Task DescribesImportedStickers()
    {
        var dir = Fixtures.TempDirectory();
        using var library = new StickerLibrary(dir);
        var file = Path.Combine(dir, "imported.gif");
        File.WriteAllBytes(file, ImagePrep.GifData(Fixtures.AnimatedFrames()));
        var imported = library.ImportFile(file, new StickerSource.WhatsApp("imported.gif"))!;
        var describer = new FakeDescriber();
        var pipeline = new StickerPipeline(describer, library, new FakeOcr("hi!"), DescribePriority.Background);

        var described = await pipeline.DescribeStoredAsync(imported);
        Assert.Equal(3, describer.LastFrameCount);
        Assert.True(described.Reading!.Animated);
        Assert.Equal("hi!", described.OcrText);
        Assert.Empty(library.Undescribed(5));
    }
}

public class VisionDescriberTests
{
    private sealed class ScriptedModel(string reply) : VisionLanguageDescriber
    {
        public VisionRequest? Last;
        public override EngineStatus Status => new(EngineState.Ready);

        protected override Task<string> GenerateAsync(VisionRequest request, DescribePriority priority, CancellationToken ct)
        {
            Last = request;
            Assert.All(request.Images, i => Assert.Equal(255, i[0, 0].A)); // flattened
            return Task.FromResult(reply);
        }
    }

    [Fact]
    public async Task BuildsPromptsAndParsesReplies()
    {
        var model = new ScriptedModel("KIND: sticker\nSUBJECT: Party popper\nTEXT: yay\nMEANING: celebrating");
        model.UpdateOptions(new DescriberOptions { Language = CultureInfo.GetCultureInfo("en-US") });
        using var image = Fixtures.Sticker(Art.Party, "yay", size: 1024);
        var reading = await model.ReadAsync([image], new DescriptionContext.ScreenItem("Sticker"), "yay 4:05 PM");
        Assert.Equal(new StickerReading(StickerKind.Sticker, "Party popper", "yay", "celebrating"), reading);
        Assert.Contains("Text that OCR found on it: yay\n", model.Last!.Prompt.ReplaceLineEndings("\n"));
        Assert.Contains("The screen reader calls this item: Sticker", model.Last.Prompt);
        Assert.Equal(768, model.Last.Images.Single().Width);
        Assert.Equal(100, model.Last.MaxNewTokens);
        Assert.DoesNotContain("MEANING", model.Last.Prompt); // only asked for when the setting is on
    }

    [Fact]
    public async Task SendsAtMostFourFrames()
    {
        var model = new ScriptedModel("KIND: gif\nSUBJECT: Cat\nMOTION: waves\nTEXT: none\nMEANING: hello");
        var frames = Enumerable.Range(0, 6).Select(i => Fixtures.Sticker(Art.Cat, artX: i * 0.05f, size: 128)).ToList();
        var reading = await model.ReadAsync(frames, DescriptionContext.File);
        Assert.Equal(4, model.Last!.Images.Count);
        Assert.Equal("Cat, waves", reading.Subject);
        Assert.Contains("These are 4 frames", model.Last.Prompt);
    }

    [Fact]
    public async Task DetailUsesTheFirstLookAndCleansTheReply()
    {
        var model = new ScriptedModel("**Sure!** A frog in a teacup.\n- It sips.");
        using var image = Fixtures.Sticker(Art.Frog);
        var text = await model.DetailedAsync([image], DescriptionContext.File, "", new StickerReading(StickerKind.Sticker, "Frog"));
        Assert.Equal("A frog in a teacup. It sips.", text);
        Assert.Contains("A quick first look said: Frog.", model.Last!.Prompt);
        Assert.Equal(220, model.Last.MaxNewTokens);
    }

    [Fact]
    public async Task ExplainsWhenTheModelIsMissing()
    {
        var missing = new MissingModel();
        using var image = Fixtures.Sticker(Art.Frog);
        var error = await Assert.ThrowsAsync<DescribeException>(() => missing.ReadAsync([image], DescriptionContext.File));
        Assert.Equal(DescribeErrorKind.EngineUnavailable, error.Kind);
        Assert.Contains("Setup", error.Message);
        await Assert.ThrowsAsync<DescribeException>(() => new ScriptedModel("").ReadAsync([], DescriptionContext.File));
    }

    private sealed class MissingModel : VisionLanguageDescriber
    {
        public override EngineStatus Status => new(EngineState.ModelMissing);
        protected override Task<string> GenerateAsync(VisionRequest request, DescribePriority priority, CancellationToken ct) => throw new InvalidOperationException();
    }
}

public class DescriberGateTests
{
    [Fact]
    public async Task ForegroundPreemptsBackground()
    {
        var gate = new DescriberGate();
        var background = await gate.EnterAsync(DescribePriority.Background, CancellationToken.None);
        Assert.False(background.Token.IsCancellationRequested);

        var foregroundTask = gate.EnterAsync(DescribePriority.Foreground, CancellationToken.None);
        Assert.True(background.Token.IsCancellationRequested); // asked to stop
        Assert.False(foregroundTask.IsCompleted);              // but holds the slot until it does
        background.Dispose();
        using (var foreground = await foregroundTask.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            // Background work waits while foreground work runs.
            var waiting = gate.EnterAsync(DescribePriority.Background, CancellationToken.None);
            await Task.Delay(100);
            Assert.False(waiting.IsCompleted);
            foreground.Dispose();
            using var next = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(next.Token.IsCancellationRequested);
        }
    }
}

public class MotionSamplerTests
{
    private static Task NoDelay(TimeSpan delay, CancellationToken ct) => Task.CompletedTask;

    [Fact]
    public async Task StillImagesGiveOneFrame()
    {
        var capturer = new FakeCapturer();
        using var first = await capturer.CaptureAsync(default);
        var frames = await MotionSampler.CaptureMotionAsync(capturer, default, first, false, NoDelay);
        Assert.Single(frames);
        Assert.Equal(3, capturer.Captures);
    }

    [Fact]
    public async Task AnimationsGiveUpToThreeDistinctFrames()
    {
        var capturer = new FakeCapturer
        {
            Frames = Enumerable.Range(0, 6).Select(i => Fixtures.Sticker(Art.Wave, artX: i * 0.06f, size: 160)).ToList(),
        };
        var first = await capturer.CaptureAsync(default);
        var delays = new List<TimeSpan>();
        var frames = await MotionSampler.CaptureMotionAsync(capturer, default, first, false, (d, _) =>
        {
            delays.Add(d);
            return Task.CompletedTask;
        });
        Assert.Equal(3, frames.Count);
        Assert.Same(first, frames[0]);
        // One quick 150 ms look found motion; one more look 200 ms later: about 0.35-0.7 s in all.
        Assert.Equal([TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(200)], delays);
    }
}

public class SupportTests
{
    [Fact]
    public void EarconsAreValidWavs()
    {
        var wav = ToneSynth.Wav(ToneSynth.Notes(Earcon.Failed), volume: 0.35);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        var samples = (int)(0.09 * 44_100) + (int)(0.04 * 44_100) + (int)(0.12 * 44_100);
        Assert.Equal(44 + samples * 2, wav.Length);
        Assert.Equal(wav.Length - 8, BitConverter.ToInt32(wav, 4));
        var loud = ToneSynth.Wav(ToneSynth.Notes(Earcon.Tick), volume: 1);
        var quiet = ToneSynth.Wav(ToneSynth.Notes(Earcon.Tick), volume: 0.1);
        Assert.True(MaxSample(loud) > MaxSample(quiet) * 5);
    }

    private static int MaxSample(byte[] wav)
    {
        var max = 0;
        for (var i = 44; i < wav.Length; i += 2) max = Math.Max(max, Math.Abs((int)BitConverter.ToInt16(wav, i)));
        return max;
    }

    [Fact]
    public void FindsWhatsAppStyleStickerFiles()
    {
        var root = Fixtures.TempDirectory();
        Directory.CreateDirectory(Path.Combine(root, "stickers", "deep"));
        File.WriteAllBytes(Path.Combine(root, "a.webp"), new byte[100]);
        File.WriteAllBytes(Path.Combine(root, "stickers", "deep", "b.WEBP"), new byte[100]);
        File.WriteAllBytes(Path.Combine(root, "big.webp"), new byte[1_200_000]);
        File.WriteAllBytes(Path.Combine(root, "photo.jpg"), new byte[100]);
        File.SetLastWriteTimeUtc(Path.Combine(root, "a.webp"), DateTime.UtcNow.AddDays(-2));

        var result = StickerFileScanner.FindStickerFiles([root, Path.Combine(root, "missing")]);
        Assert.False(result.PermissionDenied);
        Assert.Equal(["b.WEBP", "a.webp"], result.Files.Select(Path.GetFileName));
        Assert.Single(StickerFileScanner.FindStickerFiles([root], limit: 1).Files);
    }

    [Fact]
    public void LocatesTheModel()
    {
        var app = Fixtures.TempDirectory();
        var local = Fixtures.TempDirectory();
        var candidates = ModelCatalog.CandidateDirectories(app, local);
        Assert.Equal(Path.Combine(app, "models", "phi-3.5-vision"), candidates[0]);
        Assert.Equal(Path.Combine(local, "StickerSense", "models", "phi-3.5-vision"), candidates[1]);
        Assert.Null(ModelCatalog.FindInstalled(candidates));

        var user = candidates[1];
        Directory.CreateDirectory(user);
        File.WriteAllText(Path.Combine(user, "phi-3.5-v-instruct-text.onnx"), "x");
        Assert.True(ModelCatalog.IsPartial(user));
        foreach (var file in ModelCatalog.RequiredFiles) File.WriteAllText(Path.Combine(user, file), "x");
        Assert.Equal(user, ModelCatalog.FindInstalled(candidates));
        Assert.False(ModelCatalog.IsPartial(user));
        Assert.Equal(ModelVariant.DirectML, ModelCatalog.DetectVariant(user)); // small text graph = GPU build

        ModelCatalog.WriteMarker(user, ModelVariant.Cpu);
        Assert.Equal(ModelVariant.Cpu, ModelCatalog.DetectVariant(user));
        Assert.Equal("cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4", ModelCatalog.HuggingFaceFolder(ModelVariant.Cpu));
        Assert.Equal("gpu/gpu-int4-rtn-block-32", ModelCatalog.HuggingFaceFolder(ModelVariant.DirectML));
    }

    [Fact]
    public void SwitchingModelBuildsClearsTheOtherBuildsFiles()
    {
        var dir = Fixtures.TempDirectory();
        foreach (var file in ModelCatalog.RequiredFiles) File.WriteAllText(Path.Combine(dir, file), "x");
        File.WriteAllText(Path.Combine(dir, "phi-3.5-v-instruct-text.onnx.data.part"), "half");
        ModelCatalog.WriteMarker(dir, ModelVariant.Cpu);

        HuggingFaceDownloader.StartVariant(dir, ModelVariant.Cpu); // same build: keep everything to resume
        Assert.True(ModelCatalog.IsComplete(dir));

        HuggingFaceDownloader.StartVariant(dir, ModelVariant.DirectML);
        Assert.False(ModelCatalog.IsComplete(dir));
        Assert.False(File.Exists(Path.Combine(dir, "phi-3.5-v-instruct-text.onnx.data.part")));
        Assert.False(File.Exists(Path.Combine(dir, ModelCatalog.MarkerFile)));
    }

    [Fact]
    public void ParsesHuggingFaceListings()
    {
        const string json = """
            [{"type":"file","oid":"a","size":2121,"path":"gpu/gpu-int4-rtn-block-32/genai_config.json"},
             {"type":"file","oid":"b","size":135,"path":"gpu/gpu-int4-rtn-block-32/phi-3.5-v-instruct-text.onnx.data",
              "lfs":{"oid":"c","size":2120189952,"pointerSize":135}},
             {"type":"directory","oid":"d","size":0,"path":"gpu/gpu-int4-rtn-block-32/sub"}]
            """;
        var files = HuggingFaceDownloader.ParseTree(Encoding.UTF8.GetBytes(json));
        Assert.Equal(2, files.Count);
        Assert.Equal(new RemoteFile("gpu/gpu-int4-rtn-block-32/genai_config.json", 2121), files[0]);
        Assert.Equal(2_120_189_952, files[1].Size);
    }

    [Fact]
    public void AnnouncesProgressEveryTenPercent()
    {
        var milestones = new PercentMilestones();
        var spoken = new[] { 0, 3, 9, 10, 12, 19, 20, 55, 54, 100 }.Select(milestones.Next).Where(m => m is not null).ToList();
        Assert.Equal([10, 20, 50, 100], spoken);
        Assert.Equal(25, new DownloadProgress(25, 100, "x").Percent);
        Assert.Equal(0, new DownloadProgress(5, 0, "x").Percent);
    }
}
