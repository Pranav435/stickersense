using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using StickerSense.App.Engine;
using StickerSense.App.Platform;
using StickerSense.App.Speech;
using StickerSense.Core;

namespace StickerSense.App.Diagnostics;

/// <summary>
/// <c>StickerSense.exe --self-test</c>: checks every part of StickerSense that can be checked
/// without a person at the keyboard, and prints a JSON report. Useful for bug reports.
/// </summary>
/// <remarks>
/// Options: <c>--out report.json</c>, <c>--model-dir folder</c> (repeatable; default: the installed
/// model), <c>--device auto|cpu|gpu</c>, <c>--no-models</c>, <c>--timeout-minutes 10</c>.
/// </remarks>
internal sealed class SelfTest(string[] args, TextWriter output)
{
    private readonly ListLog log = new(line => output.WriteLine($"  log: {line}"));

    public static int Run(string[] args)
    {
        var output = ConsoleOutput.Open();
        var test = new SelfTest(args, output);
        JsonObject? report = null;
        // A WPF application, so pack:// resources (the icon) and WinRT (OCR) behave as in the app.
        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try
            {
                report = await test.RunAsync();
            }
            finally
            {
                app.Shutdown();
            }
        };
        app.Run();
        var json = report?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "{}";
        output.WriteLine(json);
        if (Value(args, "--out") is { } path) File.WriteAllText(path, json, Encoding.UTF8);
        return report?["ok"]?.GetValue<bool>() == true ? 0 : 1;
    }

    private static string? Value(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private IEnumerable<string> Values(string name)
    {
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) yield return args[i + 1];
        }
    }

    private async Task<JsonObject> RunAsync()
    {
        var report = new JsonObject
        {
            ["app"] = new JsonObject
            {
                ["version"] = typeof(SelfTest).Assembly.GetName().Version?.ToString(),
                ["path"] = Environment.ProcessPath,
                ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
                ["dotnet"] = RuntimeInformation.FrameworkDescription,
                ["startedAt"] = DateTimeOffset.Now.ToString("O"),
            },
            ["os"] = new JsonObject
            {
                ["description"] = RuntimeInformation.OSDescription,
                ["version"] = Environment.OSVersion.Version.ToString(),
                ["session"] = Process.GetCurrentProcess().SessionId,
                ["interactive"] = Environment.UserInteractive,
                ["uiCulture"] = System.Globalization.CultureInfo.CurrentUICulture.Name,
            },
        };
        var ok = true;
        async Task Section(string name, Func<Task<JsonObject>> check)
        {
            output.WriteLine($"== {name}");
            var watch = Stopwatch.StartNew();
            JsonObject result;
            try
            {
                result = await check();
            }
            catch (Exception e)
            {
                result = new JsonObject { ["ok"] = false, ["error"] = e.ToString() };
            }
            result["ms"] ??= watch.ElapsedMilliseconds;
            if (result["ok"]?.GetValue<bool>() == false) ok = false;
            report[name] = result;
            output.WriteLine($"   {(result["ok"]?.GetValue<bool>() == false ? "FAILED" : "ok")} in {watch.ElapsedMilliseconds} ms");
        }

        await Section("gpu", () => Task.FromResult(Gpu()));
        await Section("screenReader", () => Task.FromResult(ScreenReaderInfo()));
        await Section("icon", () => Task.FromResult(Icon()));
        await Section("decode", () => Task.FromResult(Decode()));
        await Section("giphy", Giphy);
        await Section("library", () => Task.FromResult(Library()));
        var ocr = new WindowsOcrReader();
        await Section("ocr", () => Ocr(ocr));
        if (!args.Contains("--no-models")) await Section("models", () => Models(ocr));
        report["ok"] = ok;
        report["log"] = new JsonArray(log.Lines.Select(l => (JsonNode)l).ToArray());
        return report;
    }

    // MARK: Sections

    private static JsonObject Gpu()
    {
        var adapters = GpuInfo.Adapters();
        return new JsonObject
        {
            ["ok"] = true,
            ["adapters"] = new JsonArray(adapters.Select(a => (JsonNode)new JsonObject
            {
                ["name"] = a.Name.Trim(),
                ["vendorId"] = $"0x{a.VendorId:X4}",
                ["dedicatedMB"] = (long)(a.DedicatedMemory / (1024 * 1024)),
                ["software"] = a.IsSoftware,
            }).ToArray()),
            ["recommended"] = GpuInfo.RecommendedAdapter() is null ? "Cpu" : "DirectML",
        };
    }

    private static JsonObject ScreenReaderInfo()
    {
        static bool Running(string name)
        {
            var processes = Process.GetProcessesByName(name);
            foreach (var p in processes) p.Dispose();
            return processes.Length > 0;
        }
        var nvda = NvdaController.Load(AppContext.BaseDirectory);
        return new JsonObject
        {
            ["ok"] = true,
            ["spiScreenReaderFlag"] = ScreenReaders.SystemFlag,
            ["nvdaProcess"] = Running("nvda"),
            ["jawsProcess"] = Running("jfw"),
            ["narratorProcess"] = Running("Narrator"),
            ["detected"] = ScreenReaders.Detect().ToString(),
            ["nvdaControllerDllPresent"] = NvdaController.FileNames.Any(n => File.Exists(Path.Combine(AppContext.BaseDirectory, n))),
            ["nvdaControllerDllLoaded"] = nvda.IsLoaded,
            ["nvdaControllerDllPath"] = nvda.LoadedFrom,
            ["speechRoute"] = ScreenReaders.Detect() switch
            {
                ScreenReaderKind.Nvda when nvda.IsLoaded => "NVDA controller client",
                // NVDA only reads notifications from the app in front, which is the chat, not StickerSense.
                ScreenReaderKind.Nvda => "Windows voice (SAPI); UI Automation notification while a StickerSense window is in front",
                ScreenReaderKind.Narrator or ScreenReaderKind.Other => "UI Automation notification",
                ScreenReaderKind.Jaws => "JAWS API",
                _ => "Windows voice (SAPI)",
            },
        };
    }

    private static JsonObject Icon()
    {
        var image = AppIcon.Image as System.Windows.Media.Imaging.BitmapSource;
        using var tray = AppIcon.TrayIcon();
        return new JsonObject
        {
            ["ok"] = image is not null && tray is not null,
            ["windowIconPixels"] = image?.PixelWidth,
            ["trayIconSize"] = tray is null ? null : $"{tray.Width}x{tray.Height}",
        };
    }

    private static JsonObject Decode()
    {
        var dir = Path.Combine(Path.GetTempPath(), "StickerSense", "self-test");
        Directory.CreateDirectory(dir);
        using var still = TestStickers.Birthday();
        var frames = TestStickers.Animated();
        var results = new JsonObject();
        var ok = true;
        void Check(string name, Action<string> write, int expectedFrames)
        {
            var path = Path.Combine(dir, name);
            write(path);
            var watch = Stopwatch.StartNew();
            var decoded = ImagePrep.LoadFrames(path, maxFrames: 8);
            var count = decoded.Count;
            foreach (var frame in decoded) frame.Dispose();
            ok &= count == expectedFrames;
            results[name] = new JsonObject { ["frames"] = count, ["expected"] = expectedFrames, ["decodeMs"] = watch.ElapsedMilliseconds, ["bytes"] = new FileInfo(path).Length };
        }
        Check("still.png", p => still.SaveAsPng(p), 1);
        Check("still.webp", p => still.SaveAsWebp(p), 1);
        Check("animated.gif", p => File.WriteAllBytes(p, ImagePrep.GifData(frames)), 3);
        Check("animated.webp", p =>
        {
            using var movie = frames[0].Clone();
            foreach (var f in frames.Skip(1)) movie.Frames.AddFrame(f.Frames.RootFrame);
            movie.SaveAsWebp(p, new WebpEncoder { FileFormat = WebpFileFormatType.Lossless });
        }, 3);
        var pasted = ImagePrep.ShareableFile(Path.Combine(dir, "animated.webp"), Path.Combine(dir, "paste"));
        results["webpToGifForPasting"] = Path.GetFileName(pasted);
        foreach (var f in frames) f.Dispose();
        results["ok"] = ok && pasted.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
        return results;
    }

    private sealed class CannedGiphy : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            const string json = """
                {"data":[{"id":"abc","title":"Happy Birthday GIF by Disney","alt_text":"","images":{
                  "fixed_width":{"url":"https://media.giphy.com/media/abc/200w.gif"},
                  "fixed_width_downsampled":{"url":"https://media.giphy.com/media/abc/200w_d.gif"},
                  "downsized":{"url":"https://media.giphy.com/media/abc/giphy-downsized.gif"}}}],
                 "pagination":{"total_count":120,"count":1,"offset":0}}
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    private static async Task<JsonObject> Giphy()
    {
        using var http = new HttpClient(new CannedGiphy());
        var page = await new GiphyClient("canned", http: http).SearchAsync("birthday", GiphyKind.Gif);
        var item = page.Items.Single();
        return new JsonObject
        {
            ["ok"] = item.CleanTitle == "Happy Birthday" && item.SmallUrl is not null && page.HasMore && page.NextOffset == 1,
            ["title"] = item.CleanTitle,
            ["smallUrl"] = item.SmallUrl,
            ["nextOffset"] = page.NextOffset,
            ["hasMore"] = page.HasMore,
        };
    }

    private static JsonObject Library()
    {
        // The real location's parent, in a folder of its own so the user's library is untouched.
        var dir = Path.Combine(StickerLibrary.DefaultDirectory(), "self-test-library");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Guid id;
        using (var library = new StickerLibrary(dir))
        using (var sticker = TestStickers.Birthday())
        {
            var record = library.Remember([sticker], new Fingerprint(sticker), new StickerReading(StickerKind.Sticker, "Party face", "HAPPY BDAY", "celebrating"),
                "HAPPY BDAY", new StickerSource.Received("self-test"));
            id = record.Id;
            library.SaveNow();
        }
        using var reloaded = new StickerLibrary(dir);
        using var again = TestStickers.Birthday();
        var match = reloaded.Match(new Fingerprint(again), "HAPPY BDAY");
        var imageSaved = reloaded.Record(id)?.ImageFile is { } file && File.Exists(Path.Combine(reloaded.ImagesDirectory, file));
        var json = File.ReadAllText(Path.Combine(dir, "library.json"));
        var result = new JsonObject
        {
            ["ok"] = reloaded.Count == 1 && match?.Id == id && imageSaved,
            ["path"] = dir,
            ["reloadedCount"] = reloaded.Count,
            ["cacheHit"] = match?.Id == id,
            ["imageSaved"] = imageSaved,
            ["jsonBytes"] = json.Length,
        };
        reloaded.Dispose();
        Directory.Delete(dir, recursive: true);
        return result;
    }

    private async Task<JsonObject> Ocr(WindowsOcrReader ocr)
    {
        using var caption = TestStickers.CaptionOnly("HAPPY BDAY");
        var watch = Stopwatch.StartNew();
        var first = await ocr.ReadLinesAsync(caption);
        var firstMs = watch.ElapsedMilliseconds;
        watch.Restart();
        var warm = await ocr.ReadLinesAsync(caption);
        var warmMs = watch.ElapsedMilliseconds;
        using var sticker = TestStickers.Birthday();
        var onSticker = StickerText.TextOf(await ocr.ReadLinesAsync(sticker), null);
        var text = StickerText.TextOf(warm, null);
        return new JsonObject
        {
            ["ok"] = ocr.IsAvailable && StickerText.Normalized(text).Contains("happybday"),
            ["available"] = ocr.IsAvailable,
            ["language"] = ocr.LanguageName,
            ["firstCallMs"] = firstMs,
            ["warmCallMs"] = warmMs,
            ["firstCallText"] = StickerText.TextOf(first, null),
            ["text"] = text,
            ["readsHappyBday"] = StickerText.Normalized(text).Contains("happybday"),
            ["textOnSticker"] = onSticker,
        };
    }

    private async Task<JsonObject> Models(WindowsOcrReader ocr)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dirs = Values("--model-dir").ToList();
        if (dirs.Count == 0 && ModelCatalog.FindInstalled(ModelCatalog.CandidateDirectories(AppContext.BaseDirectory, localAppData)) is { } installed)
        {
            dirs.Add(installed);
        }
        var forced = Value(args, "--device");
        var timeout = TimeSpan.FromMinutes(int.TryParse(Value(args, "--timeout-minutes"), out var minutes) ? minutes : 10);
        var runs = new JsonArray();
        var ok = dirs.Count > 0;
        foreach (var dir in dirs)
        {
            if (!ModelCatalog.IsComplete(dir))
            {
                runs.Add(new JsonObject { ["dir"] = dir, ["ok"] = false, ["error"] = "model files missing or incomplete" });
                ok = false;
                continue;
            }
            var variant = ModelCatalog.DetectVariant(dir);
            var device = forced switch
            {
                "cpu" => EngineDevice.ProcessorOnly,
                "gpu" => EngineDevice.GraphicsCard,
                _ => variant == ModelVariant.Cpu ? EngineDevice.ProcessorOnly : EngineDevice.Automatic,
            };
            var run = await ModelRun(dir, variant, device, ocr, timeout);
            ok &= run["ok"]?.GetValue<bool>() == true;
            runs.Add(run);
        }
        return new JsonObject { ["ok"] = ok, ["runs"] = runs, ["note"] = dirs.Count == 0 ? "no model installed" : null };
    }

    private async Task<JsonObject> ModelRun(string dir, ModelVariant variant, EngineDevice device, WindowsOcrReader ocr, TimeSpan timeout)
    {
        output.WriteLine($"   model {variant} in {dir}, device {device}");
        var result = new JsonObject { ["dir"] = dir, ["variant"] = variant.ToString(), ["device"] = device.ToString() };
        using var describer = new PhiVisionDescriber(() => [dir], log, new DescriberOptions());
        describer.Reload(device);
        var watch = Stopwatch.StartNew();
        try
        {
            await describer.PrewarmAsync().WaitAsync(timeout);
        }
        catch (Exception e)
        {
            result["ok"] = false;
            result["error"] = $"load failed: {e.Message}";
            result["status"] = describer.Status.Explanation;
            return result;
        }
        result["loadMs"] = watch.ElapsedMilliseconds;
        // The loaded model is recorded just after the load task finishes.
        for (var i = 0; i < 50 && describer.RunningOn is null; i++) await Task.Delay(20);
        result["runningOn"] = describer.RunningOn;
        result["engineMemoryMB"] = describer.EngineMemoryMegabytes;
        result["appMemoryMB"] = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);

        // A fresh, empty library each run, so nothing is answered from an earlier run's cache.
        var libraryDir = Path.Combine(Path.GetTempPath(), "StickerSense-self-test-" + Guid.NewGuid().ToString("N"));
        var library = new StickerLibrary(libraryDir);
        var pipeline = new StickerPipeline(describer, library, new TimeLimitedOcrReader(ocr, TimeSpan.FromSeconds(1.5)));
        var stickers = new JsonArray();
        var ok = true;
        foreach (var (name, frames) in new[] { ("static birthday sticker", new List<SixLabors.ImageSharp.Image<Rgba32>> { TestStickers.Birthday() }), ("3-frame animated face", TestStickers.Animated()) })
        {
            var entry = new JsonObject { ["name"] = name, ["frames"] = frames.Count };
            try
            {
                using var cts = new CancellationTokenSource(timeout);
                watch.Restart();
                var reading = await pipeline.ReadAsync(frames, DescriptionContext.File, cts.Token);
                entry["quickMs"] = watch.ElapsedMilliseconds;
                entry["quick"] = new JsonObject
                {
                    ["kind"] = reading.Kind.RawValue(),
                    ["subject"] = reading.Subject,
                    ["text"] = reading.Text,
                    ["animated"] = reading.Animated,
                    ["spoken"] = SpokenText.Short(reading),
                };
                output.WriteLine($"   {name}: {SpokenText.Short(reading)} ({entry["quickMs"]} ms)");
                watch.Restart();
                var ocrText = await pipeline.OcrTextAsync(frames, DescriptionContext.File, cts.Token);
                var detail = await describer.DetailedAsync(frames, DescriptionContext.File, ocrText, reading, DescribePriority.Foreground, cts.Token);
                entry["detailMs"] = watch.ElapsedMilliseconds;
                entry["detail"] = detail;
                output.WriteLine($"   detail ({entry["detailMs"]} ms): {detail}");
            }
            catch (Exception e)
            {
                entry["error"] = e.Message;
                ok = false;
            }
            finally
            {
                foreach (var frame in frames) frame.Dispose();
            }
            stickers.Add(entry);
        }
        library.Dispose();
        try
        {
            Directory.Delete(libraryDir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        result["stickers"] = stickers;
        result["ok"] = ok;
        return result;
    }
}
