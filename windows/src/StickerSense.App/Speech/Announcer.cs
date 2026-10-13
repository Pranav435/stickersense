using System.Speech.Synthesis;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using StickerSense.App.Platform;
using StickerSense.Core;

namespace StickerSense.App.Speech;

/// <summary>Where descriptions are spoken.</summary>
internal enum SpeechOutput
{
    /// <summary>The running screen reader (NVDA, JAWS, Narrator…), falling back to the Windows voice.</summary>
    ScreenReader,
    /// <summary>Always the Windows (SAPI) voice.</summary>
    WindowsVoice,
}

/// <summary>
/// Gets text to the user's ears and braille display: NVDA through its controller client, JAWS
/// through its COM API, other screen readers (Narrator) through a UI Automation notification,
/// and the Windows voice when no screen reader is running. Safe to call from any thread.
/// </summary>
/// <remarks>
/// NVDA ignores UI Automation notifications from any app except the one with focus, so while
/// someone is in a chat app it would never read StickerSense's notifications. Without its
/// controller client, StickerSense therefore uses the Windows voice for NVDA users, and
/// notifications only while one of its own windows is in front.
/// </remarks>
/// <remarks>
/// Fire and forget: the screen reader is never polled to check that it spoke — every such query
/// competes with the screen reader's own work. Which screen reader runs is read from the process
/// list, at most every few seconds.
/// </remarks>
internal sealed class Announcer : IAnnouncer, IDisposable
{
    private readonly Dispatcher dispatcher;
    private readonly JawsApi jaws = new();
    private NotificationWindow? notifier;
    private SpeechSynthesizer? synthesizer;
    private (ScreenReaderKind Kind, DateTime At) detected = (ScreenReaderKind.None, DateTime.MinValue);
    private string? lastRoute;

    public Announcer(Dispatcher dispatcher, string appDirectory)
    {
        this.dispatcher = dispatcher;
        Nvda = NvdaController.Load(appDirectory);
    }

    public NvdaController Nvda { get; }

    public SpeechOutput Output { get; set; } = SpeechOutput.ScreenReader;

    /// <summary>Notes in the activity log whenever the way speech goes out changes.</summary>
    public IActivityLog? Log { get; set; }

    /// <summary>Which screen reader is running (checked at most every few seconds).</summary>
    public ScreenReaderKind ScreenReader(bool refresh = false)
    {
        if (refresh || DateTime.UtcNow - detected.At > TimeSpan.FromSeconds(10))
        {
            detected = (ScreenReaders.Detect(), DateTime.UtcNow);
        }
        return detected.Kind;
    }

    public void Say(string text, bool interrupt = true)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => Say(text, interrupt));
            return;
        }
        if (SessionLock.IsLocked())
        {
            Route(null);
            return;
        }
        if (Output == SpeechOutput.WindowsVoice)
        {
            Route("the Windows voice (chosen in Settings)");
            SpeakWithWindowsVoice(text, interrupt);
            return;
        }
        switch (ScreenReader())
        {
            case ScreenReaderKind.Nvda:
                if (Nvda.Speak(text, interrupt))
                {
                    Route("NVDA's controller client");
                }
                else if (OwnsForegroundWindow())
                {
                    Route("a UI Automation notification (NVDA, StickerSense window in front)");
                    Notify(text, interrupt);
                }
                else
                {
                    Route(Nvda.IsLoaded
                        ? "the Windows voice (NVDA's controller client didn't answer)"
                        : "the Windows voice (NVDA only reads notifications from the app in front; add nvdaControllerClient.dll to speak through NVDA)");
                    SpeakWithWindowsVoice(text, interrupt);
                }
                break;
            case ScreenReaderKind.Jaws:
                if (jaws.Say(text, interrupt))
                {
                    Route("JAWS");
                }
                else
                {
                    Route("a UI Automation notification (JAWS didn't answer)");
                    Notify(text, interrupt);
                }
                break;
            case ScreenReaderKind.Narrator:
            case ScreenReaderKind.Other:
                Route("a UI Automation notification");
                Notify(text, interrupt);
                break;
            default:
                Route("the Windows voice (no screen reader running)");
                SpeakWithWindowsVoice(text, interrupt);
                break;
        }
    }

    /// <summary>Logs how speech goes out when that changes; null: not speaking because Windows is locked.</summary>
    private void Route(string? route)
    {
        var message = route is null ? "Not speaking while Windows is locked." : $"Speaking through {route}.";
        if (message == lastRoute) return;
        lastRoute = message;
        Log?.Add(message);
    }

    private static bool OwnsForegroundWindow()
    {
        var window = NativeMethods.GetForegroundWindow();
        if (window == IntPtr.Zero) return false;
        NativeMethods.GetWindowThreadProcessId(window, out var process);
        return process == (uint)Environment.ProcessId;
    }

    /// <summary>A UI Automation notification event; Narrator (and NVDA without its DLL) speak and braille it.</summary>
    private void Notify(string text, bool interrupt)
    {
        notifier ??= NotificationWindow.Create();
        notifier.Notify(text, interrupt);
    }

    private void SpeakWithWindowsVoice(string text, bool interrupt)
    {
        try
        {
            if (synthesizer is null)
            {
                synthesizer = new SpeechSynthesizer();
                synthesizer.SetOutputToDefaultAudioDevice();
            }
            if (interrupt) synthesizer.SpeakAsyncCancelAll();
            synthesizer.SpeakAsync(text);
        }
        catch (Exception e) when (e is InvalidOperationException or PlatformNotSupportedException or System.Runtime.InteropServices.COMException)
        {
            synthesizer = null;
        }
    }

    public void Dispose()
    {
        synthesizer?.Dispose();
        notifier?.Close();
    }
}

/// <summary>
/// A tiny, invisible, never-activated window that owns the element UI Automation notifications
/// are raised from.
/// </summary>
internal sealed class NotificationWindow : Window
{
    private readonly TextBlock label = new() { Text = "StickerSense" };

    private NotificationWindow()
    {
        Title = "StickerSense announcements";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Opacity = 0.01;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        Width = 1;
        Height = 1;
        Left = 0;
        Top = 0;
        Content = label;
        AutomationProperties.SetName(label, "StickerSense");
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE).ToInt64();
            style |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TRANSPARENT;
            NativeMethods.SetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE, new IntPtr(style));
        };
    }

    public static NotificationWindow Create()
    {
        var window = new NotificationWindow();
        window.Show();
        return window;
    }

    public void Notify(string text, bool interrupt)
    {
        var peer = UIElementAutomationPeer.FromElement(label) ?? UIElementAutomationPeer.CreatePeerForElement(label);
        peer?.RaiseNotificationEvent(
            AutomationNotificationKind.Other,
            interrupt ? AutomationNotificationProcessing.ImportantMostRecent : AutomationNotificationProcessing.ImportantAll,
            text,
            "StickerSense");
    }
}
