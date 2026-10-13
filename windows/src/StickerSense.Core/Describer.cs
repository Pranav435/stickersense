using System.Globalization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace StickerSense.Core;

/// <summary>What the image was captured from — changes how the model is told to look at it.</summary>
public abstract record DescriptionContext
{
    private DescriptionContext() { }

    /// <summary>A crop of the focused item on screen. May include chat chrome (timestamps, ticks).</summary>
    public sealed record ScreenItem(string? ScreenReaderLabel) : DescriptionContext;

    /// <summary>A clean sticker / GIF file (library, WhatsApp import, GIPHY).</summary>
    public sealed record ImageFile : DescriptionContext;

    public static DescriptionContext File { get; } = new ImageFile();

    public string? Label => this is ScreenItem item ? item.ScreenReaderLabel?.Trim() : null;
}

public enum EngineState
{
    Ready,
    /// <summary>Installed; loads on first use.</summary>
    NotLoaded,
    Loading,
    ModelMissing,
    ModelIncomplete,
    LoadFailed,
}

public sealed record EngineStatus(EngineState State, string Detail = "")
{
    public bool IsReady => State == EngineState.Ready;

    /// <summary>Requests can be made (they wait for the model to finish loading).</summary>
    public bool CanDescribe => State is EngineState.Ready or EngineState.NotLoaded or EngineState.Loading;

    /// <summary>Spoken/visible explanation with what to do about it.</summary>
    public string Explanation => State switch
    {
        EngineState.Ready => "The on-device model is ready.",
        EngineState.NotLoaded => "The on-device model is installed. It loads the first time it's needed.",
        EngineState.Loading => "The on-device model is still loading. Try again in a few seconds.",
        EngineState.ModelMissing => "The description model isn't installed yet. Open StickerSense Setup to download it.",
        EngineState.ModelIncomplete => "The description model's files are incomplete. Open StickerSense Setup to finish the download.",
        _ => Detail.Length == 0
            ? "The description model couldn't start. Open StickerSense Setup to check it."
            : $"The description model couldn't start: {Detail}. Open StickerSense Setup to check it.",
    };
}

public sealed record DescriberOptions
{
    /// <summary>Language for descriptions; null = the system UI language.</summary>
    public CultureInfo? Language { get; init; }

    /// <summary>Also generate what the sticker means. Off by default: every extra line costs time.</summary>
    public bool IncludeMeaning { get; init; }

    public CultureInfo EffectiveLanguage => Language ?? CultureInfo.CurrentUICulture;
}

/// <summary>
/// User-facing requests (shortcut, auto mode, picker detail) run before background work
/// (imports, GIPHY results), and pre-empt it.
/// </summary>
public enum DescribePriority
{
    Foreground,
    Background,
}

/// <summary>
/// Describes stickers. Frames are in time order; pass several for animated stickers so the
/// motion gets described. The engine is swappable behind this interface.
/// </summary>
public interface IStickerDescriber
{
    EngineStatus Status { get; }

    void UpdateOptions(DescriberOptions options);

    /// <summary>Loads the model ahead of time so the next description starts quickly.</summary>
    Task PrewarmAsync(CancellationToken ct = default);

    /// <summary>
    /// Readies whatever a detailed description needs. Called right after a short description is
    /// spoken, since a second press for detail usually follows within seconds.
    /// </summary>
    Task PrewarmDetailAsync(CancellationToken ct = default) => PrewarmAsync(ct);

    /// <summary>One quick structured reading of a sticker.</summary>
    Task<StickerReading> ReadAsync(
        IReadOnlyList<Image<Rgba32>> frames,
        DescriptionContext context,
        string ocrText = "",
        DescribePriority priority = DescribePriority.Foreground,
        CancellationToken ct = default);

    /// <summary>A richer few-sentence description for when the short one isn't enough.</summary>
    Task<string> DetailedAsync(
        IReadOnlyList<Image<Rgba32>> frames,
        DescriptionContext context,
        string ocrText = "",
        StickerReading? shortReading = null,
        DescribePriority priority = DescribePriority.Foreground,
        CancellationToken ct = default);
}

/// <summary>
/// One generation request for a vision-language model. The images are prepared (flattened, sized).
/// <paramref name="ReplyStart"/> is written as the start of the model's own reply, which it then
/// continues: small models that ignore "reply with these lines" follow a reply already begun with
/// "KIND:". Engines put it after the assistant tag; the reply passed back starts with it.
/// </summary>
public sealed record VisionRequest(
    string Instructions,
    string Prompt,
    IReadOnlyList<Image<Rgba32>> Images,
    int MaxNewTokens,
    double Temperature,
    string ReplyStart = "");

/// <summary>
/// Shared logic for vision-language-model describers: frame preparation, prompts and parsing.
/// Engines only implement <see cref="GenerateAsync"/>.
/// </summary>
public abstract class VisionLanguageDescriber : IStickerDescriber
{
    public const int MaxFrames = 4;

    protected VisionLanguageDescriber(DescriberOptions? options = null)
    {
        Options = options ?? new DescriberOptions();
    }

    public DescriberOptions Options { get; private set; }

    public abstract EngineStatus Status { get; }

    public void UpdateOptions(DescriberOptions options) => Options = options;

    public virtual Task PrewarmAsync(CancellationToken ct = default) => Task.CompletedTask;

    public virtual Task PrewarmDetailAsync(CancellationToken ct = default) => PrewarmAsync(ct);

    /// <summary>Generates the model's reply text for one request: what follows <see cref="VisionRequest.ReplyStart"/>.</summary>
    protected abstract Task<string> GenerateAsync(VisionRequest request, DescribePriority priority, CancellationToken ct);

    public async Task<StickerReading> ReadAsync(
        IReadOnlyList<Image<Rgba32>> frames,
        DescriptionContext context,
        string ocrText = "",
        DescribePriority priority = DescribePriority.Foreground,
        CancellationToken ct = default)
    {
        EnsureUsable();
        var prepared = await PrepareAsync(frames, ct).ConfigureAwait(false);
        try
        {
            var animated = prepared.Count > 1;
            // On a sticker, the label's words (sender, time, chat) are chrome; on a text message they're the message.
            var chromeLabel = LabelClassifier.ChromeLabel(context.Label);
            var hint = StickerText.RemovingLabelText(CleanLines(ocrText), chromeLabel);
            var language = Options.EffectiveLanguage;
            var request = new VisionRequest(
                StickerPrompts.ShortInstructions(language),
                StickerPrompts.ShortPrompt(prepared.Count, hint, context.Label, language, Options.IncludeMeaning),
                prepared,
                MaxNewTokens: 100,
                Temperature: 0.2,
                ReplyStart: "KIND:");
            var output = request.ReplyStart + await GenerateGuardedAsync(request, priority, ct).ConfigureAwait(false);
            return ReadingParser.Parse(output, animated, hint, chromeLabel);
        }
        finally
        {
            foreach (var image in prepared) image.Dispose();
        }
    }

    public async Task<string> DetailedAsync(
        IReadOnlyList<Image<Rgba32>> frames,
        DescriptionContext context,
        string ocrText = "",
        StickerReading? shortReading = null,
        DescribePriority priority = DescribePriority.Foreground,
        CancellationToken ct = default)
    {
        EnsureUsable();
        var prepared = await PrepareAsync(frames, ct).ConfigureAwait(false);
        try
        {
            var language = Options.EffectiveLanguage;
            var request = new VisionRequest(
                StickerPrompts.DetailInstructions(language),
                StickerPrompts.DetailPrompt(prepared.Count, ocrText, context.Label, shortReading),
                prepared,
                MaxNewTokens: 220,
                Temperature: 0.3);
            var output = await GenerateGuardedAsync(request, priority, ct).ConfigureAwait(false);
            var text = ReadingParser.CleanDetail(output);
            if (text.Length == 0) throw DescribeException.Failed("the model gave no answer");
            return text;
        }
        finally
        {
            foreach (var image in prepared) image.Dispose();
        }
    }

    /// <summary>Strips timestamps and ticks from each OCR line and drops lines that are only chrome.</summary>
    private static string CleanLines(string ocrText) =>
        string.Join("\n", ocrText.Split('\n').Select(StickerText.StripChrome).Where(l => l.Length > 0 && !StickerText.IsChatChrome(l)));

    private void EnsureUsable()
    {
        var status = Status;
        if (!status.CanDescribe) throw DescribeException.EngineUnavailable(status);
    }

    private async Task<string> GenerateGuardedAsync(VisionRequest request, DescribePriority priority, CancellationToken ct)
    {
        try
        {
            return await GenerateAsync(request, priority, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not (OperationCanceledException or UserFacingException))
        {
            throw DescribeException.Failed(e.Message, e);
        }
    }

    private static Task<List<Image<Rgba32>>> PrepareAsync(IReadOnlyList<Image<Rgba32>> frames, CancellationToken ct)
    {
        if (frames.Count == 0) throw DescribeException.Failed("no image");
        return Task.Run(() => frames.Take(MaxFrames).Select(f => ImagePrep.PrepareForModel(f)).ToList(), ct);
    }
}

/// <summary>
/// Runs one model request at a time. Foreground requests jump the queue and cancel a running
/// background request (which throws <see cref="OperationCanceledException"/> and can be retried).
/// </summary>
public sealed class DescriberGate
{
    private readonly SemaphoreSlim slot = new(1, 1);
    private readonly object sync = new();
    private int foregroundWaiting;
    private CancellationTokenSource? runningBackground;

    public async Task<Lease> EnterAsync(DescribePriority priority, CancellationToken ct)
    {
        if (priority == DescribePriority.Foreground)
        {
            lock (sync)
            {
                foregroundWaiting++;
                runningBackground?.Cancel();
            }
            try
            {
                await slot.WaitAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                lock (sync) foregroundWaiting--;
            }
            return new Lease(this, ct, null);
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (Volatile.Read(ref foregroundWaiting) > 0)
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
                continue;
            }
            await slot.WaitAsync(ct).ConfigureAwait(false);
            lock (sync)
            {
                if (foregroundWaiting == 0)
                {
                    var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    runningBackground = linked;
                    return new Lease(this, linked.Token, linked);
                }
            }
            slot.Release();
        }
    }

    private void Exit(CancellationTokenSource? background)
    {
        if (background is not null)
        {
            lock (sync)
            {
                if (ReferenceEquals(runningBackground, background)) runningBackground = null;
            }
            background.Dispose();
        }
        slot.Release();
    }

    /// <summary>Held while a request runs. <see cref="Token"/> is cancelled if a foreground request pre-empts it.</summary>
    public sealed class Lease : IDisposable
    {
        private readonly DescriberGate gate;
        private readonly CancellationTokenSource? background;
        private int disposed;

        internal Lease(DescriberGate gate, CancellationToken token, CancellationTokenSource? background)
        {
            this.gate = gate;
            Token = token;
            this.background = background;
        }

        public CancellationToken Token { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) gate.Exit(background);
        }
    }
}
