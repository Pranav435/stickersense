using System.Globalization;
using System.Text;

namespace StickerSense.Core;

/// <summary>
/// The instructions and prompts given to the vision-language model. Mirrors the Swift
/// describer; since small local models have no guided generation, the short reading asks for
/// a fixed line format that <see cref="ReadingParser"/> reads back.
/// </summary>
public static class StickerPrompts
{
    public static bool IsEnglish(CultureInfo language) =>
        language.TwoLetterISOLanguageName is "en" or "iv" or "";

    /// <summary>English name of the description language, e.g. "French".</summary>
    public static string LanguageName(CultureInfo language)
    {
        var culture = language;
        while (!culture.IsNeutralCulture && !Equals(culture.Parent, CultureInfo.InvariantCulture) && culture.Parent.Name.Length > 0)
        {
            culture = culture.Parent;
        }
        var name = culture.EnglishName;
        var bracket = name.IndexOf(" (", StringComparison.Ordinal);
        return bracket > 0 ? name[..bracket] : name;
    }

    public static string LanguageInstruction(CultureInfo language) =>
        IsEnglish(language)
            ? ""
            : $" Write your descriptions in {LanguageName(language)}, but copy text that is written on the sticker exactly as written, in its original language.";

    /// <summary>
    /// The quick reading's instructions. Short on purpose: on a processor every prompt token costs
    /// time before the first word, and the reply format is spelled out in the prompt itself.
    /// </summary>
    public static string ShortInstructions(CultureInfo language) =>
        "You describe chat stickers, GIFs, emoji and memes for a blind screen reader user. " +
        "Describe only the picture: ignore chat parts such as names, times, ticks and bubbles. Be brief and concrete. " +
        "Name a well-known character, meme or emoji only when you are sure; never name real people. " +
        "Never invent words that aren't in the picture." + LanguageInstruction(language);

    public static string DetailInstructions(CultureInfo language) =>
        "You describe chat stickers, GIFs, emoji and memes in rich detail for a blind person who uses a screen reader. " +
        "The picture may be cropped from a chat app; ignore chat interface parts such as the sender's name, timestamps, ticks and bubble backgrounds. " +
        "In 3 or 4 short, plain sentences, cover: the character or subject (name well-known cartoon characters and memes only when you are sure; never make up a name), " +
        "pose and facial expression, notable colours and art style, any words on the sticker in quotation marks, any motion if there are several frames, " +
        "and finally what people usually mean when they send it. Speak naturally, as if telling a friend; don't repeat these instructions, " +
        "don't use lists or markdown, and don't mention frames. Never guess the names of real people." +
        LanguageInstruction(language);

    private static readonly string KindChoices =
        string.Join(", ", StickerKinds.All.SkipLast(1).Select(k => k.RawValue())) + " or " + StickerKinds.All[^1].RawValue();

    /// <summary>
    /// The short-reading prompt. One frame = still image; several = animation frames in time order.
    /// It asks only for what gets spoken — every extra line costs time. The sticker's words come
    /// from OCR, not the model.
    /// </summary>
    /// <param name="sideBySide">The frames are one picture, side by side, rather than one picture each.</param>
    public static string ShortPrompt(int frameCount, string ocrHint, string? screenReaderLabel, CultureInfo language, bool includeMeaning = false, bool sideBySide = false)
    {
        var animated = frameCount > 1;
        var b = new StringBuilder();
        b.AppendLine(!animated
            ? "Describe this sticker."
            : sideBySide
                ? $"This picture shows {frameCount} frames of one animated sticker or GIF side by side, in time order from left to right. They show the same sticker at different moments: describe it once, and what changes from frame to frame."
                : $"These are {frameCount} frames, in time order, of one animated sticker or GIF. Describe it and what changes between the frames.");
        var ocr = Flat(ocrHint);
        if (ocr.Length > 0) b.AppendLine($"Words on it (from OCR): {ocr}");
        if (!string.IsNullOrWhiteSpace(screenReaderLabel)) b.AppendLine($"The screen reader calls it: {screenReaderLabel.Trim()}");

        b.AppendLine($"Reply with exactly these {Number(ShortReplyLines(animated, includeMeaning))} lines:");
        b.AppendLine($"KIND: one word from {KindChoices}");
        b.AppendLine(animated
            ? "SUBJECT: who or what it shows, once, in at most 8 words, e.g. Cartoon cat"
            : "SUBJECT: who or what, and what they're doing or feeling, in at most 12 words, e.g. Shiba dog shrugging");
        if (animated) b.AppendLine("MOTION: what changes from frame to frame, in at most 10 words, starting with a verb, e.g. waves, then winks");
        if (includeMeaning) b.AppendLine("MEANING: what sending it means, in 2 to 6 words, e.g. I don't know");
        if (!IsEnglish(language)) b.AppendLine($"Keep the labels and the KIND word in English; write the rest in {LanguageName(language)}.");
        return b.ToString().TrimEnd();
    }

    /// <summary>Lines the quick reading asks for: KIND, SUBJECT, then MOTION and MEANING when wanted.</summary>
    public static int ShortReplyLines(bool animated, bool includeMeaning) => 2 + (animated ? 1 : 0) + (includeMeaning ? 1 : 0);

    /// <summary>
    /// Enough new tokens for the quick reading's lines and no more: a model that keeps talking
    /// after them only costs time (about a seventh of a second a token on a processor).
    /// </summary>
    public static int ShortReplyTokens(bool animated, bool includeMeaning) => 30 + (animated ? 18 : 0) + (includeMeaning ? 14 : 0);

    private static string Number(int count) => count switch { 2 => "two", 3 => "three", 4 => "four", _ => count.ToString(CultureInfo.InvariantCulture) };

    private static string Flat(string text) =>
        string.Join(" / ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public static string DetailPrompt(int frameCount, string ocrText, string? screenReaderLabel, StickerReading? shortReading, bool sideBySide = false)
    {
        var b = new StringBuilder();
        b.AppendLine(frameCount <= 1
            ? "Describe this sticker or image in detail."
            : sideBySide
                ? $"This picture shows {frameCount} frames of one animated sticker or GIF side by side, in time order from left to right. They show the same sticker at different moments: describe it in detail as one sticker, including how it moves."
                : $"These are {frameCount} frames, in time order, of one animated sticker or GIF. Describe it in detail, including how it moves.");
        AppendHints(b, ocrText, "may include chat timestamps to ignore", screenReaderLabel);
        if (shortReading is not null && shortReading.Subject.Length > 0)
        {
            b.AppendLine($"For reference, a quick first look said: {shortReading.Subject}.");
        }
        return b.ToString().TrimEnd();
    }

    private static void AppendHints(StringBuilder b, string ocrText, string ocrCaveat, string? label)
    {
        var ocr = Flat(ocrText);
        if (ocr.Length > 0) b.AppendLine($"Text that OCR found in the picture ({ocrCaveat}): {ocr}");
        if (!string.IsNullOrWhiteSpace(label)) b.AppendLine($"The screen reader calls this item: {label.Trim()}");
    }
}

/// <summary>Prompt format for Phi-3 / Phi-3.5 vision (ONNX Runtime GenAI "phi3v"). Its image inputs are in <see cref="PhiVisionInputs"/>.</summary>
public static class PhiVisionPrompt
{
    /// <summary>
    /// Chat template of Phi-3.5-vision-instruct, with one <c>&lt;|image_N|&gt;</c> tag per image,
    /// ending with the start of the reply (<see cref="VisionRequest.ReplyStart"/>) for the model to continue.
    /// </summary>
    public static string Build(string instructions, string prompt, int imageCount, string replyStart = "")
    {
        var b = new StringBuilder();
        b.Append("<|system|>\n").Append(instructions).Append("<|end|>\n");
        b.Append("<|user|>\n");
        for (var i = 1; i <= imageCount; i++) b.Append("<|image_").Append(i).Append("|>\n");
        b.Append(prompt).Append("<|end|>\n");
        b.Append("<|assistant|>\n").Append(replyStart);
        return b.ToString();
    }
}
