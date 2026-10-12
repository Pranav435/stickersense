namespace StickerSense.Core;

/// <summary>One key press as the platform saw it, already translated with the user's keyboard layout.</summary>
/// <param name="Character">What the key types (e.g. ":"), or null for keys that don't type anything.</param>
/// <param name="ShortcutModifierHeld">Control, Alt or the Windows key is down: a shortcut, not typing.</param>
/// <param name="IsModifierKey">The key itself is Shift, Control, Alt, Windows or Caps Lock (Shift is needed to type ":").</param>
public readonly record struct TypedKey(string? Character, bool ShortcutModifierHeld = false, bool IsModifierKey = false);

/// <summary>
/// Spots two colons ("::") typed within a second with no other key between, the way Slack and
/// Discord open their emoji pickers. Keeps nothing but the time of the last colon.
/// </summary>
public sealed class ColonDetector(TimeProvider? clock = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private DateTimeOffset? lastColon;

    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>True when this key completes "::".</summary>
    public bool Observe(TypedKey key)
    {
        if (key.IsModifierKey) return false; // Shift on the way to ":"
        if (key.ShortcutModifierHeld || key.Character != ":")
        {
            lastColon = null;
            return false;
        }
        var now = clock.GetUtcNow();
        if (lastColon is { } previous && now - previous < Window)
        {
            lastColon = null; // a third colon starts over
            return true;
        }
        lastColon = now;
        return false;
    }

    public void Reset() => lastColon = null;
}

/// <summary>What the colon trigger needs from the platform.</summary>
public interface IColonTriggerHost
{
    bool Enabled { get; }

    /// <summary>Same apps as auto mode.</summary>
    AutoScope Scope { get; }

    /// <summary>The foreground app (a cheap local call): its process name, whether it's StickerSense, and a handle to return to.</summary>
    (string AppName, bool IsOwnApp, object Window) Foreground();

    /// <summary>One UI Automation call: is the focused element a text input? Made only after "::".</summary>
    Task<bool> FocusIsTextInputAsync();

    /// <summary>Opens the picker for that window; the picked sticker replaces <paramref name="charactersToReplace"/> typed characters.</summary>
    void OpenPicker(object window, int charactersToReplace);
}

/// <summary>Opens the sticker picker when "::" is typed into a chat's text field.</summary>
public sealed class ColonTrigger(IColonTriggerHost host, ColonDetector? detector = null)
{
    private readonly ColonDetector detector = detector ?? new ColonDetector();

    /// <summary>Feed every key press. Returns true when the picker was opened.</summary>
    public async Task<bool> KeyPressedAsync(TypedKey key)
    {
        if (!host.Enabled)
        {
            detector.Reset();
            return false;
        }
        if (!detector.Observe(key)) return false;
        var (appName, isOwnApp, window) = host.Foreground();
        if (isOwnApp || !StickerDetector.IsWatched(appName, host.Scope)) return false;
        if (!await host.FocusIsTextInputAsync()) return false;
        host.OpenPicker(window, charactersToReplace: 2);
        return true;
    }
}
