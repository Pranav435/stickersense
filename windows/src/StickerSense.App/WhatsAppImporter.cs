using System.IO;
using StickerSense.Core;

namespace StickerSense.App;

/// <summary>
/// Finds the sticker files WhatsApp Desktop keeps on disk (stickers you've received, sent,
/// favourited or installed) and adds them to the library. Read-only: nothing in WhatsApp's
/// data is changed.
/// </summary>
internal static class WhatsAppImporter
{
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string Container =>
        Path.Combine(LocalAppData, "Packages", "5319275A.WhatsAppDesktop_cv1g1gvanyjgm", "LocalState");

    public static bool IsInstalled => Directory.Exists(Container);

    public sealed record Result(int Found, int Added, bool PermissionDenied);

    public static async Task<Result> ImportAllAsync(StickerLibrary library, CancellationToken ct = default)
    {
        var scan = await Task.Run(() => StickerFileScanner.FindStickerFiles([Container]), ct);
        var added = await Task.Run(() =>
        {
            var count = 0;
            foreach (var file in scan.Files)
            {
                ct.ThrowIfCancellationRequested();
                var relative = file.Replace(LocalAppData, "%LOCALAPPDATA%", StringComparison.OrdinalIgnoreCase);
                if (library.ImportFile(file, new StickerSource.WhatsApp(relative)) is not null) count++;
            }
            return count;
        }, ct);
        return new Result(scan.Files.Count, added, scan.PermissionDenied);
    }
}
