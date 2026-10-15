import CoreGraphics
import Foundation
import Testing
@testable import StickerKit

@Suite("Making stickers")
struct GenerateTests {
    // MARK: Ideas

    @Test func quotedWordsAreTheCaption() {
        #expect(StickerIdeas.splitQuoted("corgi \"thank you\"") == ("corgi", "thank you"))
        #expect(StickerIdeas.splitQuoted("“Happy birthday” cake with candles") == ("cake with candles", "Happy birthday"))
        #expect(StickerIdeas.splitQuoted("sleepy cat") == ("sleepy cat", nil))
        #expect(StickerIdeas.splitQuoted("cat \"\"") == ("cat \"\"", nil))
    }

    @Test func fallbackDrawsTheSearch() {
        let idea = StickerIdeas.fallback(for: "sleepy cat")
        #expect(idea.caption.isEmpty)
        #expect(idea.pictures == Array(repeating: "sleepy cat", count: StickerIdeas.variations))
        let quotedOnly = StickerIdeas.fallback(for: "\"lol\"")
        #expect(quotedOnly.caption == "lol")
        #expect(quotedOnly.pictures[0].contains("lol"))
    }

    @Test func cleansTheModelsPlan() {
        let idea = StickerIdeas.cleaned(
            caption: "\"Thank you\"",
            pictures: ["1. a corgi bowing politely.", "2) a corgi bowing politely", "a corgi holding a sign that says thanks", "- a cat with flowers", ""],
            query: "corgi saying thank you"
        )
        #expect(idea.caption == "Thank you")
        #expect(idea.pictures == ["a corgi bowing politely", "a cat with flowers", "a corgi bowing politely", "a corgi bowing politely"])
    }

    @Test func quotedCaptionBeatsTheModel() {
        let idea = StickerIdeas.cleaned(caption: "Thanks!", pictures: ["a corgi"], query: "corgi \"ty <3\"")
        #expect(idea.caption == "ty <3")
        #expect(idea.pictures.count == StickerIdeas.variations)
    }

    @Test func dropsEmptyCaptionWords() {
        #expect(StickerIdeas.cleaned(caption: "none", pictures: ["a cat"], query: "cat").caption.isEmpty)
        #expect(StickerIdeas.cleaned(caption: "", pictures: [], query: "pizza").pictures == Array(repeating: "pizza", count: 4))
    }

    @Test func aCaptionThatOnlyNamesWhatsDrawnIsDropped() {
        let angry = StickerIdeas.cleaned(caption: "angry cat", pictures: ["an angry cat growling", "an angry cat with a fist raised",
                                                                          "an angry cat standing on hind legs", "an angry cat biting its tail"], query: "angry cat")
        #expect(angry.caption.isEmpty)
        let birthday = StickerIdeas.cleaned(caption: "happy birthday", pictures: ["a birthday cake with candles lit", "a puppy in a party hat",
                                                                                  "a bear with a balloon", "a cat with a present"], query: "happy birthday")
        #expect(birthday.caption == "happy birthday")
    }

    @Test func peopleNobodyAskedForBecomeAnimals() {
        let idea = StickerIdeas.cleaned(caption: "good morning", pictures: ["a person with a bright smile holding a rose", "a man wearing a green jacket",
                                                                            "a sleepy owl with a coffee cup", "a woman with glasses"], query: "good morning")
        #expect(idea.caption == "good morning")
        #expect(idea.pictures == ["a cute cat feeling good morning", "a cute dog feeling good morning", "a sleepy owl with a coffee cup", "a cute bunny feeling good morning"])
        #expect(StickerIdeas.cleaned(caption: "", pictures: ["a girl dancing in the rain"], query: "girl dancing").pictures[0] == "a girl dancing in the rain")
        #expect(StickerIdeas.asksForLettering("a gift box with a card reading “Happy Birthday”"))
    }

    @Test func longCaptionsAreCutAtAWord() {
        let caption = StickerIdeas.clampCaption("this is a really very long caption that goes on and on forever")
        #expect(caption.count <= StickerIdeas.maxCaptionLength)
        #expect(!caption.hasSuffix(" "))
        #expect("this is a really very long caption that goes on".hasPrefix(caption))
    }

    @Test func letteringIsLeftToStickerSense() {
        #expect(StickerIdeas.asksForLettering("a banner reading hello"))
        #expect(StickerIdeas.asksForLettering("a dog with the words good boy"))
        #expect(!StickerIdeas.asksForLettering("a cat texting on a phone"))
    }

    @Test func promptAddsTheStickerStyle() {
        #expect(StickerIdeas.prompt(for: " a happy corgi. ") == "a happy corgi, \(StickerIdeas.style)")
    }

    // MARK: Art

    @Test func distanceTransformIsEuclidean() {
        var inside = [Bool](repeating: false, count: 7 * 7)
        inside[3 * 7 + 3] = true
        let d = StickerArt.distanceToInside(inside, width: 7, height: 7)
        #expect(d[3 * 7 + 3] == 0)
        #expect(abs(d[3 * 7 + 6] - 3) < 1e-9)
        #expect(abs(d[0] - (18.0).squareRoot()) < 1e-9)
    }

    @Test func composesASquareStickerWithBorderAndCaption() throws {
        let subject = Self.disc(size: 300, color: CGColor(red: 0.9, green: 0.2, blue: 0.2, alpha: 1))
        let art = try #require(StickerArt.compose(subject, caption: "Thank you"))
        #expect(art.width == StickerArt.size && art.height == StickerArt.size)
        let pixels = Self.pixels(art)
        // Corners stay transparent.
        #expect(pixels.alpha(0, 0) == 0)
        #expect(pixels.alpha(511, 511) == 0)
        // Centre column, top down: transparent, then the white border, then the red subject.
        let column = (0..<512).map { pixels.rgba(256, $0) }
        let firstVisible = try #require(column.firstIndex { $0.a > 200 })
        #expect(column[firstVisible + 2].r > 230 && column[firstVisible + 2].g > 230, "white border above the subject")
        #expect(column.contains { $0.r > 200 && $0.g < 80 }, "the red subject")
        // Dark lettering in the lower part.
        let lower = (330..<500).flatMap { y in (60..<452).map { pixels.rgba($0, y) } }
        #expect(lower.filter { $0.a > 250 && $0.r < 60 && $0.g < 60 }.count > 800, "caption letters")
        // The subject is no longer cut off by the canvas: it's scaled to fit.
        #expect(pixels.alpha(256, 3) == 0)
    }

    @Test func noCaptionCentresTheSubject() throws {
        let subject = Self.disc(size: 200, color: CGColor(red: 0.2, green: 0.4, blue: 0.9, alpha: 1))
        let art = try #require(StickerArt.compose(subject, caption: ""))
        let pixels = Self.pixels(art)
        let rows = (0..<512).filter { y in pixels.alpha(256, y) > 128 }
        let top = try #require(rows.first), bottom = try #require(rows.last)
        #expect(abs((top + bottom) / 2 - 256) <= 2)
    }

    @Test func longCaptionsBreakIntoTwoLines() {
        let lines = StickerArt.captionLines("good morning sunshine my friend", maxWidth: 480, maxHeight: 170, scale: 1)
        #expect(lines.count == 2)
        #expect(lines.allSatisfy { $0.width <= 480 })
        #expect(StickerArt.captionLines("lol", maxWidth: 480, maxHeight: 170, scale: 1).count == 1)
    }

    @Test func cutsOutTheSubjectOfAGeneratedPicture() throws {
        // A cartoon on white, like the image model's: Vision lifts it off the background.
        let picture = Fixtures.sticker("🐶", size: 512)
        let flat = ImagePrep.flattened(picture)
        let cut = try #require(StickerArt.cutOut(flat))
        let pixels = Self.pixels(cut)
        #expect(pixels.alpha(4, 4) < 10)
        #expect(pixels.alpha(256, 256) > 200)
    }

    // MARK: Library

    @Test func generatedSourceRoundTripsInTheSharedFormat() throws {
        let data = try JSONEncoder().encode(StickerSource.generated(prompt: "corgi saying thank you"))
        #expect(String(decoding: data, as: UTF8.self) == #"{"generated":{"prompt":"corgi saying thank you"}}"#)
        #expect(try JSONDecoder().decode(StickerSource.self, from: data) == .generated(prompt: "corgi saying thank you"))
        #expect(StickerSource.generated(prompt: "x").filterTag == .generated)
        #expect(StickerSource.generated(prompt: "x").origin == "Made by you")
        #expect(StickerSource.whatsApp(relativePath: "a").origin == "From WhatsApp")
    }

    @Test func pastedGeneratedStickerJoinsTheLibrary() async throws {
        let dir = Fixtures.tempDirectory()
        let library = StickerLibrary(directory: dir)
        let search = PickerSearch(library: library, describer: StickerDescriber())
        let art = try #require(StickerArt.compose(Self.disc(size: 300, color: CGColor(red: 0.9, green: 0.6, blue: 0.1, alpha: 1)), caption: "yay"))
        let file = dir.appendingPathComponent("made.png")
        try ImagePrep.pngData(art).write(to: file)
        let sticker = GeneratedSticker(query: "orange ball saying yay", caption: "yay", picture: "an orange ball", fileURL: file, number: 1,
                                       reading: StickerReading(kind: .illustration, subject: "Orange ball", text: "yay"))
        #expect(GeneratedSticker.label(sticker) == "New sticker 1: Orange ball, “yay”")
        await search.markSent(.generated(sticker))
        let found = await library.search("orange ball", filter: .generated)
        #expect(found.count == 1)
        #expect(found.first?.timesSent == 1)
        #expect(found.first?.accessibilityLabel == "Orange ball, “yay”. Made by you")
        // The search that made it finds it again.
        #expect(await library.search("saying", filter: .all).count == 1)
    }

    @Test func undescribedLabelSaysWhatWasAskedFor() {
        let sticker = GeneratedSticker(query: "q", caption: "lol", picture: "a laughing cat", fileURL: URL(fileURLWithPath: "/x.png"), number: 3)
        #expect(GeneratedSticker.label(sticker) == "New sticker 3: a laughing cat, “lol”, still being described")
    }

    // MARK: Helpers

    static func disc(size: Int, color: CGColor) -> CGImage {
        let ctx = ImagePrep.rgbContext(width: size, height: size, alpha: true)!
        ctx.setFillColor(color)
        ctx.fillEllipse(in: CGRect(x: 20, y: 20, width: size - 40, height: size - 40))
        return ctx.makeImage()!
    }

    struct Pixels {
        let data: [UInt8]
        let width: Int
        func rgba(_ x: Int, _ y: Int) -> (r: UInt8, g: UInt8, b: UInt8, a: UInt8) {
            let i = (y * width + x) * 4
            return (data[i], data[i + 1], data[i + 2], data[i + 3])
        }
        func alpha(_ x: Int, _ y: Int) -> UInt8 { rgba(x, y).a }
    }

    /// Top-down RGBA (premultiplied) pixels.
    static func pixels(_ image: CGImage) -> Pixels {
        let ctx = CGContext(data: nil, width: image.width, height: image.height, bitsPerComponent: 8, bytesPerRow: image.width * 4,
                            space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
        let buffer = UnsafeBufferPointer(start: ctx.data!.bindMemory(to: UInt8.self, capacity: image.width * image.height * 4), count: image.width * image.height * 4)
        return Pixels(data: Array(buffer), width: image.width)
    }
}

/// The real on-device model planning stickers (`STICKERSENSE_MODEL_TESTS=1`).
@Suite("Planning stickers on device", .enabled(if: ProcessInfo.processInfo.environment["STICKERSENSE_MODEL_TESTS"] == "1"), .serialized)
struct StickerPlanModelTests {
    @Test func chatPhrasesGetACaption() async {
        let start = Date()
        let idea = await StickerIdeaWriter.ideas(for: "corgi saying thank you")
        print("PLAN (\(String(format: "%.2f", Date().timeIntervalSince(start)))s):", idea)
        #expect(idea.caption.lowercased().contains("thank"))
        #expect(idea.pictures.count == StickerIdeas.variations)
        #expect(idea.pictures.contains { $0.lowercased().contains("corgi") })
        #expect(Set(idea.pictures).count >= 3, "different ideas")
    }

    @Test func picturesDontGetACaption() async {
        let idea = await StickerIdeaWriter.ideas(for: "sleepy panda")
        print("PLAN:", idea)
        #expect(idea.caption.isEmpty)
        #expect(idea.pictures.allSatisfy { $0.lowercased().contains("panda") })
    }

    @Test func reactionsBecomeACharacter() async {
        let idea = await StickerIdeaWriter.ideas(for: "lol")
        print("PLAN:", idea)
        #expect(idea.caption.lowercased().contains("lol"))
        #expect(!idea.pictures[0].lowercased().hasPrefix("lol"))
    }
}
