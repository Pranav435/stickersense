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
        // Three frames side by side: one crop each plus one overview.
        Assert.Equal(601, PhiVisionInputs.Tokens(1, 3));
    }

    [Fact]
    public void ChoosesCropGrids()
    {
        Assert.Equal((1, 1), PhiVisionInputs.Grid(512, 512));
        Assert.Equal((1, 1), PhiVisionInputs.Grid(400, 300));
        Assert.Equal((1, 3), PhiVisionInputs.Grid(1008, 336));
        Assert.Equal((2, 1), PhiVisionInputs.Grid(200, 400));
        Assert.Equal((1, 4), PhiVisionInputs.Grid(3000, 100)); // capped
    }

    [Fact]
    public void ReplacesImageTagsWithPlaceholders()
    {
        var prompt = PhiVisionPrompt.Build("Be brief.", "Describe.", 2);
        var chunks = PhiVisionInputs.TextChunks(prompt);
        Assert.Equal(["<|system|>\nBe brief.<|end|>\n<|user|>\n", "\n", "\nDescribe.<|end|>\n<|assistant|>\n"], chunks);

        var ids = PhiVisionInputs.InputIds([[1, 2], [3], [4, 5]], [313, 601]);
        Assert.Equal(5 + 313 + 601, ids.Length);
        Assert.Equal([1, 2], ids[..2]);
        Assert.All(ids[2..315], id => Assert.Equal(-1, id));
        Assert.Equal(3, ids[315]);
        Assert.All(ids[316..917], id => Assert.Equal(-2, id));
        Assert.Equal([4, 5], ids[917..]);
        Assert.Throws<ArgumentException>(() => PhiVisionInputs.InputIds([[1], [2]], [313, 313]));
    }

    [Fact]
    public void NormalisesPixelsLikeClip()
    {
        using var red = new Image<Rgba32>(336, 336, new Rgba32(255, 0, 0));
        var batch = PhiVisionInputs.Prepare([red]);
        const int plane = 336 * 336;
        Assert.Equal(2, batch.Slots);
        Assert.Equal(2 * 3 * plane, batch.PixelValues.Length);
        Assert.Equal([336L, 336L], batch.ImageSizes);
        Assert.Equal([313L], batch.TokenCounts);
        var values = batch.PixelValues;
        Assert.Equal((1f - 0.48145466f) / 0.26862954f, values[0], 4);
        Assert.Equal((0f - 0.4578275f) / 0.26130258f, values[plane], 4);
        Assert.Equal((0f - 0.40821073f) / 0.27577711f, values[2 * plane + plane - 1], 4);
        // The overview and the single crop are the same picture.
        Assert.Equal(values[..(3 * plane)], values[(3 * plane)..]);
    }

    [Fact]
    public void SplitsStripsIntoOneCropPerFrame()
    {
        using var red = new Image<Rgba32>(100, 100, new Rgba32(255, 0, 0));
        using var blue = new Image<Rgba32>(100, 100, new Rgba32(0, 0, 255));
        using var strip = ImagePrep.SideBySide([red, blue, red], 336);
        Assert.Equal(1008, strip.Width);
        using var single = new Image<Rgba32>(200, 200, new Rgba32(0, 255, 0));
        var batch = PhiVisionInputs.Prepare([strip, single]);
        const int plane = 336 * 336;
        Assert.Equal(4, batch.Slots); // overview + 3 crops; the single picture is padded
        Assert.Equal([336L, 1008L, 336L, 336L], batch.ImageSizes);
        Assert.Equal([601L, 313L], batch.TokenCounts);
        float Blue(int slot) => batch.PixelValues[slot * 3 * plane + 2 * plane + 168 * 336 + 168];
        Assert.True(Blue(2) > 1.5f);   // the second frame's crop is blue in the middle
        Assert.True(Blue(1) < 0f);     // the first is red
        // Padding slots of the second picture stay zero.
        Assert.All(batch.PixelValues[((4 + 2) * 3 * plane)..((4 + 4) * 3 * plane)], v => Assert.Equal(0f, v));
    }

    [Fact]
    public void LetterboxesOntoWhite()
    {
        using var wide = new Image<Rgba32>(100, 50, new Rgba32(0, 0, 0));
        using var square = ImagePrep.Fit(wide, 336, 336);
        Assert.Equal(336, square.Width);
        Assert.Equal(336, square.Height);
        Assert.Equal(new Rgba32(255, 255, 255), square[168, 2]);    // band above the picture
        Assert.Equal(new Rgba32(0, 0, 0), square[168, 168]);
        using var clear = new Image<Rgba32>(40, 40, new Rgba32(0, 0, 0, 0));
        using var flattened = ImagePrep.Fit(clear, 336, 336);
        Assert.Equal(new Rgba32(255, 255, 255), flattened[168, 168]);  // transparency becomes white
    }
}
