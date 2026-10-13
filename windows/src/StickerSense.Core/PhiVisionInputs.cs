using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace StickerSense.Core;

/// <summary>
/// Phi-3.5-vision's image inputs, built here instead of by ONNX Runtime GenAI's processor.
/// </summary>
/// <remarks>
/// The stock processor always cuts a picture into 16 crops of 336 pixels plus an overview, so
/// every image costs about 2,500 tokens however small it is: about a minute per sticker on a
/// typical laptop processor, and long enough on an integrated graphics card to trip Windows'
/// GPU timeout. A sticker is small and simple, so one crop plus the overview (313 tokens) is
/// enough, and measured on an i7-11700 it's about six times faster with the same description.
/// Words on stickers come from Windows' text recognition anyway, not from the model reading them.
/// </remarks>
public static class PhiVisionInputs
{
    public const int CropSize = 336;

    /// <summary>Each picture: its overview plus one crop. (Both are the same 336-pixel square.)</summary>
    public const int SlotsPerImage = 2;

    /// <summary>Feature tokens for a crop grid: 144 per crop and for the overview, one separator, and 12 newlines per row.</summary>
    public static int Tokens(int rows, int columns) => (rows * columns + 1) * 144 + 1 + (rows + 1) * 12;

    /// <summary>Tokens each picture takes with one crop: 313.</summary>
    public static int TokensPerImage => Tokens(1, 1);

    // CLIP's normalisation, which Phi-3.5-vision's image encoder was trained with.
    private static readonly float[] Mean = [0.48145466f, 0.4578275f, 0.40821073f];
    private static readonly float[] Std = [0.26862954f, 0.26130258f, 0.27577711f];

    private static readonly Regex ImageTag = new(@"<\|image_\d+\|>", RegexOptions.CultureInvariant);

    /// <summary>
    /// The model's <c>pixel_values</c>: for each picture, its overview and its one crop, each
    /// 3 × 336 × 336, channel first, CLIP-normalised. Shape [pictures, 2, 3, 336, 336].
    /// Transparent parts become white, and pictures are letterboxed onto white squares.
    /// </summary>
    public static float[] PixelValues(IReadOnlyList<Image<Rgba32>> images)
    {
        const int plane = CropSize * CropSize;
        var values = new float[images.Count * SlotsPerImage * 3 * plane];
        for (var n = 0; n < images.Count; n++)
        {
            using var square = Square(images[n]);
            var first = n * SlotsPerImage * 3 * plane;
            square.ProcessPixelRows(rows =>
            {
                for (var y = 0; y < CropSize; y++)
                {
                    var row = rows.GetRowSpan(y);
                    for (var x = 0; x < CropSize; x++)
                    {
                        var p = row[x];
                        var i = first + y * CropSize + x;
                        values[i] = (p.R / 255f - Mean[0]) / Std[0];
                        values[i + plane] = (p.G / 255f - Mean[1]) / Std[1];
                        values[i + 2 * plane] = (p.B / 255f - Mean[2]) / Std[2];
                    }
                }
            });
            // With a single crop, the overview and the crop are the same picture.
            Array.Copy(values, first, values, first + 3 * plane, 3 * plane);
        }
        return values;
    }

    /// <summary>The picture scaled to fit a white 336-pixel square, centred, transparency flattened onto white.</summary>
    public static Image<Rgba32> Square(Image<Rgba32> image)
    {
        var scale = Math.Min((double)CropSize / image.Width, (double)CropSize / image.Height);
        var width = Math.Clamp((int)Math.Round(image.Width * scale), 1, CropSize);
        var height = Math.Clamp((int)Math.Round(image.Height * scale), 1, CropSize);
        using var resized = image.Frames.CloneFrame(0);
        resized.Mutate(c => c.Resize(width, height, KnownResamplers.Bicubic));
        var square = new Image<Rgba32>(CropSize, CropSize, new Rgba32(255, 255, 255));
        square.Mutate(c => c.DrawImage(resized, new Point((CropSize - width) / 2, (CropSize - height) / 2), 1f));
        return square;
    }

    /// <summary>The model's <c>image_sizes</c>: each picture's resized height and width, [pictures, 2].</summary>
    public static long[] ImageSizes(int imageCount) =>
        Enumerable.Repeat(new long[] { CropSize, CropSize }, imageCount).SelectMany(s => s).ToArray();

    /// <summary>Tokens per picture, for GenAI's <c>num_image_tokens</c> input.</summary>
    public static long[] ImageTokenCounts(int imageCount) => Enumerable.Repeat((long)TokensPerImage, imageCount).ToArray();

    /// <summary>The prompt's text around its <c>&lt;|image_N|&gt;</c> tags, each piece to be tokenized on its own.</summary>
    public static IReadOnlyList<string> TextChunks(string prompt) => ImageTag.Split(prompt);

    /// <summary>
    /// The model's <c>input_ids</c>, as GenAI's processor builds them: the text pieces' tokens, with
    /// the n-th image tag replaced by <see cref="TokensPerImage"/> copies of −n, where the
    /// embedding model puts that picture's features.
    /// </summary>
    public static int[] InputIds(IReadOnlyList<int[]> chunkTokens, int imageCount)
    {
        if (chunkTokens.Count != imageCount + 1)
        {
            throw new ArgumentException($"Expected {imageCount + 1} text pieces around {imageCount} image tags, got {chunkTokens.Count}.", nameof(chunkTokens));
        }
        var ids = new List<int>(chunkTokens.Sum(c => c.Length) + imageCount * TokensPerImage);
        for (var i = 0; i < chunkTokens.Count; i++)
        {
            ids.AddRange(chunkTokens[i]);
            if (i < imageCount) ids.AddRange(Enumerable.Repeat(-(i + 1), TokensPerImage));
        }
        return ids.ToArray();
    }
}
