using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace StickerSense.App.Platform;

/// <summary>A global shortcut: Control + Alt + Shift + a letter or digit.</summary>
internal readonly record struct KeyCombo(string Key)
{
    public static readonly IReadOnlyList<string> Keys =
        Enumerable.Range('A', 26).Select(c => ((char)c).ToString())
            .Concat(Enumerable.Range('0', 10).Select(c => ((char)c).ToString()))
            .ToList();

    public static KeyCombo For(string key, string fallback) =>
        new(Keys.Contains(key.ToUpperInvariant()) ? key.ToUpperInvariant() : fallback);

    /// <summary>Virtual-key code: letters and digits use their ASCII codes.</summary>
    public uint VirtualKey => Key[0];

    /// <summary>Spoken form, e.g. "Control Alt Shift D".</summary>
    public string SpokenName => $"Control Alt Shift {Key}";

    /// <summary>Compact form for menus, e.g. "Ctrl+Alt+Shift+D".</summary>
    public string MenuName => $"Ctrl+Alt+Shift+{Key}";
}

/// <summary>
/// System-wide hotkeys via RegisterHotKey on a message-only window. The key press is swallowed,
/// so it doesn't type into the chat.
/// </summary>
internal sealed class HotkeyManager : IDisposable
{
    private readonly HwndSource window;
    private readonly Dictionary<int, Action> handlers = [];
    private int nextId = 1;

    public HotkeyManager()
    {
        var parameters = new HwndSourceParameters("StickerSense hotkeys")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            WindowStyle = 0,
        };
        window = new HwndSource(parameters);
        window.AddHook(WndProc);
    }

    /// <summary>Returns false when another app already owns the combination.</summary>
    public bool Register(KeyCombo combo, Action handler)
    {
        var id = nextId++;
        const uint modifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT;
        if (!NativeMethods.RegisterHotKey(window.Handle, id, modifiers, combo.VirtualKey)) return false;
        handlers[id] = handler;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in handlers.Keys) NativeMethods.UnregisterHotKey(window.Handle, id);
        handlers.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && handlers.TryGetValue(wParam.ToInt32(), out var handler))
        {
            handled = true;
            handler();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        window.RemoveHook(WndProc);
        window.Dispose();
    }
}
