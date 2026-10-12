using System.Diagnostics;
using System.Globalization;
using System.IO;
using StickerSense.Core;

namespace StickerSense.App;

/// <summary>
/// A small activity log (<c>%LOCALAPPDATA%\StickerSense\StickerSense.log</c>) — handy for "why didn't
/// it speak?" questions and for tuning sticker detection in new chat apps. Never logs images.
/// </summary>
internal sealed class DebugLog : IActivityLog
{
    private readonly object gate = new();

    public DebugLog(string directory)
    {
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, "StickerSense.log");
        try
        {
            // Keep the file small: start fresh when it passes 1 MB.
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000) File.Delete(FilePath);
        }
        catch (IOException)
        {
        }
    }

    public string FilePath { get; }

    public void Add(string message)
    {
        var line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} {message}";
        Debug.WriteLine("StickerSense: " + message);
        lock (gate)
        {
            try
            {
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
