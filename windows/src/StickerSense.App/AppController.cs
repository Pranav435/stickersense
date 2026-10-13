using System.Windows;
using System.Windows.Threading;
using StickerSense.App.Platform;
using StickerSense.App.Views;
using StickerSense.Core;
using Forms = System.Windows.Forms;

namespace StickerSense.App;

/// <summary>
/// Wires everything together, like the Mac app's AppDelegate: hotkeys, the tray menu, windows,
/// following focus for auto mode, and the describe controller.
/// </summary>
internal sealed class AppController : IDisposable, IColonTriggerHost
{
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly AppServices app;
    private readonly HotkeyManager hotkeys = new();
    private readonly WindowManager windows = new();
    private readonly ForegroundTracker foreground = new();
    private readonly RawKeyboard keyboard = new();
    private readonly ColonTrigger colonTrigger;
    private UiaFocusSource? focus;
    private DescribeController? controller;
    private TrayIcon? tray;
    private bool listening;

    public AppController(IReadOnlyList<string> alsoWatched)
    {
        app = new AppServices(dispatcher);
        app.Settings.AlsoWatchedApps = alsoWatched;
        if (alsoWatched.Count > 0) app.Log.Add($"Test mode: also watching {string.Join(", ", alsoWatched)}");
        colonTrigger = new ColonTrigger(this);
        keyboard.KeyPressed += key => _ = colonTrigger.KeyPressedAsync(key);
    }

    public async Task StartAsync(bool launchedAtSignIn)
    {
        app.Settings.Changed += hotkeysChanged =>
        {
            app.ApplySettings();
            if (hotkeysChanged) RegisterHotkeys();
            _ = UpdateAutoListeningAsync();
            UpdateColonTrigger();
        };
        UpdateColonTrigger();

        IFocusSource focusSource;
        try
        {
            focus = await UiaFocusSource.CreateAsync();
            focusSource = focus;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidCastException or UnauthorizedAccessException)
        {
            // Without UI Automation, the shortcut still works on the item under the mouse.
            app.Log.Add($"UI Automation unavailable: {e}");
            app.Announcer.Say("StickerSense can't follow focus on this computer, so it describes what's under the mouse pointer instead.", interrupt: false);
            focusSource = new MouseFocusSource();
        }
        controller = new DescribeController(app.Pipeline, focusSource, new ScreenCapturer(), app.Announcer, app.Earcons, app.Settings, app.Log,
            Displays.LargestScreenArea);
        if (focus is not null) focus.FocusChanged += target => dispatcher.BeginInvoke(() => controller.FocusChanged(target));
        foreground.AppSwitched += () => controller.AppSwitched();
        await UpdateAutoListeningAsync();

        tray = new TrayIcon(PopulateMenu, ShowMain);
        StartupRegistration.Refresh();
        RegisterHotkeys();
        app.Log.Add($"Launched. Engine: {app.EngineStatus.Explanation} Screen reader: {Speech.ScreenReaders.DisplayName(app.Announcer.ScreenReader())}. " +
                    $"NVDA client: {app.Announcer.Nvda.LoadedFrom ?? "not found"}. OCR: {(app.Ocr.IsAvailable ? app.Ocr.LanguageName : "none")}.");

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            await app.PrewarmAsync();
        });
        app.StartIndexing();

        // Started by hand (not at sign-in): show a window, like any app. Setup first while it's needed.
        if (!app.Settings.HasCompletedSetup) ShowSetup();
        else if (!launchedAtSignIn) ShowMain();
    }

    // MARK: Hotkeys

    private void RegisterHotkeys()
    {
        hotkeys.UnregisterAll();
        var failed = new List<string>();
        var registered = new List<string>();
        void Register(KeyCombo combo, Action action)
        {
            if (hotkeys.Register(combo, action)) registered.Add(combo.SpokenName);
            else failed.Add(combo.SpokenName);
        }
        Register(app.Settings.DescribeCombo, DescribePressed);
        Register(app.Settings.PickerCombo, ShowPicker);
        Register(app.Settings.HistoryCombo, ShowHistory);
        Register(app.Settings.AutoToggleCombo, ToggleAuto);
        if (registered.Count > 0) app.Log.Add($"Shortcuts registered: {string.Join(", ", registered)}.");
        if (failed.Count == 0) return;
        var message = $"StickerSense couldn't use the shortcut {string.Join(" and ", failed)} because it's already in use. Choose a different key in StickerSense Settings.";
        app.Log.Add(message);
        app.Announcer.Say(message, interrupt: false);
    }

    private void DescribePressed()
    {
        if (controller is not null) _ = controller.ShortcutPressedAsync();
    }

    private void ToggleAuto()
    {
        app.Settings.AutoDescribe = !app.Settings.AutoDescribe;
        app.Earcons.Play(Earcon.Tick);
        app.Announcer.Say(app.Settings.AutoDescribe ? "Automatic sticker descriptions on." : "Automatic sticker descriptions off.");
    }

    /// <summary>Focus events are only subscribed to while auto mode is on.</summary>
    private async Task UpdateAutoListeningAsync()
    {
        if (focus is null) return;
        var wanted = app.Settings.AutoDescribe;
        if (wanted == listening) return;
        listening = wanted;
        controller?.ResetAuto();
        if (wanted) await focus.StartListeningAsync();
        else await focus.StopListeningAsync();
    }

    // MARK: Typing "::"

    private void UpdateColonTrigger()
    {
        if (!app.Settings.ColonTrigger)
        {
            keyboard.Stop();
            return;
        }
        if (!keyboard.Start()) app.Log.Add("Couldn't listen for \"::\" (Raw Input registration failed).");
    }

    bool IColonTriggerHost.Enabled => app.Settings.ColonTrigger;

    AutoScope IColonTriggerHost.Scope => app.Settings.AutoScope;

    IReadOnlyCollection<string> IColonTriggerHost.AlsoWatchedApps => app.Settings.AlsoWatchedApps;

    (string AppName, bool IsOwnApp, object Window) IColonTriggerHost.Foreground()
    {
        var window = NativeMethods.GetForegroundWindow();
        return (ForegroundApp.ProcessName(window), ForegroundApp.IsOwnWindow(window), window);
    }

    Task<bool> IColonTriggerHost.FocusIsTextInputAsync() => focus?.FocusIsTextInputAsync() ?? Task.FromResult(true);

    void IColonTriggerHost.OpenPicker(object window, int charactersToReplace)
    {
        app.Log.Add("\"::\" typed: opening the picker");
        ShowPicker((IntPtr)window, charactersToReplace);
    }

    // MARK: Windows

    public void ShowPicker() => ShowPicker(foreground.TargetWindow(), charactersToReplace: 0);

    /// <param name="target">The chat to paste into (remembered before the picker takes focus).</param>
    /// <param name="charactersToReplace">2 when opened by typing "::": the colons are replaced by the sticker.</param>
    public void ShowPicker(IntPtr target, int charactersToReplace)
    {
        var model = new PickerViewModel(app, target) { CharactersToReplace = charactersToReplace };
        windows.Show("picker", () => new PickerWindow(model), replace: true);
    }

    public void ShowHistory()
    {
        windows.Show("history", () => new HistoryWindow(app, app.Settings.DescribeCombo.SpokenName));
    }

    public void ShowSettings() => windows.Show("settings", () => new SettingsWindow(app, ShowSetup));

    /// <summary>The main window: status, a way into everything, and the shortcuts.</summary>
    public void ShowMain() => windows.Show("main", () => new HomeWindow(app, new HomeWindow.Actions(ShowPicker, ShowHistory, ShowSettings, ShowSetup, SpeakHelp)));

    /// <summary>"Hear what StickerSense can do": every shortcut, spoken.</summary>
    public async void SpeakHelp()
    {
        var settings = app.Settings;
        var text = SpokenText.Help(settings.DescribeCombo.SpokenName, settings.PickerCombo.SpokenName, settings.HistoryCombo.SpokenName,
            settings.AutoToggleCombo.SpokenName, settings.ColonTrigger, settings.AutoDescribe);
        await Task.Delay(400); // after the screen reader says what was pressed
        app.Announcer.Say(text);
    }

    public void ShowSetup() => windows.Show("setup", () => new SetupWindow(app, app.Settings.DescribeCombo.SpokenName));

    // MARK: Tray menu

    private void PopulateMenu(Forms.ToolStripItemCollection items)
    {
        var settings = app.Settings;
        items.Add(TrayIcon.Item("StickerSense &Main Window", ShowMain));
        items.Add(new Forms.ToolStripSeparator());
        items.Add(TrayIcon.Item($"&Describe What Has Focus ({settings.DescribeCombo.MenuName})", DescribeFromMenu));
        items.Add(TrayIcon.Item($"Sticker &Picker… ({settings.PickerCombo.MenuName})", ShowPicker));
        items.Add(TrayIcon.Item($"Sticker &History… ({settings.HistoryCombo.MenuName})", ShowHistory));
        items.Add(new Forms.ToolStripSeparator());
        items.Add(TrayIcon.Item($"Describe Stickers &Automatically ({settings.AutoToggleCombo.MenuName})", ToggleAuto, isChecked: settings.AutoDescribe));
        if (!app.EngineStatus.CanDescribe) items.Add(TrayIcon.Item(app.EngineStatus.Explanation, ShowSetup));
        items.Add(new Forms.ToolStripSeparator());
        items.Add(TrayIcon.Item("&What Can StickerSense Do?", SpeakHelp));
        items.Add(TrayIcon.Item("&Settings…", ShowSettings));
        items.Add(TrayIcon.Item("S&etup…", ShowSetup));
        items.Add(new Forms.ToolStripSeparator());
        items.Add(TrayIcon.Item("&Quit StickerSense", Quit));
    }

    /// <summary>From the tray, focus is on the taskbar: go back to the app first, then describe.</summary>
    private async void DescribeFromMenu()
    {
        var target = foreground.LastExternalWindow;
        if (target != IntPtr.Zero) NativeMethods.SetForegroundWindow(target);
        await Task.Delay(600);
        DescribePressed();
    }

    public void Quit()
    {
        windows.CloseAll();
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        tray?.Dispose();
        keyboard.Dispose();
        hotkeys.Dispose();
        foreground.Dispose();
        focus?.Dispose();
        app.Dispose();
    }
}

/// <summary>Fallback when UI Automation isn't available: the sticker-sized square around the mouse pointer.</summary>
internal sealed class MouseFocusSource : IFocusSource
{
    public Task<FocusTarget> CurrentTargetAsync(CancellationToken ct = default)
    {
        var square = Displays.SquareAroundMouse(out var scale);
        var foreground = NativeMethods.GetForegroundWindow();
        return Task.FromResult(new FocusTarget
        {
            Bounds = square,
            AppName = ForegroundApp.ProcessName(foreground),
            IsOwnApp = ForegroundApp.IsOwnWindow(foreground),
            DisplayScale = scale,
        });
    }
}
