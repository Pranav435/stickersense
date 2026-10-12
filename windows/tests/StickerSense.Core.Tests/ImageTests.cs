using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit.Abstractions;

namespace StickerSense.Core.Tests;

public class ImagePrepTests
{
    [Fact]
    public void FlattensTransparencyOntoWhite()
    {
        using var sticker = Fixtures.Sticker(Art.Dog);
        Assert.Equal(0, sticker[0, 0].A);
        using var flat = ImagePrep.Flattened(sticker);
        // The corner was transparent; it must now be white, not black.
        var thumb = new Thumbprint(flat);
        Assert.True(thumb.Rgb[0] > 240 && thumb.Rgb[1] > 240 && thumb.Rgb[2] > 240);
        Assert.Equal(255, flat[0, 0].A);
    }

    [Fact]
    public void DownscalesButNeverUpscales()
    {
        using var big = Fixtures.Sticker(Art.Dog, size: 1600);
        using var scaled = ImagePrep.Downscaled(big, 768);
        Assert.Equal(768, Math.Max(scaled.Width, scaled.Height));
        using var small = Fixtures.Sticker(Art.Dog, size: 200);
        using var same = ImagePrep.Downscaled(small, 768);
        Assert.Equal(200, same.Width);
    }

    [Fact]
    public void SamplesFramesEvenly()
    {
        Assert.Equal([0], ImagePrep.SampleIndices(1, 4));
        Assert.Equal([0, 1, 2], ImagePrep.SampleIndices(3, 4));
        Assert.Equal([0, 10, 19, 29], ImagePrep.SampleIndices(30, 4));
        Assert.Equal([0], ImagePrep.SampleIndices(30, 1));
        Assert.Empty(ImagePrep.SampleIndices(0, 4));
    }

    [Fact]
    public void GifRoundTripKeepsFrames()
    {
        var frames = Fixtures.AnimatedFrames();
        var data = ImagePrep.GifData(frames);
        Assert.Equal("gif", ImagePrep.DetectExtension(data));
        var decoded = ImagePrep.LoadFrames(data, maxFrames: 4);
        Assert.Equal(3, decoded.Count);
        Assert.All(decoded, f => Assert.Equal(512, f.Width));
    }

    [Fact]
    public void LoadsLongAnimationsAsFourSpreadFrames()
    {
        var frames = Enumerable.Range(0, 12).Select(i => Fixtures.Sticker(Art.Cat, artX: 0.02f + i * 0.03f, size: 128)).ToList();
        var decoded = ImagePrep.LoadFrames(ImagePrep.GifData(frames), maxFrames: 4);
        Assert.Equal(4, decoded.Count);
    }

    [Fact]
    public void ReadsWebPStickers()
    {
        var dir = Fixtures.TempDirectory();
        var path = Path.Combine(dir, "sticker.webp");
        using (var sticker = Fixtures.Sticker(Art.Frog, "hmm"))
        {
            sticker.SaveAsWebp(path, new WebpEncoder { FileFormat = WebpFileFormatType.Lossless });
        }
        var frames = ImagePrep.LoadFrames(path);
        Assert.Single(frames);
        Assert.Equal(1, ImagePrep.FrameCount(path));
        Assert.Equal(0, frames[0][0, 0].A); // transparency survives
    }

    [Fact]
    public void MakesWebPStickersPasteable()
    {
        var dir = Fixtures.TempDirectory();
        var still = Path.Combine(dir, "still.webp");
        using (var sticker = Fixtures.Sticker(Art.Fox)) sticker.SaveAsWebp(still);
        Assert.EndsWith(".png", ImagePrep.ShareableFile(still, Path.Combine(dir, "out")));

        var animated = Path.Combine(dir, "moving.webp");
        var frames = Fixtures.AnimatedFrames();
        using (var movie = frames[0].Clone())
        {
            foreach (var frame in frames.Skip(1)) movie.Frames.AddFrame(frame.Frames.RootFrame);
            foreach (var frame in movie.Frames) frame.Metadata.GetWebpMetadata().FrameDelay = 400;
            movie.SaveAsWebp(animated);
        }
        var gif = ImagePrep.ShareableFile(animated, Path.Combine(dir, "out"));
        Assert.EndsWith(".gif", gif);
        Assert.Equal(3, ImagePrep.FrameCount(gif));
        using var decoded = SixLabors.ImageSharp.Image.Load(gif);
        Assert.Equal(40, decoded.Frames[1].Metadata.GetGifMetadata().FrameDelay);

        var png = Path.Combine(dir, "plain.png");
        Assert.Equal(png, ImagePrep.ShareableFile(png, dir)); // already fine, untouched
    }

    [Fact]
    public void RejectsGarbage()
    {
        Assert.Throws<ImagePrepException>(() => ImagePrep.LoadFrames("not an image"u8.ToArray()));
        Assert.Null(ImagePrep.DetectExtension("nope"u8));
    }

    [Fact]
    public void DistinctFramesDropsDuplicates()
    {
        using var still = Fixtures.Sticker(Art.Dog, "idk");
        Assert.Single(ImagePrep.DistinctFrames([still, still, still]));
        Assert.Equal(3, ImagePrep.DistinctFrames(Fixtures.AnimatedFrames()).Count);
    }

    [Fact]
    public void DetectsSmallMotion()
    {
        using var a = Fixtures.Sticker(Art.Wave, "bye", artX: 0.12f);
        using var b = Fixtures.Sticker(Art.Wave, "bye", artX: 0.16f);
        Assert.True(new MotionSignature(a).Differs(new MotionSignature(b)));
        Assert.False(new MotionSignature(a).Differs(new MotionSignature(a)));
    }

    [Fact]
    public void PreparesForModelFlatAndSized()
    {
        using var big = Fixtures.Sticker(Art.Party, size: 1024);
        using var prepared = ImagePrep.PrepareForModel(big);
        Assert.Equal(768, prepared.Width);
        Assert.Equal(255, prepared[0, 0].A);
        Assert.True(prepared[0, 0].R > 240);
    }
}

public class FingerprintTests(ITestOutputHelper output)
{
    [Fact]
    public void SameStickerAtDifferentSizeMatches()
    {
        using var original = Fixtures.Sticker(Art.Dog, "idk lol");
        using var onScreen = Fixtures.OnScreenCapture(original, 0.45);
        Fingerprint a = new(original), b = new(onScreen);
        Assert.True(a.IsSameSticker(b), $"distance {a.HammingDistance(b)}, colour {a.Thumb.Difference(b.Thumb):0.0000}");
    }

    [Fact]
    public void DifferentStickersDoNotMatch()
    {
        var arts = new[]
        {
            Art.Joy, Art.Sob, Art.SweatSmile, Art.Rofl, Art.Blush, Art.HeartEyes, Art.SmilingTear, Art.Laughing, Art.SlightSmile,
            Art.Grinning, Art.Dog, Art.Cat, Art.Bear, Art.Fox, Art.Cake, Art.Fire, Art.RedHeart, Art.OrangeHeart,
        };
        var prints = arts.Select(art =>
        {
            using var image = Fixtures.Sticker(art);
            return new Fingerprint(image);
        }).ToArray();
        var closest = (Distance: int.MaxValue, Colour: 1.0, Pair: "");
        for (var i = 0; i < arts.Length; i++)
        {
            for (var j = i + 1; j < arts.Length; j++)
            {
                int d = prints[i].HammingDistance(prints[j]);
                double t = prints[i].Thumb.Difference(prints[j].Thumb);
                if (d < closest.Distance) closest = (d, t, $"{arts[i]} vs {arts[j]}");
                Assert.False(prints[i].IsSameSticker(prints[j]), $"{arts[i]} vs {arts[j]}: distance {d}, colour {t:0.0000}");
            }
        }
        output.WriteLine($"closest different pair: {closest}");
    }

    [Fact]
    public void NearIdenticalFacesDoNotMatch()
    {
        // Same yellow face; only the eyes or mouth differ, like 🙂 vs 😊 or 😂 vs 😅.
        foreach (var (x, y) in new[] { (Art.SlightSmile, Art.SmilingTear), (Art.Joy, Art.SweatSmile), (Art.Joy, Art.Rofl), (Art.Blush, Art.Party), (Art.Cat, Art.HeartEyesCat) })
        {
            using var a = Fixtures.Sticker(x);
            using var b = Fixtures.Sticker(y);
            Fingerprint fa = new(a), fb = new(b);
            output.WriteLine($"{x} vs {y}: distance {fa.HammingDistance(fb)}, colour {fa.Thumb.Difference(fb.Thumb):0.0000}");
            Assert.False(fa.IsSameSticker(fb), $"{x} vs {y}");
        }
    }

    [Fact]
    public void SameStickerAcrossScalesIsStable()
    {
        using var original = Fixtures.Sticker(Art.Party, "yay", captionColor: Color.Black);
        var worst = (Distance: 0, Colour: 0.0);
        var a = new Fingerprint(original);
        foreach (var scale in new[] { 0.25, 0.4, 0.6, 0.8, 1.0 })
        {
            using var capture = Fixtures.OnScreenCapture(original, scale);
            var b = new Fingerprint(capture);
            worst = (Math.Max(worst.Distance, a.HammingDistance(b)), Math.Max(worst.Colour, a.Thumb.Difference(b.Thumb)));
            Assert.True(a.IsSameSticker(b), $"scale {scale}: {a.HammingDistance(b)} / {a.Thumb.Difference(b.Thumb):0.0000}");
        }
        output.WriteLine($"worst same-sticker distance: {worst}");
    }

    [Fact]
    public void PositionAndPaddingDoNotMatter()
    {
        using var original = Fixtures.Sticker(Art.Fox, "hey");
        var a = new Fingerprint(original);
        using var loose = Fixtures.Padded(original, canvasSize: 420, stickerSize: 300, x: 90, y: 20);
        var b = new Fingerprint(loose);
        Assert.True(a.IsSameSticker(b), $"{a.HammingDistance(b)} / {a.Thumb.Difference(b.Thumb):0.0000}");
    }

    [Fact]
    public void ColourVariantsDoNotMatch()
    {
        using var red = Fixtures.Sticker(Art.RedHeart, background: Color.ParseHex("FF3B30"));
        using var blue = Fixtures.Sticker(Art.RedHeart, background: Color.ParseHex("007AFF"));
        Assert.False(new Fingerprint(red).IsSameSticker(new Fingerprint(blue)));
    }

    [Fact]
    public void DifferentCaptionOnSameArtIsClose()
    {
        // Packs reuse one drawing with different words; the fingerprint alone may not tell them
        // apart, which is why the library also compares OCR text.
        using var morning = Fixtures.Sticker(Art.Sun, "Good morning", captionColor: Color.Black);
        using var night = Fixtures.Sticker(Art.Sun, "Good night", captionColor: Color.Black);
        Fingerprint a = new(morning), b = new(night);
        output.WriteLine($"distance {a.HammingDistance(b)}, colour {a.Thumb.Difference(b.Thumb):0.0000}");
        Assert.True(a.HammingDistance(b) < 64);
    }

    [Fact]
    public void HammingDistanceCountsBits()
    {
        var thumb = new Thumbprint(new byte[768]);
        var a = new Fingerprint([0, 0, 0, 0], thumb);
        var b = new Fingerprint([0b1011, 0, ulong.MaxValue, 0], thumb);
        Assert.Equal(67, a.HammingDistance(b));
        Assert.Equal(int.MaxValue, a.HammingDistance(new Fingerprint([0], thumb)));
        Assert.Equal(0, thumb.Difference(new Thumbprint(new byte[768])));
        Assert.Equal(1, thumb.Difference(new Thumbprint([])));
    }
}
