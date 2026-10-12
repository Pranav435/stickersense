using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace StickerSense.Core;

/// <summary>A rectangle as fractions (0…1) of an image's width and height, top-left origin.</summary>
public readonly record struct NormalizedBox(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public static NormalizedBox FromPixels(double x, double y, double width, double height, int imageWidth, int imageHeight) =>
        new(x / imageWidth, y / imageHeight, width / imageWidth, height / imageHeight);
}

/// <summary>One line of recognised text and where it is in the image.</summary>
public sealed record OcrLine(string Text, NormalizedBox Box);

/// <summary>Reads the words written in an image (Windows.Media.Ocr in the app). Returns raw lines; see <see cref="StickerText"/> for filtering.</summary>
public interface IOcrReader
{
    Task<IReadOnlyList<OcrLine>> ReadLinesAsync(Image<Rgba32> image, CancellationToken ct = default);
}

public sealed class NullOcrReader : IOcrReader
{
    public Task<IReadOnlyList<OcrLine>> ReadLinesAsync(Image<Rgba32> image, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OcrLine>>([]);
}

/// <summary>
/// Gives up on OCR after <see cref="Timeout"/> and returns no lines, rather than make the user wait
/// (the first recognition can be slow while the engine loads). The recognition itself carries on
/// in the background and warms the engine up for the next time.
/// </summary>
public sealed class TimeLimitedOcrReader(IOcrReader inner, TimeSpan timeout) : IOcrReader
{
    public TimeSpan Timeout { get; } = timeout;

    public async Task<IReadOnlyList<OcrLine>> ReadLinesAsync(Image<Rgba32> image, CancellationToken ct = default)
    {
        // The recognition may outlive this call, and the caller disposes its image: give it a copy.
        var copy = image.Clone();
        var recognise = RecogniseAsync(copy);
        var finished = await Task.WhenAny(recognise, Task.Delay(Timeout, ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (finished != recognise) return [];
        try
        {
            return await recognise.ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<OcrLine>> RecogniseAsync(Image<Rgba32> copy)
    {
        try
        {
            return await inner.ReadLinesAsync(copy, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            copy.Dispose();
        }
    }
}
