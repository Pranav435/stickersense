namespace StickerSense.Core;

/// <summary>
/// Describes imported stickers (WhatsApp) one at a time, but only while the computer is genuinely
/// idle — no keyboard or mouse input anywhere for 90 seconds, and battery saver off — so the
/// on-device model never competes with the screen reader while the user is working. The moment
/// input happens, the sticker being described is abandoned (and retried later).
/// </summary>
public sealed class BackgroundIndexer
{
    private readonly StickerPipeline pipeline;
    private readonly Func<TimeSpan> sinceLastInput;
    private readonly Func<bool> powerSaving;
    private readonly IActivityLog log;
    private readonly TimeProvider clock;

    /// <param name="pipeline">A background-priority pipeline.</param>
    /// <param name="sinceLastInput">Time since the last keyboard or mouse input, system-wide (a cheap local call).</param>
    /// <param name="powerSaving">True while battery saver is on.</param>
    public BackgroundIndexer(StickerPipeline pipeline, Func<TimeSpan> sinceLastInput, Func<bool> powerSaving, IActivityLog log, TimeProvider? clock = null)
    {
        this.pipeline = pipeline;
        this.sinceLastInput = sinceLastInput;
        this.powerSaving = powerSaving;
        this.log = log;
        this.clock = clock ?? TimeProvider.System;
    }

    public TimeSpan RequiredIdle { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>Pause between stickers.</summary>
    public TimeSpan Pause { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>How often to check again while the user is busy.</summary>
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How often to look for input while a sticker is being described.</summary>
    public TimeSpan InputPoll { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Raised after each sticker is described (or given up on).</summary>
    public event EventHandler? Progressed;

    public bool IsIdle => sinceLastInput() >= RequiredIdle && !powerSaving();

    /// <summary>Runs until every sticker is described or <paramref name="ct"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!IsIdle || !pipeline.Describer.Status.CanDescribe)
            {
                await Task.Delay(CheckInterval, clock, ct).ConfigureAwait(false);
                continue;
            }
            var next = pipeline.Library.Undescribed(1).FirstOrDefault();
            if (next is null) return;

            using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var watcher = StopOnInputAsync(run);
            try
            {
                await pipeline.DescribeStoredAsync(next, run.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The user is back (or a request of theirs pre-empted this); try again when idle.
                log.Add("Background describing paused: the computer is in use.");
                continue;
            }
            catch (DescribeException e) when (e.Kind == DescribeErrorKind.EngineUnavailable)
            {
                await Task.Delay(CheckInterval, clock, ct).ConfigureAwait(false);
                continue;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Unreadable or failed: mark it so it isn't retried forever.
                pipeline.Library.SetReading(new StickerReading(StickerKind.Sticker, "Sticker that couldn't be described"), "", next.Id);
                log.Add($"Background describe failed: {e.Message}");
            }
            finally
            {
                run.Cancel();
                await watcher.ConfigureAwait(false);
            }
            Progressed?.Invoke(this, EventArgs.Empty);
            await Task.Delay(Pause, clock, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Cancels <paramref name="run"/> as soon as there's keyboard or mouse input (or battery saver comes on).</summary>
    private async Task StopOnInputAsync(CancellationTokenSource run)
    {
        try
        {
            while (!run.IsCancellationRequested)
            {
                await Task.Delay(InputPoll, clock, run.Token).ConfigureAwait(false);
                if (!IsIdle)
                {
                    run.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
