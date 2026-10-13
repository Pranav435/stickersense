using StickerSense.Core;

namespace StickerSense.App.Engine;

/// <summary>Which hardware the model runs on.</summary>
internal enum EngineDevice
{
    /// <summary>
    /// The graphics card through DirectML when the DirectML model is installed and the card has
    /// memory of its own; otherwise the processor (faster than built-in graphics).
    /// </summary>
    Automatic,
    ProcessorOnly,
    /// <summary>The graphics card whenever the DirectML model is installed, even built-in graphics.</summary>
    GraphicsCard,
}

/// <summary>
/// Describes stickers on this PC with Phi-3.5-vision-instruct (int4 ONNX) through ONNX Runtime
/// GenAI, which runs in a separate engine process (<see cref="EngineHost"/>): a native fault in
/// the model can't take StickerSense down, and a new engine starts after one. The DirectML build
/// runs on the graphics card and falls back to the processor if the card can't run it. One model
/// is shared by every request; <see cref="DescriberGate"/> keeps user requests ahead of
/// background work.
/// </summary>
internal sealed class PhiVisionDescriber : VisionLanguageDescriber, IDisposable
{
    /// <summary>After this many engine failures in one session, stop restarting it by itself.</summary>
    private const int MaxAutomaticRestarts = 3;

    private readonly Func<IReadOnlyList<string>> candidateDirectories;
    private readonly IActivityLog log;
    private readonly DescriberGate gate = new();
    private readonly object sync = new();
    private Task<EngineProcess>? loading;
    private EngineProcess? loaded;
    private EngineStatus status;
    private int restarts;

    public PhiVisionDescriber(Func<IReadOnlyList<string>> candidateDirectories, IActivityLog log, DescriberOptions? options = null)
        : base(options)
    {
        this.candidateDirectories = candidateDirectories;
        this.log = log;
        status = CheckFiles();
    }

    public EngineDevice Device { get; private set; } = EngineDevice.Automatic;

    /// <summary>"the graphics card (DirectML)" or "the processor" once loaded.</summary>
    public string? RunningOn => loaded?.RunningOn;

    /// <summary>Memory the engine process uses, in megabytes (0 when it isn't running).</summary>
    public long EngineMemoryMegabytes => loaded?.WorkingSetMegabytes ?? 0;

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
        EngineProcess? old;
        lock (sync)
        {
            Device = device;
            old = loaded;
            loaded = null;
            loading = null;
        }
        // Wait for any running request before closing the engine it uses.
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

    private Task<EngineProcess> EnsureLoadedAsync()
    {
        Task<EngineProcess> current;
        lock (sync)
        {
            if (loaded is not null) return Task.FromResult(loaded);
            if (loading is not null) return loading;
            var directory = ModelCatalog.FindInstalled(candidateDirectories());
            if (directory is null)
            {
                status = CheckFiles();
                return Task.FromException<EngineProcess>(DescribeException.EngineUnavailable(status));
            }
            var device = Device;
            status = new EngineStatus(EngineState.Loading);
            current = loading = Task.Run(() => LoadAsync(directory, device));
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
                if (t.IsCompletedSuccessfully)
                {
                    loaded = t.Result;
                    loaded.Stopped += message => OnEngineStopped(t.Result, message);
                }
                loading = null;
            }
            var error = t.Exception?.InnerException;
            if (error is not null) log.Add($"Model failed to load: {error.Message}");
            SetStatus(t.IsCompletedSuccessfully
                ? new EngineStatus(EngineState.Ready)
                : new EngineStatus(EngineState.LoadFailed, error?.Message ?? "unknown error"));
        }, TaskScheduler.Default);
        return current;
    }

    private Task<EngineProcess> LoadAsync(string directory, EngineDevice device)
    {
        var variant = ModelCatalog.DetectVariant(directory);
        log.Add($"Loading {variant} model from {directory}");
        var tryGraphicsCard = false;
        if (variant == ModelVariant.DirectML && device == EngineDevice.Automatic && Platform.GpuInfo.RecommendedAdapter() is null)
        {
            log.Add("The graphics are built in and share the computer's memory, so the model runs on the processor, which is faster on them.");
        }
        else if (variant == ModelVariant.DirectML && device is EngineDevice.Automatic or EngineDevice.GraphicsCard)
        {
            tryGraphicsCard = true;
        }
        return EngineProcess.StartAsync(directory, tryGraphicsCard, StickerPrompts.ShortInstructions(Options.EffectiveLanguage), message => log.Add(message));
    }

    protected override async Task<string> GenerateAsync(VisionRequest request, DescribePriority priority, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var engine = await LoadedEngineAsync(ct).ConfigureAwait(false);
            using var lease = await gate.EnterAsync(priority, ct).ConfigureAwait(false);
            try
            {
                var started = DateTime.UtcNow;
                var reply = await engine.GenerateAsync(request, lease.Token).ConfigureAwait(false);
                log.Add($"Model replied in {(DateTime.UtcNow - started).TotalSeconds:0.0}s ({request.Images.Count} image(s))");
                return reply;
            }
            catch (EngineFailedException e) when (e.OnGraphicsCard && attempt == 1)
            {
                // E.g. the graphics card ran out of memory: carry on with the processor for this session.
                log.Add($"DirectML failed while describing; switching to the processor. {e.Message}");
                Reload(EngineDevice.ProcessorOnly);
            }
            catch (EngineFailedException e)
            {
                log.Add($"The model failed: {e.Message}");
                throw DescribeException.Failed("the model ran into a problem. The activity log has the details", e);
            }
            catch (EngineStoppedException e)
            {
                // OnEngineStopped logs it and starts a new engine.
                throw DescribeException.Failed("the description engine stopped. Try again in a few seconds", e);
            }
        }
    }

    /// <summary>The engine process went away unexpectedly (a closed or replaced engine is ignored).</summary>
    private void OnEngineStopped(EngineProcess engine, string message)
    {
        lock (sync)
        {
            if (!ReferenceEquals(loaded, engine)) return;
            loaded = null;
        }
        engine.Dispose();
        var again = Interlocked.Increment(ref restarts) <= MaxAutomaticRestarts;
        log.Add(again ? $"{message} Starting it again." : $"{message} It has stopped {MaxAutomaticRestarts} times, so it starts again only when needed.");
        SetStatus(CheckFiles());
        if (again) _ = PrewarmQuietlyAsync();
    }

    private async Task PrewarmQuietlyAsync()
    {
        try
        {
            await EnsureLoadedAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The status says what went wrong.
        }
    }

    private async Task<EngineProcess> LoadedEngineAsync(CancellationToken ct)
    {
        try
        {
            return await EnsureLoadedAsync().WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not (OperationCanceledException or DescribeException))
        {
            // Engine couldn't start, unsupported graphics card, damaged model files…
            throw DescribeException.EngineUnavailable(new EngineStatus(EngineState.LoadFailed, e.Message));
        }
    }

    public void Dispose()
    {
        EngineProcess? engine;
        lock (sync)
        {
            engine = loaded;
            loaded = null;
            loading = null;
        }
        engine?.Dispose();
    }
}
