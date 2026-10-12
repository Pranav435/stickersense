using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace StickerSense.App;

/// <summary>The app icon (StickerSense.ico, embedded as a resource) for windows and the notification area.</summary>
internal static class AppIcon
{
    private static readonly Uri Location = new("pack://application:,,,/StickerSense.ico", UriKind.Absolute);
    private static ImageSource? image;

    /// <summary>For WPF windows' title bars and the taskbar.</summary>
    public static ImageSource? Image
    {
        get
        {
            if (image is not null) return image;
            try
            {
                var decoder = new IconBitmapDecoder(Location, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                image = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
                image.Freeze();
            }
            catch (Exception e) when (e is IOException or NotSupportedException or FileFormatException or InvalidOperationException)
            {
                image = null;
            }
            return image;
        }
    }

    /// <summary>For the notification-area icon, at the system's small-icon size (scaled for the display).</summary>
    public static System.Drawing.Icon? TrayIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(Location);
            if (resource is null) return null;
            using var stream = resource.Stream;
            return new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
        }
        catch (Exception e) when (e is IOException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
