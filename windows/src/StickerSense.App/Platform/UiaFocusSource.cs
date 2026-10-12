using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using StickerSense.Core;

namespace StickerSense.App.Platform;

/// <summary>
/// Follows keyboard / screen-reader focus with UI Automation (the COM API). Every UIA call runs
/// on a thread-pool (MTA) thread, never the UI thread, so asking about StickerSense's own windows
/// can't deadlock. Focus changes arrive as events — nothing is polled.
/// </summary>
/// <remarks>
/// Calls into another app are served on that app's UI thread — the thread the screen reader is
/// waiting on. So focus events carry everything auto mode needs (name, control type, bounds…)
/// through a cache request, and the handler makes no further calls. The only extra lookup is
/// <see cref="RefineAsync"/>, for items whose name says nothing, after the user rests on them.
/// </remarks>
internal sealed class UiaFocusSource : IFocusSource, IDisposable
{
    private readonly IUIAutomation automation;
    private readonly IUIAutomationCacheRequest cache;
    private readonly int ownProcessId = Environment.ProcessId;
    private FocusHandler? handler;

    private UiaFocusSource()
    {
        automation = new CUIAutomation8();
        cache = automation.CreateCacheRequest();
        cache.AddProperty(UIA_PropertyIds.UIA_BoundingRectanglePropertyId);
        cache.AddProperty(UIA_PropertyIds.UIA_NamePropertyId);
        cache.AddProperty(UIA_PropertyIds.UIA_ControlTypePropertyId);
        cache.AddProperty(UIA_PropertyIds.UIA_LocalizedControlTypePropertyId);
        cache.AddProperty(UIA_PropertyIds.UIA_ProcessIdPropertyId);
    }

    /// <summary>Raised on a UI Automation thread whenever focus moves to a new element.</summary>
    public event Action<FocusTarget>? FocusChanged;

    /// <summary>Creates the UIA client on an MTA thread.</summary>
    public static Task<UiaFocusSource> CreateAsync() => Task.Run(() => new UiaFocusSource());

    public Task StartListeningAsync() => Task.Run(() =>
    {
        if (handler is not null) return;
        handler = new FocusHandler(this);
        automation.AddFocusChangedEventHandler(cache, handler);
    });

    public Task StopListeningAsync() => Task.Run(() =>
    {
        if (handler is null) return;
        try
        {
            automation.RemoveFocusChangedEventHandler(handler);
        }
        catch (COMException)
        {
        }
        handler = null;
    });

    /// <summary>The focused element; a sticker-sized square around the mouse when nothing usable has focus.</summary>
    public Task<FocusTarget> CurrentTargetAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        try
        {
            var element = automation.GetFocusedElementBuildCache(cache);
            if (element is not null && ToTarget(element) is { } target && IsUsable(target.Bounds)) return target;
        }
        catch (COMException)
        {
            // The focused app didn't answer (or went away); fall back to the mouse.
        }
        catch (InvalidCastException)
        {
        }
        catch (UnauthorizedAccessException)
        {
            // An elevated (administrator) window has focus; UI Automation can't look inside it.
        }
        var square = Displays.SquareAroundMouse(out var scale);
        var foreground = NativeMethods.GetForegroundWindow();
        return new FocusTarget
        {
            Bounds = square,
            AppName = ForegroundApp.ProcessName(foreground),
            IsOwnApp = ForegroundApp.IsOwnWindow(foreground),
            DisplayScale = scale,
        };
    }, ct);

    private static bool IsUsable(PixelRect bounds) =>
        bounds.Width >= 4 && bounds.Height >= 4 && !Displays.VirtualScreen.Intersect(bounds).IsEmpty;

    /// <summary>
    /// Whether keyboard focus is in a text input (an Edit or Document element, such as a chat's
    /// compose box). One call, made only after "::" is typed; unknown counts as yes.
    /// </summary>
    public Task<bool> FocusIsTextInputAsync() => Task.Run(() =>
    {
        try
        {
            var element = automation.GetFocusedElementBuildCache(cache);
            if (element is null) return true;
            var type = element.CachedControlType;
            return type == UIA_ControlTypeIds.UIA_EditControlTypeId || type == UIA_ControlTypeIds.UIA_DocumentControlTypeId;
        }
        catch (Exception e) when (e is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            return true;
        }
    });

    /// <summary>One lookup inside an item whose name says nothing: the first image directly in it, and that image's name.</summary>
    public Task<FocusTarget> RefineAsync(FocusTarget target, CancellationToken ct = default) => Task.Run(() =>
    {
        if (target.Element is not IUIAutomationElement element) return target;
        try
        {
            var isImage = automation.CreatePropertyCondition(UIA_PropertyIds.UIA_ControlTypePropertyId, UIA_ControlTypeIds.UIA_ImageControlTypeId);
            var image = element.FindFirstBuildCache(TreeScope.TreeScope_Children, isImage, cache);
            if (image is null) return target;
            var name = (image.CachedName ?? "").Trim();
            return target with
            {
                IsImage = true,
                Name = target.Name.Length == 0 ? name : name.Length == 0 ? target.Name : $"{target.Name}, {name}",
            };
        }
        catch (Exception e) when (e is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            return target;
        }
    }, ct);

    /// <summary>Builds the target from cached properties only: no calls into the other app.</summary>
    private FocusTarget? ToTarget(IUIAutomationElement element)
    {
        var rect = element.CachedBoundingRectangle;
        var bounds = new PixelRect(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);
        var foreground = NativeMethods.GetForegroundWindow();
        return new FocusTarget
        {
            Bounds = bounds,
            Name = (element.CachedName ?? "").Trim(),
            RoleText = (element.CachedLocalizedControlType ?? "").Trim(),
            IsImage = element.CachedControlType == UIA_ControlTypeIds.UIA_ImageControlTypeId,
            AppName = ForegroundApp.ProcessName(foreground),
            IsOwnApp = element.CachedProcessId == ownProcessId || ForegroundApp.IsOwnWindow(foreground),
            DisplayScale = bounds.IsEmpty ? 1.0 : Displays.ScaleFor(bounds),
            Element = element,
        };
    }

    private void OnFocus(IUIAutomationElement sender)
    {
        try
        {
            if (ToTarget(sender) is { } target && IsUsable(target.Bounds)) FocusChanged?.Invoke(target);
        }
        catch (COMException)
        {
        }
        catch (InvalidCastException)
        {
        }
        catch (UnauthorizedAccessException)
        {
            // An elevated (administrator) window has focus; UI Automation can't look inside it.
        }
    }

    public void Dispose()
    {
        try
        {
            StopListeningAsync().Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
    }

    [ComVisible(true)]
    private sealed class FocusHandler(UiaFocusSource owner) : IUIAutomationFocusChangedEventHandler
    {
        public void HandleFocusChangedEvent(IUIAutomationElement sender) => owner.OnFocus(sender);
    }
}
