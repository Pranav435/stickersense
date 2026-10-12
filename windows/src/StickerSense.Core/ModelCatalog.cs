using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace StickerSense.Core;

/// <summary>Which build of Phi-3.5-vision-instruct (int4 ONNX) is installed or wanted.</summary>
public enum ModelVariant
{
    /// <summary>For graphics cards via DirectML (gpu/gpu-int4-rtn-block-32).</summary>
    DirectML,
    /// <summary>For the processor only (cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4).</summary>
    Cpu,
}

/// <summary>Where the on-device model lives and what it consists of.</summary>
public static class ModelCatalog
{
    public const string Repository = "microsoft/Phi-3.5-vision-instruct-onnx";
    public const string FolderName = "phi-3.5-vision";
    public const string MarkerFile = "stickersense-model.json";

    public static string HuggingFaceFolder(ModelVariant variant) => variant == ModelVariant.DirectML
        ? "gpu/gpu-int4-rtn-block-32"
        : "cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4";

    /// <summary>Approximate download size, for the Setup window.</summary>
    public static string ApproximateSize(ModelVariant variant) => variant == ModelVariant.DirectML ? "2.6 gigabytes" : "3.2 gigabytes";

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
    /// Lookup order: the bundled release (<c>&lt;app folder&gt;\models\phi-3.5-vision</c>), then
    /// the per-user download (<c>%LOCALAPPDATA%\StickerSense\models\phi-3.5-vision</c>).
    /// </summary>
    public static IReadOnlyList<string> CandidateDirectories(string appDirectory, string localAppData) =>
    [
        Path.Combine(appDirectory, "models", FolderName),
        UserModelDirectory(localAppData),
    ];

    public static string UserModelDirectory(string localAppData) => Path.Combine(localAppData, "StickerSense", "models", FolderName);

    public static string? FindInstalled(IEnumerable<string> candidates) => candidates.FirstOrDefault(IsComplete);

    public static bool IsComplete(string directory) =>
        Directory.Exists(directory) && RequiredFiles.All(f => File.Exists(Path.Combine(directory, f)) && new FileInfo(Path.Combine(directory, f)).Length > 0);

    /// <summary>Some files are there but not all (an interrupted download).</summary>
    public static bool IsPartial(string directory) =>
        Directory.Exists(directory) && !IsComplete(directory) &&
        Directory.EnumerateFiles(directory).Any(f => Path.GetFileName(f).StartsWith("phi-3.5", StringComparison.OrdinalIgnoreCase));

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
        var text = new FileInfo(Path.Combine(directory, "phi-3.5-v-instruct-text.onnx"));
        return text.Exists && text.Length > 10_000_000 ? ModelVariant.Cpu : ModelVariant.DirectML;
    }

    internal static void WriteMarker(string directory, ModelVariant variant)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["variant"] = variant.ToString(),
            ["repository"] = Repository,
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
            files = await ListAsync(ModelCatalog.Repository, folder, ct).ConfigureAwait(false);
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
            await DownloadFileAsync(file, target, bytes => progress?.Report(new DownloadProgress(before + bytes, total, name)), ct).ConfigureAwait(false);
            done += file.Size;
        }
        ModelCatalog.WriteMarker(destination, variant);
        File.Delete(Path.Combine(destination, InProgressFile));
        progress?.Report(new DownloadProgress(total, total, ""));
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

    private async Task DownloadFileAsync(RemoteFile file, string target, Action<long> report, CancellationToken ct)
    {
        var part = target + ".part";
        var offset = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (offset > file.Size) offset = 0;
        var uri = $"https://huggingface.co/{ModelCatalog.Repository}/resolve/main/{file.Path}";
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
                if (file.Size > 0 && offset != file.Size)
                {
                    throw new IOException($"{Path.GetFileName(target)} ended early ({offset} of {file.Size} bytes).");
                }
                File.Move(part, target, overwrite: true);
                report(file.Size);
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
