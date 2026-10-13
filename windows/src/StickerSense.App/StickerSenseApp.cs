using System.Windows;
using System.Windows.Forms.Integration;
using System.Windows.Threading;

namespace StickerSense.App;

/// <summary>The WPF application: no main window, just the tray icon and windows opened on demand.</summary>
internal sealed class StickerSenseApp : Application
{
    private readonly bool launchedAtSignIn;
    private readonly IReadOnlyList<string> alsoWatched;
    private AppController? controller;

    public StickerSenseApp(bool launchedAtSignIn, IReadOnlyList<string> alsoWatched)
    {
        this.launchedAtSignIn = launchedAtSignIn;
        this.alsoWatched = alsoWatched;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // The tray menu is WinForms; this routes keyboard messages to it inside WPF's message loop.
        WindowsFormsHost.EnableWindowsFormsInterop();
        System.Windows.Forms.Application.EnableVisualStyles();
        DispatcherUnhandledException += OnUnhandledException;
        try
        {
            controller = new AppController(alsoWatched);
            await controller.StartAsync(launchedAtSignIn);
        }
        catch (Exception error)
        {
            Log($"Startup failed: {error}");
            MessageBox.Show($"StickerSense couldn't start: {error.Message}", "StickerSense", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>Starting StickerSense again (Start menu, desktop) brings up its main window, like reopening the Mac app.</summary>
    public void ShowMainWindow() => controller?.ShowMain();

    /// <summary><c>StickerSense.exe --quit</c> from another process: the same as Quit in the tray menu.</summary>
    public void Quit()
    {
        if (controller is not null) controller.Quit();
        else Shutdown();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep running: a tray helper that silently disappears is worse than one failed action.
        Log($"Unhandled: {e.Exception}");
        e.Handled = true;
    }

    private static void Log(string message)
    {
        try
        {
            var directory = Core.StickerLibrary.DefaultDirectory();
            System.IO.Directory.CreateDirectory(directory);
            System.IO.File.AppendAllText(System.IO.Path.Combine(directory, "StickerSense.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (Exception logError) when (logError is System.IO.IOException or UnauthorizedAccessException)
        {
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        controller?.Dispose();
        base.OnExit(e);
    }
}
