using System.Diagnostics;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using StickerSense.App.Platform;
using StickerSense.Core;

namespace StickerSense.App.Engine;

/// <summary>
/// Draws pictures with stable-diffusion.cpp's sd-cli (MIT), shipped in the <c>sd</c> folder next to
/// StickerSense.exe. One process per picture: it costs about a second, keeps a crash or a full
/// memory away from StickerSense, and gives all its memory back afterwards. It uses a graphics
/// card with memory of its own through Vulkan, and the processor otherwise (or when Vulkan fails).
/// </summary>
internal sealed class SdCliGenerator(StickerModel model, string modelsDirectory, IActivityLog log) : IImageGenerator
{
    /// <summary>Set once Vulkan has failed in this session: the processor from then on.</summary>
    private static volatile bool vulkanFailed;

    public static string Folder => Path.Combine(AppContext.BaseDirectory, "sd");

    public static string? ExecutablePath => File.Exists(Path.Combine(Folder, "sd-cli.exe")) ? Path.Combine(Folder, "sd-cli.exe") : null;

    /// <summary>A graphics card worth using (its own memory) and the Vulkan build to use it.</summary>
    public static bool UsesGraphicsCard =>
        !vulkanFailed && File.Exists(Path.Combine(Folder, "ggml-vulkan.dll")) && GpuInfo.RecommendedAdapter() is not null;

    public string RunningOn => UsesGraphicsCard ? $"the graphics card ({GpuInfo.RecommendedAdapter()?.Name})" : "the processor";

    public async Task<Image<Rgba32>> GenerateAsync(string prompt, int seed, CancellationToken ct)
    {
        var exe = ExecutablePath ?? throw new StickerMakerException("The sticker maker is missing from this copy of StickerSense. Download StickerSense again.");
        var gpu = UsesGraphicsCard;
        try
        {
            return await RunAsync(exe, prompt, seed, gpu, ct).ConfigureAwait(false);
        }
        catch (StickerMakerException e) when (gpu && !ct.IsCancellationRequested)
        {
            vulkanFailed = true;
            log.Add($"Sticker maker: the graphics card failed, using the processor from now on. {e.Message}");
            return await RunAsync(exe, prompt, seed, gpu: false, ct).ConfigureAwait(false);
        }
    }

    private async Task<Image<Rgba32>> RunAsync(string exe, string prompt, int seed, bool gpu, CancellationToken ct)
    {
        var work = Path.Combine(Path.GetTempPath(), "StickerSense", "make");
        Directory.CreateDirectory(work);
        var output = Path.Combine(work, $"{Guid.NewGuid():N}.png");
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Folder,
        };
        foreach (var argument in StickerMakerCatalog.Arguments(model, modelsDirectory)) start.ArgumentList.Add(argument);
        foreach (var argument in new[] { "-p", prompt, "-W", "512", "-H", "512", "-s", seed.ToString(System.Globalization.CultureInfo.InvariantCulture), "-o", output })
        {
            start.ArgumentList.Add(argument);
        }
        if (!gpu)
        {
            start.ArgumentList.Add("--backend");
            start.ArgumentList.Add("cpu");
        }
        var tail = new Queue<string>();
        void Keep(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (tail)
            {
                tail.Enqueue(line);
                if (tail.Count > 8) tail.Dequeue();
            }
        }
        var watch = Stopwatch.StartNew();
        using var process = Process.Start(start) ?? throw new StickerMakerException("The sticker maker didn't start.");
        process.OutputDataReceived += (_, e) => Keep(e.Data);
        process.ErrorDataReceived += (_, e) => Keep(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            throw;
        }
        try
        {
            if (process.ExitCode != 0 || !File.Exists(output))
            {
                string last;
                lock (tail) last = string.Join(" | ", tail);
                throw new StickerMakerException($"the image model stopped (code {process.ExitCode}) on {(gpu ? "the graphics card" : "the processor")}: {last}");
            }
            var frames = ImagePrep.LoadFrames(output, 1);
            log.Add($"Sticker maker: drew a picture in {watch.Elapsed.TotalSeconds:0.0}s on {(gpu ? "the graphics card" : "the processor")}");
            return frames[0];
        }
        finally
        {
            try
            {
                File.Delete(output);
            }
            catch (IOException)
            {
            }
        }
    }
}

/// <summary>Making a sticker failed in a way worth telling the user.</summary>
internal sealed class StickerMakerException(string message) : UserFacingException(message);
