using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using StickerSense.Core;

namespace StickerSense.App.Views;

/// <summary>Keeps one instance of each window and brings it to the front.</summary>
internal sealed class WindowManager
{
    private readonly Dictionary<string, Window> windows = [];

    public T Show<T>(string id, Func<T> create, bool replace = false) where T : Window
    {
        if (windows.TryGetValue(id, out var existing))
        {
            if (!replace)
            {
                Bring(existing);
                return (T)existing;
            }
            existing.Close();
        }
        var window = create();
        // Every StickerSense window is an ordinary app window: on the taskbar and in Alt+Tab.
        window.Icon ??= AppIcon.Image;
        window.ShowInTaskbar = true;
        window.Topmost = false;
        windows[id] = window;
        window.Closed += (_, _) =>
        {
            if (windows.TryGetValue(id, out var current) && ReferenceEquals(current, window)) windows.Remove(id);
        };
        window.Show();
        Bring(window);
        return window;
    }

    public void Close(string id)
    {
        if (windows.TryGetValue(id, out var window)) window.Close();
    }

    public void CloseAll()
    {
        foreach (var window in windows.Values.ToList()) window.Close();
    }

    private static void Bring(Window window)
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero && Platform.NativeMethods.GetForegroundWindow() != handle) Platform.ForegroundWindow.Force(handle);
        window.Focus();
    }
}

/// <summary>Small, decoded-off-the-UI-thread thumbnails (handles WebP, which WPF may not decode).</summary>
internal static class Thumbnails
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static async Task<ImageSource?> FromFileAsync(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        try
        {
            return ToBitmap(await Task.Run(() => Encode(ImagePrep.LoadFrames(path, maxFrames: 1))));
        }
        catch (Exception e) when (e is ImagePrepException or IOException)
        {
            return null;
        }
    }

    public static async Task<ImageSource?> FromUrlAsync(string url)
    {
        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            return ToBitmap(await Task.Run(() => Encode(ImagePrep.LoadFrames(bytes, maxFrames: 1))));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or ImagePrepException)
        {
            return null;
        }
    }

    private static byte[] Encode(List<Image<Rgba32>> frames)
    {
        try
        {
            using var small = ImagePrep.Downscaled(frames[0], 112);
            return ImagePrep.PngData(small);
        }
        finally
        {
            foreach (var frame in frames) frame.Dispose();
        }
    }

    private static BitmapImage ToBitmap(byte[] png)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(png);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
