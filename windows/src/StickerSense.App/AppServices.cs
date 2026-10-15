using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using StickerSense.App.Engine;
using StickerSense.App.Platform;
using StickerSense.App.Speech;
using StickerSense.Core;

namespace StickerSense.App;

/// <summary>Shared state for the tray menu, windows and controllers. Lives on the UI thread.</summary>
internal sealed class AppServices : INotifyPropertyChanged, IDisposable
{
    private readonly Dispatcher dispatcher;
    private readonly CancellationTokenSource shutdown = new();
    private Task? indexer;
    private int libraryCount;
    private int undescribedCount;
    private string importStatus = "";
    private EngineStatus engineStatus;
    private int refreshPending;
    private Task<bool>? stickerDownload;
    private int stickerDownloadPercent = -1;

    public AppServices(Dispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
        DataDirectory = StickerLibrary.DefaultDirectory();
        Log = new DebugLog(DataDirectory);
        Settings = new SettingsStore(DataDirectory);
        Announcer = new Announcer(dispatcher, AppContext.BaseDirectory) { Output = Settings.SpeechOutput, Log = Log };
        Earcons = new EarconPlayer { IsEnabled = Settings.Earcons, Volume = Settings.EarconVolume };
        Library = new StickerLibrary(DataDirectory);
        Ocr = new WindowsOcrReader();
        _ = Ocr.WarmUpAsync();
        // Never wait more than 1.5 s for text: a slow first recognition finishes in the background.
        var ocr = new TimeLimitedOcrReader(Ocr, TimeSpan.FromSeconds(1.5));
        Describer = new PhiVisionDescriber(ModelDirectories, Log, Settings.DescriberOptions);
        Describer.Reload(Settings.EngineDevice);
        engineStatus = Describer.Status;
        Describer.StatusChanged += status => dispatcher.BeginInvoke(() => EngineStatus = status);
        Pipeline = new StickerPipeline(Describer, Library, ocr, DescribePriority.Foreground);
        BackgroundPipeline = new StickerPipeline(Describer, Library, ocr, DescribePriority.Background);
        PickerSearch = new PickerSearch(BackgroundPipeline, Path.Combine(Path.GetTempPath(), "StickerSense", "giphy"));
        Library.Changed += (_, _) => ScheduleRefreshCounts();
        RefreshCounts();
    }

    public string DataDirectory { get; }
    public DebugLog Log { get; }
    public SettingsStore Settings { get; }
    public Announcer Announcer { get; }
    public EarconPlayer Earcons { get; }
    public StickerLibrary Library { get; }
    public WindowsOcrReader Ocr { get; }
    public PhiVisionDescriber Describer { get; }

    /// <summary>For things the user asked for (shortcut, auto mode, picker detail).</summary>
    public StickerPipeline Pipeline { get; }

    /// <summary>For imports and GIPHY results; never queues in front of a user request.</summary>
    public StickerPipeline BackgroundPipeline { get; }

    /// <summary>The picker's search: library first, then GIPHY pages, with GIPHY descriptions kept on disk.</summary>
    public PickerSearch PickerSearch { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int LibraryCount { get => libraryCount; private set => Set(ref libraryCount, value); }
    public int UndescribedCount { get => undescribedCount; private set => Set(ref undescribedCount, value); }
    public string ImportStatus { get => importStatus; private set => Set(ref importStatus, value); }
    public EngineStatus EngineStatus { get => engineStatus; private set => Set(ref engineStatus, value); }

    /// <summary>How far the sticker model's download has got (0 to 100), or -1 when none is running.</summary>
    public int StickerDownloadPercent { get => stickerDownloadPercent; private set => Set(ref stickerDownloadPercent, value); }

    /// <summary>Bundled release first (<c>&lt;app&gt;\models</c>), then the per-user download.</summary>
    public IReadOnlyList<string> ModelDirectories() =>
        ModelCatalog.CandidateDirectories(AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Settings.PreferredModel);

    public static string UserModelDirectory(ModelVariant variant) =>
        ModelCatalog.UserModelDirectory(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), variant);

    /// <summary>The models installed in either the release folder or the downloads, fast one first.</summary>
    public IReadOnlyList<ModelVariant> InstalledModels() =>
        ModelCatalog.CandidateDirectories(AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            .Where(ModelCatalog.IsComplete)
            .Select(ModelCatalog.DetectVariant)
            .Distinct()
            .ToList();

    public void ApplySettings()
    {
        Describer.UpdateOptions(Settings.DescriberOptions);
        Earcons.IsEnabled = Settings.Earcons;
        Earcons.Volume = Settings.EarconVolume;
        Announcer.Output = Settings.SpeechOutput;
        if (Settings.EngineDevice != Describer.Device || (Describer.LoadedDirectory is { } loaded && loaded != Describer.ModelDirectory))
        {
            Describer.Reload(Settings.EngineDevice);
            _ = PrewarmAsync();
        }
    }

    /// <summary>Coalesces bursts of library changes (imports add thousands) into one recount.</summary>
    private void ScheduleRefreshCounts()
    {
        if (Interlocked.Exchange(ref refreshPending, 1) == 1) return;
        _ = Task.Delay(500).ContinueWith(_ => dispatcher.BeginInvoke(() =>
        {
            Volatile.Write(ref refreshPending, 0);
            RefreshCounts();
        }, DispatcherPriority.Background), TaskScheduler.Default);
    }

    public void RefreshCounts()
    {
        LibraryCount = Library.Search("", limit: int.MaxValue).Count;
        UndescribedCount = Library.Undescribed(int.MaxValue).Count;
    }

    public async Task PrewarmAsync()
    {
        try
        {
            await Describer.PrewarmAsync();
        }
        catch (Exception e)
        {
            Log.Add($"Prewarm: {e.Message}");
        }
    }

    // MARK: Model download

    public async Task DownloadModelAsync(ModelVariant variant, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        var destination = UserModelDirectory(variant);
        Log.Add($"Downloading the {variant} model to {destination}");
        await new HuggingFaceDownloader().DownloadAsync(variant, destination, progress, ct);
        Describer.Reload(Settings.EngineDevice);
        // The model just downloaded is the one wanted (after the reload, so it isn't reloaded twice).
        Settings.PreferredModel = variant;
        _ = PrewarmAsync();
    }

    // MARK: Making stickers

    public static string StickerModelsDirectory => StickerMakerCatalog.ModelsDirectory(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public bool IsStickerModelInstalled(StickerModel model) => StickerMakerCatalog.IsInstalled(model, StickerModelsDirectory);

    /// <summary>
    /// Draws, cuts out and captions stickers with the model chosen in Settings. Dispose the
    /// masker after the batch (it frees the cut-out model).
    /// </summary>
    public (StickerFactory Factory, IsNetMasker Masker) StickerFactoryFor(StickerModel model)
    {
        var masker = new IsNetMasker(StickerMakerCatalog.CutOutPath(StickerModelsDirectory), Log);
        return (new StickerFactory(new SdCliGenerator(model, StickerModelsDirectory, Log), masker), masker);
    }

    /// <summary>
    /// Downloads the sticker model (and the cut-out model), saying how it's going every quarter.
    /// Returns whether it's installed afterwards. A second call while one runs waits for it.
    /// </summary>
    public Task<bool> DownloadStickerModelAsync(StickerModel model)
    {
        if (IsStickerModelInstalled(model)) return Task.FromResult(true);
        return stickerDownload ??= RunStickerDownloadAsync(model);
    }

    private async Task<bool> RunStickerDownloadAsync(StickerModel model)
    {
        StickerDownloadPercent = 0;
        Announcer.Say($"Downloading the sticker maker, {StickerMakerCatalog.SpokenSize(model)}. You can keep using StickerSense meanwhile.", interrupt: false);
        Log.Add($"Downloading the {model} sticker model to {StickerModelsDirectory}");
        var milestones = new PercentMilestones(25);
        var progress = new Progress<DownloadProgress>(p =>
        {
            StickerDownloadPercent = p.Percent;
            if (milestones.Next(p.Percent) is { } m && m < 100) Announcer.Say($"Sticker maker download: {m} percent.", interrupt: false);
        });
        try
        {
            await StickerMakerCatalog.DownloadAsync(model, StickerModelsDirectory, progress, ct: shutdown.Token);
            Log.Add("Sticker model downloaded");
            Earcons.Play(Earcon.Ready);
            Announcer.Say("The sticker maker is ready.", interrupt: false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception e) when (e is ModelDownloadException or IOException or System.Net.Http.HttpRequestException)
        {
            Log.Add($"Sticker model download failed: {e.Message}");
            Earcons.Play(Earcon.Failed);
            Announcer.Say(e is ModelDownloadException ? e.Message : "The sticker maker didn't download. Check the internet connection and try again; it continues where it left off.");
            return false;
        }
        finally
        {
            StickerDownloadPercent = -1;
            stickerDownload = null;
        }
    }

    // MARK: WhatsApp import

    public async Task ImportWhatsAppAsync(bool announce = true)
    {
        if (!WhatsAppImporter.IsInstalled)
        {
            ImportStatus = "WhatsApp Desktop isn't installed from the Microsoft Store, so there's nothing to import.";
            if (announce) Announcer.Say(ImportStatus);
            return;
        }
        ImportStatus = "Looking for WhatsApp stickers…";
        if (announce) Announcer.Say(ImportStatus, interrupt: false);
        try
        {
            var result = await WhatsAppImporter.ImportAllAsync(Library, shutdown.Token);
            ImportStatus = result switch
            {
                { PermissionDenied: true } => "Windows didn't let StickerSense read WhatsApp's folder.",
                { Found: 0 } => "No WhatsApp sticker files found yet. Stickers appear after WhatsApp has downloaded them.",
                _ => $"Found {result.Found} WhatsApp stickers, {result.Added} new. Describing them in the background while you're idle.",
            };
        }
        catch (OperationCanceledException)
        {
            return;
        }
        Log.Add($"WhatsApp import: {ImportStatus}");
        if (announce) Announcer.Say(ImportStatus, interrupt: false);
        RefreshCounts();
        StartIndexing();
    }

    // MARK: Background describing

    /// <summary>
    /// Describes imported stickers one by one, only while the computer is genuinely idle (no input
    /// for 90 seconds, battery saver off), stopping the moment a key is pressed or the mouse moves.
    /// </summary>
    public void StartIndexing()
    {
        if (indexer is { IsCompleted: false }) return;
        var run = new BackgroundIndexer(BackgroundPipeline, SystemIdle.SinceLastInput, SystemIdle.BatterySaverOn, Log);
        run.Progressed += (_, _) => ScheduleRefreshCounts();
        indexer = run.RunAsync(shutdown.Token).ContinueWith(_ => dispatcher.BeginInvoke(RefreshCounts), TaskScheduler.Default);
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public void Dispose()
    {
        shutdown.Cancel();
        PickerSearch.Dispose();
        Library.Dispose();
        Describer.Dispose();
        Announcer.Dispose();
    }
}
