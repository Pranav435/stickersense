using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace StickerSense.Core;

/// <summary>Which on-device model (int4 ONNX) is installed or wanted.</summary>
public enum ModelVariant
{
    /// <summary>Phi-3.5-vision-instruct for graphics cards via DirectML (gpu/gpu-int4-rtn-block-32).</summary>
    DirectML,
    /// <summary>Phi-3.5-vision-instruct for the processor (cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4).</summary>
    Cpu,
    /// <summary>
    /// Qwen3-VL-2B-Instruct for the processor: about two and a half times as fast as Phi-3.5-vision
    /// there (measured on an i7-11700), with descriptions as good. The default for processors.
    /// </summary>
    Qwen3VL2B,
}

/// <summary>Where the on-device models live and what they consist of.</summary>
public static class ModelCatalog
{
    public const string PhiRepository = "microsoft/Phi-3.5-vision-instruct-onnx";
    public const string QwenRepository = "onnx-community/Qwen3-VL-2B-Instruct-ONNX";
    public const string FolderName = "phi-3.5-vision";
    public const string QwenFolderName = "qwen3-vl-2b";
    public const string MarkerFile = "stickersense-model.json";

    /// <summary>Every model StickerSense can use, the fast processor model first.</summary>
    public static readonly IReadOnlyList<ModelVariant> All = [ModelVariant.Qwen3VL2B, ModelVariant.DirectML, ModelVariant.Cpu];

    public static string Repository(ModelVariant variant) => variant == ModelVariant.Qwen3VL2B ? QwenRepository : PhiRepository;

    public static string HuggingFaceFolder(ModelVariant variant) => variant switch
    {
        ModelVariant.DirectML => "gpu/gpu-int4-rtn-block-32",
        ModelVariant.Qwen3VL2B => "onnxruntime/cpu_and_mobile/cpu-int4-rtn-block-32",
        _ => "cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4",
    };

    /// <summary>The folder (under <c>models</c>) a variant lives in. Both Phi builds share one.</summary>
    public static string LocalFolder(ModelVariant variant) => variant == ModelVariant.Qwen3VL2B ? QwenFolderName : FolderName;

    /// <summary>What to call it out loud.</summary>
    public static string DisplayName(ModelVariant variant) => variant switch
    {
        ModelVariant.Qwen3VL2B => "Qwen3-VL 2B",
        ModelVariant.DirectML => "Phi-3.5-vision for the graphics card",
        _ => "Phi-3.5-vision for the processor",
    };

    /// <summary>The model's own name, whichever build: "Qwen3-VL 2B" or "Phi-3.5-vision".</summary>
    public static string FamilyName(ModelVariant variant) => variant == ModelVariant.Qwen3VL2B ? "Qwen3-VL 2B" : "Phi-3.5-vision";

    /// <summary>Approximate download size, for the Setup window.</summary>
    public static string ApproximateSize(ModelVariant variant) => variant switch
    {
        ModelVariant.Qwen3VL2B => "1.4 gigabytes",
        ModelVariant.DirectML => "2.6 gigabytes",
        _ => "3.2 gigabytes",
    };

    public static readonly IReadOnlyList<string> RequiredFiles =
    [
        "genai_config.json",
        "processor_config.json",
        "tokenizer.json",
        "tokenizer_config.json",
        "special_tokens_map.json",
        "phi-3.5-v-instruct-text.onnx",
        "phi-3.5-v-instruct-text.onnx.data",
        "phi-3.5-v-instruct-vision.onnx",
        "phi-3.5-v-instruct-vision.onnx.data",
        "phi-3.5-v-instruct-embedding.onnx",
        "phi-3.5-v-instruct-embedding.onnx.data",
    ];

    /// <summary>
    /// Lookup order: each model's folder in the bundled release (<c>&lt;app folder&gt;\models\…</c>),
    /// then in the per-user downloads (<c>%LOCALAPPDATA%\StickerSense\models\…</c>). The preferred
    /// model comes first; otherwise the fast processor model (Qwen3-VL 2B), then Phi-3.5-vision.
    /// </summary>
    public static IReadOnlyList<string> CandidateDirectories(string appDirectory, string localAppData, ModelVariant? preferred = null)
    {
        var folders = new List<string> { QwenFolderName, FolderName };
        if (preferred is { } choice)
        {
            folders.Remove(LocalFolder(choice));
            folders.Insert(0, LocalFolder(choice));
        }
        return folders.SelectMany(folder => new[]
        {
            Path.Combine(appDirectory, "models", folder),
            Path.Combine(localAppData, "StickerSense", "models", folder),
        }).ToList();
    }

    /// <summary>Where Setup downloads a model to.</summary>
    public static string UserModelDirectory(string localAppData, ModelVariant variant = ModelVariant.Qwen3VL2B) =>
        Path.Combine(localAppData, "StickerSense", "models", LocalFolder(variant));

    public static string? FindInstalled(IEnumerable<string> candidates) => candidates.FirstOrDefault(IsComplete);

    public static bool IsComplete(string directory) =>
        Directory.Exists(directory)
        && (RequiredFiles.All(f => File.Exists(Path.Combine(directory, f)) && new FileInfo(Path.Combine(directory, f)).Length > 0)
            || (ModelType(directory) is { } type && type != "phi3v" && GenAiFilesPresent(directory)));

    /// <summary>The model type named in a folder's genai_config.json ("phi3v", "qwen3_vl"…), or null.</summary>
    public static string? ModelType(string directory)
    {
        var path = Path.Combine(directory, "genai_config.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("model", out var model) && model.TryGetProperty("type", out var type) ? type.GetString() : null;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>Every model file a genai_config.json names (decoder, vision, embedding), plus the tokenizer, is there.</summary>
    private static bool GenAiFilesPresent(string directory)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "genai_config.json")));
            var model = doc.RootElement.GetProperty("model");
            var files = new List<string> { "tokenizer.json" };
            foreach (var part in new[] { "decoder", "vision", "embedding" })
            {
                if (model.TryGetProperty(part, out var section) && section.TryGetProperty("filename", out var name) && name.GetString() is { } file) files.Add(file);
            }
            return files.Count >= 4 && files.All(f => File.Exists(Path.Combine(directory, f)));
        }
        catch (Exception e) when (e is JsonException or IOException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Some files are there but not all (an interrupted download).</summary>
    public static bool IsPartial(string directory) =>
        Directory.Exists(directory) && !IsComplete(directory) &&
        Directory.EnumerateFiles(directory).Any(f =>
            Path.GetFileName(f).StartsWith("phi-3.5", StringComparison.OrdinalIgnoreCase)
            || f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(f) == ".stickersense-downloading");

    /// <summary>The installed variant: from our marker file, else guessed (the CPU text graph is far larger).</summary>
    public static ModelVariant DetectVariant(string directory)
    {
        var marker = Path.Combine(directory, MarkerFile);
        if (File.Exists(marker))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(marker));
                if (doc.RootElement.TryGetProperty("variant", out var v) && Enum.TryParse<ModelVariant>(v.GetString(), true, out var parsed)) return parsed;
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
            }
        }
        if (ModelType(directory) is "qwen3_vl") return ModelVariant.Qwen3VL2B;
        var text = new FileInfo(Path.Combine(directory, "phi-3.5-v-instruct-text.onnx"));
        return text.Exists && text.Length > 10_000_000 ? ModelVariant.Cpu : ModelVariant.DirectML;
    }

    internal static void WriteMarker(string directory, ModelVariant variant)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["variant"] = variant.ToString(),
            ["repository"] = Repository(variant),
            ["folder"] = HuggingFaceFolder(variant),
            ["downloadedAt"] = DateTimeOffset.UtcNow.ToString("O"),
        });
        File.WriteAllText(Path.Combine(directory, MarkerFile), json);
    }
}

public readonly record struct DownloadProgress(long BytesDone, long BytesTotal, string CurrentFile)
{
    public int Percent => BytesTotal <= 0 ? 0 : (int)Math.Clamp(BytesDone * 100 / BytesTotal, 0, 100);
}

public sealed record RemoteFile(string Path, long Size);

public sealed class ModelDownloadException : UserFacingException
{
    public ModelDownloadException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Downloads a model folder from Hugging Face, resuming partial files.</summary>
public sealed class HuggingFaceDownloader
{
    private readonly HttpClient http;

    public HuggingFaceDownloader(HttpClient? http = null)
    {
        this.http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<IReadOnlyList<RemoteFile>> ListAsync(string repository, string folder, CancellationToken ct = default)
    {
        var uri = $"https://huggingface.co/api/models/{repository}/tree/main/{folder}";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await http.GetAsync(uri, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ModelDownloadException($"Hugging Face didn't list the model files (error {(int)response.StatusCode}).");
        }
        return ParseTree(await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
    }

    internal static IReadOnlyList<RemoteFile> ParseTree(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        using var doc = JsonDocument.ParseValue(ref reader);
        var files = new List<RemoteFile>();
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            if (entry.TryGetProperty("type", out var type) && type.GetString() != "file") continue;
            if (!entry.TryGetProperty("path", out var path) || path.GetString() is not { Length: > 0 } p) continue;
            long size = 0;
            if (entry.TryGetProperty("lfs", out var lfs) && lfs.ValueKind == JsonValueKind.Object && lfs.TryGetProperty("size", out var lfsSize)) size = lfsSize.GetInt64();
            else if (entry.TryGetProperty("size", out var plainSize)) size = plainSize.GetInt64();
            files.Add(new RemoteFile(p, size));
        }
        return files;
    }

    /// <summary>Downloads every file of the variant into <paramref name="destination"/>, skipping complete ones.</summary>
    public async Task DownloadAsync(ModelVariant variant, string destination, IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        var folder = ModelCatalog.HuggingFaceFolder(variant);
        IReadOnlyList<RemoteFile> files;
        try
        {
            files = await ListAsync(ModelCatalog.Repository(variant), folder, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new ModelDownloadException("Couldn't reach Hugging Face. Check the internet connection.", e);
        }
        if (files.Count == 0) throw new ModelDownloadException("Hugging Face listed no model files.");

        Directory.CreateDirectory(destination);
        StartVariant(destination, variant);
        var total = files.Sum(f => f.Size);
        var already = files.Sum(f => ExistingBytes(destination, f));
        EnsureDiskSpace(destination, total - already);

        long done = 0;
        foreach (var file in files)
        {
            var name = Path.GetFileName(file.Path);
            var target = Path.Combine(destination, name);
            if (File.Exists(target) && new FileInfo(target).Length == file.Size)
            {
                done += file.Size;
                progress?.Report(new DownloadProgress(done, total, name));
                continue;
            }
            var before = done;
            await DownloadFileAsync($"https://huggingface.co/{ModelCatalog.Repository(variant)}/resolve/main/{file.Path}", file.Size, target,
                bytes => progress?.Report(new DownloadProgress(before + bytes, total, name)), ct).ConfigureAwait(false);
            done += file.Size;
        }
        ModelCatalog.WriteMarker(destination, variant);
        File.Delete(Path.Combine(destination, InProgressFile));
        progress?.Report(new DownloadProgress(total, total, ""));
    }

    /// <summary>
    /// Downloads files from anywhere (the sticker maker's models), resuming partial ones, into the
    /// paths given, and checks each against its size and SHA-256. Files already there are kept.
    /// </summary>
    public async Task DownloadFilesAsync(IReadOnlyList<(ModelFile File, string Path)> files, IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        var total = files.Sum(f => f.File.Size);
        var already = files.Sum(f => ExistingBytes(f.Path, f.File.Size));
        if (files.FirstOrDefault() is { Path: { } first } && Path.GetDirectoryName(first) is { } folder)
        {
            Directory.CreateDirectory(folder);
            EnsureDiskSpace(folder, total - already);
        }
        long done = 0;
        foreach (var (file, target) in files)
        {
            if (File.Exists(target) && new FileInfo(target).Length == file.Size)
            {
                done += file.Size;
                progress?.Report(new DownloadProgress(done, total, file.Name));
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var before = done;
            await DownloadFileAsync(file.Url, file.Size, target, bytes => progress?.Report(new DownloadProgress(before + bytes, total, file.Name)), ct).ConfigureAwait(false);
            if (!string.Equals(await Task.Run(() => StickerMakerCatalog.Sha256Of(target), ct).ConfigureAwait(false), file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(target);
                throw new ModelDownloadException($"{file.Name} didn't download correctly (its checksum is wrong). Try again.");
            }
            done += file.Size;
        }
        progress?.Report(new DownloadProgress(total, total, ""));
    }

    private static long ExistingBytes(string target, long size)
    {
        var file = new FileInfo(target);
        if (file.Exists && file.Length == size) return size;
        var part = new FileInfo(target + ".part");
        return part.Exists ? Math.Min(part.Length, size) : 0;
    }

    private const string InProgressFile = ".stickersense-downloading";

    /// <summary>
    /// Both builds use the same file names, so files (or half-downloaded parts) of the other build
    /// are removed before switching rather than resumed into a broken mix.
    /// </summary>
    internal static void StartVariant(string destination, ModelVariant variant)
    {
        var inProgress = Path.Combine(destination, InProgressFile);
        var marker = Path.Combine(destination, ModelCatalog.MarkerFile);
        string? previous = null;
        if (File.Exists(inProgress)) previous = File.ReadAllText(inProgress).Trim();
        else if (File.Exists(marker)) previous = ModelCatalog.DetectVariant(destination).ToString();
        if (previous is not null && previous != variant.ToString())
        {
            foreach (var name in ModelCatalog.RequiredFiles.Append(ModelCatalog.MarkerFile))
            {
                File.Delete(Path.Combine(destination, name));
                File.Delete(Path.Combine(destination, name + ".part"));
            }
        }
        File.WriteAllText(inProgress, variant.ToString());
    }

    private static long ExistingBytes(string destination, RemoteFile file)
    {
        var target = new FileInfo(Path.Combine(destination, Path.GetFileName(file.Path)));
        if (target.Exists && target.Length == file.Size) return file.Size;
        var part = new FileInfo(target.FullName + ".part");
        return part.Exists ? Math.Min(part.Length, file.Size) : 0;
    }

    private static void EnsureDiskSpace(string destination, long needed)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(destination));
            if (string.IsNullOrEmpty(root)) return;
            var free = new DriveInfo(root).AvailableFreeSpace;
            const long margin = 300L * 1024 * 1024;
            if (free < needed + margin)
            {
                throw new ModelDownloadException(
                    $"Not enough disk space. The model needs about {Math.Ceiling((needed + margin) / 1e9):0} gigabytes free on drive {root.TrimEnd('\\', '/')}.");
            }
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Can't tell; let the download try.
        }
    }

    private async Task DownloadFileAsync(string uri, long size, string target, Action<long> report, CancellationToken ct)
    {
        var part = target + ".part";
        var offset = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (offset > size) offset = 0;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (offset > 0 && response.StatusCode == HttpStatusCode.OK) offset = 0; // server ignored the range
                if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.PartialContent)
                {
                    throw new ModelDownloadException($"Download of {Path.GetFileName(target)} failed (error {(int)response.StatusCode}).");
                }
                await using (var output = new FileStream(part, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                {
                    var buffer = new byte[1 << 16];
                    long written = offset;
                    var lastReport = 0L;
                    int read;
                    while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        written += read;
                        if (written - lastReport > 1 << 20)
                        {
                            report(written);
                            lastReport = written;
                        }
                    }
                    offset = written;
                }
                if (size > 0 && offset != size)
                {
                    throw new IOException($"{Path.GetFileName(target)} ended early ({offset} of {size} bytes).");
                }
                File.Move(part, target, overwrite: true);
                report(size);
                return;
            }
            catch (Exception e) when (e is IOException or HttpRequestException && attempt < 4 && !ct.IsCancellationRequested)
            {
                // Network hiccup: resume from what we have.
                offset = File.Exists(part) ? new FileInfo(part).Length : 0;
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
            }
            catch (HttpRequestException e)
            {
                throw new ModelDownloadException("The download stopped. Check the internet connection, then try again; it continues where it left off.", e);
            }
            catch (IOException e)
            {
                throw new ModelDownloadException($"Couldn't save the model: {e.Message}", e);
            }
        }
    }
}

/// <summary>Turns a stream of percentages into occasional announcements (every 10 % by default).</summary>
public sealed class PercentMilestones(int step = 10)
{
    private int lastAnnounced = -1;

    /// <summary>The milestone to announce now, or null when nothing new was reached.</summary>
    public int? Next(int percent)
    {
        var milestone = percent / step * step;
        if (milestone == 0 || milestone <= lastAnnounced) return null;
        lastAnnounced = milestone;
        return milestone;
    }

    public void Reset() => lastAnnounced = -1;
}
