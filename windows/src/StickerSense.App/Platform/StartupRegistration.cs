using System.IO;
using Microsoft.Win32;

namespace StickerSense.App.Platform;

/// <summary>"Open StickerSense when I sign in", via the per-user Run key (no admin rights needed).</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "StickerSense";
    /// <summary>Passed when Windows starts StickerSense at sign-in: it then stays quietly in the notification area.</summary>
    public const string BackgroundArgument = "--background";

    /// <summary>True when started at sign-in (also accepts the older "--startup").</summary>
    public static bool IsBackgroundLaunch(IEnumerable<string> args) =>
        args.Any(a => a.Equals(BackgroundArgument, StringComparison.OrdinalIgnoreCase) || a.Equals("--startup", StringComparison.OrdinalIgnoreCase));

    /// <summary>Re-writes an existing registration (the app may have moved, or used the older argument).</summary>
    public static void Refresh()
    {
        try
        {
            if (IsEnabled) IsEnabled = true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
        }
    }

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value && Environment.ProcessPath is { } exe) key.SetValue(ValueName, $"\"{exe}\" {BackgroundArgument}");
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
