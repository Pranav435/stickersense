using StickerSense.App.Platform;

namespace StickerSense.App;

internal static class Program
{
    private const string InstanceName = @"Local\StickerSense.SingleInstance";
    private const string ShowEventName = @"Local\StickerSense.ShowMainWindow";

    [STAThread]
    public static int Main(string[] args)
    {
        var background = StartupRegistration.IsBackgroundLaunch(args);
        // One StickerSense per user session; starting it again by hand brings up the running one's main window.
        using var instance = new Mutex(initiallyOwned: true, InstanceName, out var isFirst);
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
        var app = new StickerSenseApp(launchedAtSignIn: background);
        var registration = ThreadPool.RegisterWaitForSingleObject(showRequests,
            (_, _) => app.Dispatcher.BeginInvoke(app.ShowMainWindow), null, Timeout.Infinite, executeOnlyOnce: false);
        try
        {
            return app.Run();
        }
        finally
        {
            registration.Unregister(null);
        }
    }
}
