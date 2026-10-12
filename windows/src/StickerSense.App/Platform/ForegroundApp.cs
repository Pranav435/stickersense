using System.Collections.Concurrent;
using System.Diagnostics;
using StickerSense.Core;

namespace StickerSense.App.Platform;

/// <summary>Which app a window belongs to (process name and a friendly name to speak).</summary>
internal static class ForegroundApp
{
    private static readonly ConcurrentDictionary<uint, (string Process, string Friendly)> Names = new();
    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    /// <summary>Windows the shell owns: never treat these as "the chat the user came from".</summary>
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
        "Progman", "WorkerW", "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "#32768",
    };

    /// <summary>Process name without ".exe", lowercase (e.g. "whatsapp", "chrome"); "" if unknown.</summary>
    public static string ProcessName(IntPtr hwnd) => Lookup(ResolveProcessId(hwnd)).Process;

    /// <summary>What to call the app out loud, e.g. "WhatsApp" or "Google Chrome".</summary>
    public static string FriendlyName(IntPtr hwnd) => Lookup(ResolveProcessId(hwnd)).Friendly;

    public static bool IsOwnWindow(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return pid == OwnProcessId;
    }

    public static bool IsShellWindow(IntPtr hwnd) => ShellClasses.Contains(ClassName(hwnd));

    public static string ClassName(IntPtr hwnd)
    {
        var buffer = new char[256];
        var length = NativeMethods.GetClassName(hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : "";
    }

    /// <summary>UWP apps live under ApplicationFrameHost; the real app owns a child window.</summary>
    private static uint ResolveProcessId(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return 0;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (!Lookup(pid).Process.Equals("applicationframehost", StringComparison.OrdinalIgnoreCase)) return pid;
        var hostPid = pid;
        uint child = 0;
        NativeMethods.EnumChildWindows(hwnd, (h, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(h, out var childPid);
            if (childPid == hostPid) return true;
            child = childPid;
            return false;
        }, IntPtr.Zero);
        return child != 0 ? child : pid;
    }

    private static (string Process, string Friendly) Lookup(uint pid)
    {
        if (pid == 0) return ("", "the app");
        if (Names.TryGetValue(pid, out var cached)) return cached;
        var result = ("", "the app");
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var name = process.ProcessName.ToLowerInvariant();
            var friendly = AppNames.FriendlyName(name);
            if (friendly == name)
            {
                try
                {
                    var description = process.MainModule?.FileVersionInfo.FileDescription;
                    friendly = string.IsNullOrWhiteSpace(description) ? process.ProcessName : description.Trim();
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    friendly = process.ProcessName; // elevated or protected process
                }
            }
            result = (name, friendly);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return result; // exited
        }
        Names[pid] = result;
        return result;
    }
}

/// <summary>
/// Remembers the last app window the user was in that isn't StickerSense or the taskbar, so the
/// picker can paste into it even when opened from the tray menu.
/// </summary>
internal sealed class ForegroundTracker : IDisposable
{
    private readonly NativeMethods.WinEventProc callback;
    private readonly IntPtr hook;

    public ForegroundTracker()
    {
        callback = OnForeground; // keep the delegate alive
        hook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, callback, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
        Consider(NativeMethods.GetForegroundWindow());
    }

    public IntPtr LastExternalWindow { get; private set; }

    /// <summary>
    /// Raised (on the UI thread) when another app comes to the front. The screen reader is busiest
    /// right then, so auto mode keeps quiet for a moment.
    /// </summary>
    public event Action? AppSwitched;

    private uint lastProcessId;

    /// <summary>The window to return to: the current foreground window if it's a real app, else the last one.</summary>
    public IntPtr TargetWindow()
    {
        Consider(NativeMethods.GetForegroundWindow());
        return LastExternalWindow != IntPtr.Zero && NativeMethods.IsWindow(LastExternalWindow) ? LastExternalWindow : IntPtr.Zero;
    }

    private void OnForeground(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        Consider(hwnd);
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == lastProcessId) return; // a dialog or menu of the same app isn't an app switch
        lastProcessId = pid;
        AppSwitched?.Invoke();
    }

    private void Consider(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || ForegroundApp.IsOwnWindow(hwnd) || ForegroundApp.IsShellWindow(hwnd)) return;
        LastExternalWindow = hwnd;
    }

    public void Dispose()
    {
        if (hook != IntPtr.Zero) NativeMethods.UnhookWinEvent(hook);
    }
}

/// <summary>Bringing a StickerSense window to the front when Windows' foreground lock would refuse.</summary>
internal static class ForegroundWindow
{
    /// <summary>
    /// After "::" (heard through Raw Input, unlike a hotkey) Windows doesn't let a background app
    /// take focus, so the picker would only flash in the taskbar. Briefly sharing input state with
    /// the foreground app's thread lets the activation through. Only used when normal activation failed.
    /// </summary>
    public static void Force(IntPtr window)
    {
        if (window == IntPtr.Zero || NativeMethods.GetForegroundWindow() == window) return;
        var foregroundThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
        var ourThread = NativeMethods.GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != ourThread && NativeMethods.AttachThreadInput(ourThread, foregroundThread, true);
        try
        {
            NativeMethods.BringWindowToTop(window);
            NativeMethods.SetForegroundWindow(window);
        }
        finally
        {
            if (attached) NativeMethods.AttachThreadInput(ourThread, foregroundThread, false);
        }
    }
}
