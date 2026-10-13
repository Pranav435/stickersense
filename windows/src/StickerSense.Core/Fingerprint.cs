using System.Numerics;
using System.Text.Json.Serialization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace StickerSense.Core;

/// <summary>
/// Identifies "the same sticker" across screenshots, even when it is shown at a different size
/// or position. The image is first cropped to its content (dropping the chat or transparent
/// background around it), then summarised two ways: a 256-bit gradient hash (shape) and a
/// 16×16 colour thumbnail (detail and colour).
/// </summary>
/// <remarks>
/// Same format and thresholds as the Swift version. The crop is located to a fraction of a pixel
/// and sampled by exact area averaging, so a sticker shown at 25 % size still lands on the same
/// cells; the hash cells overlap their neighbours slightly so half-pixel shifts don't flip bits.
/// </remarks>
public sealed class Fingerprint : IEquatable<Fingerprint>
{
    [JsonConstructor]
    public Fingerprint(ulong[] dHash, Thumbprint thumb)
    {
        DHash = dHash;
        Thumb = thumb;
    }

    public Fingerprint(Image<Rgba32> image)
    {
        var pixels = PixelPlanes.Flattened(image);
        var box = ContentCrop.Locate(pixels);
        DHash = DifferenceHash(pixels, box);
        Thumb = new Thumbprint(pixels.AreaRgb(box, Thumbprint.Side, Thumbprint.Side));
    }

    /// <summary>16×16 difference hash, packed into four 64-bit words.</summary>
    [JsonPropertyName("dHash")]
    public ulong[] DHash { get; }

    [JsonPropertyName("thumb")]
    public Thumbprint Thumb { get; }

    public int HammingDistance(Fingerprint other)
    {
        if (DHash.Length != other.DHash.Length) return int.MaxValue;
        var total = 0;
        for (var i = 0; i < DHash.Length; i++) total += BitOperations.PopCount(DHash[i] ^ other.DHash[i]);
        return total;
    }

    /// <summary>Strict on purpose: speaking the wrong cached description is far worse than a short wait.</summary>
    public bool IsSameSticker(Fingerprint other) =>
        HammingDistance(other) <= 10 && Thumb.Difference(other.Thumb) <= 0.0065;

    private const int HashWidth = 17, HashHeight = 16, HashSubcells = 4;

    /// <summary>Bit set where a cell is brighter than its right-hand neighbour (17×16 cells → 256 bits).</summary>
    internal static ulong[] DifferenceHash(PixelPlanes pixels, ContentBox box)
    {
        // Sample a 4× finer grid, then give each hash cell a tent-weighted window reaching a
        // quarter of a cell into its neighbours.
        const int fw = HashWidth * HashSubcells, fh = HashHeight * HashSubcells;
        var fine = pixels.AreaGray(box, fw, fh);
        const double half = 0.75 * HashSubcells;
        var gray = new byte[HashWidth * HashHeight];
        for (var cy = 0; cy < HashHeight; cy++)
        {
            for (var cx = 0; cx < HashWidth; cx++)
            {
                double centerX = (cx + 0.5) * HashSubcells, centerY = (cy + 0.5) * HashSubcells, sum = 0, weights = 0;
                for (var y = (int)Math.Max(0, centerY - half); y < Math.Min(fh, centerY + half); y++)
                {
                    var wy = 1 - Math.Abs(y + 0.5 - centerY) / half;
                    if (wy <= 0) continue;
                    for (var x = (int)Math.Max(0, centerX - half); x < Math.Min(fw, centerX + half); x++)
                    {
                        var wx = 1 - Math.Abs(x + 0.5 - centerX) / half;
                        if (wx <= 0) continue;
                        sum += fine[y * fw + x] * wx * wy;
                        weights += wx * wy;
                    }
                }
                gray[cy * HashWidth + cx] = (byte)Math.Clamp(Math.Round(sum / weights), 0, 255);
            }
        }

        var words = new ulong[4];
        var bit = 0;
        for (var y = 0; y < HashHeight; y++)
        {
            for (var x = 0; x < HashWidth - 1; x++)
            {
                if (gray[y * HashWidth + x] > gray[y * HashWidth + x + 1]) words[bit / 64] |= 1UL << (bit % 64);
                bit++;
            }
        }
        return words;
    }

    public bool Equals(Fingerprint? other) =>
        other is not null && DHash.AsSpan().SequenceEqual(other.DHash) && Thumb.Equals(other.Thumb);

    public override bool Equals(object? obj) => Equals(obj as Fingerprint);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var word in DHash) hash.Add(word);
        return hash.ToHashCode();
    }
}

/// <summary>A small colour thumbnail used to compare images.</summary>
public sealed class Thumbprint : IEquatable<Thumbprint>
{
    public const int Side = 16;

    [JsonConstructor]
    public Thumbprint(byte[] rgb) => Rgb = rgb;

    /// <summary>Thumbnail of a whole image (flattened onto white).</summary>
    public Thumbprint(Image<Rgba32> image, int side = Side)
    {
        var pixels = PixelPlanes.Flattened(image);
        Rgb = pixels.AreaRgb(ContentBox.Whole(pixels), side, side);
    }

    /// <summary>RGB bytes, row-major, <c>side * side * 3</c> long. Base64 in JSON, like Swift's <c>Data</c>.</summary>
    [JsonPropertyName("rgb")]
    public byte[] Rgb { get; }

    /// <summary>Every pixel about the same colour (a blank capture): each channel varies by 10 or less.</summary>
    [JsonIgnore]
    public bool IsUniform
    {
        get
        {
            if (Rgb.Length < 3) return true;
            for (var channel = 0; channel < 3; channel++)
            {
                int low = 255, high = 0;
                for (var i = channel; i < Rgb.Length; i += 3)
                {
                    low = Math.Min(low, Rgb[i]);
                    high = Math.Max(high, Rgb[i]);
                }
                if (high - low > 10) return false;
            }
            return true;
        }
    }

    /// <summary>Mean absolute difference in 0…1.</summary>
    public double Difference(Thumbprint other)
    {
        if (Rgb.Length != other.Rgb.Length || Rgb.Length == 0) return 1;
        long total = 0;
        for (var i = 0; i < Rgb.Length; i++) total += Math.Abs(Rgb[i] - other.Rgb[i]);
        return total / (double)(Rgb.Length * 255);
    }

    public bool Equals(Thumbprint? other) => other is not null && Rgb.AsSpan().SequenceEqual(other.Rgb);

    public override bool Equals(object? obj) => Equals(obj as Thumbprint);

    public override int GetHashCode() => Rgb.Length == 0 ? 0 : HashCode.Combine(Rgb.Length, Rgb[0], Rgb[^1]);
}

/// <summary>A rectangle in pixel coordinates with fractional edges.</summary>
internal readonly record struct ContentBox(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
    public static ContentBox Whole(PixelPlanes pixels) => new(0, 0, pixels.Width, pixels.Height);
}

/// <summary>Finds the sticker inside a uniform background so position and padding don't matter.</summary>
internal static class ContentCrop
{
    private const int Grid = 128;

    /// <summary>The content's box, or the whole image when there's no uniform background or too little content.</summary>
    public static ContentBox Locate(PixelPlanes pixels)
    {
        var whole = ContentBox.Whole(pixels);
        // Background = the colour the four corners agree on (if they do), judged on a coarse grid
        // so a stray pixel doesn't matter.
        var coarse = pixels.AreaRgb(whole, Grid, Grid);
        (int R, int G, int B) Corner(int x, int y)
        {
            var i = (y * Grid + x) * 3;
            return (coarse[i], coarse[i + 1], coarse[i + 2]);
        }
        static int Distance((int R, int G, int B) a, (int R, int G, int B) b) =>
            Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
        var corners = new[] { Corner(0, 0), Corner(Grid - 1, 0), Corner(0, Grid - 1), Corner(Grid - 1, Grid - 1) };
        var bg = corners[0];
        if (!corners.All(c => Distance(c, bg) < 24)) return whole;

        // How far each column and row is from the background, ignoring faint noise.
        int w = pixels.Width, h = pixels.Height;
        var columns = new double[w];
        var rows = new double[h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                var d = Math.Abs(pixels.R[i] - bg.R) + Math.Abs(pixels.G[i] - bg.G) + Math.Abs(pixels.B[i] - bg.B) - 12f;
                if (d <= 0) continue;
                columns[x] += d;
                rows[y] += d;
            }
        }
        var total = columns.Sum();
        if (total <= 0) return whole;

        // Trim 1 % of the content's weight from each side. Unlike a threshold, this lands at the
        // same relative spot at any scale, to a fraction of a pixel.
        const double trim = 0.01;
        var box = new ContentBox(
            Quantile(columns, trim * total), Quantile(rows, trim * total),
            Quantile(columns, (1 - trim) * total), Quantile(rows, (1 - trim) * total));
        if (box.Width < w * 8.0 / Grid || box.Height < h * 8.0 / Grid) return whole;
        return box;
    }

    /// <summary>Position where the running sum of <paramref name="weights"/> reaches <paramref name="target"/>.</summary>
    private static double Quantile(double[] weights, double target)
    {
        var sum = 0.0;
        for (var i = 0; i < weights.Length; i++)
        {
            if (weights[i] > 0 && sum + weights[i] >= target) return i + (target - sum) / weights[i];
            sum += weights[i];
        }
        return weights.Length;
    }
}

/// <summary>Detects motion between two screen captures of the same region.</summary>
public sealed class MotionSignature
{
    internal const int Side = 48;
    private readonly byte[] gray;

    public MotionSignature(Image<Rgba32> image)
    {
        var pixels = PixelPlanes.Flattened(image);
        var values = pixels.AreaGray(ContentBox.Whole(pixels), Side, Side);
        gray = values.Select(v => (byte)Math.Clamp(Math.Round(v), 0, 255)).ToArray();
    }

    /// <summary>Fraction of pixels that changed noticeably (0…1).</summary>
    public double ChangedFraction(MotionSignature other)
    {
        if (gray.Length != other.gray.Length || gray.Length == 0) return 1;
        var changed = 0;
        for (var i = 0; i < gray.Length; i++)
        {
            if (Math.Abs(gray[i] - other.gray[i]) > 24) changed++;
        }
        return changed / (double)gray.Length;
    }

    /// <summary>True when the region visibly moved — e.g. an animated sticker or GIF.</summary>
    public bool Differs(MotionSignature other) => ChangedFraction(other) > 0.004;
}

/// <summary>An image flattened onto white as float colour planes, with exact area sampling.</summary>
internal sealed class PixelPlanes
{
    private PixelPlanes(int width, int height)
    {
        Width = width;
        Height = height;
        R = new float[width * height];
        G = new float[width * height];
        B = new float[width * height];
    }

    public int Width { get; }
    public int Height { get; }
    public float[] R { get; }
    public float[] G { get; }
    public float[] B { get; }

    /// <summary>Very large images are reduced first (area averaging) — fingerprints only need coarse detail.</summary>
    private const int MaxSide = 1024;

    public static PixelPlanes Flattened(Image<Rgba32> image)
    {
        using var flat = ImagePrep.Flattened(image);
        var planes = new PixelPlanes(flat.Width, flat.Height);
        flat.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var i = y * planes.Width + x;
                    planes.R[i] = row[x].R;
                    planes.G[i] = row[x].G;
                    planes.B[i] = row[x].B;
                }
            }
        });
        var longest = Math.Max(planes.Width, planes.Height);
        if (longest <= MaxSide) return planes;
        var scale = (double)MaxSide / longest;
        int w = Math.Max(1, (int)(planes.Width * scale)), h = Math.Max(1, (int)(planes.Height * scale));
        var reduced = new PixelPlanes(w, h);
        var whole = ContentBox.Whole(planes);
        planes.Area(planes.R, whole, w, h).CopyTo(reduced.R, 0);
        planes.Area(planes.G, whole, w, h).CopyTo(reduced.G, 0);
        planes.Area(planes.B, whole, w, h).CopyTo(reduced.B, 0);
        return reduced;
    }

    /// <summary>Area-averaged RGB bytes of <paramref name="box"/> on a <paramref name="w"/>×<paramref name="h"/> grid.</summary>
    public byte[] AreaRgb(ContentBox box, int w, int h)
    {
        var r = Area(R, box, w, h);
        var g = Area(G, box, w, h);
        var b = Area(B, box, w, h);
        var rgb = new byte[w * h * 3];
        for (var i = 0; i < w * h; i++)
        {
            rgb[i * 3] = ToByte(r[i]);
            rgb[i * 3 + 1] = ToByte(g[i]);
            rgb[i * 3 + 2] = ToByte(b[i]);
        }
        return rgb;
    }

    /// <summary>Area-averaged luma (Rec. 601) of <paramref name="box"/> on a <paramref name="w"/>×<paramref name="h"/> grid.</summary>
    public float[] AreaGray(ContentBox box, int w, int h)
    {
        var r = Area(R, box, w, h);
        var g = Area(G, box, w, h);
        var b = Area(B, box, w, h);
        var gray = new float[w * h];
        for (var i = 0; i < gray.Length; i++) gray[i] = 0.299f * r[i] + 0.587f * g[i] + 0.114f * b[i];
        return gray;
    }

    /// <summary>
    /// Exact box filter: every output cell is the coverage-weighted mean of the source pixels
    /// under it, with fractional coverage at the cell edges.
    /// </summary>
    public float[] Area(float[] plane, ContentBox box, int outWidth, int outHeight)
    {
        double cellWidth = box.Width / outWidth, cellHeight = box.Height / outHeight;
        var columns = new double[Height * outWidth];
        for (var ox = 0; ox < outWidth; ox++)
        {
            double x0 = box.Left + ox * cellWidth, x1 = x0 + cellWidth;
            int start = Math.Max(0, (int)Math.Floor(x0)), end = Math.Min(Width, (int)Math.Ceiling(x1));
            for (var x = start; x < end; x++)
            {
                var weight = Math.Min(x + 1, x1) - Math.Max(x, x0);
                if (weight <= 0) continue;
                for (var y = 0; y < Height; y++) columns[y * outWidth + ox] += plane[y * Width + x] * weight;
            }
        }
        var result = new float[outWidth * outHeight];
        var area = cellWidth * cellHeight;
        for (var oy = 0; oy < outHeight; oy++)
        {
            double y0 = box.Top + oy * cellHeight, y1 = y0 + cellHeight;
            int start = Math.Max(0, (int)Math.Floor(y0)), end = Math.Min(Height, (int)Math.Ceiling(y1));
            for (var ox = 0; ox < outWidth; ox++)
            {
                var sum = 0.0;
                for (var y = start; y < end; y++)
                {
                    var weight = Math.Min(y + 1, y1) - Math.Max(y, y0);
                    if (weight > 0) sum += columns[y * outWidth + ox] * weight;
                }
                result[oy * outWidth + ox] = (float)(sum / area);
            }
        }
        return result;
    }

    private static byte ToByte(float value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
}
