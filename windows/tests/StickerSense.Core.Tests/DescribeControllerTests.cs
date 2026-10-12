namespace StickerSense.Core.Tests;

public sealed class DescribeControllerTests : IDisposable
{
    private const string Short = "Shiba dog shrugging, “idk”.";
    private readonly UiThread ui = new();
    private readonly FakeDescriber describer = new();
    private readonly FakeFocus focus = new();
    private readonly FakeCapturer capturer = new();
    private readonly Recorder recorder = new();
    private readonly TestSettings settings = new();
    private readonly ManualClock clock = new();
    private readonly StickerLibrary library = new(Fixtures.TempDirectory());
    private readonly DescribeController controller;

    public DescribeControllerTests()
    {
        var pipeline = new StickerPipeline(describer, library, new NullOcrReader());
        controller = new DescribeController(pipeline, focus, capturer, recorder, recorder, settings, recorder, () => 1920L * 1080, clock)
        {
            AutoSettleDelay = TimeSpan.FromMilliseconds(30),
            QuietAfterAppSwitch = TimeSpan.FromMilliseconds(250),
        };
    }

    public void Dispose()
    {
        ui.Dispose();
        library.Dispose();
    }

    private Task Press() => ui.InvokeAsync(controller.ShortcutPressedAsync);

    [Fact]
    public async Task PressOnceShortTwiceDetailThriceRepeat()
    {
        await Press();
        Assert.Equal($"say!:{Short}", recorder.Spoken.Last());
        Assert.Equal("earcon:Ready", recorder.Events[^2]); // chime right before speech
        Assert.Equal(1, describer.Reads);

        clock.Advance(TimeSpan.FromSeconds(1));
        await Press();
        Assert.Equal($"say!:{describer.Detail}", recorder.Spoken.Last());
        Assert.Equal(1, describer.Details);

        clock.Advance(TimeSpan.FromSeconds(1));
        var before = recorder.Events.Count;
        await Press();
        Assert.Equal([$"say!:{describer.Detail}"], recorder.Events.Skip(before)); // repeated, no chime, no new work
        Assert.Equal(1, describer.Details);

        // Later, the same sticker is answered from the cache.
        clock.Advance(TimeSpan.FromSeconds(10));
        await Press();
        Assert.Equal($"say!:{Short}", recorder.Spoken.Last());
        Assert.Equal(1, describer.Reads);
        Assert.Contains(recorder.Log, l => l.Contains("(cached)"));
    }

    [Fact]
    public async Task SecondPressAfterTheWindowIsAShortDescriptionAgain()
    {
        await Press();
        clock.Advance(TimeSpan.FromSeconds(4));
        await Press();
        Assert.Equal(0, describer.Details);
        Assert.Equal([$"say!:{Short}", $"say!:{Short}"], recorder.Spoken);
    }

    [Fact]
    public async Task PressingAgainWhileWorkingGivesDetailRightAfter()
    {
        describer.Hold = new TaskCompletionSource();
        var first = await ui.StartAsync(controller.ShortcutPressedAsync);
        await Eventually.True(() => describer.ReadStarted, "the first look to start");
        await Press();
        Assert.Contains("earcon:Tick", recorder.Events);
        describer.Hold.SetResult();
        await first;
        Assert.Equal([$"say!:{Short}", $"say!:{describer.Detail}"], recorder.Spoken);
        Assert.Contains("earcon:Working", recorder.Events); // it took longer than the blip delay
    }

    [Fact]
    public async Task StillStickersAreDescribedWhileMotionIsChecked()
    {
        var capturesAtFirstRead = -1;
        describer.OnRead = () =>
        {
            if (capturesAtFirstRead < 0) capturesAtFirstRead = capturer.Captures;
        };
        await Press();
        // The model started on the first frame before the two motion checks were captured.
        Assert.Equal(1, capturesAtFirstRead);
        Assert.Equal(3, capturer.Captures);
        Assert.Equal(1, describer.Reads); // no second pass for a still sticker
        Assert.Equal($"say!:{Short}", recorder.Spoken.Single());
        Assert.Equal(1, describer.DetailPrewarms); // the detail is readied once the short one is spoken
    }

    [Fact]
    public async Task MotionCancelsTheStillGuess()
    {
        capturer.Frames = Fixtures.AnimatedFrames();
        describer.Hold = new TaskCompletionSource();
        var run = await ui.StartAsync(controller.ShortcutPressedAsync);
        await Eventually.True(() => describer.Cancellations == 1, "the still guess to be cancelled");
        describer.Hold.SetResult();
        await run;
        Assert.Equal([1, 3], describer.FrameCounts); // the guess, then the animation
        Assert.Single(recorder.Spoken);
        var record = library.Search("").Single();
        Assert.True(record.Reading!.Animated);
    }

    [Fact]
    public async Task CancellingTheJobStopsTheGuessToo()
    {
        describer.Hold = new TaskCompletionSource();
        var first = await ui.StartAsync(controller.ShortcutPressedAsync);
        await Eventually.True(() => describer.ReadStarted, "the guess to start");
        focus.Target = focus.Target with { Name = "Sticker from Raj", Bounds = new PixelRect(400, 700, 160, 160) };
        var second = await ui.StartAsync(controller.ShortcutPressedAsync);
        await first;
        Assert.True(describer.Cancellations >= 1);
        describer.Hold.SetResult();
        await second;
        Assert.Single(recorder.Spoken);
    }

    [Fact]
    public async Task LargeAreasAreExplained()
    {
        focus.Target = focus.Target with { Bounds = new PixelRect(0, 0, 1900, 1000) };
        await Press();
        Assert.Equal([$"say!:{DescribeController.TooLargeMessage}"], recorder.Spoken);
        Assert.Contains("earcon:Failed", recorder.Events);
        Assert.Equal(0, capturer.Captures);
    }

    [Fact]
    public async Task FailuresAreSpokenWithATone()
    {
        describer.Status = new EngineStatus(EngineState.ModelMissing);
        await Press();
        Assert.Equal(["earcon:Failed"], recorder.Events.Where(e => e.StartsWith("earcon:F")));
        Assert.Equal($"say!:{describer.Status.Explanation}", recorder.Spoken.Single());
    }

    [Fact]
    public async Task AnimatedStickersAreCapturedAsSeveralFrames()
    {
        capturer.Frames = Fixtures.AnimatedFrames();
        await Press();
        Assert.InRange(describer.LastFrameCount, 2, 4);
        var record = library.Search("").Single();
        Assert.True(record.Reading!.Animated);
        Assert.EndsWith(".gif", record.ImageFile);
    }

    [Fact]
    public async Task AutoModeSpeaksStickersPolitely()
    {
        await ui.InvokeAsync(() => controller.FocusChanged(focus.Target));
        await Eventually.True(() => recorder.Spoken.Count > 0, "the automatic description");
        Assert.Equal($"say:{Short}", recorder.Spoken.Single()); // queued, not interrupting
        Assert.Contains("earcon:Ready", recorder.Events);
        Assert.DoesNotContain("earcon:Working", recorder.Events);

        // Landing on it again soon after doesn't repeat it.
        await ui.InvokeAsync(() => controller.FocusChanged(focus.Target with { Name = "Message" }));
        await ui.InvokeAsync(() => controller.FocusChanged(focus.Target));
        await Task.Delay(200);
        Assert.Single(recorder.Spoken);

        // The shortcut on an automatically described item means "more".
        clock.Advance(TimeSpan.FromSeconds(30));
        await Press();
        Assert.Equal($"say!:{describer.Detail}", recorder.Spoken.Last());
    }

    [Fact]
    public async Task AutoModeSkipsPhotosAndText()
    {
        describer.Reading = new StickerReading(StickerKind.Photo, "Beach at sunset");
        await ui.InvokeAsync(() => controller.FocusChanged(focus.Target));
        await Eventually.True(() => recorder.Log.Any(l => l.Contains("Auto: skipped photo")), "the photo to be skipped");
        Assert.Empty(recorder.Spoken);
    }

    [Fact]
    public async Task AutoModeDropsTheWorkWhenFocusMovesOn()
    {
        describer.Hold = new TaskCompletionSource();
        await ui.InvokeAsync(() => controller.FocusChanged(focus.Target));
        await Eventually.True(() => describer.ReadStarted, "the automatic look to start");
        var elsewhere = focus.Target with { Name = "Message from Sam, lunch?, 15:49", IsImage = false, RoleText = "list item", Bounds = new PixelRect(400, 500, 600, 60) };
        await ui.InvokeAsync(() => controller.FocusChanged(elsewhere));
        // The model work for the old item is cancelled, not just left unspoken.
        await Eventually.True(() => recorder.Log.Any(l => l.Contains("Cancelled")), "the old description to be cancelled");
        describer.Hold.SetResult();
        await Task.Delay(100);
        Assert.Empty(recorder.Spoken);
    }

    [Fact]
    public async Task AutoModeOnlyDescribesItemsTheUserRestsOn()
    {
        // Arrowing past a sticker onto a text message within the settle time: nothing is captured.
        var message = focus.Target with { Name = "Message from Sam, lunch?, 15:49", IsImage = false, RoleText = "list item", Bounds = new PixelRect(400, 500, 600, 60) };
        await ui.InvokeAsync(() =>
        {
            controller.FocusChanged(focus.Target);
            controller.FocusChanged(message);
        });
        await Task.Delay(250);
        Assert.Equal(0, capturer.Captures);
    }

    [Fact]
    public async Task AutoModeKeepsQuietRightAfterAnAppSwitch()
    {
        await ui.InvokeAsync(() =>
        {
            controller.AppSwitched();
            controller.FocusChanged(focus.Target);
        });
        await Task.Delay(120);
        Assert.Equal(0, capturer.Captures); // the screen reader is settling: no capture, no model

        // Once it has settled, the sticker the user is resting on is described.
        clock.Advance(TimeSpan.FromSeconds(2));
        await Eventually.True(() => recorder.Spoken.Count == 1, "the description after the quiet period");
        Assert.Equal($"say:{Short}", recorder.Spoken.Single());
    }

    [Fact]
    public async Task OnlyAmbiguousLabelsGetACloserLook()
    {
        // A clear text message: decided from the focus event alone, no extra queries.
        await ui.InvokeAsync(() => controller.FocusChanged(focus.Target with { Name = "Message from Sam, lunch?, 15:49", IsImage = false, RoleText = "list item", Bounds = new PixelRect(0, 0, 600, 60) }));
        await Task.Delay(150);
        Assert.Equal(0, focus.Refines);

        // An unnamed group: one closer look, which finds a sticker image inside.
        focus.Refine = t => t with { Name = "Sticker", IsImage = true };
        await ui.InvokeAsync(() => controller.FocusChanged(focus.Target with { Name = "", RoleText = "group", IsImage = false }));
        await Eventually.True(() => recorder.Spoken.Count == 1, "the sticker found by the closer look");
        Assert.Equal(1, focus.Refines);
    }

    [Fact]
    public async Task MovingToAnotherItemCancelsTheOldRequest()
    {
        describer.Hold = new TaskCompletionSource();
        var first = await ui.StartAsync(controller.ShortcutPressedAsync);
        await Eventually.True(() => describer.ReadStarted, "the first look to start");

        focus.Target = focus.Target with { Name = "Sticker from Raj", Bounds = new PixelRect(400, 700, 160, 160) };
        var second = await ui.StartAsync(controller.ShortcutPressedAsync);
        await first;
        Assert.Contains(recorder.Log, l => l.Contains("Cancelled"));
        describer.Hold.SetResult();
        await second;
        Assert.Equal([$"say!:{Short}"], recorder.Spoken); // one answer, for the item the user is on now
    }

    [Fact]
    public async Task LongWaitsGetFeedback()
    {
        var patient = new DescribeController(new StickerPipeline(describer, library, new NullOcrReader()), focus, capturer, recorder, recorder, settings, recorder,
            () => 1920L * 1080, clock)
        {
            WorkingBlipDelay = TimeSpan.FromMilliseconds(10),
            WorkingBlipInterval = TimeSpan.FromMilliseconds(60),
        };
        describer.Hold = new TaskCompletionSource();
        var run = await ui.StartAsync(patient.ShortcutPressedAsync);
        await Eventually.True(() => recorder.Spoken.Contains("say:Still working."), "the still-working message");
        describer.Hold.SetResult();
        await run;
        Assert.Single(recorder.Spoken, "say:Still working.");
        Assert.True(recorder.Events.Count(e => e == "earcon:Working") >= 3);
    }

    [Fact]
    public async Task AutoModeWatchesOnlyChatAppsByDefault()
    {
        var notepad = focus.Target with { AppName = "notepad" };
        await ui.InvokeAsync(() => controller.FocusChanged(notepad));
        await ui.InvokeAsync(() => controller.FocusChanged(focus.Target with { IsOwnApp = true, Name = "Practice sticker" }));
        await Task.Delay(300);
        Assert.Equal(0, describer.Reads);

        settings.AutoScope = AutoScope.Everywhere;
        await ui.InvokeAsync(() => controller.FocusChanged(notepad with { Name = "GIF" }));
        await Eventually.True(() => recorder.Spoken.Count == 1, "the description in another app");

        settings.AutoDescribe = false;
        await ui.InvokeAsync(() => controller.FocusChanged(notepad with { Name = "Sticker pack" }));
        await Task.Delay(200);
        Assert.Equal(1, describer.Reads);
    }

    [Fact]
    public async Task AutoModeIgnoresThingsThatArentStickers()
    {
        await ui.InvokeAsync(() => controller.FocusChanged(focus.Target with { Name = "Message from Sam: lunch?", IsImage = false, RoleText = "list item", Bounds = new PixelRect(0, 0, 600, 60) }));
        await Task.Delay(200);
        Assert.Equal(0, capturer.Captures);
    }

    [Fact]
    public async Task AutoModeWarnsOnceWhenTheModelIsMissing()
    {
        describer.Status = new EngineStatus(EngineState.ModelMissing);
        await ui.InvokeAsync(() => controller.FocusChanged(focus.Target));
        await Eventually.True(() => recorder.Spoken.Count == 1, "the warning");
        await ui.InvokeAsync(() => controller.FocusChanged(focus.Target with { Bounds = new PixelRect(400, 600, 160, 160) }));
        await Eventually.True(() => describer.Reads == 2, "the second attempt");
        await Task.Delay(100);
        Assert.Equal($"say:StickerSense: {describer.Status.Explanation}", recorder.Spoken.Single());
        Assert.DoesNotContain("earcon:Failed", recorder.Events);
    }
}
