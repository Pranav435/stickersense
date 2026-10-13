using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Transforms;

namespace StickerSense.Core;

public sealed class ImagePrepException : Exception
{
    public ImagePrepException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Image utilities shared by the whole app: decoding stickers, flattening transparency,
/// scaling, pulling frames out of animated stickers and writing images back out.
/// Every method that returns an image returns a new one the caller owns (and disposes).
/// </summary>
public static class ImagePrep
{
    /// <summary>Animated files are decoded up to this many frames; samples are spread across them.</summary>
    public const int MaxDecodedFrames = 240;

    /// <summary>Only the formats stickers actually come in. TIFF and friends are deliberately absent.</summary>
    public static Configuration Configuration { get; } = new(
        new PngConfigurationModule(),
        new GifConfigurationModule(),
        new WebpConfigurationModule(),
        new JpegConfigurationModule(),
        new BmpConfigurationModule());

    private static DecoderOptions Decoder => new()
    {
        Configuration = Configuration,
        SkipMetadata = true,
        MaxFrames = MaxDecodedFrames,
    };

    // MARK: Flattening and scaling

    /// <summary>
    /// Stickers are usually transparent. Models read transparent pixels as black, so composite
    /// onto white (what most chat apps show behind stickers anyway).
    /// </summary>
    public static Image<Rgba32> Flattened(Image<Rgba32> image)
    {
        var result = image.Frames.CloneFrame(0);
        result.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    ref var p = ref row[x];
                    if (p.A == 255) continue;
                    int a = p.A, inverse = 255 - a;
                    p.R = (byte)((p.R * a + 255 * inverse + 127) / 255);
                    p.G = (byte)((p.G * a + 255 * inverse + 127) / 255);
                    p.B = (byte)((p.B * a + 255 * inverse + 127) / 255);
                    p.A = 255;
                }
            }
        });
        return result;
    }

    /// <summary>Scales down so the longest side is at most <paramref name="maxDimension"/> pixels. Never scales up.</summary>
    public static Image<Rgba32> Downscaled(Image<Rgba32> image, int maxDimension)
    {
        var longest = Math.Max(image.Width, image.Height);
        if (longest <= maxDimension || longest == 0) return image.Frames.CloneFrame(0);
        var scale = (double)maxDimension / longest;
        var w = Math.Max(1, (int)Math.Round(image.Width * scale, MidpointRounding.AwayFromZero));
        var h = Math.Max(1, (int)Math.Round(image.Height * scale, MidpointRounding.AwayFromZero));
        return Resized(image, w, h, KnownResamplers.Bicubic);
    }

    /// <summary>
    /// True for a capture with nothing in it — one flat colour. That happens when the screen
    /// region is blank or protected; describing it would only say "black rectangle".
    /// </summary>
    public static bool IsBlank(Image<Rgba32> image) => new Thumbprint(image, side: 24).IsUniform;

    /// <summary>
    /// The frames in one row, each scaled into a white square of <paramref name="cell"/> pixels,
    /// first frame on the left.
    /// </summary>
    public static Image<Rgba32> SideBySide(IReadOnlyList<Image<Rgba32>> frames, int cell)
    {
        var strip = new Image<Rgba32>(cell * frames.Count, cell, new Rgba32(255, 255, 255));
        for (var i = 0; i < frames.Count; i++)
        {
            using var square = Fit(frames[i], cell, cell);
            var left = i * cell;
            strip.Mutate(c => c.DrawImage(square, new Point(left, 0), 1f));
        }
        return strip;
    }

    /// <summary>
    /// The image scaled to fit inside <paramref name="width"/>×<paramref name="height"/>, centred on
    /// white, with transparency flattened onto white.
    /// </summary>
    public static Image<Rgba32> Fit(Image<Rgba32> image, int width, int height)
    {
        var scale = Math.Min((double)width / image.Width, (double)height / image.Height);
        var w = Math.Clamp((int)Math.Round(image.Width * scale), 1, width);
        var h = Math.Clamp((int)Math.Round(image.Height * scale), 1, height);
        using var resized = image.Frames.CloneFrame(0);
        resized.Mutate(c => c.Resize(w, h, KnownResamplers.Bicubic));
        var canvas = new Image<Rgba32>(width, height, new Rgba32(255, 255, 255));
        canvas.Mutate(c => c.DrawImage(resized, new Point((width - w) / 2, (height - h) / 2), 1f));
        return canvas;
    }

    /// <summary>Everything the describer needs: flattened, reasonably sized.</summary>
    public static Image<Rgba32> PrepareForModel(Image<Rgba32> image, int maxDimension = 768)
    {
        using var flat = Flattened(image);
        return Downscaled(flat, maxDimension);
    }

    /// <summary>Resizes the first frame to exactly <paramref name="width"/>×<paramref name="height"/>.</summary>
    internal static Image<Rgba32> Resized(Image<Rgba32> image, int width, int height, IResampler sampler)
    {
        var frame = image.Frames.CloneFrame(0);
        frame.Mutate(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(width, height),
            Mode = ResizeMode.Stretch,
            Sampler = sampler,
        }));
        return frame;
    }

    /// <summary>
    /// Paints over <paramref name="boxes"/> (the sender's name, the time) with the colour just
    /// outside each box's corners, so neither the model nor the fingerprint sees chat text.
    /// Returns a new image; with no boxes, a plain copy.
    /// </summary>
    public static Image<Rgba32> Masking(Image<Rgba32> image, IReadOnlyList<NormalizedBox> boxes)
    {
        var result = image.Frames.CloneFrame(0);
        if (boxes.Count == 0) return result;
        int w = result.Width, h = result.Height;
        result.ProcessPixelRows(accessor =>
        {
            static Rgba32 Sample(PixelAccessor<Rgba32> pixels, int x, int y) =>
                pixels.GetRowSpan(Math.Clamp(y, 0, pixels.Height - 1))[Math.Clamp(x, 0, pixels.Width - 1)];
            foreach (var box in boxes)
            {
                // Pixel rectangle grown by 3 pixels, right/bottom exclusive.
                var left = (int)Math.Floor(box.X * w) - 3;
                var top = (int)Math.Floor(box.Y * h) - 3;
                var right = (int)Math.Ceiling(box.Right * w) + 3;
                var bottom = (int)Math.Ceiling(box.Bottom * h) + 3;
                // Average the colour just outside the box's four corners.
                var samples = new[]
                {
                    Sample(accessor, left - 1, top - 1), Sample(accessor, right, top - 1),
                    Sample(accessor, left - 1, bottom), Sample(accessor, right, bottom),
                };
                var fill = new Rgba32(
                    (byte)Math.Round(samples.Average(p => p.R)),
                    (byte)Math.Round(samples.Average(p => p.G)),
                    (byte)Math.Round(samples.Average(p => p.B)),
                    (byte)Math.Round(samples.Average(p => p.A)));
                for (var y = Math.Max(0, top); y < Math.Min(h, bottom); y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (var x = Math.Max(0, left); x < Math.Min(w, right); x++) row[x] = fill;
                }
            }
        });
        return result;
    }

    // MARK: Decoding

    /// <summary>
    /// Loads an image file (PNG, JPEG, GIF, WebP, BMP) and returns up to <paramref name="maxFrames"/>
    /// frames spread evenly across the animation. Static images return one frame.
    /// </summary>
    public static List<Image<Rgba32>> LoadFrames(string path, int maxFrames = 4)
    {
        try
        {
            using var image = Image.Load<Rgba32>(Decoder, path);
            return SampleFrames(image, maxFrames);
        }
        catch (Exception e) when (e is ImageFormatException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            throw new ImagePrepException($"unreadable image: {e.Message}", e);
        }
    }

    public static List<Image<Rgba32>> LoadFrames(ReadOnlySpan<byte> data, int maxFrames = 4)
    {
        try
        {
            using var image = Image.Load<Rgba32>(Decoder, data);
            return SampleFrames(image, maxFrames);
        }
        catch (Exception e) when (e is ImageFormatException or NotSupportedException)
        {
            throw new ImagePrepException($"unreadable image: {e.Message}", e);
        }
    }

    /// <summary>Total frame count without decoding pixels (1 for still images).</summary>
    public static int FrameCount(string path)
    {
        try
        {
            var info = Image.Identify(Decoder, path);
            return Math.Max(1, info.FrameMetadataCollection.Count);
        }
        catch (Exception e) when (e is ImageFormatException or IOException or NotSupportedException)
        {
            throw new ImagePrepException($"unreadable image: {e.Message}", e);
        }
    }

    private static List<Image<Rgba32>> SampleFrames(Image<Rgba32> image, int maxFrames)
    {
        var indices = SampleIndices(image.Frames.Count, maxFrames);
        if (indices.Count == 0) throw new ImagePrepException("no frames");
        return indices.Select(i => image.Frames.CloneFrame(i)).ToList();
    }

    /// <summary>Evenly spread frame indices, always including the first frame.</summary>
    public static IReadOnlyList<int> SampleIndices(int count, int max)
    {
        if (count <= 0 || max <= 0) return [];
        if (count <= max) return Enumerable.Range(0, count).ToList();
        if (max == 1) return [0];
        return Enumerable.Range(0, max)
            .Select(i => (int)Math.Round(i * (double)(count - 1) / (max - 1), MidpointRounding.AwayFromZero))
            .ToList();
    }

    /// <summary>
    /// Drops frames that look the same as the frame before them, so a sticker that only animates
    /// briefly doesn't waste the model's attention on duplicates. Returns the kept instances;
    /// dropped frames are not disposed.
    /// </summary>
    public static List<Image<Rgba32>> DistinctFrames(IReadOnlyList<Image<Rgba32>> frames)
    {
        if (frames.Count == 0) return [];
        var result = new List<Image<Rgba32>> { frames[0] };
        var last = new MotionSignature(frames[0]);
        foreach (var frame in frames.Skip(1))
        {
            var signature = new MotionSignature(frame);
            if (signature.Differs(last))
            {
                result.Add(frame);
                last = signature;
            }
        }
        return result;
    }

    // MARK: Encoding

    public static byte[] PngData(Image<Rgba32> image)
    {
        using var stream = new MemoryStream();
        using var frame = image.Frames.CloneFrame(0);
        frame.SaveAsPng(stream, new PngEncoder());
        return stream.ToArray();
    }

    /// <summary>Writes an animated GIF (or a single-frame GIF) that loops forever.</summary>
    public static byte[] GifData(IReadOnlyList<Image<Rgba32>> frames, double frameDelaySeconds = 0.25)
    {
        if (frames.Count == 0) throw new ImagePrepException("no frames to encode");
        var delay = Math.Max(2, (int)Math.Round(frameDelaySeconds * 100));
        using var gif = frames[0].Frames.CloneFrame(0);
        gif.Metadata.GetGifMetadata().RepeatCount = 0;
        SetGifFrame(gif.Frames.RootFrame, delay);
        foreach (var frame in frames.Skip(1))
        {
            using var sized = frame.Width == gif.Width && frame.Height == gif.Height
                ? frame.Frames.CloneFrame(0)
                : Resized(frame, gif.Width, gif.Height, KnownResamplers.Bicubic);
            SetGifFrame(gif.Frames.AddFrame(sized.Frames.RootFrame), delay);
        }
        using var stream = new MemoryStream();
        gif.SaveAsGif(stream, new GifEncoder { ColorTableMode = GifColorTableMode.Local });
        return stream.ToArray();
    }

    private static void SetGifFrame(ImageFrame<Rgba32> frame, int delayCentiseconds)
    {
        var metadata = frame.Metadata.GetGifMetadata();
        metadata.FrameDelay = delayCentiseconds;
        metadata.DisposalMethod = GifDisposalMethod.RestoreToBackground;
    }

    /// <summary>
    /// A copy chat apps accept when pasted: GIF, PNG and JPEG files are used as they are; anything
    /// else (WhatsApp's WebP) becomes an animated GIF, keeping frame timing, or a PNG.
    /// </summary>
    public static string ShareableFile(string path, string tempDirectory)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".gif" or ".png" or ".jpg" or ".jpeg") return path;
        try
        {
            using var image = Image.Load<Rgba32>(Decoder, path);
            Directory.CreateDirectory(tempDirectory);
            var name = Path.Combine(tempDirectory, Path.GetFileNameWithoutExtension(path));
            if (image.Frames.Count > 1)
            {
                foreach (var frame in image.Frames)
                {
                    var milliseconds = frame.Metadata.GetWebpMetadata().FrameDelay;
                    SetGifFrame(frame, Math.Max(2, (int)Math.Round(milliseconds / 10.0)));
                }
                image.Metadata.GetGifMetadata().RepeatCount = 0;
                image.SaveAsGif(name + ".gif", new GifEncoder { ColorTableMode = GifColorTableMode.Local });
                return name + ".gif";
            }
            image.SaveAsPng(name + ".png");
            return name + ".png";
        }
        catch (Exception e) when (e is ImageFormatException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            throw new ImagePrepException($"couldn't convert the sticker: {e.Message}", e);
        }
    }

    /// <summary>Lowercase file extension (without the dot) for the encoded data's format, or null.</summary>
    public static string? DetectExtension(ReadOnlySpan<byte> data)
    {
        try
        {
            var format = Image.DetectFormat(Decoder, data);
            return format.FileExtensions.FirstOrDefault()?.ToLowerInvariant();
        }
        catch (Exception e) when (e is ImageFormatException or NotSupportedException)
        {
            return null;
        }
    }
}
