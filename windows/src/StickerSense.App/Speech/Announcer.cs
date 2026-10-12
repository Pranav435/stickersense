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

    public Announcer(Dispatcher dispatcher, string appDirectory)
    {
        this.dispatcher = dispatcher;
        Nvda = NvdaController.Load(appDirectory);
    }

    public NvdaController Nvda { get; }

    public SpeechOutput Output { get; set; } = SpeechOutput.ScreenReader;

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
        if (Output == SpeechOutput.WindowsVoice)
        {
            SpeakWithWindowsVoice(text, interrupt);
            return;
        }
        switch (ScreenReader())
        {
            case ScreenReaderKind.Nvda:
                if (!Nvda.Speak(text, interrupt)) Notify(text, interrupt);
                break;
            case ScreenReaderKind.Jaws:
                if (!jaws.Say(text, interrupt)) Notify(text, interrupt);
                break;
            case ScreenReaderKind.Narrator:
            case ScreenReaderKind.Other:
                Notify(text, interrupt);
                break;
            default:
                SpeakWithWindowsVoice(text, interrupt);
                break;
        }
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
