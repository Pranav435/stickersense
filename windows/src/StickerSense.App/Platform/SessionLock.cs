using System.Runtime.InteropServices;

namespace StickerSense.App.Platform;

/// <summary>
/// Whether Windows is locked. NVDA keeps running on the lock screen, and NV Access asks apps not
/// to send it text then (anyone at the computer would hear it), so StickerSense stays quiet.
/// </summary>
internal static partial class SessionLock
{
    private const uint DesktopReadObjects = 0x0001;
    private const int UoiName = 2;

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint desiredAccess);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseDesktop(IntPtr desktop);

    [LibraryImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetUserObjectInformation(IntPtr handle, int index, char* info, uint length, out uint needed);

    /// <summary>
    /// True while the lock screen (LockApp) is in front or a secure desktop (sign-in, Ctrl+Alt+Del,
    /// elevation prompt) has the keyboard.
    /// </summary>
    public static unsafe bool IsLocked()
    {
        var desktop = OpenInputDesktop(0, false, DesktopReadObjects);
        // Only Winlogon's secure desktop refuses this.
        if (desktop == IntPtr.Zero) return true;
        try
        {
            var name = stackalloc char[64];
            if (GetUserObjectInformation(desktop, UoiName, name, 64 * sizeof(char), out _) &&
                !new string(name).Equals("Default", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        finally
        {
            CloseDesktop(desktop);
        }
        var foreground = NativeMethods.GetForegroundWindow();
        return foreground != IntPtr.Zero && ForegroundApp.ProcessName(foreground) == "lockapp";
    }
}
