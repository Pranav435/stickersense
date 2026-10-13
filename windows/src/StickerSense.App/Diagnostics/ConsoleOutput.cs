using System.IO;
using System.Runtime.InteropServices;
using StickerSense.Core;

namespace StickerSense.App.Diagnostics;

/// <summary>
/// StickerSense is a windowed app, so it has no console of its own. For the command-line modes,
/// write to the console it was started from (or to wherever its output is redirected).
/// </summary>
internal static class ConsoleOutput
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int handle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    public static TextWriter Open()
    {
        const int stdOutput = -11, attachParent = -1;
        var handle = GetStdHandle(stdOutput);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            AttachConsole(attachParent);
        }
        var writer = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        return writer;
    }
}

/// <summary>Collects log lines (for the self-test report) and passes them on.</summary>
internal sealed class ListLog(Action<string>? echo = null) : IActivityLog
{
    private readonly List<string> lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (lines) return lines.ToList();
        }
    }

    public void Add(string message)
    {
        lock (lines) lines.Add(message);
        echo?.Invoke(message);
    }
}
