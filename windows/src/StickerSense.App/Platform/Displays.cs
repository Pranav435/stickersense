using StickerSense.Core;
using Forms = System.Windows.Forms;

namespace StickerSense.App.Platform;

/// <summary>Monitor geometry in physical pixels (the app is per-monitor DPI aware).</summary>
internal static class Displays
{
    /// <summary>Area of the largest monitor, for the "focus is on a large area" check.</summary>
    public static long LargestScreenArea() =>
        Forms.Screen.AllScreens.Select(s => (long)s.Bounds.Width * s.Bounds.Height).DefaultIfEmpty(1920L * 1080).Max();

    public static PixelRect VirtualScreen
    {
        get
        {
            var v = Forms.SystemInformation.VirtualScreen;
            return new PixelRect(v.X, v.Y, v.Width, v.Height);
        }
    }

    /// <summary>Display scale of the monitor showing <paramref name="rect"/> (1.0 = 100 %).</summary>
    public static double ScaleFor(PixelRect rect)
    {
        try
        {
            var r = new NativeMethods.RECT { Left = rect.X, Top = rect.Y, Right = rect.Right, Bottom = rect.Bottom };
            var monitor = NativeMethods.MonitorFromRect(r, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero && NativeMethods.GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0)
            {
                return dpiX / 96.0;
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
        return 1.0;
    }

    /// <summary>
    /// The fallback target: a sticker-sized square around the mouse pointer — 220 × 220 logical
    /// pixels, i.e. scaled with the monitor's display scaling like the Mac's 220 points.
    /// </summary>
    public static PixelRect SquareAroundMouse(out double scale)
    {
        NativeMethods.GetCursorPos(out var point);
        scale = ScaleFor(new PixelRect(point.X, point.Y, 1, 1));
        return PixelRect.Around(point.X, point.Y, (int)Math.Round(220 * scale));
    }
}
