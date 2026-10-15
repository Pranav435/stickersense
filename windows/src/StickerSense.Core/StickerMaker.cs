using System.Security.Cryptography;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace StickerSense.Core;

/// <summary>The image model that makes stickers.</summary>
public enum StickerModel
{
    /// <summary>SDXS-512 (IDKiro, OpenRAIL++): one small step, a few seconds a sticker.</summary>
    Fast,
    /// <summary>
    /// SD-Turbo (Stability AI Community License) with the tiny TAESD decoder: follows the search
    /// more closely and varies more, at about twice the download.
    /// </summary>
    Better,
}

/// <summary>One file to download, checked against its size and SHA-256.</summary>
public sealed record ModelFile(string Name, string Url, long Size, string Sha256);

/// <summary>
/// The sticker maker's files. None ships with StickerSense: the chosen model and the cut-out
/// model are downloaded from Hugging Face and GitHub the first time they're needed. The Mac app
/// uses the same image models (StickerMaker.swift); it cuts out with Apple's Vision instead.
/// </summary>
public static class StickerMakerCatalog
{
    /// <summary>ISNet trained on anime and cartoons (Apache 2.0), from rembg's model release.</summary>
    public static readonly ModelFile CutOutModel = new(
        "isnet-anime.onnx",
        "https://github.com/danielgatis/rembg/releases/download/v0.0.0/isnet-anime.onnx",
        176_069_933, "f15622d853e8260172812b657053460e20806f04b9e05147d49af7bed31a6e99");

    public const string CutOutFolder = "isnet-anime";

    public static IReadOnlyList<ModelFile> Files(StickerModel model) => model switch
    {
        StickerModel.Better =>
        [
            new("sd_turbo-f16-q8_0.gguf",
                "https://huggingface.co/Green-Sky/SD-Turbo-GGUF/resolve/19a31586d02d64a73b4419bc193b3ecfaf38e1f0/sd_turbo-f16-q8_0.gguf",
                2_023_745_376, "d50be7655f0a554cf8041c145d88b210bd5f3c545423119dee62ae08cae51580"),
            new("taesd.safetensors",
                "https://huggingface.co/madebyollin/taesd/resolve/614f76814bbe30edbe2e627ace1c2234c81a2c0e/diffusion_pytorch_model.safetensors",
                9_793_292, "db169d69145ec4ff064e49d99c95fa05d3eb04ee453de35824a6d0f325513549"),
        ],
        _ =>
        [
            new("sdxs.safetensors",
                "https://huggingface.co/akleine/sdxs-512/resolve/17ef024d73ebee1db9b0643f9c90454dae6c5772/sdxs.safetensors",
                882_587_118, "6cca5bfd11b588cdfb4602018c7e623d24c95fdfdc5ab2d4b9e6978b3186980f"),
        ],
    };

    public static string FolderName(StickerModel model) => model == StickerModel.Better ? "sd-turbo" : "sdxs-512";

    public static string Title(StickerModel model) => model == StickerModel.Better ? "Better (SD-Turbo, 2.2 GB)" : "Fast (SDXS, 1.1 GB)";

    /// <summary>The whole download (the model and the cut-out model), to say out loud.</summary>
    public static string SpokenSize(StickerModel model) => model == StickerModel.Better ? "2.2 gigabytes" : "1.1 gigabytes";

    public static string Credit(StickerModel model) => model == StickerModel.Better
        ? "SD-Turbo by Stability AI, under the Stability AI Community License. Powered by Stability AI."
        : "SDXS-512 by IDKiro and others, under the OpenRAIL++ licence.";

    /// <summary><c>%LOCALAPPDATA%\StickerSense\models</c>, next to the description model.</summary>
    public static string ModelsDirectory(string localAppData) => Path.Combine(localAppData, "StickerSense", "models");

    /// <summary>Every file the model needs, with where it goes.</summary>
    public static IReadOnlyList<(ModelFile File, string Path)> Needed(StickerModel model, string modelsDirectory) =>
        Files(model).Select(f => (f, Path.Combine(modelsDirectory, FolderName(model), f.Name)))
            .Append((CutOutModel, Path.Combine(modelsDirectory, CutOutFolder, CutOutModel.Name)))
            .ToList();

    public static bool IsInstalled(StickerModel model, string modelsDirectory) =>
        Needed(model, modelsDirectory).All(n => File.Exists(n.Path) && new FileInfo(n.Path).Length == n.File.Size);

    public static string CutOutPath(string modelsDirectory) => Path.Combine(modelsDirectory, CutOutFolder, CutOutModel.Name);

    /// <summary>sd-cli's options for the model (both are one-step models: guidance off).</summary>
    public static IReadOnlyList<string> Arguments(StickerModel model, string modelsDirectory)
    {
        var folder = Path.Combine(modelsDirectory, FolderName(model));
        return model == StickerModel.Better
            ? ["-m", Path.Combine(folder, "sd_turbo-f16-q8_0.gguf"), "--taesd", Path.Combine(folder, "taesd.safetensors"), "--cfg-scale", "1", "--steps", "1"]
            : ["-m", Path.Combine(folder, "sdxs.safetensors"), "--cfg-scale", "1", "--steps", "1"];
    }

    /// <summary>Downloads whatever the model still needs, checking each file's SHA-256.</summary>
    public static async Task DownloadAsync(StickerModel model, string modelsDirectory, IProgress<DownloadProgress>? progress, HuggingFaceDownloader? downloader = null, CancellationToken ct = default)
    {
        downloader ??= new HuggingFaceDownloader();
        await downloader.DownloadFilesAsync(Needed(model, modelsDirectory), progress, ct).ConfigureAwait(false);
    }

    internal static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

/// <summary>Draws one picture for a prompt (stable-diffusion.cpp's sd-cli in the app).</summary>
public interface IImageGenerator
{
    Task<Image<Rgba32>> GenerateAsync(string prompt, int seed, CancellationToken ct);
}

/// <summary>Finds a picture's subject: how much of each pixel is subject, 0 to 1 (null when unsure).</summary>
public interface ISubjectMasker
{
    Task<float[]?> MaskAsync(Image<Rgba32> image, CancellationToken ct);
}

/// <summary>Turns picture ideas into finished sticker files: draw, cut out, caption, border.</summary>
public sealed class StickerFactory(IImageGenerator generator, ISubjectMasker? masker)
{
    /// <summary>One finished sticker, saved as a PNG in <paramref name="folder"/>. Not yet described.</summary>
    public async Task<GeneratedSticker> MakeAsync(string picture, string caption, string query, int number, string folder, CancellationToken ct = default)
    {
        using var image = await generator.GenerateAsync(StickerIdeas.Prompt(picture), Random.Shared.Next(1, 1_000_000), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var mask = masker is null ? null : await masker.MaskAsync(image, ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            mask ??= StickerArt.PlainBackgroundMask(image);
            using var subject = mask is null ? image.Frames.CloneFrame(0) : StickerArt.ApplyMask(image, mask);
            using var art = StickerArt.Compose(subject, caption);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"{Guid.NewGuid():N}.png");
            File.WriteAllBytes(path, ImagePrep.PngData(art));
            return new GeneratedSticker(Guid.NewGuid(), query, caption, picture, path, number);
        }, ct).ConfigureAwait(false);
    }
}

/// <summary>A language model's plain-text answer (the description model, used without a picture).</summary>
public interface ITextCompleter
{
    /// <summary>The reply that follows <paramref name="replyStart"/>.</summary>
    Task<string> CompleteAsync(string instructions, string prompt, string replyStart, int maxNewTokens, int stopAfterLines, CancellationToken ct);
}

/// <summary>Plans stickers with the on-device language model, falling back to plain rules.</summary>
public static class StickerIdeaWriter
{
    public static async Task<StickerIdea> IdeasAsync(string query, ITextCompleter? model, Action<string>? log = null, CancellationToken ct = default)
    {
        query = query.Trim();
        if (model is null) return StickerIdeas.Fallback(query);
        try
        {
            var reply = await model.CompleteAsync(StickerIdeas.Instructions, StickerIdeas.LinesRequest(query), StickerIdeas.ReplyStart,
                maxNewTokens: 110, stopAfterLines: StickerIdeas.ReplyLines, ct).ConfigureAwait(false);
            return StickerIdeas.ParseLines(StickerIdeas.ReplyStart + reply, query);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log?.Invoke($"Planning stickers without the language model: {e.Message}");
            return StickerIdeas.Fallback(query);
        }
    }
}
