using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace StickerSense.Core;

/// <summary>
/// Turns a generated picture into a sticker: the subject lifted off its background, the caption
/// written in bold lettering, and a white die-cut border around both, on a transparent square.
/// The Mac app draws stickers the same way (StickerArt.swift).
/// </summary>
public static class StickerArt
{
    /// <summary>Stickers are 512 pixels square, like WhatsApp's.</summary>
    public const int Size = 512;

    /// <summary>The white border's width, at 512 pixels.</summary>
    private const double Border = 14;

    /// <summary>A faint grey edge outside the white border, so it shows on white chat backgrounds.</summary>
    private const double Edge = 1.5;

    // MARK: Cutting out

    /// <summary>
    /// The picture with its background made transparent: <paramref name="mask"/> holds how much
    /// of each pixel is subject (0 to 1, row by row, the picture's size). Faint mask values are
    /// dropped, so specks of background don't get a border of their own.
    /// </summary>
    public static Image<Rgba32> ApplyMask(Image<Rgba32> image, float[] mask)
    {
        if (mask.Length != image.Width * image.Height) throw new ArgumentException("The mask doesn't match the picture.", nameof(mask));
        var result = image.Frames.CloneFrame(0);
        result.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var m = mask[y * row.Length + x];
                    m = m < 0.08f ? 0 : Math.Min(1, (m - 0.08f) / 0.84f);
                    row[x].A = (byte)Math.Round(row[x].A * m);
                }
            }
        });
        return result;
    }

    /// <summary>
    /// A mask without any model, for when the cut-out model isn't there: everything connected to
    /// the picture's edge that's close to the edge's own colour (the plain white background the
    /// image model is asked for) is background. Null when the edge isn't one plain colour.
    /// </summary>
    public static float[]? PlainBackgroundMask(Image<Rgba32> image, int tolerance = 38)
    {
        int w = image.Width, h = image.Height;
        var pixels = new Rgba32[w * h];
        image.Frames.RootFrame.CopyPixelDataTo(pixels);
        // The background colour: the median of the edge, and the edge must mostly match it.
        var edge = new List<Rgba32>();
        for (var x = 0; x < w; x++) { edge.Add(pixels[x]); edge.Add(pixels[(h - 1) * w + x]); }
        for (var y = 0; y < h; y++) { edge.Add(pixels[y * w]); edge.Add(pixels[y * w + w - 1]); }
        var background = new Rgba32(Median(edge, p => p.R), Median(edge, p => p.G), Median(edge, p => p.B));
        bool Near(Rgba32 p) => Math.Abs(p.R - background.R) + Math.Abs(p.G - background.G) + Math.Abs(p.B - background.B) <= tolerance;
        if (edge.Count(Near) < edge.Count * 0.7) return null;

        var mask = Enumerable.Repeat(1f, w * h).ToArray();
        var queue = new Queue<int>();
        void Visit(int i)
        {
            if (mask[i] == 0 || !Near(pixels[i])) return;
            mask[i] = 0;
            queue.Enqueue(i);
        }
        for (var x = 0; x < w; x++) { Visit(x); Visit((h - 1) * w + x); }
        for (var y = 0; y < h; y++) { Visit(y * w); Visit(y * w + w - 1); }
        while (queue.TryDequeue(out var i))
        {
            int x = i % w, y = i / w;
            if (x > 0) Visit(i - 1);
            if (x < w - 1) Visit(i + 1);
            if (y > 0) Visit(i - w);
            if (y < h - 1) Visit(i + w);
        }
        return mask;
    }

    private static byte Median(List<Rgba32> values, Func<Rgba32, byte> channel)
    {
        var sorted = values.Select(channel).OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }

    // MARK: Composing

    /// <summary>
    /// Lays out <paramref name="subject"/> (transparent around it) and <paramref name="caption"/>
    /// on a transparent square with a white die-cut border. An opaque subject gets a border around
    /// its square. The caller disposes the result.
    /// </summary>
    public static Image<Rgba32> Compose(Image<Rgba32> subject, string caption, int size = Size)
    {
        var scale = size / 512.0;
        double border = Border * scale, edge = Edge * scale;
        var pad = Math.Ceiling(border + edge + 4 * scale);
        var box = size - 2 * pad;
        using var trimmed = Trimmed(subject) ?? throw new ImagePrepException("the picture came out empty");

        // The caption sits at the bottom, overlapping the subject's lower edge a little.
        var lines = CaptionLines(caption, box, box * 0.34, scale);
        var captionHeight = lines.Sum(l => l.Height);
        var boxHeight = box - captionHeight * 0.7;
        var fit = Math.Min(box / trimmed.Width, boxHeight / trimmed.Height);
        int drawnWidth = Math.Max(1, (int)Math.Round(trimmed.Width * fit)), drawnHeight = Math.Max(1, (int)Math.Round(trimmed.Height * fit));
        var x = (int)Math.Round((size - drawnWidth) / 2.0);
        // Image rows go top-down: centre without a caption, otherwise sit on it.
        var y = (int)Math.Round(lines.Count == 0 ? pad + (boxHeight - drawnHeight) / 2 : pad + boxHeight - drawnHeight);

        var art = new Image<Rgba32>(ImagePrep.Configuration, size, size);
        using (var resized = trimmed.Clone(c => c.Resize(drawnWidth, drawnHeight, KnownResamplers.Lanczos3)))
        {
            art.Mutate(c => c.DrawImage(resized, new Point(x, y), 1f));
        }
        var top = size - pad - captionHeight;
        foreach (var line in lines)
        {
            var options = new RichTextOptions(line.Font)
            {
                Origin = new PointF(size / 2f, (float)(top + line.Stroke * 0.25)),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var outline = new SolidPen(new PenOptions(Color.White, (float)line.Stroke) { JointStyle = SixLabors.ImageSharp.Drawing.JointStyle.Round });
            art.Mutate(c => c
                .DrawText(options, line.Text, outline)
                .DrawText(options, line.Text, Brushes.Solid(Color.FromRgb(26, 26, 31))));
            top += line.Height;
        }
        AddBorder(art, border, edge);
        return art;
    }

    /// <summary>The image cropped to its non-transparent pixels, or null if it's entirely transparent.</summary>
    internal static Image<Rgba32>? Trimmed(Image<Rgba32> image)
    {
        int minX = image.Width, minY = image.Height, maxX = -1, maxY = -1;
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    if (row[x].A <= 24) continue;
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
            }
        });
        if (maxX < minX || maxY < minY) return null;
        var crop = new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
        return image.Frames.CloneFrame(0).Clone(c => c.Crop(crop));
    }

    // MARK: Caption

    internal sealed record CaptionLine(string Text, Font Font, double Width, double Height, double Stroke);

    /// <summary>The caption in one line, or two when one would be too small, shrunk to fit.</summary>
    internal static IReadOnlyList<CaptionLine> CaptionLines(string caption, double maxWidth, double maxHeight, double scale)
    {
        var text = caption.Trim();
        if (text.Length == 0 || CaptionFamily() is not { } family) return [];
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var splits = new List<string[]> { new[] { text } };
        // Every way of breaking into two lines, most balanced first.
        splits.AddRange(Enumerable.Range(1, Math.Max(0, words.Length - 1))
            .Select(i => new[] { string.Join(" ", words[..i]), string.Join(" ", words[i..]) })
            .OrderBy(p => Math.Max(p[0].Length, p[1].Length)));
        double largest = 92 * scale, smallest = 34 * scale;
        IReadOnlyList<CaptionLine> best = [];
        var bestSize = 0.0;
        foreach (var parts in splits.Take(3))
        {
            var size = largest;
            while (size >= smallest)
            {
                var trial = parts.Select(p => Make(p, family, size)).ToList();
                if (trial.All(l => l.Width <= maxWidth) && trial.Sum(l => l.Height) <= maxHeight) break;
                size -= 2 * scale;
            }
            // One line wins unless two lines are clearly bigger.
            if (size > bestSize * (best.Count == 1 ? 1.25 : 1))
            {
                bestSize = size;
                best = parts.Select(p => Make(p, family, Math.Max(size, smallest))).ToList();
            }
        }
        return best;
    }

    private static CaptionLine Make(string text, FontFamily family, double size)
    {
        var heavy = family.Name.Contains("Black", StringComparison.OrdinalIgnoreCase) || family.Name.Contains("Heavy", StringComparison.OrdinalIgnoreCase);
        var font = family.CreateFont((float)size, heavy ? FontStyle.Regular : FontStyle.Bold);
        var bounds = TextMeasurer.MeasureAdvance(text, new TextOptions(font));
        var stroke = size * 0.22;
        return new CaptionLine(text, font, bounds.Width + stroke, size * 1.2 + stroke * 0.5, stroke);
    }

    /// <summary>The boldest everyday font there is (Segoe UI Black on Windows).</summary>
    private static FontFamily? CaptionFamily()
    {
        foreach (var name in new[] { "Segoe UI Black", "Arial Black", "Avenir Next Heavy", "Arial Rounded MT Bold", "Verdana", "Arial", "Helvetica", "DejaVu Sans" })
        {
            if (SystemFonts.TryGet(name, out var family)) return family;
        }
        return SystemFonts.Families.Cast<FontFamily?>().FirstOrDefault();
    }

    // MARK: Border

    /// <summary>Puts a white border (and a faint grey edge) around everything visible, in place.</summary>
    internal static void AddBorder(Image<Rgba32> image, double border, double edge)
    {
        int w = image.Width, h = image.Height;
        var pixels = new Rgba32[w * h];
        image.CopyPixelDataTo(pixels);
        var inside = pixels.Select(p => p.A >= 128).ToArray();
        var distance = DistanceToInside(inside, w, h);
        for (var i = 0; i < pixels.Length; i++)
        {
            var d = distance[i];
            // Under the content: white within the border, then the grey edge, anti-aliased.
            var white = Math.Clamp(border + 0.5 - d, 0, 1);
            var outer = Math.Clamp(border + edge + 0.5 - d, 0, 1);
            var backAlpha = Math.Max(white, outer * 0.22);
            if (backAlpha <= 0) continue;
            var grey = white >= outer * 0.22 ? 255.0 : 89.0;
            ref var p = ref pixels[i];
            var a = p.A / 255.0;
            var outAlpha = a + backAlpha * (1 - a);
            byte Mix(byte c) => (byte)Math.Round(Math.Clamp((c * a + grey * backAlpha * (1 - a)) / outAlpha, 0, 255));
            p = new Rgba32(Mix(p.R), Mix(p.G), Mix(p.B), (byte)Math.Round(outAlpha * 255));
        }
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++) pixels.AsSpan(y * w, w).CopyTo(rows.GetRowSpan(y));
        });
    }

    /// <summary>
    /// Euclidean distance from every pixel to the nearest <paramref name="inside"/> pixel (0
    /// inside), by Felzenszwalb and Huttenlocher's two-pass squared distance transform.
    /// </summary>
    internal static double[] DistanceToInside(bool[] inside, int w, int h)
    {
        double infinity = (double)w * w + (double)h * h;
        var grid = inside.Select(i => i ? 0 : infinity).ToArray();
        var line = new double[Math.Max(w, h)];
        var output = new double[Math.Max(w, h)];
        for (var x = 0; x < w; x++)
        {
            for (var y = 0; y < h; y++) line[y] = grid[y * w + x];
            Transform1D(line, h, output);
            for (var y = 0; y < h; y++) grid[y * w + x] = output[y];
        }
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++) line[x] = grid[y * w + x];
            Transform1D(line, w, output);
            for (var x = 0; x < w; x++) grid[y * w + x] = Math.Sqrt(output[x]);
        }
        return grid;
    }

    private static void Transform1D(double[] f, int n, double[] d)
    {
        var v = new int[n];
        var z = new double[n + 1];
        var k = 0;
        z[0] = double.NegativeInfinity;
        z[1] = double.PositiveInfinity;
        for (var q = 1; q < n; q++)
        {
            var s = (f[q] + (double)q * q - (f[v[k]] + (double)v[k] * v[k])) / (2.0 * q - 2.0 * v[k]);
            while (s <= z[k])
            {
                k--;
                s = (f[q] + (double)q * q - (f[v[k]] + (double)v[k] * v[k])) / (2.0 * q - 2.0 * v[k]);
            }
            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = double.PositiveInfinity;
        }
        k = 0;
        for (var q = 0; q < n; q++)
        {
            while (z[k + 1] < q) k++;
            var dq = (double)(q - v[k]);
            d[q] = dq * dq + f[v[k]];
        }
    }
}
