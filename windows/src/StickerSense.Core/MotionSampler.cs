using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace StickerSense.Core;

/// <summary>Captures one region of the screen. Throws <see cref="CaptureException"/> with a spoken message.</summary>
public interface IScreenCapturer
{
    Task<Image<Rgba32>> CaptureAsync(PixelRect rect, CancellationToken ct = default);
}

/// <summary>Finds out whether a captured region is animated, by capturing it a few more times.</summary>
public static class MotionSampler
{
    /// <summary>
    /// Returns 1 frame for a still image, or up to 3 distinct frames for an animated one: two quick
    /// looks 150 ms apart to see whether it moves (most stickers don't — and a chat shows a GIF as
    /// a still preview until it's opened, so GIFs get the check too), then up to two more 200 ms
    /// apart, so three frames span about 0.7 s of a sticker's loop. GIFs are longer clips: their
    /// two extra looks are 550 ms apart, so the model sees the scene change.
    /// <paramref name="first"/> is reused as the first frame. Frames that aren't returned are disposed.
    /// </summary>
    public static async Task<List<Image<Rgba32>>> CaptureMotionAsync(
        IScreenCapturer capturer,
        PixelRect rect,
        Image<Rgba32> first,
        bool alreadyKnownAnimated,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        CancellationToken ct = default)
    {
        const int maxFrames = 3;
        delay ??= Task.Delay;
        var frames = new List<Image<Rgba32>> { first };
        var signatures = new List<MotionSignature> { new(first) };
        // Even for GIFs, look first: a chat shows a still preview until a GIF is opened.
        var animated = false;
        try
        {
            for (var i = 0; i < 2 && !animated; i++)
            {
                await delay(TimeSpan.FromMilliseconds(150), ct).ConfigureAwait(false);
                var frame = await capturer.CaptureAsync(rect, ct).ConfigureAwait(false);
                var signature = new MotionSignature(frame);
                if (signature.Differs(signatures[0]))
                {
                    animated = true;
                    frames.Add(frame);
                    signatures.Add(signature);
                }
                else
                {
                    frame.Dispose();
                }
            }
            if (!animated) return frames;
            // It moves: one or two more looks a little later.
            for (var attempts = 0; frames.Count < maxFrames && attempts < (alreadyKnownAnimated ? 2 : 3); attempts++)
            {
                await delay(TimeSpan.FromMilliseconds(alreadyKnownAnimated ? 550 : 200), ct).ConfigureAwait(false);
                var frame = await capturer.CaptureAsync(rect, ct).ConfigureAwait(false);
                var signature = new MotionSignature(frame);
                if (signatures.All(signature.Differs))
                {
                    frames.Add(frame);
                    signatures.Add(signature);
                }
                else
                {
                    frame.Dispose();
                }
            }
            return frames;
        }
        catch
        {
            foreach (var frame in frames.Skip(1)) frame.Dispose(); // the caller still owns `first`
            throw;
        }
    }
}
