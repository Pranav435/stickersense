namespace StickerSense.App.Platform;

/// <summary>Cheap, local checks of whether the user is at the computer. Nothing here talks to other apps.</summary>
internal static class SystemIdle
{
    /// <summary>Time since the last keyboard or mouse input anywhere on the system (GetLastInputInfo).</summary>
    public static TimeSpan SinceLastInput()
    {
        var info = new NativeMethods.LASTINPUTINFO { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.LASTINPUTINFO>() };
        if (!NativeMethods.GetLastInputInfo(ref info)) return TimeSpan.Zero; // unknown: assume busy
        var elapsed = unchecked((uint)Environment.TickCount - info.Time); // wraps every 49.7 days
        return TimeSpan.FromMilliseconds(elapsed);
    }

    /// <summary>Battery saver is on (GetSystemPowerStatus).</summary>
    public static bool BatterySaverOn() =>
        NativeMethods.GetSystemPowerStatus(out var status) && status.SystemStatusFlag == 1;
}
