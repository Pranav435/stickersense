using System.Diagnostics;
using System.IO;
using StickerSense.App.Engine;
using StickerSense.App.Platform;
using StickerSense.Core;

namespace StickerSense.App.Diagnostics;

/// <summary>
/// <c>StickerSense.exe --make-sticker "corgi saying thank you" [--count 4] [--model fast|better] [--out folder] [--download]</c>:
/// makes stickers the way the picker does, without any windows, printing each step and how long it
/// took. With the description model installed it also plans captions and describes each sticker.
/// For testing the sticker maker (CI runs it) and for bug reports; it doesn't touch the library.
/// </summary>
internal static class MakeStickerCommand
{
    public static int Run(string[] args)
    {
        var output = ConsoleOutput.Open();
        var code = 1;
        // A WPF application, so WinRT (OCR) behaves as in the app.
        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try
            {
                code = await RunAsync(args, output);
            }
            catch (Exception e)
            {
                output.WriteLine($"FAILED: {e}");
            }
            finally
            {
                app.Shutdown();
            }
        };
        app.Run();
        return code;
    }

    private static string? Value(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static async Task<int> RunAsync(string[] args, TextWriter output)
    {
        var query = Value(args, "--make-sticker");
        if (string.IsNullOrWhiteSpace(query))
        {
            output.WriteLine("Usage: StickerSense.exe --make-sticker \"what to draw\" [--count 4] [--model fast|better] [--out folder] [--download]");
            return 64;
        }
        var count = int.TryParse(Value(args, "--count"), out var n) ? n : StickerIdeas.Variations;
        var model = Value(args, "--model")?.ToLowerInvariant() == "better" ? StickerModel.Better : StickerModel.Fast;
        var folder = Path.GetFullPath(Value(args, "--out") ?? Environment.CurrentDirectory);
        var log = new ListLog(line => output.WriteLine($"  log: {line}"));
        var modelsDirectory = AppServices.StickerModelsDirectory;
        output.WriteLine($"sd-cli: {SdCliGenerator.ExecutablePath ?? "MISSING"}");
        output.WriteLine($"Drawing on: {(SdCliGenerator.UsesGraphicsCard ? GpuInfo.RecommendedAdapter()?.Name : "the processor")}");

        if (!StickerMakerCatalog.IsInstalled(model, modelsDirectory))
        {
            if (!args.Contains("--download", StringComparer.OrdinalIgnoreCase))
            {
                output.WriteLine($"The {model} sticker model isn't downloaded. Add --download to fetch it ({StickerMakerCatalog.SpokenSize(model)}).");
                return 2;
            }
            output.WriteLine($"Downloading the {model} sticker model to {modelsDirectory}");
            var milestones = new PercentMilestones(10);
            var watch = Stopwatch.StartNew();
            await StickerMakerCatalog.DownloadAsync(model, modelsDirectory, new Reporter(p =>
            {
                if (milestones.Next(p.Percent) is { } m) output.WriteLine($"  {m}% {p.CurrentFile}");
            }));
            output.WriteLine($"Downloaded in {watch.Elapsed.TotalSeconds:0}s");
        }

        // The description model plans the captions and describes each sticker, when it's installed.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var describerDirectory = ModelCatalog.FindInstalled(ModelCatalog.CandidateDirectories(AppContext.BaseDirectory, localAppData));
        using var describer = describerDirectory is null ? null : new PhiVisionDescriber(() => [describerDirectory], log, new DescriberOptions());
        output.WriteLine(describer is null ? "No description model installed: planning with plain rules, not describing." : $"Description model: {describerDirectory}");

        var total = Stopwatch.StartNew();
        var step = Stopwatch.StartNew();
        var idea = await StickerIdeaWriter.IdeasAsync(query, describer, log.Add);
        output.WriteLine($"Plan ({step.Elapsed.TotalSeconds:0.0}s): caption \"{idea.Caption}\"; pictures: {string.Join(" | ", idea.Pictures)}");

        using var masker = new IsNetMasker(StickerMakerCatalog.CutOutPath(modelsDirectory), log);
        var factory = new StickerFactory(new SdCliGenerator(model, modelsDirectory, log), masker);
        using var search = describer is null ? null : new PickerSearch(
            new StickerPipeline(describer, new StickerLibrary(Path.Combine(Path.GetTempPath(), "StickerSense", $"make-test-{Guid.NewGuid():N}")), new WindowsOcrReader()),
            Path.Combine(Path.GetTempPath(), "StickerSense", "giphy"));
        var made = 0;
        for (var number = 1; number <= count; number++)
        {
            var picture = idea.Pictures[(number - 1) % idea.Pictures.Count];
            step.Restart();
            try
            {
                var sticker = await factory.MakeAsync(picture, idea.Caption, query, number, folder);
                var drawn = step.Elapsed.TotalSeconds;
                if (search is not null) sticker = sticker with { Reading = await search.DescribeAsync(sticker) };
                output.WriteLine($"Sticker {number}: made in {drawn:0.0}s, described in {step.Elapsed.TotalSeconds - drawn:0.0}s: {sticker.Label} -> {Path.GetFileName(sticker.FilePath)}");
                made++;
            }
            catch (Exception e) when (e is StickerMakerException or ImagePrepException or IOException or DescribeException)
            {
                output.WriteLine($"Sticker {number} FAILED: {e.Message}");
            }
        }
        output.WriteLine($"Made {made} of {count} in {total.Elapsed.TotalSeconds:0.0}s, in {folder}");
        return made == count ? 0 : 1;
    }

    /// <summary>Reports progress on the downloading thread (Progress&lt;T&gt; would post to the UI thread).</summary>
    private sealed class Reporter(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }
}
