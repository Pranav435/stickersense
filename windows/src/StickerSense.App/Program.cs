using StickerSense.App.Platform;

namespace StickerSense.App;

internal static class Program
{
    private const string InstanceName = @"Local\StickerSense.SingleInstance";
    private const string ShowEventName = @"Local\StickerSense.ShowMainWindow";
    private const string QuitEventName = @"Local\StickerSense.Quit";

    [STAThread]
    public static int Main(string[] args)
    {
        // The model's own process, started by StickerSense (see Engine.EngineHost).
        if (args.Contains("--engine", StringComparer.Ordinal)) return Engine.EngineHost.Run();
        // Command-line tools, for tests and bug reports; they don't touch the running app.
        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase)) return Diagnostics.SelfTest.Run(args);
        if (args.Contains("--download-model", StringComparer.OrdinalIgnoreCase)) return Diagnostics.DownloadModelCommand.Run(args);
        if (args.Contains("--test-window", StringComparer.OrdinalIgnoreCase)) return Diagnostics.TestWindowCommand.Run(args);

        var background = StartupRegistration.IsBackgroundLaunch(args);
        // One StickerSense per user session; starting it again by hand brings up the running one's main window.
        using var instance = new Mutex(initiallyOwned: true, InstanceName, out var isFirst);
        if (args.Contains("--quit", StringComparer.OrdinalIgnoreCase)) return isFirst ? 0 : QuitRunningInstance(instance);
        if (!isFirst)
        {
            if (background) return 0;
            try
            {
                using var show = EventWaitHandle.OpenExisting(ShowEventName);
                show.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }
            return 0;
        }

        using var showRequests = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        using var quitRequests = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        // Test-only: treat these processes as chat apps (e.g. "--watch-app stickersense" for the test window).
        var alsoWatched = args.Select((arg, i) => (arg, i))
            .Where(p => p.arg.Equals("--watch-app", StringComparison.OrdinalIgnoreCase) && p.i + 1 < args.Length)
            .Select(p => args[p.i + 1]).ToList();
        var app = new StickerSenseApp(launchedAtSignIn: background, alsoWatched);
        var registration = ThreadPool.RegisterWaitForSingleObject(showRequests,
            (_, _) => app.Dispatcher.BeginInvoke(app.ShowMainWindow), null, Timeout.Infinite, executeOnlyOnce: false);
        var quitRegistration = ThreadPool.RegisterWaitForSingleObject(quitRequests,
            (_, _) => app.Dispatcher.BeginInvoke(app.Quit), null, Timeout.Infinite, executeOnlyOnce: true);
        try
        {
            return app.Run();
        }
        finally
        {
            registration.Unregister(null);
            quitRegistration.Unregister(null);
        }
    }

    /// <summary>
    /// <c>StickerSense.exe --quit</c>: closes the running StickerSense, as its Quit menu item does
    /// (for installers, updates and scripts). Waits up to 15 seconds for it to finish.
    /// </summary>
    private static int QuitRunningInstance(Mutex instance)
    {
        try
        {
            using var quit = EventWaitHandle.OpenExisting(QuitEventName);
            quit.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return 1; // an older StickerSense that doesn't listen for this
        }
        try
        {
            if (!instance.WaitOne(TimeSpan.FromSeconds(15))) return 1;
        }
        catch (AbandonedMutexException)
        {
            // It exited before releasing the mutex: that's the normal way out.
        }
        instance.ReleaseMutex();
        return 0;
    }
}
