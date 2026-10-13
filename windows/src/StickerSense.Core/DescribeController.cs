using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace StickerSense.Core;

/// <summary>Where the item to describe is.</summary>
public interface IFocusSource
{
    /// <summary>
    /// The item with keyboard / screen-reader focus, or a 220×220 pixel square around the mouse
    /// pointer when nothing usable has focus.
    /// </summary>
    Task<FocusTarget> CurrentTargetAsync(CancellationToken ct = default);

    /// <summary>
    /// A closer look at an item whose label doesn't say what it is ("", "image"): e.g. an image
    /// inside it. This queries the other app, so it's only used for ambiguous labels, after the
    /// user has rested on the item.
    /// </summary>
    Task<FocusTarget> RefineAsync(FocusTarget target, CancellationToken ct = default) => Task.FromResult(target);
}

/// <summary>Gets text to the user's ears and braille display. Safe to call from any thread.</summary>
public interface IAnnouncer
{
    /// <param name="interrupt">Cut off current speech (answers the user asked for). Auto descriptions queue politely instead.</param>
    void Say(string text, bool interrupt = true);
}

public enum Earcon
{
    /// <summary>Soft upward blip: "working on it".</summary>
    Working,
    /// <summary>Gentle two-note chime: "here it comes" (played just before speech).</summary>
    Ready,
    /// <summary>Low double tone: "that didn't work".</summary>
    Failed,
    /// <summary>Very short tick: something was added or toggled.</summary>
    Tick,
}

public interface IEarcons
{
    void Play(Earcon sound);
}

public interface IDescribeSettings
{
    bool AutoDescribe { get; }
    AutoScope AutoScope { get; }
    bool IncludeMeaning { get; }

    /// <summary>Extra process names treated as chat apps (test-only, from the command line).</summary>
    IReadOnlyCollection<string> AlsoWatchedApps => [];
}

public interface IActivityLog
{
    void Add(string message);
}

/// <summary>
/// The heart of the app: describes whatever has focus when the shortcut is pressed or (auto
/// mode) when focus rests on a sticker or GIF. Press once for a short description, again
/// within 3 seconds for a detailed one; a third press repeats the detail.
/// </summary>
/// <remarks>
/// Built to stay out of the screen reader's way: auto mode works only from the data delivered
/// with focus events (no extra queries into the chat app), waits until the user rests on an
/// item, keeps quiet right after an app switch, and runs one description at a time.
/// Call it from the UI thread: its state isn't locked, it relies on await returning there.
/// </remarks>
public sealed class DescribeController
{
    private readonly StickerPipeline pipeline;
    private readonly IFocusSource focus;
    private readonly IScreenCapturer capturer;
    private readonly IAnnouncer announcer;
    private readonly IEarcons earcons;
    private readonly IDescribeSettings settings;
    private readonly IActivityLog log;
    private readonly Func<long> largestScreenArea;
    private readonly TimeProvider clock;

    private Spoken? last;
    /// <summary>The one description being worked on. Only one runs at a time, so repeated presses never pile up.</summary>
    private Job? job;
    private CancellationTokenSource? autoPending;
    private CancellationTokenSource? afterQuiet;
    private FocusTarget? lastAutoTarget;
    /// <summary>Focus has visited another item since the last description was spoken.</summary>
    private bool movedAwaySinceLastSpoken = true;
    private FocusTarget? latestFocus;
    private DateTimeOffset quietUntil = DateTimeOffset.MinValue;
    private DateTimeOffset? lastEngineWarning;

    public DescribeController(
        StickerPipeline pipeline,
        IFocusSource focus,
        IScreenCapturer capturer,
        IAnnouncer announcer,
        IEarcons earcons,
        IDescribeSettings settings,
        IActivityLog log,
        Func<long> largestScreenArea,
        TimeProvider? clock = null)
    {
        this.pipeline = pipeline;
        this.focus = focus;
        this.capturer = capturer;
        this.announcer = announcer;
        this.earcons = earcons;
        this.settings = settings;
        this.log = log;
        this.largestScreenArea = largestScreenArea;
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>How long after a description a second press means "more detail".</summary>
    public TimeSpan DetailWindow { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Auto mode only looks at items the user rests on this long (and lets the screen reader speak first).</summary>
    public TimeSpan AutoSettleDelay { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>After switching apps the screen reader is busiest; auto mode stays out of its way this long.</summary>
    public TimeSpan QuietAfterAppSwitch { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>The "working" blip only plays if the answer doesn't come from the cache almost instantly.</summary>
    public TimeSpan WorkingBlipDelay { get; init; } = TimeSpan.FromMilliseconds(180);

    /// <summary>While a description takes long, a blip this often (and "Still working" once, at the third).</summary>
    public TimeSpan WorkingBlipInterval { get; init; } = TimeSpan.FromMilliseconds(2200);

    /// <summary>Said when the shortcut lands on a blank (one-colour) area.</summary>
    public const string NothingVisibleMessage = "There's nothing visible there to describe.";

    public const string TooLargeMessage =
        "Focus is on a large area, like a whole chat or window. Move focus onto the sticker, then press the shortcut again.";

    /// <summary>Raised after a description is stored in the library.</summary>
    public event EventHandler? LibraryChanged;

    private enum Level { Short, Detailed }

    private enum JobKind { Short, Detail }

    private sealed record Spoken(
        FocusTarget Target,
        StickerRecord Record,
        List<Image<Rgba32>> Frames,
        DescriptionContext Context,
        Level Level,
        string Text,
        DateTimeOffset At,
        bool ViaAuto);

    private sealed class Job(FocusTarget target, JobKind kind, bool auto)
    {
        private readonly CancellationTokenSource cancellation = new();

        public FocusTarget Target { get; } = target;
        public JobKind Kind { get; } = kind;
        public bool Auto { get; } = auto;
        public CancellationToken Token => cancellation.Token;

        /// <summary>The shortcut was pressed again on the same item while its short description was coming.</summary>
        public bool DetailRequested { get; set; }

        public void Cancel() => cancellation.Cancel();
    }

    // MARK: Shortcut

    public async Task ShortcutPressedAsync()
    {
        FocusTarget target;
        try
        {
            target = await focus.CurrentTargetAsync();
        }
        catch (Exception e)
        {
            Fail(e);
            return;
        }
        // Only sticker and GIF labels go in the log — never a text message's words.
        log.Add($"Shortcut on: {LoggableLabel(target)} {target.Bounds}");

        if (job is { } running)
        {
            if (running.Target.IsLikelySameItem(target))
            {
                // Already working on this item. A press during the short description asks for the
                // detail straight after; a press during the detail just confirms we heard it.
                if (running.Kind == JobKind.Short) running.DetailRequested = true;
                earcons.Play(Earcon.Tick);
                return;
            }
            // The user moved on: drop the old request and start on the new item.
            running.Cancel();
            job = null;
        }

        if (last is { } previous && previous.Target.IsLikelySameItem(target))
        {
            var recent = clock.GetUtcNow() - previous.At < DetailWindow;
            // After an automatic description, the shortcut on the same item always means "more".
            if (recent || previous.ViaAuto)
            {
                if (previous.Level == Level.Short) await RunJobAsync(new Job(target, JobKind.Detail, auto: false), j => RunDetailAsync(previous, j));
                else Speak(previous.Text, interrupt: true, chime: false);
                return;
            }
        }
        await DescribeShortAsync(target, auto: false, verdict: null);
    }

    // MARK: Auto mode

    /// <summary>Called for every focus change, with the data UI Automation delivered with the event.</summary>
    public void FocusChanged(FocusTarget target)
    {
        latestFocus = target;
        // Focus visited another item: coming back to the last one should speak it again. The same
        // element raising focus again, or its name changing, isn't leaving it.
        if (last is { } previous && !IsSameElement(previous.Target, target)) movedAwaySinceLastSpoken = true;
        if (lastAutoTarget is not null && !IsSameElement(lastAutoTarget, target)) lastAutoTarget = null;
        if (!settings.AutoDescribe) return;
        // Right after an app switch the screen reader is busiest: don't start anything.
        if (clock.GetUtcNow() < quietUntil) return;
        Evaluate(target);
    }

    /// <summary>The foreground app changed (from a window event; no queries).</summary>
    public void AppSwitched()
    {
        quietUntil = clock.GetUtcNow() + QuietAfterAppSwitch;
        lastAutoTarget = null;
        autoPending?.Cancel();
        if (job is { Auto: true } running)
        {
            running.Cancel();
            job = null;
        }
        // Once it has settled, consider whatever the user ended up on.
        afterQuiet?.Cancel();
        var cts = afterQuiet = new CancellationTokenSource();
        _ = AfterQuietAsync(cts.Token);
    }

    /// <summary>Forget auto-mode state (auto mode switched on or off).</summary>
    public void ResetAuto()
    {
        autoPending?.Cancel();
        afterQuiet?.Cancel();
        lastAutoTarget = null;
        if (job is { Auto: true } running)
        {
            running.Cancel();
            job = null;
        }
    }

    private async Task AfterQuietAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(QuietAfterAppSwitch, clock, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (settings.AutoDescribe && latestFocus is { } resting) Evaluate(resting);
    }

    private void Evaluate(FocusTarget target)
    {
        // Focus moved: an automatic description of the previous item is no longer wanted.
        if (job is { Auto: true } running && !running.Target.IsLikelySameItem(target))
        {
            running.Cancel();
            job = null;
        }
        autoPending?.Cancel();
        if (target.IsOwnApp || !StickerDetector.IsWatched(target.AppName, settings.AutoScope, settings.AlsoWatchedApps)) return;
        if (target.IsSameItem(lastAutoTarget)) return;
        lastAutoTarget = target;

        // Decide from the event's own data when possible. Asking the app for more is a call served
        // on the chat app's UI thread — the thread the screen reader is waiting on — so only do it
        // when the label is ambiguous.
        var verdict = StickerDetector.Verdict(target);
        // (The accessible name alone: the role text is always there and says nothing about stickers.)
        var ambiguous = verdict == StickerVerdict.NotSticker && LabelClassifier.IsAmbiguous(target.Name);
        if (verdict == StickerVerdict.NotSticker && !ambiguous) return;
        // Don't repeat ourselves while focus stays on the item we just spoke about. Coming back to it
        // after visiting another item speaks it again (instantly, from the library).
        if (last is { } previous && IsSameElement(previous.Target, target) && !movedAwaySinceLastSpoken) return;
        if (job is not null) return;

        var cts = autoPending = new CancellationTokenSource();
        _ = RunAutoAsync(target, verdict, ambiguous, cts.Token);
    }

    private async Task RunAutoAsync(FocusTarget target, StickerVerdict verdict, bool ambiguous, CancellationToken ct)
    {
        try
        {
            // Only items the user rests on; also lets the screen reader finish its own announcement.
            await Task.Delay(AutoSettleDelay, clock, ct);
            if (latestFocus is not { } now || !now.IsLikelySameItem(target)) return;
            if (ambiguous)
            {
                var refined = await focus.RefineAsync(target, ct);
                verdict = StickerDetector.Verdict(refined);
                if (verdict == StickerVerdict.NotSticker) return;
            }
            if (job is not null || ct.IsCancellationRequested) return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        log.Add($"Auto: {verdict} — {target.Text}");
        await DescribeShortAsync(target, auto: true, verdict);
    }

    // MARK: Describing

    private async Task RunJobAsync(Job newJob, Func<Job, Task> work)
    {
        job = newJob;
        try
        {
            await work(newJob);
        }
        finally
        {
            if (ReferenceEquals(job, newJob)) job = null;
        }
    }

    private async Task DescribeShortAsync(FocusTarget target, bool auto, StickerVerdict? verdict)
    {
        if (IsTooLarge(target.Bounds))
        {
            if (!auto) Fail(TooLargeMessage);
            return;
        }
        await RunJobAsync(new Job(target, JobKind.Short, auto), j => RunShortAsync(j, verdict));
    }

    private async Task RunShortAsync(Job current, StickerVerdict? verdict)
    {
        var target = current.Target;
        var auto = current.Auto;
        var ct = current.Token;
        var started = clock.GetTimestamp();
        using var progress = new CancellationTokenSource();
        if (!auto) _ = ProgressFeedbackAsync(progress.Token);
        List<Image<Rgba32>>? frames = null;
        try
        {
            var rect = target.Bounds;
            var first = await capturer.CaptureAsync(rect, ct);
            frames = [first];
            // A flat, empty capture (blank or protected screen area): nothing to describe.
            if (ImagePrep.IsBlank(first))
            {
                log.Add($"Blank capture at {rect}, not describing");
                if (!auto) Fail(NothingVisibleMessage);
                return;
            }
            var context = new DescriptionContext.ScreenItem(target.Text.Length == 0 ? null : target.Text);
            var probe = await pipeline.ProbeAsync(first, context, ct);
            var source = new StickerSource.Received(target.AppName.Length == 0 ? "unknown" : target.AppName);
            StickerRecord record;
            // "Your GIF" / "Your sticker": probably something sent from the picker, which already has a
            // good description made from the original file. Checked before the cache: that beats a
            // description made from a chat's paused preview.
            var recentlySent = IsOwnItem(target.Text) ? pipeline.Library.MatchRecentlySent(probe.Fingerprint) : null;
            // A GIF StickerSense already described with its motion, showing its paused preview now.
            var knownGif = recentlySent is null && probe.Cached is null && (verdict ?? StickerDetector.Verdict(target)) == StickerVerdict.Gif
                ? pipeline.Library.MatchAnimationFrame(probe.Fingerprint)
                : null;
            if (recentlySent is { Reading: not null })
            {
                log.Add("Recognised as something you sent");
                record = recentlySent;
                var masked = StickerPipeline.Mask(frames, probe);
                DisposeAll(frames);
                frames = masked;
            }
            else if (knownGif is { Reading: not null })
            {
                log.Add("Recognised this GIF from an earlier view");
                record = knownGif;
                var masked = StickerPipeline.Mask(frames, probe);
                DisposeAll(frames);
                frames = masked;
            }
            else if (probe.Cached is not null)
            {
                // Seen before: paint out the sender's name and time, and answer from the cache.
                var masked = StickerPipeline.Mask(frames, probe);
                DisposeAll(frames);
                frames = masked;
                record = await pipeline.DescribeAsync(frames, probe, context, source, ct: ct);
            }
            else
            {
                var fresh = await DescribeFreshAsync(rect, first, probe, context, source, verdict, ct);
                DisposeAll(frames);
                frames = fresh.Frames;
                record = fresh.Record;
            }
            ct.ThrowIfCancellationRequested();
            if (record.Reading is not { } reading) return;
            progress.Cancel();
            LibraryChanged?.Invoke(this, EventArgs.Empty);

            // A shortcut press during an automatic look turns it into a requested one.
            var requested = !auto || current.DetailRequested;
            if (!requested)
            {
                // Only stickers and GIFs are announced automatically, and only if the user is still there
                // (judged from focus events; nothing is queried).
                if (!reading.Kind.IsCollectible())
                {
                    log.Add($"Auto: skipped {reading.Kind.RawValue()}");
                    return;
                }
                if (latestFocus is not { } now || !(now.IsLikelySameItem(target) || now.Bounds == target.Bounds))
                {
                    log.Add("Auto: focus moved on, not speaking");
                    return;
                }
            }
            var text = SpokenText.Short(reading, settings.IncludeMeaning);
            var seconds = clock.GetElapsedTime(started).TotalSeconds;
            var cached = probe.Cached is not null || recentlySent is { Reading: not null } || knownGif is { Reading: not null };
            log.Add($"{(auto ? "Auto" : "Short")} in {seconds:0.00}s{(cached ? " (cached)" : "")} [{frames.Count} frame(s)]: {text}");
            Speak(text, interrupt: requested, chime: true);
            // A press for the detail usually follows within seconds: get it ready now.
            _ = PrewarmDetailAsync();
            var spoken = new Spoken(target, record, frames, context, Level.Short, text, clock.GetUtcNow(), ViaAuto: !requested);
            Remember(spoken);
            frames = null; // now owned by `last`

            if (current.DetailRequested)
            {
                current.DetailRequested = false;
                await RunDetailAsync(spoken, current);
            }
        }
        catch (OperationCanceledException)
        {
            log.Add("Cancelled (moved to another item)");
        }
        catch (Exception e)
        {
            if (ct.IsCancellationRequested) return;
            if (auto && !current.DetailRequested)
            {
                log.Add($"Auto failed: {e.Message}");
                WarnOnceIfEngineUnavailable(e);
            }
            else
            {
                Fail(e);
            }
        }
        finally
        {
            progress.Cancel();
            if (frames is not null) DisposeAll(frames);
        }
    }

    /// <summary>
    /// Describes a sticker not seen before. Most stickers are still, so the first (masked) frame
    /// is described straight away while motion detection runs; if it turns out to move, that guess
    /// is cancelled and the frames are described instead. Returns the masked frames (owned by the
    /// caller) and the stored record. <paramref name="first"/> stays owned by the caller.
    /// </summary>
    private async Task<(List<Image<Rgba32>> Frames, StickerRecord Record)> DescribeFreshAsync(
        PixelRect rect, Image<Rgba32> first, StickerProbe probe, DescriptionContext context, StickerSource source, StickerVerdict? verdict, CancellationToken ct)
    {
        Image<Rgba32>? maskedFirst = ImagePrep.Masking(first, probe.ChromeBoxes);
        using var speculation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var speculative = verdict == StickerVerdict.Gif ? null : pipeline.ReadStillAsync(maskedFirst, probe, context, speculation.Token);
        List<Image<Rgba32>>? captured = null;
        try
        {
            captured = await MotionSampler.CaptureMotionAsync(capturer, rect, first, verdict == StickerVerdict.Gif,
                (delay, token) => Task.Delay(delay, clock, token), ct);
            if (captured.Count == 1 && speculative is not null)
            {
                var reading = await speculative;
                List<Image<Rgba32>> still = [maskedFirst];
                maskedFirst = null; // now owned by the result
                try
                {
                    return (still, await pipeline.StoreAsync(still, probe, reading, source));
                }
                catch
                {
                    DisposeAll(still);
                    throw;
                }
            }
            // It moves: the still guess is no use. Describe the frames instead.
            speculation.Cancel();
            await IgnoringFailure(speculative);
            var masked = StickerPipeline.Mask(captured, probe);
            try
            {
                // An animation StickerSense already knows (e.g. a GIF you sent from the picker),
                // recognised from whichever frames the chat is showing: use its description.
                if (masked.Count > 1 && pipeline.Library.MatchAnimated(masked.Select(f => new Fingerprint(f)).ToList()) is { Reading: not null } known)
                {
                    log.Add("Recognised animation from its frames");
                    return (masked, known);
                }
                return (masked, await pipeline.DescribeAsync(masked, probe, context, source, ct: ct));
            }
            catch
            {
                DisposeAll(masked);
                throw;
            }
        }
        finally
        {
            // Never leave the model working on a guess nobody wants (cancelled job, failure, motion).
            speculation.Cancel();
            await IgnoringFailure(speculative);
            maskedFirst?.Dispose();
            if (captured is not null)
            {
                foreach (var frame in captured)
                {
                    if (!ReferenceEquals(frame, first)) frame.Dispose();
                }
            }
        }
    }

    /// <summary>WhatsApp labels the user's own messages "Your GIF, …" or "Your sticker, …".</summary>
    internal static bool IsOwnItem(string label) =>
        LabelClassifier.Clean(label).TrimStart().StartsWith("your ", StringComparison.Ordinal);

    private static async Task IgnoringFailure(Task? task)
    {
        if (task is null) return;
        try
        {
            await task;
        }
        catch
        {
            // Cancelled or failed: its result isn't needed.
        }
    }

    private async Task PrewarmDetailAsync()
    {
        try
        {
            await pipeline.Describer.PrewarmDetailAsync();
        }
        catch (Exception e)
        {
            log.Add($"Detail prewarm: {e.Message}");
        }
    }

    private async Task RunDetailAsync(Spoken spoken, Job current)
    {
        using var progress = new CancellationTokenSource();
        _ = ProgressFeedbackAsync(progress.Token);
        try
        {
            var started = clock.GetTimestamp();
            var text = await pipeline.DetailAsync(spoken.Record, spoken.Frames, spoken.Context, current.Token);
            current.Token.ThrowIfCancellationRequested();
            progress.Cancel();
            log.Add($"Detail in {clock.GetElapsedTime(started).TotalSeconds:0.00}s: {text}");
            Speak(text, interrupt: true, chime: true);
            Remember(spoken with { Level = Level.Detailed, Text = text, At = clock.GetUtcNow(), ViaAuto = false });
        }
        catch (OperationCanceledException)
        {
            log.Add("Detail cancelled");
        }
        catch (Exception e)
        {
            if (!current.Token.IsCancellationRequested) Fail(e);
        }
        finally
        {
            progress.Cancel();
        }
    }

    private void Remember(Spoken spoken)
    {
        if (last is { } previous && !ReferenceEquals(previous.Frames, spoken.Frames)) DisposeAll(previous.Frames);
        last = spoken;
        movedAwaySinceLastSpoken = false;
    }

    /// <summary>The same element: same label and size (perhaps scrolled), or the same spot with a new name.</summary>
    private static bool IsSameElement(FocusTarget a, FocusTarget b) =>
        a.IsLikelySameItem(b)
        || (Math.Abs(a.Bounds.CenterX - b.Bounds.CenterX) < 3 && Math.Abs(a.Bounds.CenterY - b.Bounds.CenterY) < 3
            && Math.Abs(a.Bounds.Width - b.Bounds.Width) < 4 && Math.Abs(a.Bounds.Height - b.Bounds.Height) < 4);

    /// <summary>The label, when it says sticker or GIF; otherwise a placeholder, so a message's words never reach the log.</summary>
    private static string LoggableLabel(FocusTarget target) =>
        target.Text.Length == 0 ? "(no name)" : StickerDetector.Verdict(target) != StickerVerdict.NotSticker ? target.Text : "(not a sticker label)";

    /// <summary>
    /// Feedback while waiting: a soft blip after a moment (skipped for instant cache hits), then a
    /// blip every few seconds, and "Still working" once if it's taking a while.
    /// </summary>
    private async Task ProgressFeedbackAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(WorkingBlipDelay, clock, ct);
            earcons.Play(Earcon.Working);
            for (var ticks = 1; ; ticks++)
            {
                await Task.Delay(WorkingBlipInterval, clock, ct);
                if (ticks == 3) announcer.Say("Still working.", interrupt: false);
                else earcons.Play(Earcon.Working);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // MARK: Output helpers

    private void Speak(string text, bool interrupt, bool chime)
    {
        if (chime) earcons.Play(Earcon.Ready);
        announcer.Say(text, interrupt);
    }

    private void Fail(Exception error) => Fail(SpokenText.Failure(error));

    private void Fail(string message)
    {
        log.Add($"Error: {message}");
        earcons.Play(Earcon.Failed);
        announcer.Say(message, interrupt: true);
    }

    private void WarnOnceIfEngineUnavailable(Exception error)
    {
        if (error is not DescribeException { Kind: DescribeErrorKind.EngineUnavailable, Status: { } status }) return;
        var now = clock.GetUtcNow();
        if (lastEngineWarning is { } warned && now - warned < TimeSpan.FromMinutes(10)) return;
        lastEngineWarning = now;
        announcer.Say($"StickerSense: {status.Explanation}", interrupt: false);
    }

    private bool IsTooLarge(PixelRect rect)
    {
        var largest = largestScreenArea();
        return largest > 0 && rect.Area > largest * 0.45;
    }

    private static void DisposeAll(List<Image<Rgba32>> frames)
    {
        foreach (var frame in frames) frame.Dispose();
    }
}
