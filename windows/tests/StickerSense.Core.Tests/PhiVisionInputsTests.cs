using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using StickerSense.Core;
using Xunit;

namespace StickerSense.Core.Tests;

public class PhiVisionInputsTests
{
    [Fact]
    public void CountsImageTokens()
    {
        Assert.Equal(313, PhiVisionInputs.TokensPerImage);
        // What GenAI's own processor spends on any square picture (16 crops): measured as 2,662 input ids with a 153-token prompt.
        Assert.Equal(2509, PhiVisionInputs.Tokens(4, 4));
        Assert.Equal([336L, 336L, 336L, 336L], PhiVisionInputs.ImageSizes(2));
        Assert.Equal([313L, 313L], PhiVisionInputs.ImageTokenCounts(2));
    }

    [Fact]
    public void ReplacesImageTagsWithPlaceholders()
    {
        var prompt = PhiVisionPrompt.Build("Be brief.", "Describe.", 2);
        var chunks = PhiVisionInputs.TextChunks(prompt);
        Assert.Equal(["<|system|>\nBe brief.<|end|>\n<|user|>\n", "\n", "\nDescribe.<|end|>\n<|assistant|>\n"], chunks);

        var ids = PhiVisionInputs.InputIds([[1, 2], [3], [4, 5]], 2);
        Assert.Equal(5 + 2 * 313, ids.Length);
        Assert.Equal([1, 2], ids[..2]);
        Assert.All(ids[2..315], id => Assert.Equal(-1, id));
        Assert.Equal(3, ids[315]);
        Assert.All(ids[316..629], id => Assert.Equal(-2, id));
        Assert.Equal([4, 5], ids[629..]);
        Assert.Throws<ArgumentException>(() => PhiVisionInputs.InputIds([[1], [2]], 2));
    }

    [Fact]
    public void NormalisesPixelsLikeClip()
    {
        using var red = new Image<Rgba32>(336, 336, new Rgba32(255, 0, 0));
        var values = PhiVisionInputs.PixelValues([red]);
        const int plane = 336 * 336;
        Assert.Equal(2 * 3 * plane, values.Length);
        Assert.Equal((1f - 0.48145466f) / 0.26862954f, values[0], 4);
        Assert.Equal((0f - 0.4578275f) / 0.26130258f, values[plane], 4);
        Assert.Equal((0f - 0.40821073f) / 0.27577711f, values[2 * plane + plane - 1], 4);
        // The overview and the single crop are the same picture.
        Assert.Equal(values[..(3 * plane)], values[(3 * plane)..]);
    }

    [Fact]
    public void LetterboxesOntoWhite()
    {
        using var wide = new Image<Rgba32>(100, 50, new Rgba32(0, 0, 0));
        using var square = PhiVisionInputs.Square(wide);
        Assert.Equal(336, square.Width);
        Assert.Equal(336, square.Height);
        Assert.Equal(new Rgba32(255, 255, 255), square[168, 2]);    // band above the picture
        Assert.Equal(new Rgba32(0, 0, 0), square[168, 168]);
        using var clear = new Image<Rgba32>(40, 40, new Rgba32(0, 0, 0, 0));
        using var flattened = PhiVisionInputs.Square(clear);
        Assert.Equal(new Rgba32(255, 255, 255), flattened[168, 168]);  // transparency becomes white
    }
}
