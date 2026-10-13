using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace StickerSense.Core;

/// <summary>Phi-3.5-vision's image inputs for one request (see <see cref="PhiVisionInputs.Prepare"/>).</summary>
/// <param name="PixelValues">[pictures, Slots, 3, 336, 336], CLIP-normalised, channel first.</param>
/// <param name="Slots">Overview plus crops of the picture with the most crops; others are zero-padded.</param>
/// <param name="ImageSizes">Each picture's resized height and width: [pictures, 2].</param>
/// <param name="TokenCounts">Tokens each picture takes, for GenAI's <c>num_image_tokens</c>.</param>
public sealed record PhiImageBatch(float[] PixelValues, int Slots, long[] ImageSizes, long[] TokenCounts);

/// <summary>
/// Phi-3.5-vision's image inputs, built here instead of by ONNX Runtime GenAI's processor.
/// </summary>
/// <remarks>
/// The stock processor always cuts a picture into 16 crops of 336 pixels plus an overview, so
/// every image costs about 2,500 tokens however small it is: about a minute per sticker on a
/// typical laptop processor, and long enough on an integrated graphics card to trip Windows'
/// GPU timeout. A sticker is small and simple, so one crop plus the overview (313 tokens) is
/// enough, and measured on an i7-11700 it's about six times faster with the same description.
/// Animation frames side by side (<see cref="ImagePrep.SideBySide"/>) get one crop per frame
/// and a single overview. Words on stickers come from Windows' text recognition anyway.
/// </remarks>
public static class PhiVisionInputs
{
    public const int CropSize = 336;

    /// <summary>At most this many crops across (or down) one picture.</summary>
    public const int MaxCrops = 4;

    /// <summary>Feature tokens for a crop grid: 144 per crop and for the overview, one separator, and 12 newlines per row.</summary>
    public static int Tokens(int rows, int columns) => (rows * columns + 1) * 144 + 1 + (rows + 1) * 12;

    /// <summary>Tokens a square picture takes (one crop): 313.</summary>
    public static int TokensPerImage => Tokens(1, 1);

    // CLIP's normalisation, which Phi-3.5-vision's image encoder was trained with.
    private static readonly float[] Mean = [0.48145466f, 0.4578275f, 0.40821073f];
    private static readonly float[] Std = [0.26862954f, 0.26130258f, 0.27577711f];

    private static readonly Regex ImageTag = new(@"<\|image_\d+\|>", RegexOptions.CultureInvariant);

    /// <summary>
    /// Crops for a picture: one for a square-ish picture; for a strip, one per square along it
    /// (3 frames side by side: 1 row of 3).
    /// </summary>
    public static (int Rows, int Columns) Grid(int width, int height)
    {
        if (width <= 0 || height <= 0) return (1, 1);
        return width >= height
            ? (1, Math.Clamp((int)Math.Round((double)width / height), 1, MaxCrops))
            : (Math.Clamp((int)Math.Round((double)height / width), 1, MaxCrops), 1);
    }

    /// <summary>
    /// The model's image inputs. Each picture is fitted onto white crops of its grid; its overview
    /// is the whole grid squeezed into one crop (with a single crop, the two are the same).
    /// </summary>
    public static PhiImageBatch Prepare(IReadOnlyList<Image<Rgba32>> images)
    {
        const int plane = CropSize * CropSize;
        var grids = images.Select(i => Grid(i.Width, i.Height)).ToList();
        var slots = grids.Count == 0 ? 1 : grids.Max(g => 1 + g.Rows * g.Columns);
        var values = new float[images.Count * slots * 3 * plane];
        var sizes = new long[images.Count * 2];
        var counts = new long[images.Count];
        for (var n = 0; n < images.Count; n++)
        {
            var (rows, columns) = grids[n];
            using var canvas = ImagePrep.Fit(images[n], columns * CropSize, rows * CropSize);
            var first = n * slots * 3 * plane;
            if (rows * columns == 1)
            {
                Fill(values, first, canvas, 0, 0);
                // With a single crop, the overview and the crop are the same picture.
                Array.Copy(values, first, values, first + 3 * plane, 3 * plane);
            }
            else
            {
                using (var overview = canvas.Clone(c => c.Resize(CropSize, CropSize, KnownResamplers.Bicubic)))
                {
                    Fill(values, first, overview, 0, 0);
                }
                for (var r = 0; r < rows; r++)
                {
                    for (var c = 0; c < columns; c++)
                    {
                        Fill(values, first + (1 + r * columns + c) * 3 * plane, canvas, c * CropSize, r * CropSize);
                    }
                }
            }
            sizes[2 * n] = rows * CropSize;
            sizes[2 * n + 1] = columns * CropSize;
            counts[n] = Tokens(rows, columns);
        }
        return new PhiImageBatch(values, slots, sizes, counts);
    }

    /// <summary>Writes the 336-pixel square at (left, top) as three CLIP-normalised planes starting at <paramref name="offset"/>.</summary>
    private static void Fill(float[] values, int offset, Image<Rgba32> image, int left, int top)
    {
        const int plane = CropSize * CropSize;
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < CropSize; y++)
            {
                var row = rows.GetRowSpan(top + y);
                for (var x = 0; x < CropSize; x++)
                {
                    var p = row[left + x];
                    var i = offset + y * CropSize + x;
                    values[i] = (p.R / 255f - Mean[0]) / Std[0];
                    values[i + plane] = (p.G / 255f - Mean[1]) / Std[1];
                    values[i + 2 * plane] = (p.B / 255f - Mean[2]) / Std[2];
                }
            }
        });
    }

    /// <summary>The prompt's text around its <c>&lt;|image_N|&gt;</c> tags, each piece to be tokenized on its own.</summary>
    public static IReadOnlyList<string> TextChunks(string prompt) => ImageTag.Split(prompt);

    /// <summary>
    /// The model's <c>input_ids</c>, as GenAI's processor builds them: the text pieces' tokens, with
    /// the n-th image tag replaced by that picture's token count of −n, where the embedding model
    /// puts the picture's features.
    /// </summary>
    public static int[] InputIds(IReadOnlyList<int[]> chunkTokens, IReadOnlyList<long> tokenCounts)
    {
        if (chunkTokens.Count != tokenCounts.Count + 1)
        {
            throw new ArgumentException($"Expected {tokenCounts.Count + 1} text pieces around {tokenCounts.Count} image tags, got {chunkTokens.Count}.", nameof(chunkTokens));
        }
        var ids = new List<int>(chunkTokens.Sum(c => c.Length) + (int)tokenCounts.Sum());
        for (var i = 0; i < chunkTokens.Count; i++)
        {
            ids.AddRange(chunkTokens[i]);
            if (i < tokenCounts.Count) ids.AddRange(Enumerable.Repeat(-(i + 1), (int)tokenCounts[i]));
        }
        return ids.ToArray();
    }
}
