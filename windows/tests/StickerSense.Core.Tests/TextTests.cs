using System.Globalization;

namespace StickerSense.Core.Tests;

public class TextTests
{
    [Theory]
    [InlineData("4:05 PM")]
    [InlineData("16:05")]
    [InlineData("4:05pm")]
    [InlineData("Edited")]
    [InlineData("Read 4:05 PM")]
    [InlineData("Yesterday")]
    [InlineData("✓✓")]
    [InlineData("3")]
    public void FiltersChatChrome(string chrome) => Assert.True(StickerText.IsChatChrome(chrome));

    [Theory]
    [InlineData("idk lol")]
    [InlineData("Happy birthday!")]
    [InlineData("LOL")]
    [InlineData("good morning ☀️")]
    public void KeepsRealStickerText(string real) => Assert.False(StickerText.IsChatChrome(real));

    [Fact]
    public void StripsChromeInsideLines()
    {
        Assert.Equal("yay", StickerText.StripChrome("yay 4:05 PM"));
        Assert.Equal("Good morning!", StickerText.StripChrome("Good morning! 16:05 ✓✓"));
        Assert.Equal("idk lol", StickerText.StripChrome("idk lol"));
        Assert.Equal("", StickerText.StripChrome("Edited 4:05pm"));
    }

    private static OcrLine Line(string text, double y = 0.5) => new(text, new NormalizedBox(0.1, y, 0.5, 0.08));

    [Fact]
    public void KeepsOnlyTheStickersOwnText()
    {
        Assert.Equal("HAPPY BDAY", StickerText.TextOf([Line("HAPPY BDAY 9:41"), Line("9:41 PM"), Line("Read"), Line("2")], null));
        Assert.Equal("", StickerText.TextOf([], null));
        Assert.Equal("idk\nlol", StickerText.TextOf([Line("idk"), Line("lol")], null));
        // The sender's name is in a sticker's label, so it's chat chrome.
        const string label = "Cute pack sticker from Ana Example, 15:48, Received in Family Group";
        Assert.Equal("hey", StickerText.TextOf([Line("Ana Example", 0.05), Line("hey", 0.8), Line("15:48", 0.95)], label));
    }

    [Fact]
    public void FindsChromeBoxes()
    {
        const string label = "Cute pack sticker from Ana Example, 15:48, Received in Family Group";
        var name = Line("Ana Example", 0.05);
        var time = Line("15:48", 0.95);
        var tickTime = Line("16:05 ✓✓", 0.9);
        var caption = Line("hey", 0.8);
        Assert.Equal([name.Box, time.Box, tickTime.Box], StickerText.ChromeBoxes([name, caption, time, tickTime], label));
        // Without a sticker label, names stay (it might be the sticker's own text); times still go.
        Assert.Equal([time.Box, tickTime.Box], StickerText.ChromeBoxes([name, caption, time, tickTime], null));
        // A line that had a time inside it is chrome too.
        Assert.Single(StickerText.ChromeBoxes([Line("Ana 16:05")], null));
    }

    [Fact]
    public void MergesNewLinesFromLaterFrames()
    {
        Assert.Equal("hi\nbye", StickerText.MergeLines("hi", "Hi!\nbye"));
        Assert.Equal("bye", StickerText.MergeLines("", "bye"));
    }

    [Fact]
    public void NormalisesForComparison()
    {
        Assert.Equal("happybirthday", StickerText.Normalized("Happy  Birthday!!"));
        Assert.Equal("cafe", StickerText.Normalized("Café"));
        Assert.Equal("abc123", StickerText.Normalized("ＡＢＣ１２３"));
        Assert.Equal(["shiba", "dog", "cafe"], StickerText.Tokens("Shiba-dog, CAFÉ!"));
        Assert.Equal(["animé"], StickerText.Words("Animé"));
    }

    [Fact]
    public void SpokenShortFormats()
    {
        var r = new StickerReading(StickerKind.Sticker, "Shiba dog shrugging", "idk", "whatever");
        Assert.Equal("Shiba dog shrugging, “idk”.", SpokenText.Short(r));
        Assert.Equal("Shiba dog shrugging, “idk”. Meaning: whatever.", SpokenText.Short(r, includeVibe: true));
        Assert.Equal("Photo: Beach at sunset.", SpokenText.Short(new StickerReading(StickerKind.Photo, "Beach at sunset")));
        Assert.Equal("Text: “brb”.", SpokenText.Short(new StickerReading(StickerKind.Text, "Words", "brb")));
        Assert.Equal("No sticker or picture here.", SpokenText.Short(new StickerReading(StickerKind.Nothing, "")));
        Assert.Equal("No sticker here. A chat list.", SpokenText.Short(new StickerReading(StickerKind.Nothing, "A chat list")));
        Assert.Equal("GIF: Cat waves.", SpokenText.Short(new StickerReading(StickerKind.Gif, "Cat waves", animated: true)));
        // Memes are just described; only GIF, Photo and Text are announced as a kind.
        Assert.Equal("Distracted boyfriend.", SpokenText.Short(new StickerReading(StickerKind.Meme, "Distracted boyfriend")));
    }

    [Fact]
    public void ListLabelsAndRowLabels()
    {
        var reading = new StickerReading(StickerKind.Gif, "Cat waves", "hi!", animated: true);
        Assert.Equal("Cat waves, “hi!”, animated", SpokenText.ListLabel(reading));
        var record = new StickerRecord
        {
            Id = Guid.NewGuid(),
            Fingerprint = new Fingerprint([0, 0, 0, 0], new Thumbprint([])),
            Reading = reading,
            Source = new StickerSource.Received("whatsapp.root"),
        };
        Assert.Equal("Cat waves, “hi!”, animated. From WhatsApp", record.ScreenReaderLabel);
        Assert.Equal("Not described yet, from GIPHY", (record with { Reading = null, Source = new StickerSource.Giphy("x") }).ScreenReaderLabel);
    }

    [Fact]
    public void RelativeTimesAreShort()
    {
        var now = new DateTimeOffset(2026, 10, 12, 18, 0, 0, TimeSpan.Zero).ToLocalTime();
        Assert.Equal("just now", SpokenText.RelativeTime(now.AddSeconds(-20), now));
        Assert.Equal("1 minute ago", SpokenText.RelativeTime(now.AddMinutes(-1.5), now));
        Assert.Equal("25 minutes ago", SpokenText.RelativeTime(now.AddMinutes(-25), now));
        Assert.Equal("yesterday", SpokenText.RelativeTime(now.Date.AddHours(-2), now));
    }

    [Fact]
    public void HelpSpellsOutEveryShortcut()
    {
        var help = SpokenText.Help("Control Alt Shift D", "Control Alt Shift S", "Control Alt Shift H", "Control Alt Shift A", colonTrigger: true, autoOn: true);
        Assert.StartsWith("StickerSense shortcuts. Control Alt Shift D: describe what has focus;", help);
        Assert.Contains("Or type two colons in a chat to open the picker.", help);
        Assert.Contains("Control Alt Shift A: turn automatic descriptions off.", help);
        Assert.EndsWith("Automatic descriptions are on: stickers and GIFs in chats are described when focus rests on them.", help);
        var quiet = SpokenText.Help("D", "S", "H", "A", colonTrigger: false, autoOn: false);
        Assert.DoesNotContain("colons", quiet);
        Assert.Contains("A: turn automatic descriptions on.", quiet);
    }

    [Fact]
    public void FailuresAreSpokenPlainly()
    {
        Assert.Equal("Still working on the last one.", SpokenText.Failure(DescribeException.Busy()));
        Assert.Equal("Couldn't describe it: boom", SpokenText.Failure(DescribeException.Failed("boom")));
        Assert.Equal("Couldn't describe it.", SpokenText.Failure(new InvalidOperationException("internal")));
        Assert.Equal("Add your GIPHY API key in Settings to search GIFs.", SpokenText.Failure(new GiphyException(GiphyErrorKind.MissingKey)));
    }
}

public class ReadingParserTests
{
    [Fact]
    public void CleansModelSubjects()
    {
        Assert.Equal("A cat waving", ReadingParser.CleanSubject("a sticker of a cat waving."));
        Assert.Equal("Dog", ReadingParser.CleanSubject(" This image shows dog "));
        Assert.Equal("Cartoon cat, slides right, then winks", ReadingParser.JoinMotion("Cartoon cat", "Slides right, then winks."));
        Assert.Equal("Cartoon cat", ReadingParser.JoinMotion("Cartoon cat.", ""));
        Assert.Equal("Waves", ReadingParser.JoinMotion("", "Waves"));
        Assert.Equal("A hand, waving", ReadingParser.JoinMotion("A hand", "is waving"));
    }

    [Fact]
    public void ParsesTheLineFormat()
    {
        const string output = "KIND: sticker\nSUBJECT: Shiba dog shrugging with both paws up\nTEXT: idk\nMEANING: I don't know, whatever.";
        var reading = ReadingParser.Parse(output, animated: false, ocrText: "idk");
        Assert.Equal(new StickerReading(StickerKind.Sticker, "Shiba dog shrugging with both paws up", "idk", "I don't know, whatever"), reading);
    }

    [Fact]
    public void ToleratesMarkdownQuotesAndSpecialTokens()
    {
        const string output = "**KIND:** Meme\n- **SUBJECT:** \"this image shows a frog sipping tea\"\n* TEXT: none\nVIBE: not my business<|end|>";
        var reading = ReadingParser.Parse(output, animated: false, ocrText: "");
        Assert.Equal(StickerKind.Meme, reading.Kind);
        Assert.Equal("A frog sipping tea", reading.Subject);
        Assert.Equal("", reading.Text);
        Assert.Equal("not my business", reading.Vibe);
    }

    [Fact]
    public void JoinsMotionForAnimations()
    {
        const string output = "KIND: photo\nSUBJECT: Cartoon cat\nMOTION: Slides right, then its eyes turn into hearts\nTEXT: hi!\nMEANING: hello, love you";
        var reading = ReadingParser.Parse(output, animated: true, ocrText: "hi!");
        Assert.Equal(StickerKind.Gif, reading.Kind); // an animated "photo" is a GIF
        Assert.Equal("Cartoon cat, slides right, then its eyes turn into hearts", reading.Subject);
        Assert.True(reading.Animated);
        Assert.Equal("GIF: Cartoon cat, slides right, then its eyes turn into hearts, “hi!”.", SpokenText.Short(reading));
    }

    [Fact]
    public void ReadsRepliesThatContinueTheKindLine()
    {
        // Phi-3.5-vision, given a reply begun with "KIND:", sometimes keeps going on the same line.
        var reading = ReadingParser.Parse("KIND: emoji, SUBJECT: smiling face with party hat, wishing happy birthday", false, "HAPPY BDAY");
        Assert.Equal(StickerKind.Emoji, reading.Kind);
        Assert.Equal("Smiling face with party hat, wishing happy birthday", reading.Subject);
        // Lower-case words like "text:" inside a subject stay part of it.
        reading = ReadingParser.Parse("KIND: sticker\nSUBJECT: sign with text: hello", false, "");
        Assert.Equal("Sign with text: hello", reading.Subject);
    }

    [Fact]
    public void LeavesOutMotionTheModelDidNotSee()
    {
        var reading = ReadingParser.Parse("KIND: emoji\nSUBJECT: smiling face with smiling eyes\nMOTION: remains still with a happy expression", true, "");
        Assert.Equal("Smiling face with smiling eyes", reading.Subject);
        reading = ReadingParser.Parse("KIND: emoji\nSUBJECT: smiling face\nMOTION: no change", true, "");
        Assert.Equal("Smiling face", reading.Subject);
        reading = ReadingParser.Parse("KIND: sticker\nSUBJECT: cartoon cat\nMOTION: still waves, then winks", true, "");
        Assert.Equal("Cartoon cat, still waves, then winks", reading.Subject);
    }

    [Fact]
    public void DoesNotRepeatWordsTheSubjectAlreadySays()
    {
        var reading = new StickerReading(StickerKind.Sticker, "A smiling face with a party hat and the text \"HAPPY BDAY\"", "HAPPY BDAY", "");
        Assert.Equal("A smiling face with a party hat and the text \"HAPPY BDAY\".", SpokenText.Short(reading));
        Assert.Equal("A smiling face with a party hat and the text \"HAPPY BDAY\"", SpokenText.ListLabel(reading));
        reading = new StickerReading(StickerKind.Sticker, "Party hat", "HAPPY BDAY", "");
        Assert.Equal("Party hat, “HAPPY BDAY”.", SpokenText.Short(reading));
        // Whole words only: "hi" is not in "Shiba".
        reading = new StickerReading(StickerKind.Sticker, "Shiba dog shrugging", "hi!", "");
        Assert.Equal("Shiba dog shrugging, “hi!”.", SpokenText.Short(reading));
    }

    [Fact]
    public void DropsChatChromeTheModelCopied()
    {
        var reading = ReadingParser.Parse("KIND: sticker\nSUBJECT: Party popper\nTEXT: yay 4:05 PM\nMEANING: celebrating", false, "yay");
        Assert.Equal("yay", reading.Text);
        reading = ReadingParser.Parse("KIND: sticker\nSUBJECT: Party popper\nTEXT: Read 4:05 PM\nMEANING: celebrating", false, "Read");
        Assert.Equal("", reading.Text);
        // The sender's name from a sticker's label isn't sticker text.
        reading = ReadingParser.Parse("KIND: sticker\nSUBJECT: Cat\nTEXT: Ana Example\nMEANING: hi", false, "Ana Example", "Pack sticker from Ana Example, 15:48");
        Assert.Equal("", reading.Text);
    }

    [Fact]
    public void PrefersOcrSpellingWhenItAgrees()
    {
        var reading = ReadingParser.Parse("KIND: sticker\nSUBJECT: Cake\nTEXT: happy bday\nMEANING: celebrating", false, "HAPPY BDAY!");
        Assert.Equal("HAPPY BDAY!", reading.Text);
    }

    [Fact]
    public void StickerTextComesOnlyFromOcr()
    {
        // The model's TEXT is ignored: it once "read" "Red lips, green eyes" off a face.
        Assert.Equal("", ReadingParser.Parse("KIND: sticker\nSUBJECT: Face\nTEXT: Red lips, green eyes", false, "").Text);
        Assert.Equal("idk lol", ReadingParser.Parse("KIND: sticker\nSUBJECT: Dog", false, "idk\nlol").Text);
        // Chat chrome and label text never count.
        Assert.Equal("", StickerText.FromOcr("4:05 PM\nRead"));
        Assert.Equal("hey", StickerText.FromOcr("Ana Example\nhey 15:48", "Pack sticker from Ana Example, 15:48"));
    }

    [Fact]
    public void LongOcrTextIsCut()
    {
        var words = string.Join(" ", Enumerable.Repeat("lorem ipsum", 30));
        var text = StickerText.FromOcr(words);
        Assert.True(text.Length <= 161, $"{text.Length}");
        Assert.EndsWith("…", text);
        Assert.DoesNotContain("  ", text);
        Assert.Equal("short caption", StickerText.FromOcr("short caption"));
    }

    [Fact]
    public void FallsBackToProse()
    {
        var reading = ReadingParser.Parse("Sure! This image shows a corgi wearing sunglasses. It looks cool.", false, "");
        Assert.Equal(StickerKind.Sticker, reading.Kind);
        Assert.Equal("A corgi wearing sunglasses", reading.Subject);
        Assert.Equal(StickerKind.Gif, ReadingParser.Parse("A dancing banana", true, "").Kind);
    }

    [Fact]
    public void MotionJoinsWithoutRepeatingTheSubject()
    {
        Assert.Equal("Cartoon cat, mouth opens, then closes", ReadingParser.JoinMotion("Cartoon cat", "cat's mouth opens, then closes"));
        Assert.Equal("Cartoon cat, waves, then winks", ReadingParser.JoinMotion("Cartoon cat", "The cat waves, then winks."));
        Assert.Equal("Shiba dog, wags its tail", ReadingParser.JoinMotion("Shiba dog", "Wags its tail"));
    }

    [Theory]
    [InlineData("gif", StickerKind.Gif)]
    [InlineData("Emoji.", StickerKind.Emoji)]
    [InlineData("animation", StickerKind.Gif)]
    [InlineData("cartoon drawing", StickerKind.Illustration)]
    [InlineData("nothing", StickerKind.Nothing)]
    [InlineData("something odd", StickerKind.Sticker)]
    public void ParsesKinds(string value, StickerKind expected) => Assert.Equal(expected, ReadingParser.ParseKind(value));

    [Fact]
    public void CleansDetailedDescriptions()
    {
        const string output = "Sure! Here's a description:\n\n**The sticker** shows a shiba dog.\n- It shrugs.\n1. People send it to say \"I don't know\".<|end|>";
        Assert.Equal("The sticker shows a shiba dog. It shrugs. People send it to say \"I don't know\".", ReadingParser.CleanDetail(output));
        // A chatty opening goes, and a reply cut off at the token limit ends on its last whole sentence.
        Assert.Equal("A cartoon person shrugs. They wear orange.",
            ReadingParser.CleanDetail("I can help you with that. Let's go through the image in detail, as you requested. A cartoon person shrugs. They wear orange. The character's mouth is slightly"));
    }
}

public class PromptTests
{
    [Fact]
    public void EnglishHasNoLanguageInstruction()
    {
        Assert.Equal("", StickerPrompts.LanguageInstruction(CultureInfo.GetCultureInfo("en-GB")));
        Assert.DoesNotContain("Write your descriptions in", StickerPrompts.ShortInstructions(CultureInfo.GetCultureInfo("en-US")));
    }

    [Fact]
    public void OtherLanguagesAreRequested()
    {
        var french = CultureInfo.GetCultureInfo("fr-FR");
        Assert.Equal("French", StickerPrompts.LanguageName(french));
        Assert.Contains("Write your descriptions in French", StickerPrompts.DetailInstructions(french));
        var prompt = StickerPrompts.ShortPrompt(1, "", null, french);
        Assert.Contains("Keep the labels and the KIND word in English; write the rest in French.", prompt);
    }

    [Fact]
    public void ShortPromptAsksOnlyForWhatIsSpoken()
    {
        var english = CultureInfo.GetCultureInfo("en-US");
        var prompt = StickerPrompts.ShortPrompt(1, "idk\nlol", "Sticker, image", english);
        Assert.StartsWith("Describe this sticker.", prompt);
        Assert.Contains("Words on it (from OCR): idk / lol", prompt);
        Assert.Contains("The screen reader calls it: Sticker, image", prompt);
        Assert.Contains("Reply with exactly these two lines:", prompt);
        Assert.Contains("KIND: one word from sticker, gif, emoji, meme, photo, illustration, text or nothing", prompt);
        // The words come from OCR, and meaning only when it will be spoken.
        Assert.DoesNotContain("TEXT:", prompt);
        Assert.DoesNotContain("MEANING:", prompt);
        Assert.DoesNotContain("MOTION:", prompt);

        var withMeaning = StickerPrompts.ShortPrompt(1, "", null, english, includeMeaning: true);
        Assert.Contains("exactly these three lines", withMeaning);
        Assert.Contains("MEANING:", withMeaning);
        Assert.DoesNotContain("OCR", withMeaning);

        var animated = StickerPrompts.ShortPrompt(3, "", null, english);
        Assert.StartsWith("These are 3 frames, in time order, of one animated sticker or GIF.", animated);
        Assert.Contains("exactly these three lines", animated);
        Assert.Contains("MOTION: what changes from frame to frame", animated);
        var strip = StickerPrompts.ShortPrompt(3, "", null, english, sideBySide: true);
        Assert.StartsWith("This picture shows 3 frames of one animated sticker or GIF side by side, in time order from left to right.", strip);
    }

    [Fact]
    public void QuickInstructionsKeepTheRulesThatMatter()
    {
        var instructions = StickerPrompts.ShortInstructions(CultureInfo.GetCultureInfo("en-US"));
        Assert.Contains("ignore chat parts", instructions);
        Assert.Contains("only when you are sure; never name real people", instructions);
        Assert.Contains("Never invent words", instructions);
        Assert.True(instructions.Length < 400, $"{instructions.Length} characters");
    }

    [Fact]
    public void QuickReadingsStopAfterTheirLines()
    {
        Assert.Equal((2, 30), (StickerPrompts.ShortReplyLines(false, false), StickerPrompts.ShortReplyTokens(false, false)));
        Assert.Equal((4, 62), (StickerPrompts.ShortReplyLines(true, true), StickerPrompts.ShortReplyTokens(true, true)));
        Assert.Equal(0, ReadingParser.CompletedLines("KIND: sticker"));
        Assert.Equal(1, ReadingParser.CompletedLines("KIND: sticker\n\nSUBJECT: Dog"));
        Assert.Equal(2, ReadingParser.CompletedLines("KIND: sticker\nSUBJECT: Dog\n"));
        Assert.Equal(2, ReadingParser.CompletedSentences("A dog. It shrugs! People send it"));
        Assert.Equal(1, ReadingParser.CompletedSentences("It says \"hi.\" Then"));
        Assert.Equal(3, ReadingParser.LastLineWords("KIND: emoji\nSUBJECT: party face"));
    }

    [Fact]
    public void ReadingsKeepToTheirWordLimits()
    {
        var reading = ReadingParser.Parse("KIND: gif\nSUBJECT: Waving hand\nMOTION: moves back and forth, fingers spread, waves back and forth, then stops, then waves again", true, "");
        Assert.Equal("Waving hand, moves back and forth, fingers spread, waves back and forth", reading.Subject);
        Assert.Equal("one two three four five six seven eight nine ten eleven twelve", ReadingParser.Limit("one two three four five six seven eight nine ten eleven twelve thirteen", 12));
    }

    [Fact]
    public void DetailIsThreeOrFourSentences() =>
        Assert.Contains("3 or 4 short, plain sentences", StickerPrompts.DetailInstructions(CultureInfo.GetCultureInfo("en-US")));

    [Fact]
    public void DetailPromptMentionsFirstLook()
    {
        var prompt = StickerPrompts.DetailPrompt(1, "yay 4:05", null, new StickerReading(StickerKind.Sticker, "Party popper"));
        Assert.Equal(
            "Describe this sticker or image in detail.\nText that OCR found in the picture (may include chat timestamps to ignore): yay 4:05\nFor reference, a quick first look said: Party popper.",
            prompt.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void BuildsPhiVisionChat()
    {
        var prompt = PhiVisionPrompt.Build("Be brief.", "Describe.", 2);
        Assert.Equal("<|system|>\nBe brief.<|end|>\n<|user|>\n<|image_1|>\n<|image_2|>\nDescribe.<|end|>\n<|assistant|>\n", prompt);
        Assert.EndsWith("<|assistant|>\nKIND:", PhiVisionPrompt.Build("Be brief.", "Describe.", 1, "KIND:"));
    }
}
