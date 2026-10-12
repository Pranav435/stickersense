using System.Collections.Concurrent;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace StickerSense.Core.Tests;

/// <summary>A single-threaded synchronization context, standing in for the app's UI thread.</summary>
internal sealed class UiThread : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = [];

    public UiThread()
    {
        var thread = new Thread(() =>
        {
            SetSynchronizationContext(this);
            foreach (var (callback, state) in queue.GetConsumingEnumerable()) callback(state);
        })
        { IsBackground = true, Name = "Test UI thread" };
        thread.Start();
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        if (!queue.IsAddingCompleted) queue.Add((d, state));
    }

    public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

    public override SynchronizationContext CreateCopy() => this;

    public Task InvokeAsync(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async _ =>
        {
            try
            {
                await action();
                done.SetResult();
            }
            catch (Exception e)
            {
                done.SetException(e);
            }
        }, null);
        return done.Task;
    }

    public Task InvokeAsync(Action action) => InvokeAsync(() =>
    {
        action();
        return Task.CompletedTask;
    });

    /// <summary>Starts work on the UI thread and returns its task without waiting for it.</summary>
    public async Task<Task> StartAsync(Func<Task> action)
    {
        Task? started = null;
        await InvokeAsync(() =>
        {
            started = action();
            return Task.CompletedTask;
        });
        return started!;
    }

    public void Dispose() => queue.CompleteAdding();
}

/// <summary>"Now" moves only when the test says so; delays still use real (short) timers.</summary>
internal sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

internal sealed class FakeDescriber : IStickerDescriber
{
    public EngineStatus Status { get; set; } = new(EngineState.Ready);
    public StickerReading Reading { get; set; } = new(StickerKind.Sticker, "Shiba dog shrugging", "idk", "whatever");
    public string Detail { get; set; } = "A shiba dog shrugs with both paws up. People send it to say I don't know.";
    public TaskCompletionSource? Hold { get; set; }
    public int Reads;
    public int Details;
    public int LastFrameCount;
    public string LastOcr = "";
    public volatile bool ReadStarted;

    public void UpdateOptions(DescriberOptions options) { }

    public Task PrewarmAsync(CancellationToken ct = default) => Task.CompletedTask;

    public int Cancellations;
    public int DetailPrewarms;
    public readonly System.Collections.Concurrent.ConcurrentQueue<int> FrameCounts = new();

    /// <summary>Called as each read starts (e.g. to note how far capture had got).</summary>
    public Action? OnRead { get; set; }

    public async Task<StickerReading> ReadAsync(IReadOnlyList<Image<Rgba32>> frames, DescriptionContext context, string ocrText = "",
        DescribePriority priority = DescribePriority.Foreground, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Reads);
        LastFrameCount = frames.Count;
        FrameCounts.Enqueue(frames.Count);
        LastOcr = ocrText;
        ReadStarted = true;
        OnRead?.Invoke();
        try
        {
            if (Hold is { } hold) await hold.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref Cancellations);
            throw;
        }
        if (!Status.CanDescribe) throw DescribeException.EngineUnavailable(Status);
        return Reading with { Animated = frames.Count > 1, Text = StickerText.FromOcr(ocrText) is { Length: > 0 } t ? t : Reading.Text };
    }

    public Task PrewarmDetailAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref DetailPrewarms);
        return Task.CompletedTask;
    }

    public Task<string> DetailedAsync(IReadOnlyList<Image<Rgba32>> frames, DescriptionContext context, string ocrText = "",
        StickerReading? shortReading = null, DescribePriority priority = DescribePriority.Foreground, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Details);
        if (!Status.CanDescribe) throw DescribeException.EngineUnavailable(Status);
        return Task.FromResult(Detail);
    }
}

internal sealed class FakeFocus : IFocusSource
{
    public FocusTarget Target { get; set; } = new()
    {
        Bounds = new PixelRect(400, 300, 160, 160),
        Name = "Sticker",
        RoleText = "image",
        IsImage = true,
        AppName = "whatsapp",
    };

    public Task<FocusTarget> CurrentTargetAsync(CancellationToken ct = default) => Task.FromResult(Target);

    /// <summary>What a closer look at an ambiguous item finds (null = nothing more).</summary>
    public Func<FocusTarget, FocusTarget>? Refine { get; set; }
    public int Refines;

    public Task<FocusTarget> RefineAsync(FocusTarget target, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Refines);
        return Task.FromResult(Refine?.Invoke(target) ?? target);
    }
}

/// <summary>Returns copies of one image, or cycles through frames to look animated.</summary>
internal sealed class FakeCapturer : IScreenCapturer
{
    private int next;
    public List<Image<Rgba32>> Frames { get; set; } = [Fixtures.Sticker(Art.Dog, "idk", size: 200)];
    public int Captures;

    public Task<Image<Rgba32>> CaptureAsync(PixelRect rect, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Captures);
        var frame = Frames[next++ % Frames.Count];
        return Task.FromResult(frame.Clone());
    }
}

/// <summary>Returns fixed OCR lines; a plain string becomes one line covering the middle of the image.</summary>
internal sealed class FakeOcr : IOcrReader
{
    private readonly Func<Image<Rgba32>, IReadOnlyList<OcrLine>> read;

    public FakeOcr(string text) : this(_ => text.Length == 0 ? [] : [new OcrLine(text, new NormalizedBox(0.3, 0.75, 0.4, 0.1))]) { }

    public FakeOcr(IReadOnlyList<OcrLine> lines) : this(_ => lines) { }

    public FakeOcr(Func<Image<Rgba32>, IReadOnlyList<OcrLine>> read) => this.read = read;

    public int Reads;

    public Task<IReadOnlyList<OcrLine>> ReadLinesAsync(Image<Rgba32> image, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Reads);
        return Task.FromResult(read(image));
    }
}

/// <summary>Collects speech and earcons in one ordered list, e.g. "earcon:Ready", "say!:text" (! = interrupt).</summary>
internal sealed class Recorder : IAnnouncer, IEarcons, IActivityLog
{
    private readonly ConcurrentQueue<string> events = new();
    private readonly ConcurrentQueue<string> log = new();

    public IReadOnlyList<string> Events => events.ToArray();
    public IReadOnlyList<string> Spoken => events.Where(e => e.StartsWith("say", StringComparison.Ordinal)).ToArray();
    public IReadOnlyList<string> Log => log.ToArray();

    public void Say(string text, bool interrupt = true) => events.Enqueue((interrupt ? "say!:" : "say:") + text);
    public void Play(Earcon sound) => events.Enqueue("earcon:" + sound);
    public void Add(string message) => log.Enqueue(message);
}

internal sealed class TestSettings : IDescribeSettings
{
    public bool AutoDescribe { get; set; } = true;
    public AutoScope AutoScope { get; set; } = AutoScope.ChatApps;
    public bool IncludeMeaning { get; set; }
}

internal static class Eventually
{
    public static async Task True(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var started = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - started > timeoutMs) Assert.Fail($"Timed out waiting for {what}");
            await Task.Delay(20);
        }
    }
}
