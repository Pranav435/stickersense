namespace StickerSense.Core;

/// <summary>
/// A sticker made on this computer from a picker search. It only joins the library once it's
/// pasted or copied.
/// </summary>
/// <param name="Query">What was typed in the search.</param>
/// <param name="Caption">The words written on it (empty for none).</param>
/// <param name="Picture">What the image model was asked to draw.</param>
/// <param name="FilePath">The finished sticker: a transparent PNG.</param>
/// <param name="Number">Its place among the stickers made in this picker, from 1.</param>
public sealed record GeneratedSticker(Guid Id, string Query, string Caption, string Picture, string FilePath, int Number)
{
    /// <summary>The on-device description, once it's been described.</summary>
    public StickerReading? Reading { get; init; }

    /// <summary>
    /// "New sticker 2: Corgi waving its paw, “Thank you”", or what was asked for while it's still
    /// being described.
    /// </summary>
    public string Label => Reading is { } reading
        ? $"New sticker {Number}: {SpokenText.ListLabel(reading)}"
        : $"New sticker {Number}: {Picture}{(Caption.Length > 0 ? $", “{Caption}”" : "")}, still being described";
}
