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

    public static string ShortInstructions(CultureInfo language) =>
        "You describe chat stickers, GIFs, emoji and memes for a blind person who uses a screen reader. " +
        "The picture is usually cropped from a chat app around one message, so ignore chat interface parts: " +
        "the sender's name, timestamps, read-receipt ticks, reaction counts and bubble backgrounds. Describe only the sticker or picture itself. " +
        "Be concrete and brief: who or what it is (name well-known cartoon characters, memes and emoji when you are sure), " +
        "what they are doing, and their facial expression. Never guess the names of real people." + LanguageInstruction(language);

    public static string DetailInstructions(CultureInfo language) =>
        "You describe chat stickers, GIFs, emoji and memes in rich detail for a blind person who uses a screen reader. " +
        "The picture may be cropped from a chat app; ignore chat interface parts such as the sender's name, timestamps, ticks and bubble backgrounds. " +
        "In 3 or 4 short, plain sentences, cover: the character or subject (name well-known cartoon characters and memes when you are sure), " +
        "pose and facial expression, notable colours and art style, any words on the sticker in quotation marks, any motion if there are several frames, " +
        "and finally what people usually mean when they send it. Speak naturally, as if telling a friend; don't repeat these instructions, " +
        "don't use lists or markdown, and don't mention frames. Never guess the names of real people." +
        LanguageInstruction(language);

    private static readonly string KindChoices = string.Join(", ", StickerKinds.All.Select(k => k.RawValue()));

    /// <summary>
    /// The short-reading prompt. One frame = still image; several = animation frames in time order.
    /// It asks only for what gets spoken — every extra line costs time. The sticker's words come
    /// from OCR, not the model.
    /// </summary>
    public static string ShortPrompt(int frameCount, string ocrHint, string? screenReaderLabel, CultureInfo language, bool includeMeaning = false)
    {
        var animated = frameCount > 1;
        var b = new StringBuilder();
        b.AppendLine(animated
            ? $"These are {frameCount} frames, in time order, of one animated sticker or GIF. Describe it and what changes between the frames."
            : "Describe this sticker or image.");
        var ocr = Flat(ocrHint);
        if (ocr.Length > 0) b.AppendLine($"Text that OCR found on it: {ocr}");
        if (!string.IsNullOrWhiteSpace(screenReaderLabel)) b.AppendLine($"The screen reader calls this item: {screenReaderLabel.Trim()}");

        var labels = new List<string> { "KIND", "SUBJECT" };
        if (animated) labels.Add("MOTION");
        if (includeMeaning) labels.Add("MEANING");
        b.AppendLine($"Reply with exactly these {Number(labels.Count)} lines and nothing else:");
        b.AppendLine($"KIND: one of {KindChoices}");
        if (animated)
        {
            b.AppendLine("SUBJECT: who or what it shows in at most 8 words, e.g. Cartoon cat. Don't start with \"A sticker of\".");
            b.AppendLine("MOTION: only what moves or changes across the frames, in at most 10 words, starting with a verb and without naming the character again, e.g. waves, then winks. Compare the frames carefully.");
        }
        else
        {
            b.AppendLine("SUBJECT: what it shows in at most 12 words. Name the character or animal and its action or expression, e.g. Shiba dog shrugging with both paws up. Don't start with \"A sticker of\".");
        }
        if (includeMeaning) b.AppendLine("MEANING: what a person means by sending this, 2 to 6 words, e.g. I don't know, whatever.");
        if (!IsEnglish(language))
        {
            b.AppendLine($"Keep the labels {string.Join(", ", labels)} and the KIND value in English; write everything else in {LanguageName(language)}.");
        }
        return b.ToString().TrimEnd();
    }

    private static string Number(int count) => count switch { 2 => "two", 3 => "three", 4 => "four", _ => count.ToString(CultureInfo.InvariantCulture) };

    private static string Flat(string text) =>
        string.Join(" / ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public static string DetailPrompt(int frameCount, string ocrText, string? screenReaderLabel, StickerReading? shortReading)
    {
        var b = new StringBuilder();
        b.AppendLine(frameCount > 1
            ? $"These are {frameCount} frames, in time order, of one animated sticker or GIF. Describe it in detail, including how it moves."
            : "Describe this sticker or image in detail.");
        AppendHints(b, ocrText, "may include chat timestamps to ignore", screenReaderLabel);
        if (shortReading is not null && shortReading.Subject.Length > 0)
        {
            b.AppendLine($"A quick first look said: {shortReading.Subject}. Correct it if it was wrong.");
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
