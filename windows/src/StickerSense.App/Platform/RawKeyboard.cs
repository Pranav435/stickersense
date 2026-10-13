using System.Runtime.InteropServices;
using System.Windows.Interop;
using StickerSense.Core;

namespace StickerSense.App.Platform;

/// <summary>
/// Hears key presses system-wide through Raw Input (a message-only window with RIDEV_INPUTSINK),
/// not a low-level keyboard hook: Raw Input is delivered to us alongside normal input, so it
/// never sits in the path of anyone's keystrokes — including the screen reader's — and can't
/// slow them down. Each key down is translated with the foreground app's keyboard layout and
/// passed on as a <see cref="TypedKey"/>; nothing is stored.
/// </summary>
internal sealed class RawKeyboard : IDisposable
{
    private readonly HwndSource window;
    private readonly byte[] keyState = new byte[256];
    private readonly char[] buffer = new char[8];
    private bool listening;

    public RawKeyboard()
    {
        window = new HwndSource(new HwndSourceParameters("StickerSense keyboard")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            WindowStyle = 0,
        });
        window.AddHook(WndProc);
    }

    /// <summary>Raised on the UI thread for every key press (not releases).</summary>
    public event Action<TypedKey>? KeyPressed;

    public bool Start()
    {
        if (listening) return true;
        var device = new NativeMethods.RAWINPUTDEVICE
        {
            UsagePage = 0x01, // generic desktop
            Usage = 0x06,     // keyboard
            Flags = NativeMethods.RIDEV_INPUTSINK,
            Target = window.Handle,
        };
        listening = NativeMethods.RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICE>());
        return listening;
    }

    public void Stop()
    {
        if (!listening) return;
        var device = new NativeMethods.RAWINPUTDEVICE { UsagePage = 0x01, Usage = 0x06, Flags = NativeMethods.RIDEV_REMOVE, Target = IntPtr.Zero };
        NativeMethods.RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICE>());
        listening = false;
    }

    private unsafe IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_INPUT || !listening) return IntPtr.Zero;
        var headerSize = (uint)sizeof(NativeMethods.RAWINPUTHEADER);
        uint size = 0;
        NativeMethods.GetRawInputData(lParam, NativeMethods.RID_INPUT, null, ref size, headerSize); // ask for the size first
        if (size < headerSize + sizeof(NativeMethods.RAWKEYBOARD) || size > 256) return IntPtr.Zero;
        var data = stackalloc byte[(int)size];
        var read = NativeMethods.GetRawInputData(lParam, NativeMethods.RID_INPUT, data, ref size, headerSize);
        if (read != size) return IntPtr.Zero;
        var header = (NativeMethods.RAWINPUTHEADER*)data;
        if (header->Type != NativeMethods.RIM_TYPEKEYBOARD) return IntPtr.Zero;
        var keyboard = (NativeMethods.RAWKEYBOARD*)(data + headerSize);
        if ((keyboard->Flags & NativeMethods.RI_KEY_BREAK) != 0 || keyboard->VKey is 0 or 0xFF) return IntPtr.Zero; // releases and fake keys
        KeyPressed?.Invoke(Translate(keyboard->VKey, keyboard->MakeCode));
        return IntPtr.Zero; // DefWindowProc still runs, as Raw Input requires
    }

    private static bool Down(int key) => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0;

    private unsafe TypedKey Translate(ushort virtualKey, ushort scanCode)
    {
        if (virtualKey is NativeMethods.VK_SHIFT or NativeMethods.VK_LSHIFT or NativeMethods.VK_RSHIFT
            or NativeMethods.VK_CONTROL or NativeMethods.VK_LCONTROL or NativeMethods.VK_RCONTROL
            or NativeMethods.VK_MENU or NativeMethods.VK_LMENU or NativeMethods.VK_RMENU
            or NativeMethods.VK_LWIN or NativeMethods.VK_RWIN or NativeMethods.VK_CAPITAL)
        {
            return new TypedKey(null, IsModifierKey: true);
        }
        if (Down(NativeMethods.VK_CONTROL) || Down(NativeMethods.VK_MENU) || Down(NativeMethods.VK_LWIN) || Down(NativeMethods.VK_RWIN))
        {
            return new TypedKey(null, ShortcutModifierHeld: true);
        }

        // Our thread's keyboard state isn't the foreground app's: set Shift and Caps Lock from the system.
        Array.Clear(keyState);
        if (Down(NativeMethods.VK_SHIFT)) keyState[NativeMethods.VK_SHIFT] = 0x80;
        if (Down(NativeMethods.VK_LSHIFT)) keyState[NativeMethods.VK_LSHIFT] = 0x80;
        if (Down(NativeMethods.VK_RSHIFT)) keyState[NativeMethods.VK_RSHIFT] = 0x80;
        if ((NativeMethods.GetKeyState(NativeMethods.VK_CAPITAL) & 1) != 0) keyState[NativeMethods.VK_CAPITAL] = 0x01;

        // The foreground app's layout (people switch layouts per app); flag 0x4 leaves dead keys alone.
        var thread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
        var layout = NativeMethods.GetKeyboardLayout(thread);
        int count;
        fixed (byte* state = keyState)
        fixed (char* chars = buffer)
        {
            count = NativeMethods.ToUnicodeEx(virtualKey, scanCode, state, chars, buffer.Length, NativeMethods.TOUNICODE_KEEP_STATE, layout);
        }
        return new TypedKey(count > 0 ? new string(buffer, 0, Math.Min(count, buffer.Length)) : null);
    }

    public void Dispose()
    {
        Stop();
        window.RemoveHook(WndProc);
        window.Dispose();
    }
}
