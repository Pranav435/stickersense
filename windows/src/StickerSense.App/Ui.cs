using System.Windows;
using System.Windows.Threading;

namespace StickerSense.App;

/// <summary>Getting back onto the WPF UI thread from background work.</summary>
internal static class Ui
{
    public static Dispatcher Dispatcher => Application.Current.Dispatcher;

    public static Task RunAsync(Action action) =>
        Dispatcher.CheckAccess() ? RunInline(action) : Dispatcher.InvokeAsync(action).Task;

    private static Task RunInline(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}
