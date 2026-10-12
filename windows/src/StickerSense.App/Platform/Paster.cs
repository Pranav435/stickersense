using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using StickerSense.Core;

namespace StickerSense.App.Platform;

/// <summary>
/// Puts a sticker or GIF into the chat the user came from: copies it to the clipboard, switches
/// back to that window, deletes the "::" if that's how the picker was opened, and presses
/// Control+V. Never presses Enter — the user decides whether to send.
/// Call on the UI thread (the clipboard needs STA).
/// </summary>
internal static class Paster
{
    public sealed record Outcome(bool Pasted, string Message);

    private static string TempDirectory => Path.Combine(Path.GetTempPath(), "StickerSense", "paste");

    /// <summary>Copies the file as a file (keeps GIF animation) and, for still images, as a picture too.</summary>
    public static async Task CopyAsync(string file)
    {
        var shareable = await Task.Run(() => ImagePrep.ShareableFile(file, TempDirectory));
        var data = new DataObject();
        data.SetFileDropList(new StringCollection { shareable });
        if (!shareable.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = await File.ReadAllBytesAsync(shareable);
            data.SetData("PNG", new MemoryStream(bytes));
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = new MemoryStream(bytes);
            bitmap.EndInit();
            bitmap.Freeze();
            data.SetImage(bitmap);
        }
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return;
            }
            catch (COMException) when (attempt < 5)
            {
                await Task.Delay(60); // another app has the clipboard open
            }
        }
    }

    /// <param name="charactersToReplace">Characters to delete first: the "::" that opened the picker.</param>
    public static async Task<Outcome> PasteAsync(string file, IntPtr target, int charactersToReplace = 0)
    {
        await CopyAsync(file);
        if (target == IntPtr.Zero || !NativeMethods.IsWindow(target) || ForegroundApp.IsOwnWindow(target))
        {
            return new Outcome(false, "Copied. Paste it into your chat with Control V.");
        }
        var name = ForegroundApp.FriendlyName(target);

        // Don't let a still-held Enter (from choosing the sticker) reach the chat and send it.
        await WaitForKeysReleasedAsync(TimeSpan.FromSeconds(1.5));

        if (NativeMethods.IsIconic(target)) NativeMethods.ShowWindow(target, NativeMethods.SW_RESTORE);
        NativeMethods.SetForegroundWindow(target);
        // Wait for the chat app to come to the front before pressing Control+V.
        for (var i = 0; i < 40 && NativeMethods.GetForegroundWindow() != target; i++) await Task.Delay(25);
        if (NativeMethods.GetForegroundWindow() != target)
        {
            return new Outcome(false, $"Copied. Switch to {name} and press Control V.");
        }
        await Task.Delay(150);
        for (var i = 0; i < charactersToReplace; i++)
        {
            Press(NativeMethods.VK_BACK);
            await Task.Delay(20);
        }
        PressControlV();
        return new Outcome(true, $"Pasted into {name}. Press Enter to send.");
    }

    private static async Task WaitForKeysReleasedAsync(TimeSpan timeout)
    {
        var keys = new[] { NativeMethods.VK_RETURN, NativeMethods.VK_SPACE, NativeMethods.VK_CONTROL, NativeMethods.VK_SHIFT, NativeMethods.VK_MENU };
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && keys.Any(k => (NativeMethods.GetAsyncKeyState(k) & 0x8000) != 0))
        {
            await Task.Delay(20);
        }
    }

    private static void Press(ushort virtualKey)
    {
        var inputs = new[] { Key(virtualKey, up: false), Key(virtualKey, up: true) };
        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static void PressControlV()
    {
        var inputs = new[]
        {
            Key(NativeMethods.VK_CONTROL, up: false),
            Key(NativeMethods.VK_V, up: false),
            Key(NativeMethods.VK_V, up: true),
            Key(NativeMethods.VK_CONTROL, up: true),
        };
        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static NativeMethods.INPUT Key(ushort virtualKey, bool up) => new()
    {
        Type = NativeMethods.INPUT_KEYBOARD,
        Data = new NativeMethods.InputUnion
        {
            Keyboard = new NativeMethods.KEYBDINPUT
            {
                VirtualKey = virtualKey,
                ScanCode = (ushort)NativeMethods.MapVirtualKeyW(virtualKey, 0),
                Flags = up ? NativeMethods.KEYEVENTF_KEYUP : 0,
            },
        },
    };
}
