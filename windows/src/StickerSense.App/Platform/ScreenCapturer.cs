using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp.PixelFormats;
using StickerSense.Core;
using SharpImage = SixLabors.ImageSharp.Image;

namespace StickerSense.App.Platform;

/// <summary>Captures a region of the screen (physical pixels) with <see cref="Graphics.CopyFromScreen(int, int, int, int, System.Drawing.Size)"/>.</summary>
internal sealed class ScreenCapturer : IScreenCapturer
{
    public Task<SixLabors.ImageSharp.Image<Rgba32>> CaptureAsync(PixelRect rect, CancellationToken ct = default) =>
        Task.Run(() => Capture(rect), ct);

    private static SixLabors.ImageSharp.Image<Rgba32> Capture(PixelRect rect)
    {
        var clipped = rect.Intersect(Displays.VirtualScreen);
        if (clipped.Width < 4 || clipped.Height < 4) throw CaptureException.OffScreen();
        // Step inside the edge a little: screen readers and Windows draw focus outlines there.
        if (clipped.Width > 40 && clipped.Height > 40)
        {
            clipped = clipped.Inset(Math.Max(2, (int)Math.Round(2 * Displays.ScaleFor(clipped))));
        }

        try
        {
            using var bitmap = new Bitmap(clipped.Width, clipped.Height, PixelFormat.Format32bppRgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(clipped.X, clipped.Y, 0, 0, new System.Drawing.Size(clipped.Width, clipped.Height), CopyPixelOperation.SourceCopy);
            }
            // Reading as ARGB makes GDI+ fill in an opaque alpha channel.
            var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var bytes = new byte[data.Stride * data.Height];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                using var bgra = SharpImage.LoadPixelData<Bgra32>(bytes, bitmap.Width, bitmap.Height);
                return bgra.CloneAs<Rgba32>();
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }
        catch (Exception e) when (e is ExternalException or ArgumentException) // includes Win32Exception
        {
            throw CaptureException.Failed(e.Message, e);
        }
    }
}
