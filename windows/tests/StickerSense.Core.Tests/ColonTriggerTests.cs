namespace StickerSense.Core.Tests;

public class ColonDetectorTests
{
    private static readonly TypedKey Colon = new(":");
    private static readonly TypedKey Shift = new(null, IsModifierKey: true);

    private readonly ManualClock clock = new();
    private ColonDetector Detector() => new(clock);

    [Fact]
    public void TwoColonsWithinASecondFire()
    {
        var detector = Detector();
        Assert.False(detector.Observe(Shift));
        Assert.False(detector.Observe(Colon));
        clock.Advance(TimeSpan.FromMilliseconds(300));
        Assert.False(detector.Observe(Shift)); // Shift between is part of typing ":"
        Assert.True(detector.Observe(Colon));
    }

    [Fact]
    public void AnotherKeyBetweenDoesNot()
    {
        var detector = Detector();
        detector.Observe(Colon);
        Assert.False(detector.Observe(new TypedKey("a")));
        Assert.False(detector.Observe(Colon));
        Assert.False(detector.Observe(new TypedKey(null))); // e.g. an arrow key
        Assert.False(detector.Observe(Colon));
    }

    [Fact]
    public void MoreThanASecondApartDoesNot()
    {
        var detector = Detector();
        detector.Observe(Colon);
        clock.Advance(TimeSpan.FromSeconds(1.2));
        Assert.False(detector.Observe(Colon));
        clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.True(detector.Observe(Colon)); // but that one starts a new pair
    }

    [Fact]
    public void AThirdColonRightAfterDoesNotFireAgain()
    {
        var detector = Detector();
        detector.Observe(Colon);
        Assert.True(detector.Observe(Colon));
        Assert.False(detector.Observe(Colon));
    }

    [Fact]
    public void ShortcutsAreNotTyping()
    {
        var detector = Detector();
        detector.Observe(Colon);
        Assert.False(detector.Observe(new TypedKey(":", ShortcutModifierHeld: true)));
        Assert.False(detector.Observe(Colon)); // the shortcut broke the pair
        Assert.False(detector.Observe(new TypedKey(":", ShortcutModifierHeld: true)));
        Assert.False(detector.Observe(new TypedKey(":", ShortcutModifierHeld: true)));
    }
}

public class ColonTriggerTests
{
    private sealed class Host : IColonTriggerHost
    {
        public bool Enabled { get; set; } = true;
        public AutoScope Scope { get; set; } = AutoScope.ChatApps;
        public string App { get; set; } = "whatsapp";
        public bool OwnApp { get; set; }
        public bool TextInput { get; set; } = true;
        public int FocusChecks;
        public List<(object Window, int Replace)> Opened { get; } = [];

        public (string AppName, bool IsOwnApp, object Window) Foreground() => (App, OwnApp, "chat window");

        public Task<bool> FocusIsTextInputAsync()
        {
            FocusChecks++;
            return Task.FromResult(TextInput);
        }

        public void OpenPicker(object window, int charactersToReplace) => Opened.Add((window, charactersToReplace));
    }

    private static async Task<bool> TypeTwoColons(ColonTrigger trigger) =>
        await trigger.KeyPressedAsync(new TypedKey(":")) | await trigger.KeyPressedAsync(new TypedKey(":"));

    [Fact]
    public async Task OpensThePickerToReplaceTheColons()
    {
        var host = new Host();
        var trigger = new ColonTrigger(host);
        Assert.False(await trigger.KeyPressedAsync(new TypedKey("h")));
        Assert.True(await TypeTwoColons(trigger));
        Assert.Equal([("chat window", 2)], host.Opened.Select(o => (o.Window, o.Replace)));
        Assert.Equal(1, host.FocusChecks);
    }

    [Fact]
    public async Task OrdinaryTypingNeverQueriesTheApp()
    {
        var host = new Host();
        var trigger = new ColonTrigger(host);
        foreach (var c in "see you at 10:30 :) ok") await trigger.KeyPressedAsync(new TypedKey(c.ToString()));
        Assert.Equal(0, host.FocusChecks);
        Assert.Empty(host.Opened);
    }

    [Fact]
    public async Task OnlyInWatchedAppsTextFieldsAndWhenOn()
    {
        var host = new Host { App = "notepad" };
        var trigger = new ColonTrigger(host);
        Assert.False(await TypeTwoColons(trigger));
        host.Scope = AutoScope.Everywhere;
        Assert.True(await TypeTwoColons(trigger));

        host.OwnApp = true;
        Assert.False(await TypeTwoColons(trigger)); // StickerSense itself

        host.OwnApp = false;
        host.TextInput = false;
        Assert.False(await TypeTwoColons(trigger)); // e.g. a chat list, not the compose box

        host.TextInput = true;
        host.Enabled = false;
        Assert.False(await TypeTwoColons(trigger));
        Assert.Single(host.Opened);
    }
}
