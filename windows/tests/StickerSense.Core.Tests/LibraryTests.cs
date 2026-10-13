using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace StickerSense.Core.Tests;

public class LibraryTests
{
    private static StickerReading Reading(string subject, string text = "", string vibe = "", StickerKind kind = StickerKind.Sticker) =>
        new(kind, subject, text, vibe);

    private static StickerRecord Remember(StickerLibrary library, Image<Rgba32> image, StickerReading reading, string ocr, StickerSource source) =>
        library.Remember([image], new Fingerprint(image), reading, ocr, source);

    [Fact]
    public void RemembersMatchesAndPersists()
    {
        var dir = Fixtures.TempDirectory();
        Guid id;
        using (var library = new StickerLibrary(dir))
        {
            using var image = Fixtures.Sticker(Art.Dog, "idk lol");
            var record = Remember(library, image, Reading("Dog shrugging", "idk lol"), "idk lol", new StickerSource.Received("whatsapp"));
            Assert.NotNull(record.ImageFile);
            Assert.True(File.Exists(library.ImagePath(record)));
            id = record.Id;

            // Seen again on screen, smaller and on a chat background.
            using var onScreen = Fixtures.OnScreenCapture(image, 0.5);
            var hit = library.Match(new Fingerprint(onScreen), StickerText.StripChrome("idk lol 4:05 PM"));
            Assert.Equal(record.Id, hit?.Id);

            // Seen again: the same record is refreshed, not duplicated.
            var again = Remember(library, onScreen, Reading("Something else"), "idk lol", new StickerSource.Received("whatsapp"));
            Assert.Equal(record.Id, again.Id);
            Assert.Equal(2, again.TimesSeen);
            Assert.Equal("Dog shrugging", again.Reading?.Subject);
            library.SaveNow();
        }

        using var reloaded = new StickerLibrary(dir);
        Assert.Equal(1, reloaded.Count);
        Assert.Equal("Dog shrugging", reloaded.Record(id)?.Reading?.Subject);
    }

    [Fact]
    public void DifferentTextOnSameArtIsNotACacheHit()
    {
        using var library = new StickerLibrary(Fixtures.TempDirectory());
        using var morning = Fixtures.Sticker(Art.Sun, "Good morning", captionColor: Color.Black);
        using var night = Fixtures.Sticker(Art.Sun, "Good night", captionColor: Color.Black);
        Remember(library, morning, Reading("Smiling sun", "Good morning"), "Good morning", new StickerSource.Received("x"));
        Assert.Null(library.Match(new Fingerprint(night), "Good night"));
        Assert.NotNull(library.Match(new Fingerprint(morning), "Good mornlng")); // small OCR wobble is fine
    }

    [Fact]
    public void SearchRanksTextAndMeaning()
    {
        using var library = new StickerLibrary(Fixtures.TempDirectory());
        using var dog = Fixtures.Sticker(Art.Dog);
        using var cake = Fixtures.Sticker(Art.Cake);
        using var joy = Fixtures.Sticker(Art.Joy);
        Remember(library, dog, Reading("Shiba dog shrugging", "idk", "I don't know, whatever"), "idk", new StickerSource.Received("x"));
        Remember(library, cake, Reading("Birthday cake with candles", "Happy birthday", "celebrating"), "Happy birthday", new StickerSource.Received("x"));
        Remember(library, joy, Reading("Face crying with laughter", vibe: "that's hilarious"), "", new StickerSource.Giphy("1"));

        Assert.Equal("Birthday cake with candles", library.Search("birthday").FirstOrDefault()?.Reading?.Subject);
        Assert.Equal("Shiba dog shrugging", library.Search("dog idk").FirstOrDefault()?.Reading?.Subject);
        Assert.Equal("Face crying with laughter", library.Search("hilar").FirstOrDefault()?.Reading?.Subject);
        Assert.Empty(library.Search("dog cake"));
        Assert.Single(library.Search("", SourceFilter.Giphy));
        Assert.Equal(3, library.Search("").Count);
        // History is what was seen on screen, not GIPHY results.
        Assert.Equal(2, library.History().Count);
    }

    [Fact]
    public void FrequentlySentStickersRankFirst()
    {
        using var library = new StickerLibrary(Fixtures.TempDirectory());
        using var a = Fixtures.Sticker(Art.Grinning);
        using var b = Fixtures.Sticker(Art.Laughing);
        var first = Remember(library, a, Reading("Grinning face", vibe: "happy"), "", new StickerSource.Received("x"));
        var second = Remember(library, b, Reading("Laughing face", vibe: "happy"), "", new StickerSource.Received("x"));
        Assert.Equal(second.Id, library.Search("").First().Id); // most recent first
        for (var i = 0; i < 3; i++) library.MarkSent(first.Id);
        Assert.Equal(first.Id, library.Search("").First().Id);
        Assert.Equal(first.Id, library.Search("happy").First().Id);
    }

    [Fact]
    public void AFreshReadingCanReplaceTheOldOne()
    {
        using var library = new StickerLibrary(Fixtures.TempDirectory());
        using var image = Fixtures.Sticker(Art.Bear);
        var fingerprint = new Fingerprint(image);
        var first = library.Remember([image], fingerprint, Reading("Bear"), "", new StickerSource.Received("x"));
        library.Remember([image], fingerprint, Reading("Teddy bear waving"), "", new StickerSource.Received("x"));
        Assert.Equal("Bear", library.Record(first.Id)!.Reading!.Subject); // kept by default
        library.Remember([image], fingerprint, Reading("Teddy bear waving"), "", new StickerSource.Received("x"), replaceReading: true);
        Assert.Equal("Teddy bear waving", library.Record(first.Id)!.Reading!.Subject);
        Assert.Equal(1, library.Count);
    }

    [Fact]
    public void PhotosAreCachedButNotCollected()
    {
        using var library = new StickerLibrary(Fixtures.TempDirectory());
        using var photo = Fixtures.Sticker(Art.Beach, background: Color.ParseHex("30B0C7"));
        var record = Remember(library, photo, Reading("Beach", kind: StickerKind.Photo), "", new StickerSource.Received("x"));
        Assert.Null(record.ImageFile);
        Assert.Empty(library.Search(""));
        Assert.Single(library.History());
    }

    [Fact]
    public void AnimatedStickersAreSavedAsGif()
    {
        using var library = new StickerLibrary(Fixtures.TempDirectory());
        var frames = Fixtures.AnimatedFrames();
        var record = library.Remember(frames, new Fingerprint(frames[0]), new StickerReading(StickerKind.Gif, "Cat waves", animated: true), "hi!", new StickerSource.Received("x"));
        Assert.EndsWith(".gif", record.ImageFile);
        Assert.Equal(3, ImagePrep.LoadFrames(library.ImagePath(record)!).Count);
    }

    [Fact]
    public void ImportDeduplicates()
    {
        var dir = Fixtures.TempDirectory();
        using var library = new StickerLibrary(dir);
        var file = Path.Combine(dir, "a.png");
        using (var image = Fixtures.Sticker(Art.Frog, "hmm")) File.WriteAllBytes(file, ImagePrep.PngData(image));
        var imported = library.ImportFile(file, new StickerSource.WhatsApp("a.png"));
        Assert.NotNull(imported);
        Assert.Null(library.ImportFile(file, new StickerSource.WhatsApp("a.png")));
        Assert.Single(library.Undescribed(10));
        Assert.Null(library.ImportFile(Path.Combine(dir, "missing.webp"), new StickerSource.Imported()));

        library.SetReading(Reading("Frog thinking", "hmm"), "hmm", imported.Id);
        Assert.Empty(library.Undescribed(10));
        Assert.Equal("Frog thinking", library.Search("frog").Single().Reading?.Subject);
    }

    [Fact]
    public void DeleteRemovesTheImage()
    {
        using var library = new StickerLibrary(Fixtures.TempDirectory());
        using var image = Fixtures.Sticker(Art.Bear);
        var record = Remember(library, image, Reading("Bear"), "", new StickerSource.Received("x"));
        var path = library.ImagePath(record)!;
        var changes = 0;
        library.Changed += (_, _) => changes++;
        library.SetDetailed("A brown bear.", record.Id);
        Assert.Equal("A brown bear.", library.Record(record.Id)?.Detailed);
        library.Delete(record.Id);
        Assert.False(File.Exists(path));
        Assert.Equal(0, library.Count);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void LevenshteinAndTextAgreement()
    {
        Assert.Equal(3, StickerLibrary.Levenshtein("kitten", "sitting"));
        Assert.Equal(3, StickerLibrary.Levenshtein("", "abc"));
        Assert.True(StickerLibrary.TextsAgree("happybirthday", "happybirthdav"));
        Assert.False(StickerLibrary.TextsAgree("goodmorning", "goodnight"));
        Assert.False(StickerLibrary.TextsAgree("", "abc"));
        Assert.True(StickerLibrary.TextsAgree("", ""));
    }

    [Fact]
    public void ReadsAndWritesTheSwiftJsonFormat()
    {
        const string swiftJson = """
        [{"id":"E621E1F8-C36C-495A-93FC-0C247A3E6E5F","fingerprint":{"dHash":[1,2,3,18446744073709551615],"thumb":{"rgb":"AAEC////"}},
          "reading":{"kind":"gif","subject":"Cat waves","text":"hi!","vibe":"hello","animated":true},"ocrText":"hi!",
          "source":{"received":{"app":"net.whatsapp.WhatsApp"}},"imageFile":"E621E1F8-C36C-495A-93FC-0C247A3E6E5F.gif",
          "addedAt":"2026-10-12T16:03:00Z","lastSeenAt":"2026-10-12T16:04:00Z","timesSeen":2,"timesSent":1},
         {"id":"0B5C8D6E-1111-4A2B-9C3D-445566778899","fingerprint":{"dHash":[0,0,0,0],"thumb":{"rgb":"AAEC////"}},
          "ocrText":"","source":{"whatsApp":{"relativePath":"~/Library/x.webp"}},"imageFile":"x.webp",
          "addedAt":"2026-10-12T16:05:00Z","lastSeenAt":"2026-10-12T16:05:00Z","timesSeen":0,"timesSent":0},
         {"id":"0B5C8D6E-2222-4A2B-9C3D-445566778899","fingerprint":{"dHash":[0,0,0,0],"thumb":{"rgb":"AAEC////"}},
          "reading":{"kind":"sticker","subject":"Dog","text":"","vibe":"","animated":false},"detailed":"A dog.","ocrText":"",
          "source":{"giphy":{"id":"abc"}},"remoteURL":"https://media.giphy.com/media/abc/giphy.gif",
          "addedAt":"2026-10-12T16:06:00Z","lastSeenAt":"2026-10-12T16:06:00Z","timesSeen":1,"timesSent":4},
         {"id":"0B5C8D6E-3333-4A2B-9C3D-445566778899","fingerprint":{"dHash":[0,0,0,0],"thumb":{"rgb":"AAEC////"}},
          "ocrText":"","source":{"imported":{}},
          "addedAt":"2026-10-12T16:07:00Z","lastSeenAt":"2026-10-12T16:07:00Z","timesSeen":0,"timesSent":0}]
        """;
        var dir = Fixtures.TempDirectory();
        File.WriteAllText(Path.Combine(dir, "library.json"), swiftJson);
        using var library = new StickerLibrary(dir);
        Assert.Equal(4, library.Count);

        var cat = library.Record(Guid.Parse("E621E1F8-C36C-495A-93FC-0C247A3E6E5F"))!;
        Assert.Equal(new StickerReading(StickerKind.Gif, "Cat waves", "hi!", "hello", true), cat.Reading);
        Assert.Equal(new StickerSource.Received("net.whatsapp.WhatsApp"), cat.Source);
        Assert.Equal("WhatsApp", cat.Source.DisplayName);
        Assert.Equal(ulong.MaxValue, cat.Fingerprint.DHash[3]);
        Assert.Equal(new byte[] { 0, 1, 2, 255, 255, 255 }, cat.Fingerprint.Thumb.Rgb);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 16, 3, 0, TimeSpan.Zero), cat.AddedAt);
        Assert.IsType<StickerSource.WhatsApp>(library.Record(Guid.Parse("0B5C8D6E-1111-4A2B-9C3D-445566778899"))!.Source);
        var gif = library.Record(Guid.Parse("0B5C8D6E-2222-4A2B-9C3D-445566778899"))!;
        Assert.Equal(new StickerSource.Giphy("abc"), gif.Source);
        Assert.Equal("https://media.giphy.com/media/abc/giphy.gif", gif.RemoteUrl);
        Assert.Equal("A dog.", gif.Detailed);
        Assert.IsType<StickerSource.Imported>(library.Record(Guid.Parse("0B5C8D6E-3333-4A2B-9C3D-445566778899"))!.Source);

        library.SaveNow();
        var written = File.ReadAllText(Path.Combine(dir, "library.json"), Encoding.UTF8);
        Assert.Contains("\"id\":\"E621E1F8-C36C-495A-93FC-0C247A3E6E5F\"", written);
        Assert.Contains("\"source\":{\"received\":{\"app\":\"net.whatsapp.WhatsApp\"}}", written);
        Assert.Contains("\"source\":{\"imported\":{}}", written);
        Assert.Contains("\"source\":{\"whatsApp\":{\"relativePath\":\"~/Library/x.webp\"}}", written);
        Assert.Contains("\"addedAt\":\"2026-10-12T16:03:00Z\"", written);
        Assert.Contains("\"dHash\":[1,2,3,18446744073709551615]", written);
        Assert.Contains("\"thumb\":{\"rgb\":\"AAEC////\"}", written);
        Assert.Contains("\"remoteURL\":\"https://media.giphy.com/media/abc/giphy.gif\"", written);
        Assert.Contains("\"kind\":\"gif\"", written);
        Assert.DoesNotContain("\"detailed\":null", written);
    }

    [Fact]
    public void KeepsAnUnreadableLibraryAside()
    {
        var dir = Fixtures.TempDirectory();
        File.WriteAllText(Path.Combine(dir, "library.json"), "{ this is not json");
        using var library = new StickerLibrary(dir);
        Assert.Equal(0, library.Count);
        Assert.Single(Directory.GetFiles(dir, "library.json.unreadable-*"));
    }
}
