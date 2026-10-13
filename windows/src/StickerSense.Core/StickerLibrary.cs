using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace StickerSense.Core;

/// <summary>
/// Persistent store of described stickers. Doubles as the description cache — a sticker seen
/// before is spoken instantly — and as the searchable library behind the picker.
/// Stored as <c>library.json</c> plus an <c>images</c> folder; thread-safe.
/// </summary>
public sealed class StickerLibrary : IDisposable
{
    private readonly object gate = new();
    private readonly object saveGate = new();
    private readonly Dictionary<Guid, StickerRecord> records = [];
    private readonly TimeProvider clock;
    private readonly ITimer saveTimer;
    private bool savePending;

    public StickerLibrary(string directory, TimeProvider? clock = null)
    {
        Directory = directory;
        this.clock = clock ?? TimeProvider.System;
        System.IO.Directory.CreateDirectory(ImagesDirectory);
        saveTimer = this.clock.CreateTimer(_ => SaveNow(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Load();
    }

    public string Directory { get; }
    public string ImagesDirectory => Path.Combine(Directory, "images");
    private string FilePath => Path.Combine(Directory, "library.json");

    /// <summary>Raised (on the calling thread) after any change.</summary>
    public event EventHandler? Changed;

    /// <summary>Default location: <c>%LOCALAPPDATA%\StickerSense</c>.</summary>
    public static string DefaultDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StickerSense");

    public int Count
    {
        get { lock (gate) return records.Count; }
    }

    public StickerRecord? Record(Guid id)
    {
        lock (gate) return records.GetValueOrDefault(id);
    }

    public string? ImagePath(StickerRecord record) =>
        record.ImageFile is { } file ? Path.Combine(ImagesDirectory, file) : null;

    // MARK: Cache lookups

    /// <summary>
    /// Finds a previously described sticker that looks the same. When either has OCR text, the
    /// text must agree too — sticker packs often reuse one drawing with different words.
    /// </summary>
    public StickerRecord? Match(Fingerprint fingerprint, string ocrText)
    {
        lock (gate) return MatchLocked(fingerprint, ocrText);
    }

    private StickerRecord? MatchLocked(Fingerprint fingerprint, string ocrText)
    {
        // Every blank capture looks alike: never answer one from the cache.
        if (fingerprint.Thumb.IsUniform) return null;
        var ocr = StickerText.Normalized(ocrText);
        StickerRecord? best = null;
        var bestDistance = int.MaxValue;
        var bestAnimated = false;
        foreach (var record in records.Values)
        {
            if (!record.IsDescribed) continue;
            // The main fingerprint, or — for an animation — whichever remembered frame is showing.
            int distance;
            if (record.Fingerprint.IsSameSticker(fingerprint)) distance = record.Fingerprint.HammingDistance(fingerprint);
            else if (record.FrameFingerprints?.FirstOrDefault(f => f.IsSameSticker(fingerprint)) is { } frame) distance = frame.HammingDistance(fingerprint);
            else continue;
            var animated = record.IsAnimation;
            // Captions of a still must agree; an animation's text changes from frame to frame.
            if (!animated)
            {
                var other = StickerText.Normalized(record.OcrText);
                if ((ocr.Length > 0 || other.Length > 0) && !TextsAgree(ocr, other)) continue;
            }
            // One GIF, one description: prefer the reading made from its motion.
            if (best is null || (animated && !bestAnimated) || (animated == bestAnimated && distance < bestDistance))
            {
                best = record;
                bestDistance = distance;
                bestAnimated = animated;
            }
        }
        return best;
    }

    /// <summary>
    /// A single frame of a GIF StickerSense has already described with its motion — e.g. the
    /// paused preview a chat shows. Remembers the frame so the next lookup is a plain cache hit.
    /// </summary>
    public StickerRecord? MatchAnimationFrame(Fingerprint fingerprint)
    {
        if (fingerprint.Thumb.IsUniform) return null;
        StickerRecord? updated;
        lock (gate)
        {
            StickerRecord? best = null;
            var bestDistance = int.MaxValue;
            foreach (var record in records.Values)
            {
                if (!record.IsDescribed || !(record.Reading!.Kind == StickerKind.Gif || record.Reading.Animated) || !record.IsAnimation) continue;
                foreach (var frame in record.FrameFingerprints!)
                {
                    if (!IsLooseFrameMatch(frame, fingerprint)) continue;
                    var distance = frame.HammingDistance(fingerprint);
                    if (distance < bestDistance)
                    {
                        best = record;
                        bestDistance = distance;
                    }
                }
            }
            if (best is null) return null;
            updated = best with
            {
                FrameFingerprints = best.FrameFingerprints!.Append(fingerprint).TakeLast(MaxFrameFingerprints).ToList(),
                LastSeenAt = clock.GetUtcNow(),
                TimesSeen = best.TimesSeen + 1,
            };
            records[updated.Id] = updated;
        }
        ScheduleSave();
        return updated;
    }

    /// <summary>
    /// Recognises something the user sent from the picker recently ("Your GIF" in a chat), from a
    /// single on-screen frame. Chats re-encode GIFs and overlay badges, so the comparison is looser
    /// than <see cref="Match"/> — safe only because the candidates are the handful of items sent in the last day.
    /// </summary>
    public StickerRecord? MatchRecentlySent(Fingerprint fingerprint, TimeSpan? within = null)
    {
        if (fingerprint.Thumb.IsUniform) return null;
        var cutoff = clock.GetUtcNow() - (within ?? TimeSpan.FromDays(1));
        lock (gate)
        {
            StickerRecord? best = null;
            var bestDistance = int.MaxValue;
            foreach (var record in records.Values)
            {
                if (!record.IsDescribed || record.TimesSent <= 0 || record.LastSeenAt <= cutoff) continue;
                foreach (var stored in record.FrameFingerprints is { } frames ? frames.Prepend(record.Fingerprint) : [record.Fingerprint])
                {
                    var distance = stored.HammingDistance(fingerprint);
                    if (distance <= 22 && stored.Thumb.Difference(fingerprint.Thumb) <= 0.05 && distance < bestDistance)
                    {
                        best = record;
                        bestDistance = distance;
                    }
                }
            }
            return best;
        }
    }

    /// <summary>
    /// Recognises an animated sticker or GIF from several captured frames. Each captured frame may
    /// land anywhere in the loop, and a chat may re-encode it, so single frames are compared more
    /// loosely than stills — but at least two captured frames must each match a stored frame.
    /// </summary>
    public StickerRecord? MatchAnimated(IReadOnlyList<Fingerprint> captured)
    {
        var usable = captured.Where(f => !f.Thumb.IsUniform).ToList();
        if (usable.Count < 2) return null;
        lock (gate)
        {
            StickerRecord? best = null;
            var bestHits = 0;
            foreach (var record in records.Values)
            {
                if (!record.IsDescribed || !record.IsAnimation) continue;
                var stored = record.FrameFingerprints!;
                var hits = usable.Count(frame => stored.Any(s => IsLooseFrameMatch(s, frame)));
                if (hits >= 2 && hits > bestHits)
                {
                    best = record;
                    bestHits = hits;
                }
            }
            return best;
        }
    }

    /// <summary>At most this many frame fingerprints per record.</summary>
    public const int MaxFrameFingerprints = 24;

    /// <summary>
    /// Frames of one animation: compared more loosely than stills, since a chat may re-encode it
    /// and show any point in the loop.
    /// </summary>
    internal static bool IsLooseFrameMatch(Fingerprint a, Fingerprint b) =>
        a.HammingDistance(b) <= 14 && a.Thumb.Difference(b.Thumb) <= 0.02;

    /// <summary>Fingerprints of up to 16 frames spread across an animation.</summary>
    public static List<Fingerprint> FrameFingerprintsOf(IReadOnlyList<Image<Rgba32>> frames) =>
        ImagePrep.SampleIndices(frames.Count, 16).Select(i => new Fingerprint(frames[i])).ToList();

    internal static bool TextsAgree(string a, string b)
    {
        if (a == b) return true;
        if (a.Length == 0 || b.Length == 0) return false;
        // Allow small OCR wobble (a dropped or misread character or two).
        int shorter = Math.Min(a.Length, b.Length), longer = Math.Max(a.Length, b.Length);
        if (longer - shorter > 2) return false;
        return Levenshtein(a, b) <= Math.Max(1, longer / 8);
    }

    internal static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    // MARK: Writing

    /// <summary>Remembers a described sticker (or refreshes an existing one) and returns the stored record.</summary>
    /// <param name="replaceReading">Overwrite an existing record's reading (a fresh description), instead of keeping the old one.</param>
    public StickerRecord Remember(
        IReadOnlyList<Image<Rgba32>> frames,
        Fingerprint fingerprint,
        StickerReading reading,
        string ocrText,
        StickerSource source,
        string? remoteUrl = null,
        bool replaceReading = false)
    {
        // Never cache a blank capture: every blank image looks alike and would "match" forever.
        if (fingerprint.Thumb.IsUniform)
        {
            var now0 = clock.GetUtcNow();
            return new StickerRecord
            {
                Id = Guid.NewGuid(),
                Fingerprint = fingerprint,
                Reading = reading,
                OcrText = ocrText,
                Source = source,
                RemoteUrl = remoteUrl,
                AddedAt = now0,
                LastSeenAt = now0,
                TimesSeen = 1,
                TimesSent = 0,
            };
        }

        var frameFingerprints = frames.Count > 1 ? FrameFingerprintsOf(frames) : null;

        // Find the record first, encode the image without holding the lock, then store.
        StickerRecord? existing;
        lock (gate) existing = FindExistingLocked(fingerprint, ocrText);
        var id = existing?.Id ?? Guid.NewGuid();
        string? savedImage = null;
        if (existing?.ImageFile is null && reading.Kind.IsCollectible())
        {
            savedImage = SaveImage(frames, id, reading.Animated);
        }

        StickerRecord stored;
        var absorbedFiles = new List<string>();
        var now = clock.GetUtcNow();
        lock (gate)
        {
            existing = records.GetValueOrDefault(id) ?? FindExistingLocked(fingerprint, ocrText);
            if (existing is not null)
            {
                stored = existing with
                {
                    Reading = replaceReading ? reading : existing.Reading ?? reading,
                    LastSeenAt = now,
                    TimesSeen = existing.TimesSeen + 1,
                    ImageFile = existing.ImageFile ?? (existing.Id == id ? savedImage : null),
                    FrameFingerprints = frameFingerprints is not null && (existing.FrameFingerprints?.Count ?? 0) < frames.Count
                        ? frameFingerprints
                        : existing.FrameFingerprints,
                };
            }
            else
            {
                stored = new StickerRecord
                {
                    Id = id,
                    Fingerprint = fingerprint,
                    Reading = reading,
                    OcrText = ocrText,
                    Source = source,
                    ImageFile = savedImage,
                    RemoteUrl = remoteUrl,
                    AddedAt = now,
                    LastSeenAt = now,
                    TimesSeen = 1,
                    TimesSent = 0,
                    FrameFingerprints = frameFingerprints,
                };
            }
            records[stored.Id] = stored;
            if (existing is null && frameFingerprints is not null) stored = AbsorbStillCopiesLocked(stored, absorbedFiles);
        }
        foreach (var file in absorbedFiles) TryDelete(Path.Combine(ImagesDirectory, file));
        if (savedImage is not null && stored.ImageFile != savedImage) TryDelete(Path.Combine(ImagesDirectory, savedImage));
        ScheduleSave();
        return stored;
    }

    /// <summary>
    /// An animation first seen paused (a still preview) gets a still-frame description. Once it's
    /// described with its motion, fold those stale GIF copies into the animated record, and keep
    /// their preview frame so the paused preview is recognised directly next time.
    /// </summary>
    private StickerRecord AbsorbStillCopiesLocked(StickerRecord record, List<string> filesToDelete)
    {
        var frames = record.FrameFingerprints!.ToList();
        var timesSeen = record.TimesSeen;
        foreach (var other in records.Values.ToList())
        {
            if (other.Id == record.Id || (other.FrameFingerprints?.Count ?? 0) > 1 || other.Reading?.Kind != StickerKind.Gif) continue;
            if (other.Source is not StickerSource.Received || !frames.Any(f => IsLooseFrameMatch(f, other.Fingerprint))) continue;
            timesSeen += other.TimesSeen;
            frames.Add(other.Fingerprint);
            records.Remove(other.Id);
            if (other.ImageFile is { } file) filesToDelete.Add(file);
        }
        var folded = record with { FrameFingerprints = frames.TakeLast(MaxFrameFingerprints).ToList(), TimesSeen = timesSeen };
        records[folded.Id] = folded;
        return folded;
    }

    /// <summary>Remembers several frames of an animation (e.g. after describing an imported file).</summary>
    public void SetFrameFingerprints(IReadOnlyList<Image<Rgba32>> frames, Guid id)
    {
        if (frames.Count < 2) return;
        var fingerprints = FrameFingerprintsOf(frames);
        Update(id, r => r with { FrameFingerprints = fingerprints });
    }

    private StickerRecord? FindExistingLocked(Fingerprint fingerprint, string ocrText) =>
        MatchLocked(fingerprint, ocrText) ?? records.Values.FirstOrDefault(r => r.Fingerprint.Equals(fingerprint));

    /// <summary>
    /// Adds an image file without describing it yet (bulk imports describe in the background).
    /// Returns null when an identical sticker is already in the library or the file is unreadable.
    /// </summary>
    public StickerRecord? ImportFile(string path, StickerSource source)
    {
        Fingerprint fingerprint;
        try
        {
            var frames = ImagePrep.LoadFrames(path, maxFrames: 1);
            try
            {
                fingerprint = new Fingerprint(frames[0]);
            }
            finally
            {
                foreach (var frame in frames) frame.Dispose();
            }
        }
        catch (ImagePrepException)
        {
            return null;
        }

        lock (gate)
        {
            if (records.Values.Any(r => r.Fingerprint.HammingDistance(fingerprint) <= 2 && r.Fingerprint.Thumb.Difference(fingerprint.Thumb) < 0.02))
            {
                return null;
            }
        }
        var id = Guid.NewGuid();
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        var fileName = $"{UpperId(id)}.{(extension.Length == 0 ? "png" : extension)}";
        try
        {
            File.Copy(path, Path.Combine(ImagesDirectory, fileName));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        var now = clock.GetUtcNow();
        var record = new StickerRecord
        {
            Id = id,
            Fingerprint = fingerprint,
            Source = source,
            ImageFile = fileName,
            AddedAt = now,
            LastSeenAt = now,
        };
        lock (gate) records[id] = record;
        ScheduleSave();
        return record;
    }

    public void SetReading(StickerReading reading, string ocrText, Guid id) =>
        Update(id, r => r with { Reading = reading, OcrText = ocrText });

    public void SetDetailed(string text, Guid id) => Update(id, r => r with { Detailed = text });

    public void MarkSent(Guid id) =>
        Update(id, r => r with { TimesSent = r.TimesSent + 1, LastSeenAt = clock.GetUtcNow() });

    public void Delete(Guid id)
    {
        StickerRecord? removed;
        lock (gate)
        {
            if (!records.Remove(id, out removed)) return;
        }
        if (removed.ImageFile is { } file) TryDelete(Path.Combine(ImagesDirectory, file));
        ScheduleSave();
    }

    private void Update(Guid id, Func<StickerRecord, StickerRecord> change)
    {
        lock (gate)
        {
            if (!records.TryGetValue(id, out var record)) return;
            records[id] = change(record);
        }
        ScheduleSave();
    }

    public IReadOnlyList<StickerRecord> Undescribed(int limit)
    {
        lock (gate)
        {
            return records.Values.Where(r => !r.IsDescribed).OrderByDescending(r => r.AddedAt).Take(limit).ToList();
        }
    }

    // MARK: Reading

    /// <summary>Everything described recently, newest first — the History window.</summary>
    public IReadOnlyList<StickerRecord> History(int limit = 200)
    {
        lock (gate)
        {
            return records.Values
                .Where(r => r.IsDescribed && r.TimesSeen > 0 && r.Source is StickerSource.Received or StickerSource.Imported)
                .OrderByDescending(r => r.LastSeenAt)
                .Take(limit)
                .ToList();
        }
    }

    /// <summary>Picker search across descriptions, sticker text and meaning.</summary>
    public IReadOnlyList<StickerRecord> Search(string query, SourceFilter filter = SourceFilter.All, int limit = 200)
    {
        List<StickerRecord> candidates;
        lock (gate)
        {
            candidates = records.Values
                .Where(r => r.IsCollectible && r.ImageFile is not null && (filter == SourceFilter.All || r.Source.FilterTag == filter))
                .ToList();
        }
        var tokens = StickerText.Tokens(query);
        if (tokens.Count == 0)
        {
            candidates.Sort(RecencyOrder);
            return candidates.Take(limit).ToList();
        }
        return candidates
            .Select(r => (Record: r, Score: Score(r, tokens)))
            .Where(x => x.Score > 0)
            .OrderBy(x => x, Comparer<(StickerRecord Record, double Score)>.Create((a, b) =>
                a.Score != b.Score ? b.Score.CompareTo(a.Score) : RecencyOrder(a.Record, b.Record)))
            .Take(limit)
            .Select(x => x.Record)
            .ToList();
    }

    /// <summary>Most-sent first once something has been sent a few times, otherwise most recently seen first.</summary>
    internal static int RecencyOrder(StickerRecord a, StickerRecord b)
    {
        if (a.TimesSent != b.TimesSent && Math.Max(a.TimesSent, b.TimesSent) >= 3) return b.TimesSent.CompareTo(a.TimesSent);
        return b.LastSeenAt.CompareTo(a.LastSeenAt);
    }

    internal static double Score(StickerRecord record, IReadOnlyList<string> queryTokens)
    {
        var fields = new (string Text, double Weight)[]
        {
            (record.Reading?.Text ?? "", 3),
            (record.OcrText, 2),
            (record.Reading?.Subject ?? "", 2),
            (record.Reading?.Vibe ?? "", 2),
            (record.Detailed ?? "", 1),
            (record.Reading?.Kind.RawValue() ?? "", 1),
        };
        var fieldWords = fields.Select(f => (Words: StickerText.Tokens(f.Text).ToHashSet(), f.Weight)).ToList();
        var total = 0.0;
        foreach (var token in queryTokens)
        {
            var best = 0.0;
            foreach (var (words, weight) in fieldWords)
            {
                if (words.Contains(token)) best = Math.Max(best, weight);
                else if (words.Any(w => w.StartsWith(token, StringComparison.Ordinal))) best = Math.Max(best, weight * 0.8);
                else if (token.Length >= 4 && words.Any(w => w.Contains(token, StringComparison.Ordinal))) best = Math.Max(best, weight * 0.5);
            }
            if (best == 0) return 0; // every word of the query must match somewhere
            total += best;
        }
        return total + Math.Min(record.TimesSent, 5) * 0.1;
    }

    // MARK: Persistence

    private string? SaveImage(IReadOnlyList<Image<Rgba32>> frames, Guid id, bool animated)
    {
        if (frames.Count == 0) return null;
        try
        {
            if (animated && frames.Count > 1)
            {
                var name = $"{UpperId(id)}.gif";
                File.WriteAllBytes(Path.Combine(ImagesDirectory, name), ImagePrep.GifData(frames, frameDelaySeconds: 0.3));
                return name;
            }
            var png = $"{UpperId(id)}.png";
            File.WriteAllBytes(Path.Combine(ImagesDirectory, png), ImagePrep.PngData(frames[0]));
            return png;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ImagePrepException)
        {
            return null;
        }
    }

    private static string UpperId(Guid id) => id.ToString("D").ToUpperInvariant();

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Leaving an orphaned image behind is harmless.
        }
    }

    private void ScheduleSave()
    {
        lock (saveGate) savePending = true;
        saveTimer.Change(TimeSpan.FromMilliseconds(400), Timeout.InfiniteTimeSpan);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Writes <c>library.json</c> now (atomically).</summary>
    public void SaveNow()
    {
        List<StickerRecord> snapshot;
        lock (gate) snapshot = records.Values.OrderBy(r => r.AddedAt).ToList();
        lock (saveGate)
        {
            savePending = false;
            var temp = FilePath + ".tmp";
            try
            {
                File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions));
                File.Move(temp, FilePath, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Retried on the next change.
            }
        }
    }

    private void Load()
    {
        if (!File.Exists(FilePath)) return;
        try
        {
            var decoded = JsonSerializer.Deserialize<List<StickerRecord>>(File.ReadAllBytes(FilePath), JsonOptions) ?? [];
            // Drop descriptions of blank captures ("Black rectangle") that older versions cached.
            foreach (var record in decoded.Where(r => !r.Fingerprint.Thumb.IsUniform)) records.TryAdd(record.Id, record);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or IOException or FormatException or ArgumentException)
        {
            // Keep the unreadable file for inspection instead of overwriting it on the next save.
            try
            {
                File.Copy(FilePath, $"{FilePath}.unreadable-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);
            }
            catch (IOException)
            {
            }
        }
    }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public void Dispose()
    {
        saveTimer.Dispose();
        bool pending;
        lock (saveGate) pending = savePending;
        if (pending) SaveNow();
    }
}
