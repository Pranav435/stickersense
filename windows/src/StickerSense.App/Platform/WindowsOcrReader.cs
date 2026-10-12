using System.Runtime.InteropServices.WindowsRuntime;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using StickerSense.Core;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using OcrLine = StickerSense.Core.OcrLine;

namespace StickerSense.App.Platform;

/// <summary>
/// On-device OCR with Windows.Media.Ocr. Stickers very often carry the actual message as text
/// ("idk", "Happy birthday!"); OCR spells it far more reliably than the vision model, so the
/// sticker's words come from here, with chat chrome (timestamps, ticks) removed.
/// Windows.Media.Ocr does no spelling or language correction, so sticker slang ("idk", "lmaooo")
/// is kept as written.
/// </summary>
internal sealed class WindowsOcrReader : IOcrReader
{
    private readonly OcrEngine? engine;

    public WindowsOcrReader()
    {
        try
        {
            engine = OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            engine = null;
        }
    }

    /// <summary>
    /// The first recognition loads the engine and can be slow; run one on a tiny image at startup
    /// (in the background) so the user never waits for that.
    /// </summary>
    public Task WarmUpAsync() => Task.Run(async () =>
    {
        using var tiny = new Image<Rgba32>(64, 32, new Rgba32(255, 255, 255));
        await ReadLinesAsync(tiny);
    });

    /// <summary>False when no OCR language is installed for the user's languages.</summary>
    public bool IsAvailable => engine is not null;

    public string LanguageName => engine?.RecognizerLanguage.DisplayName ?? "";

    /// <summary>Recognised lines with their boxes as fractions of the image (so they line up with the original capture).</summary>
    public async Task<IReadOnlyList<OcrLine>> ReadLinesAsync(Image<Rgba32> image, CancellationToken ct = default)
    {
        if (engine is null) return [];
        try
        {
            using var bitmap = await Task.Run(() => ToSoftwareBitmap(image, OcrEngine.MaxImageDimension), ct);
            var result = await engine.RecognizeAsync(bitmap).AsTask(ct);
            var lines = new List<OcrLine>();
            foreach (var line in result.Lines)
            {
                if (line.Words.Count == 0) continue;
                double left = line.Words.Min(w => w.BoundingRect.X), top = line.Words.Min(w => w.BoundingRect.Y);
                double right = line.Words.Max(w => w.BoundingRect.X + w.BoundingRect.Width);
                double bottom = line.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
                lines.Add(new OcrLine(line.Text, NormalizedBox.FromPixels(left, top, right - left, bottom - top, bitmap.PixelWidth, bitmap.PixelHeight)));
            }
            return lines;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return []; // OCR is only a hint
        }
    }

    /// <summary>Small stickers are enlarged so their lettering is big enough for OCR.</summary>
    private static SoftwareBitmap ToSoftwareBitmap(Image<Rgba32> image, uint maxDimension)
    {
        var longest = Math.Max(image.Width, image.Height);
        var scale = longest < 600 ? 2.0 : 1.0;
        if (longest * scale > maxDimension) scale = maxDimension / (double)longest;
        using var flat = ImagePrep.Flattened(image);
        using var bgra = flat.CloneAs<Bgra32>();
        if (Math.Abs(scale - 1) > 0.01)
        {
            bgra.Mutate(ctx => ctx.Resize((int)(image.Width * scale), (int)(image.Height * scale), KnownResamplers.Bicubic));
        }
        var bytes = new byte[bgra.Width * bgra.Height * 4];
        bgra.CopyPixelDataTo(bytes);
        return SoftwareBitmap.CreateCopyFromBuffer(bytes.AsBuffer(), BitmapPixelFormat.Bgra8, bgra.Width, bgra.Height, BitmapAlphaMode.Premultiplied);
    }
}
