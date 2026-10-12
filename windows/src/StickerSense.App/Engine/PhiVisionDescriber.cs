using System.IO;
using System.Text;
using Microsoft.ML.OnnxRuntimeGenAI;
using StickerSense.Core;

namespace StickerSense.App.Engine;

/// <summary>Which hardware the model runs on.</summary>
internal enum EngineDevice
{
    /// <summary>The graphics card through DirectML when the DirectML model is installed, otherwise the processor.</summary>
    Automatic,
    ProcessorOnly,
}

/// <summary>
/// Describes stickers on this PC with Phi-3.5-vision-instruct (int4 ONNX) through ONNX Runtime
/// GenAI. The DirectML build runs on the graphics card and falls back to the processor if the
/// card can't run it. One model is shared by every request; <see cref="DescriberGate"/> keeps
/// user requests ahead of background work.
/// </summary>
internal sealed class PhiVisionDescriber : VisionLanguageDescriber, IDisposable
{
    private readonly Func<IReadOnlyList<string>> candidateDirectories;
    private readonly IActivityLog log;
    private readonly DescriberGate gate = new();
    private readonly object sync = new();
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "StickerSense", "frames");
    private OgaHandle? oga;
    private Task<LoadedModel>? loading;
    private LoadedModel? loaded;
    private EngineStatus status;

    public PhiVisionDescriber(Func<IReadOnlyList<string>> candidateDirectories, IActivityLog log, DescriberOptions? options = null)
        : base(options)
    {
        this.candidateDirectories = candidateDirectories;
        this.log = log;
        status = CheckFiles();
    }

    public EngineDevice Device { get; private set; } = EngineDevice.Automatic;

    /// <summary>"DirectML" or "processor" once loaded.</summary>
    public string? RunningOn => loaded?.RunningOn;

    public string? ModelDirectory => ModelCatalog.FindInstalled(candidateDirectories());

    public override EngineStatus Status
    {
        get
        {
            lock (sync) return status;
        }
    }

    /// <summary>Raised (on a background thread) when the status changes.</summary>
    public event Action<EngineStatus>? StatusChanged;

    /// <summary>Loads the model and runs one tiny generation, so the first real description doesn't pay for setup.</summary>
    public override Task PrewarmAsync(CancellationToken ct = default) => EnsureLoadedAsync();

    /// <summary>
    /// The detailed description uses the same loaded model (GenAI has no per-prompt session to
    /// prepare), so this just makes sure the model is still loaded after a reload.
    /// </summary>
    public override Task PrewarmDetailAsync(CancellationToken ct = default) => EnsureLoadedAsync();

    /// <summary>Forget the loaded model and look for it again (after a download or a device change).</summary>
    public void Reload(EngineDevice device)
    {
        LoadedModel? old;
        lock (sync)
        {
            Device = device;
            old = loaded;
            loaded = null;
            loading = null;
        }
        // Wait for any running request before freeing the model it uses.
        _ = Task.Run(async () =>
        {
            using (await gate.EnterAsync(DescribePriority.Foreground, CancellationToken.None)) old?.Dispose();
        });
        SetStatus(CheckFiles());
    }

    private EngineStatus CheckFiles()
    {
        var candidates = candidateDirectories();
        if (ModelCatalog.FindInstalled(candidates) is not null) return new EngineStatus(EngineState.NotLoaded);
        return candidates.Any(ModelCatalog.IsPartial) ? new EngineStatus(EngineState.ModelIncomplete) : new EngineStatus(EngineState.ModelMissing);
    }

    private void SetStatus(EngineStatus value)
    {
        lock (sync)
        {
            if (status == value) return;
            status = value;
        }
        StatusChanged?.Invoke(value);
    }

    private Task<LoadedModel> EnsureLoadedAsync()
    {
        Task<LoadedModel> current;
        lock (sync)
        {
            if (loaded is not null) return Task.FromResult(loaded);
            if (loading is not null) return loading;
            var directory = ModelCatalog.FindInstalled(candidateDirectories());
            if (directory is null)
            {
                status = CheckFiles();
                return Task.FromException<LoadedModel>(DescribeException.EngineUnavailable(status));
            }
            var device = Device;
            status = new EngineStatus(EngineState.Loading);
            current = loading = Task.Run(() => Load(directory, device));
        }
        StatusChanged?.Invoke(new EngineStatus(EngineState.Loading));
        _ = current.ContinueWith(t =>
        {
            lock (sync)
            {
                if (!ReferenceEquals(loading, current))
                {
                    // Reloaded while this was loading: nobody will use it.
                    if (t.IsCompletedSuccessfully) t.Result.Dispose();
                    return;
                }
                if (t.IsCompletedSuccessfully) loaded = t.Result;
                else loading = null; // allow another try
            }
            var error = t.Exception?.InnerException;
            if (error is not null) log.Add($"Model failed to load: {error}");
            SetStatus(t.IsCompletedSuccessfully
                ? new EngineStatus(EngineState.Ready)
                : new EngineStatus(EngineState.LoadFailed, error?.Message ?? "unknown error"));
        }, TaskScheduler.Default);
        return current;
    }

    private LoadedModel Load(string directory, EngineDevice device)
    {
        lock (sync) oga ??= new OgaHandle();
        var variant = ModelCatalog.DetectVariant(directory);
        log.Add($"Loading {variant} model from {directory}");
        if (variant == ModelVariant.DirectML && device == EngineDevice.Automatic)
        {
            try
            {
                return Create(directory, "dml", "the graphics card (DirectML)");
            }
            catch (OnnxRuntimeGenAIException e)
            {
                log.Add($"DirectML failed, using the processor instead: {e.Message}");
            }
        }
        return Create(directory, null, "the processor");
    }

    private LoadedModel Create(string directory, string? provider, string description)
    {
        var started = DateTime.UtcNow;
        using var config = new Config(directory);
        config.ClearProviders();
        if (provider is not null) config.AppendProvider(provider);
        var model = new Model(config);
        LoadedModel? result = null;
        try
        {
            var processorConfig = Path.Combine(directory, "processor_config.json");
            var numCrops = File.Exists(processorConfig) ? PhiVisionPrompt.ReadNumCrops(File.ReadAllText(processorConfig)) : 4;
            result = new LoadedModel(model, new MultiModalProcessor(model), new Tokenizer(model), numCrops, description, provider == "dml");
            WarmUp(result);
            log.Add($"Model ready on {description} in {(DateTime.UtcNow - started).TotalSeconds:0.0}s");
            return result;
        }
        catch
        {
            // Exactly one owner frees the model: the loaded bundle if it was made, else the model itself.
            if (result is not null) result.Dispose();
            else model.Dispose();
            throw;
        }
    }

    /// <summary>
    /// One two-token generation on a blank image. On a graphics card, the first run compiles
    /// DirectML's kernels and sets up memory (seconds); doing it now keeps the quick-description
    /// path ready, and a card that can't run the model fails here and falls back to the processor.
    /// </summary>
    private void WarmUp(LoadedModel model)
    {
        Directory.CreateDirectory(scratch);
        var path = Path.Combine(scratch, $"warmup-{Guid.NewGuid():N}.png");
        try
        {
            using (var blank = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(64, 64, new SixLabors.ImageSharp.PixelFormats.Rgba32(255, 255, 255)))
            {
                File.WriteAllBytes(path, ImagePrep.PngData(blank));
            }
            var request = new VisionRequest(StickerPrompts.ShortInstructions(Options.EffectiveLanguage), "Describe this sticker or image.", [], MaxNewTokens: 2, Temperature: 0);
            Generate(model, request, [path], CancellationToken.None);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    protected override async Task<string> GenerateAsync(VisionRequest request, DescribePriority priority, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var model = await LoadedModelAsync(ct).ConfigureAwait(false);
            using var lease = await gate.EnterAsync(priority, ct).ConfigureAwait(false);
            try
            {
                return await GenerateWithAsync(model, request, lease.Token).ConfigureAwait(false);
            }
            catch (OnnxRuntimeGenAIException e) when (model.UsesGraphicsCard && attempt == 1)
            {
                // E.g. the graphics card ran out of memory: carry on with the processor for this session.
                log.Add($"DirectML failed while describing; switching to the processor. {e}");
                Reload(EngineDevice.ProcessorOnly);
            }
            catch (OnnxRuntimeGenAIException e)
            {
                log.Add($"The model failed: {e}");
                throw DescribeException.Failed("the model ran into a problem. The activity log has the details", e);
            }
        }
    }

    private async Task<LoadedModel> LoadedModelAsync(CancellationToken ct)
    {
        try
        {
            return await EnsureLoadedAsync().WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not (OperationCanceledException or DescribeException))
        {
            // Native library missing, unsupported graphics card, damaged model files…
            throw DescribeException.EngineUnavailable(new EngineStatus(EngineState.LoadFailed, e.Message));
        }
    }

    private async Task<string> GenerateWithAsync(LoadedModel model, VisionRequest request, CancellationToken token)
    {
        Directory.CreateDirectory(scratch);
        var paths = new List<string>();
        try
        {
            foreach (var image in request.Images)
            {
                var path = Path.Combine(scratch, $"{Guid.NewGuid():N}.png");
                await File.WriteAllBytesAsync(path, ImagePrep.PngData(image), token).ConfigureAwait(false);
                paths.Add(path);
            }
            var started = DateTime.UtcNow;
            var reply = await Task.Run(() => Generate(model, request, paths.ToArray(), token), token).ConfigureAwait(false);
            log.Add($"Model replied in {(DateTime.UtcNow - started).TotalSeconds:0.0}s ({paths.Count} image(s))");
            return reply;
        }
        finally
        {
            foreach (var path in paths)
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static string Generate(LoadedModel m, VisionRequest request, string[] imagePaths, CancellationToken ct)
    {
        var prompt = PhiVisionPrompt.Build(request.Instructions, request.Prompt, imagePaths.Length);
        using var images = Images.Load(imagePaths);
        using var inputs = m.Processor.ProcessImages(prompt, images);
        int textTokens;
        using (var sequences = m.Tokenizer.Encode(PhiVisionPrompt.TextOnly(prompt))) textTokens = sequences[0].Length;

        using var parameters = new GeneratorParams(m.Model);
        parameters.SetSearchOption("max_length", PhiVisionPrompt.MaxLength(textTokens, imagePaths.Length, m.NumCrops, request.MaxNewTokens));
        parameters.SetSearchOption("do_sample", request.Temperature > 0);
        if (request.Temperature > 0)
        {
            parameters.SetSearchOption("temperature", request.Temperature);
            parameters.SetSearchOption("top_p", 0.9);
            parameters.SetSearchOption("top_k", 40);
        }
        using var generator = new Generator(m.Model, parameters);
        generator.SetInputs(inputs);
        using var stream = m.Processor.CreateStream();
        var output = new StringBuilder();
        for (var i = 0; i < request.MaxNewTokens; i++)
        {
            ct.ThrowIfCancellationRequested();
            generator.GenerateNextToken();
            if (generator.IsDone()) break;
            output.Append(stream.Decode(generator.GetNextTokens()[0]));
            if (output.ToString().Contains("<|end|>", StringComparison.Ordinal)) break;
        }
        return output.ToString();
    }

    public void Dispose()
    {
        LoadedModel? model;
        lock (sync)
        {
            model = loaded;
            loaded = null;
            loading = null;
        }
        model?.Dispose();
        oga?.Dispose();
    }

    private sealed class LoadedModel(Model model, MultiModalProcessor processor, Tokenizer tokenizer, int numCrops, string runningOn, bool usesGraphicsCard) : IDisposable
    {
        public bool UsesGraphicsCard { get; } = usesGraphicsCard;
        public Model Model { get; } = model;
        public MultiModalProcessor Processor { get; } = processor;
        public Tokenizer Tokenizer { get; } = tokenizer;
        public int NumCrops { get; } = numCrops;
        public string RunningOn { get; } = runningOn;

        public void Dispose()
        {
            Tokenizer.Dispose();
            Processor.Dispose();
            Model.Dispose();
        }
    }
}
