using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace StickerSense.Core;

/// <summary>The result of a quick look at one frame: enough to answer from the cache instantly.</summary>
public sealed record StickerProbe(Fingerprint Fingerprint, string OcrText, StickerRecord? Cached)
{
    /// <summary>
    /// Where the chat's own text (sender name, time) sits in the capture. Paint these out with
    /// <see cref="StickerPipeline.Mask"/> before describing or saving.
    /// </summary>
    public IReadOnlyList<NormalizedBox> ChromeBoxes { get; init; } = [];
}

/// <summary>
/// Ties describing, OCR, caching and the library together, so every entry point (shortcut,
/// auto mode, picker, imports) behaves the same way. Create one per priority; they can share
/// the describer and library.
/// </summary>
public sealed class StickerPipeline
{
    public StickerPipeline(IStickerDescriber describer, StickerLibrary library, IOcrReader ocr, DescribePriority priority = DescribePriority.Foreground)
    {
        Describer = describer;
        Library = library;
        Ocr = ocr;
        Priority = priority;
    }

    public IStickerDescriber Describer { get; }
    public StickerLibrary Library { get; }
    public IOcrReader Ocr { get; }
    public DescribePriority Priority { get; }

    /// <summary>
    /// OCR + fingerprint + cache lookup. Fast — run it before capturing more frames. For a screen
    /// capture, the screen reader label (when it says "sticker from Ana…") is used to find the
    /// sender's name and the time, which are painted out before fingerprinting, so the same
    /// sticker matches whoever sent it.
    /// </summary>
    public async Task<StickerProbe> ProbeAsync(Image<Rgba32> firstFrame, DescriptionContext context, CancellationToken ct = default)
    {
        using var flat = await Task.Run(() => ImagePrep.Flattened(firstFrame), ct).ConfigureAwait(false);
        var lines = await Ocr.ReadLinesAsync(flat, ct).ConfigureAwait(false);
        var label = LabelClassifier.ChromeLabel(context.Label);
        IReadOnlyList<NormalizedBox> boxes = context is DescriptionContext.ScreenItem ? StickerText.ChromeBoxes(lines, label) : [];
        var fingerprint = await Task.Run(() =>
        {
            using var masked = ImagePrep.Masking(flat, boxes);
            return new Fingerprint(masked);
        }, ct).ConfigureAwait(false);
        var text = StickerText.TextOf(lines, label);
        return new StickerProbe(fingerprint, text, Library.Match(fingerprint, text)) { ChromeBoxes = boxes };
    }

    /// <summary>Copies of the frames with the probe's chat text painted out (the caller disposes them).</summary>
    public static List<Image<Rgba32>> Mask(IReadOnlyList<Image<Rgba32>> frames, StickerProbe probe) =>
        frames.Select(frame => ImagePrep.Masking(frame, probe.ChromeBoxes)).ToList();

    /// <summary>
    /// Describes (or reuses the cached description for) a sticker and records it. When a probe is
    /// passed, the frames are expected to be masked already (<see cref="Mask"/>).
    /// </summary>
    public async Task<StickerRecord> DescribeAsync(
        IReadOnlyList<Image<Rgba32>> frames,
        StickerProbe? probe,
        DescriptionContext context,
        StickerSource source,
        string? remoteUrl = null,
        CancellationToken ct = default)
    {
        if (frames.Count == 0) throw DescribeException.Failed("no image");
        List<Image<Rgba32>>? owned = null;
        try
        {
            if (probe is null)
            {
                probe = await ProbeAsync(frames[0], context, ct).ConfigureAwait(false);
                if (probe.ChromeBoxes.Count > 0) frames = owned = Mask(frames, probe);
            }
            var reading = probe.Cached?.Reading;
            if (reading is null)
            {
                var ocr = await MergeLaterFramesAsync(frames, probe.OcrText, context, ct).ConfigureAwait(false);
                reading = await Describer.ReadAsync(frames, context, ocr, Priority, ct).ConfigureAwait(false);
            }
            var probed = probe;
            return await Task.Run(() => Library.Remember(frames, probed.Fingerprint, reading, probed.OcrText, source, remoteUrl), CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            if (owned is not null) foreach (var frame in owned) frame.Dispose();
        }
    }

    /// <summary>
    /// A quick reading of one still (masked) frame, using the probe's OCR. Started speculatively
    /// while motion detection runs: most stickers are still.
    /// </summary>
    public Task<StickerReading> ReadStillAsync(Image<Rgba32> frame, StickerProbe probe, DescriptionContext context, CancellationToken ct = default) =>
        Describer.ReadAsync([frame], context, probe.OcrText, Priority, ct);

    /// <summary>Stores a reading the caller produced itself (frames already masked), replacing any older reading.</summary>
    public Task<StickerRecord> StoreAsync(IReadOnlyList<Image<Rgba32>> frames, StickerProbe probe, StickerReading reading, StickerSource source) =>
        Task.Run(() => Library.Remember(frames, probe.Fingerprint, reading, probe.OcrText, source, replaceReading: true));

    /// <summary>A reading of frames that don't go into the library (GIPHY results): OCR of every frame, then the model.</summary>
    public async Task<StickerReading> ReadAsync(IReadOnlyList<Image<Rgba32>> frames, DescriptionContext context, CancellationToken ct = default)
    {
        var ocr = await OcrTextAsync(frames, context, ct).ConfigureAwait(false);
        return await Describer.ReadAsync(frames, context, ocr, Priority, ct).ConfigureAwait(false);
    }

    /// <summary>Text on the sticker across all frames: words often appear partway through an animation.</summary>
    public async Task<string> OcrTextAsync(IReadOnlyList<Image<Rgba32>> frames, DescriptionContext context, CancellationToken ct = default)
    {
        if (frames.Count == 0) return "";
        var first = await FrameTextAsync(frames[0], LabelClassifier.ChromeLabel(context.Label), ct).ConfigureAwait(false);
        return await MergeLaterFramesAsync(frames, first, context, ct).ConfigureAwait(false);
    }

    private async Task<string> MergeLaterFramesAsync(IReadOnlyList<Image<Rgba32>> frames, string firstText, DescriptionContext context, CancellationToken ct)
    {
        var text = firstText;
        var label = LabelClassifier.ChromeLabel(context.Label);
        foreach (var frame in frames.Skip(1))
        {
            text = StickerText.MergeLines(text, await FrameTextAsync(frame, label, ct).ConfigureAwait(false));
        }
        return text;
    }

    private async Task<string> FrameTextAsync(Image<Rgba32> frame, string? label, CancellationToken ct)
    {
        using var flat = await Task.Run(() => ImagePrep.Flattened(frame), ct).ConfigureAwait(false);
        return StickerText.TextOf(await Ocr.ReadLinesAsync(flat, ct).ConfigureAwait(false), label);
    }

    /// <summary>Detailed description, cached on the record after the first time.</summary>
    public async Task<string> DetailAsync(StickerRecord record, IReadOnlyList<Image<Rgba32>>? frames, DescriptionContext context, CancellationToken ct = default)
    {
        if (Library.Record(record.Id)?.Detailed is { Length: > 0 } cached) return cached;
        var owned = new List<Image<Rgba32>>();
        try
        {
            var images = frames;
            if ((images is null || images.Count == 0) && Library.ImagePath(record) is { } path && File.Exists(path))
            {
                owned = await Task.Run(() => LoadDistinct(path), ct).ConfigureAwait(false);
                images = owned;
            }
            if (images is null || images.Count == 0) throw DescribeException.Failed("the picture is no longer available");
            var text = await Describer.DetailedAsync(images, context, record.OcrText, record.Reading, Priority, ct).ConfigureAwait(false);
            Library.SetDetailed(text, record.Id);
            return text;
        }
        finally
        {
            foreach (var image in owned) image.Dispose();
        }
    }

    /// <summary>Describes a library record that was imported without a description (WhatsApp).</summary>
    public async Task<StickerRecord> DescribeStoredAsync(StickerRecord record, CancellationToken ct = default)
    {
        if (record.Reading is not null) return record;
        var path = Library.ImagePath(record);
        if (path is null || !File.Exists(path)) throw DescribeException.Failed("missing file");
        List<Image<Rgba32>> frames;
        try
        {
            frames = await Task.Run(() => LoadDistinct(path), ct).ConfigureAwait(false);
        }
        catch (ImagePrepException)
        {
            throw DescribeException.Failed("unreadable file");
        }
        try
        {
            var firstText = await FrameTextAsync(frames[0], null, ct).ConfigureAwait(false);
            var ocr = await MergeLaterFramesAsync(frames, firstText, DescriptionContext.File, ct).ConfigureAwait(false);
            var reading = await Describer.ReadAsync(frames, DescriptionContext.File, ocr, Priority, ct).ConfigureAwait(false);
            Library.SetReading(reading, firstText, record.Id);
            // An animation: remember frames across the loop, so it's recognised whatever a chat shows.
            if (ImagePrep.FrameCount(path) > 1)
            {
                var all = await Task.Run(() => ImagePrep.LoadFrames(path, maxFrames: 16), ct).ConfigureAwait(false);
                try
                {
                    Library.SetFrameFingerprints(all, record.Id);
                }
                finally
                {
                    foreach (var frame in all) frame.Dispose();
                }
            }
            return Library.Record(record.Id) ?? record with { Reading = reading, OcrText = firstText };
        }
        finally
        {
            foreach (var frame in frames) frame.Dispose();
        }
    }

    /// <summary>Up to four frames of a file, dropping repeats (static images give one).</summary>
    public static List<Image<Rgba32>> LoadDistinct(string path)
    {
        var frames = ImagePrep.LoadFrames(path, VisionLanguageDescriber.MaxFrames);
        var kept = ImagePrep.DistinctFrames(frames);
        foreach (var frame in frames.Except(kept)) frame.Dispose();
        return kept;
    }
}
