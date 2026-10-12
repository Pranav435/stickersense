namespace StickerSense.Core;

/// <summary>
/// Finds sticker files another app keeps on disk (WhatsApp stores every sticker as WebP).
/// Read-only: nothing in the scanned folders is changed.
/// </summary>
public static class StickerFileScanner
{
    public sealed record Result(IReadOnlyList<string> Files, bool PermissionDenied);

    /// <summary>WebP images under <paramref name="maxBytes"/>, newest first.</summary>
    public static Result FindStickerFiles(IEnumerable<string> roots, int limit = 4000, long maxBytes = 1_000_000)
    {
        var files = new List<FileInfo>();
        var denied = false;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            MatchCasing = MatchCasing.CaseInsensitive,
        };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var path in Directory.EnumerateFiles(root, "*.webp", options))
                {
                    try
                    {
                        var info = new FileInfo(path);
                        if (info.Length >= maxBytes) continue;
                        files.Add(info);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                    }
                    if (files.Count >= limit) break;
                }
            }
            catch (UnauthorizedAccessException)
            {
                denied = true;
            }
            catch (IOException)
            {
                denied = true;
            }
            if (files.Count >= limit) break;
        }
        // Newest first, so recent stickers get described first.
        var sorted = files.OrderByDescending(f => f.LastWriteTimeUtc).Select(f => f.FullName).ToList();
        return new Result(sorted, denied && sorted.Count == 0);
    }
}
