import CoreGraphics
import Foundation
import Testing
@testable import StickerKit

@Suite("Image preparation")
struct ImagePrepTests {
    @Test func flattensTransparencyOntoWhite() throws {
        let sticker = Fixtures.sticker("🐶")
        #expect(sticker.alphaInfo != .none)
        let flat = ImagePrep.flattened(sticker)
        #expect([.none, .noneSkipFirst, .noneSkipLast].contains(flat.alphaInfo))
        // The corner was transparent; it must now be white, not black.
        let print = Thumbprint(flat)
        #expect(print.rgb[0] > 240 && print.rgb[1] > 240 && print.rgb[2] > 240)
    }

    @Test func downscalesButNeverUpscales() {
        let big = Fixtures.sticker("🐶", size: 1600)
        let scaled = ImagePrep.downscaled(big, maxDimension: 768)
        #expect(max(scaled.width, scaled.height) == 768)
        let small = Fixtures.sticker("🐶", size: 200)
        #expect(ImagePrep.downscaled(small, maxDimension: 768).width == 200)
    }

    @Test func samplesFramesEvenly() {
        #expect(ImagePrep.sampleIndices(count: 1, max: 4) == [0])
        #expect(ImagePrep.sampleIndices(count: 3, max: 4) == [0, 1, 2])
        #expect(ImagePrep.sampleIndices(count: 30, max: 4) == [0, 10, 19, 29])
        #expect(ImagePrep.sampleIndices(count: 30, max: 1) == [0])
    }

    @Test func gifRoundTripKeepsFrames() throws {
        let frames = Fixtures.animatedFrames()
        let data = try ImagePrep.gifData(frames)
        let decoded = try ImagePrep.frames(from: data, maxFrames: 4)
        #expect(decoded.count == 3)
    }

    @Test func distinctFramesDropsDuplicates() {
        let still = Fixtures.sticker("🐶", caption: "idk")
        #expect(ImagePrep.distinctFrames([still, still, still]).count == 1)
        #expect(ImagePrep.distinctFrames(Fixtures.animatedFrames()).count == 3)
    }

    @Test func detectsSmallMotion() {
        let a = Fixtures.sticker("👋", caption: "bye", emojiX: 0.12)
        let b = Fixtures.sticker("👋", caption: "bye", emojiX: 0.16)
        #expect(MotionSignature(a).differs(from: MotionSignature(b)))
        #expect(!MotionSignature(a).differs(from: MotionSignature(a)))
    }
}

@Suite("Fingerprints")
struct FingerprintTests {
    @Test func sameStickerAtDifferentSizeMatches() {
        let original = Fixtures.sticker("🐶", caption: "idk lol")
        let onScreen = Fixtures.onScreenCapture(of: original, scale: 0.45)
        let a = Fingerprint(original), b = Fingerprint(onScreen)
        #expect(a.isSameSticker(as: b), "distance \(a.hammingDistance(to: b)), colour \(a.thumb.difference(to: b.thumb))")
    }

    @Test func differentStickersDoNotMatch() {
        let faces = ["😂", "😭", "😅", "🤣", "😊", "😍", "🥲", "😆", "🙂", "😀", "🐶", "🐱", "🐻", "🦊", "🎂", "🔥", "❤️", "🧡"]
        var closest = (Int.max, 1.0, "")
        for i in faces.indices {
            for j in faces.indices where j > i {
                let a = Fingerprint(Fixtures.sticker(faces[i])), b = Fingerprint(Fixtures.sticker(faces[j]))
                let d = a.hammingDistance(to: b), t = a.thumb.difference(to: b.thumb)
                if d < closest.0 { closest = (d, t, "\(faces[i]) vs \(faces[j])") }
                #expect(!a.isSameSticker(as: b), "\(faces[i]) vs \(faces[j]): distance \(d), colour \(t)")
            }
        }
        print("closest different pair:", closest)
    }

    @Test func sameStickerAcrossScalesIsStable() {
        let original = Fixtures.sticker("🥳", caption: "yay", captionColor: .black)
        var worst = (0, 0.0)
        for scale in [0.25, 0.4, 0.6, 0.8, 1.0] {
            let a = Fingerprint(original), b = Fingerprint(Fixtures.onScreenCapture(of: original, scale: scale))
            worst = (max(worst.0, a.hammingDistance(to: b)), max(worst.1, a.thumb.difference(to: b.thumb)))
            #expect(a.isSameSticker(as: b), "scale \(scale): \(a.hammingDistance(to: b)) / \(a.thumb.difference(to: b.thumb))")
        }
        print("worst same-sticker distance:", worst)
    }

    @Test func colourVariantsDoNotMatch() {
        let a = Fingerprint(Fixtures.sticker("❤️", background: .systemRed))
        let b = Fingerprint(Fixtures.sticker("❤️", background: .systemBlue))
        #expect(!a.isSameSticker(as: b))
    }
}

@Suite("Text")
struct TextTests {
    @Test func filtersChatChrome() {
        for chrome in ["4:05 PM", "16:05", "4:05pm", "Edited", "Read 4:05 PM", "Yesterday", "✓✓", "3"] {
            #expect(TextReader.isChatChrome(chrome), "\(chrome)")
        }
        for real in ["idk lol", "Happy birthday!", "LOL", "good morning ☀️"] {
            #expect(!TextReader.isChatChrome(real), "\(real)")
        }
    }

    @Test func stripsChromeInsideLines() {
        #expect(TextReader.stripChrome("yay 4:05 PM") == "yay")
        #expect(TextReader.stripChrome("Good morning! 16:05 ✓✓") == "Good morning!")
        #expect(TextReader.stripChrome("idk lol") == "idk lol")
        #expect(TextReader.stripChrome("Edited 4:05pm") == "")
    }

    @Test func readsStickerText() async {
        await TextReader.warmUp().value
        let image = Fixtures.sticker("🎂", caption: "HAPPY BDAY", captionColor: .black)
        let text = await TextReader.read(ImagePrep.flattened(image))
        #expect(TextReader.normalized(text).contains("happybday"), "OCR read: \(text)")
    }

    @Test func spokenShortFormats() {
        let r = StickerReading(kind: .sticker, subject: "Shiba dog shrugging", text: "idk", vibe: "whatever")
        #expect(SpokenText.short(r) == "Shiba dog shrugging, “idk”.")
        #expect(SpokenText.short(r, includeVibe: true) == "Shiba dog shrugging, “idk”. Meaning: whatever.")
        #expect(SpokenText.short(StickerReading(kind: .photo, subject: "Beach at sunset")) == "Photo: Beach at sunset.")
        #expect(SpokenText.short(StickerReading(kind: .text, subject: "Words", text: "brb")) == "Text: “brb”.")
        #expect(SpokenText.short(StickerReading(kind: .nothing, subject: "")) == "No sticker or picture here.")
    }

    @Test func cleansModelSubjects() {
        #expect(StickerDescriber.cleanSubject("a sticker of a cat waving.") == "A cat waving")
        #expect(StickerDescriber.cleanSubject(" This image shows dog ") == "Dog")
        #expect(StickerDescriber.joinMotion("Cartoon cat", "Slides right, then winks.") == "Cartoon cat, slides right, then winks")
        #expect(StickerDescriber.joinMotion("Cartoon cat.", "") == "Cartoon cat")
    }
}

@Suite("Library")
struct LibraryTests {
    func reading(_ subject: String, text: String = "", vibe: String = "", kind: StickerKind = .sticker) -> StickerReading {
        StickerReading(kind: kind, subject: subject, text: text, vibe: vibe)
    }

    @Test func remembersMatchesAndPersists() async throws {
        let dir = Fixtures.tempDirectory()
        let library = StickerLibrary(directory: dir)
        let image = Fixtures.sticker("🐶", caption: "idk lol")
        let fp = Fingerprint(image)
        let record = await library.remember(frames: [image], fingerprint: fp, reading: reading("Dog shrugging", text: "idk lol"), ocrText: "idk lol", source: .received(app: "net.whatsapp.WhatsApp"))
        #expect(record.imageFile != nil)

        // Seen again on screen, smaller and on a chat background.
        let onScreen = Fixtures.onScreenCapture(of: image, scale: 0.5)
        let hit = await library.match(Fingerprint(onScreen), ocrText: "idk lol 4:05 PM".replacingOccurrences(of: " 4:05 PM", with: ""))
        #expect(hit?.id == record.id)

        await library.saveNow()
        let reloaded = StickerLibrary(directory: dir)
        #expect(await reloaded.count == 1)
        #expect(await reloaded.record(record.id)?.reading?.subject == "Dog shrugging")
    }

    @Test func differentTextOnSameArtIsNotACacheHit() async {
        let library = StickerLibrary(directory: Fixtures.tempDirectory())
        let morning = Fixtures.sticker("☀️", caption: "Good morning", captionColor: .black)
        let night = Fixtures.sticker("☀️", caption: "Good night", captionColor: .black)
        await library.remember(frames: [morning], fingerprint: Fingerprint(morning), reading: reading("Smiling sun", text: "Good morning"), ocrText: "Good morning", source: .received(app: "x"))
        let hit = await library.match(Fingerprint(night), ocrText: "Good night")
        #expect(hit == nil)
    }

    @Test func searchRanksTextAndMeaning() async {
        let library = StickerLibrary(directory: Fixtures.tempDirectory())
        let images = ["🐶", "🎂", "😂"].map { Fixtures.sticker($0) }
        await library.remember(frames: [images[0]], fingerprint: Fingerprint(images[0]), reading: reading("Shiba dog shrugging", text: "idk", vibe: "I don't know, whatever"), ocrText: "idk", source: .received(app: "x"))
        await library.remember(frames: [images[1]], fingerprint: Fingerprint(images[1]), reading: reading("Birthday cake with candles", text: "Happy birthday", vibe: "celebrating"), ocrText: "Happy birthday", source: .received(app: "x"))
        await library.remember(frames: [images[2]], fingerprint: Fingerprint(images[2]), reading: reading("Face crying with laughter", vibe: "that's hilarious"), ocrText: "", source: .giphy(id: "1"))

        #expect(await library.search("birthday").first?.reading?.subject == "Birthday cake with candles")
        #expect(await library.search("dog idk").first?.reading?.subject == "Shiba dog shrugging")
        #expect(await library.search("hilar").first?.reading?.subject == "Face crying with laughter")
        #expect(await library.search("dog cake").isEmpty)
        #expect(await library.search("", filter: .giphy).count == 1)
        #expect(await library.search("").count == 3)
        // History is what was seen on screen, not GIPHY results.
        #expect(await library.history().count == 2)
    }

    @Test func photosAreCachedButNotCollected() async {
        let library = StickerLibrary(directory: Fixtures.tempDirectory())
        let photo = Fixtures.sticker("🏖️", background: .systemTeal)
        let record = await library.remember(frames: [photo], fingerprint: Fingerprint(photo), reading: reading("Beach", kind: .photo), ocrText: "", source: .received(app: "x"))
        #expect(record.imageFile == nil)
        #expect(await library.search("").isEmpty)
        #expect(await library.history().count == 1)
    }

    @Test func importDeduplicates() async throws {
        let dir = Fixtures.tempDirectory()
        let library = StickerLibrary(directory: dir)
        let file = dir.appendingPathComponent("a.png")
        try ImagePrep.pngData(Fixtures.sticker("🐸", caption: "hmm")).write(to: file)
        #expect(await library.importFile(file, source: .whatsApp(relativePath: "a.png")) != nil)
        #expect(await library.importFile(file, source: .whatsApp(relativePath: "a.png")) == nil)
        #expect(await library.undescribed(limit: 10).count == 1)
    }

    @Test func levenshteinAndTextAgreement() {
        #expect(StickerLibrary.levenshtein("kitten", "sitting") == 3)
        #expect(StickerLibrary.levenshtein("", "abc") == 3)
        #expect(StickerLibrary.textsAgree("happybirthday", "happybirthdav"))
        #expect(!StickerLibrary.textsAgree("goodmorning", "goodnight"))
    }
}

@Suite("GIPHY")
struct GiphyTests {
    @Test func parsesSearchResults() throws {
        let json = """
        {"data":[{"id":"abc","title":"Happy Birthday GIF by Disney","alt_text":"","images":{
          "fixed_width":{"url":"https://media.giphy.com/media/abc/200w.gif"},
          "downsized":{"url":"https://media.giphy.com/media/abc/giphy-downsized.gif"}}},
          {"id":"broken","title":"x","images":{}}]}
        """
        let items = try GiphyClient.parse(Data(json.utf8), kind: .gif)
        #expect(items.count == 1)
        #expect(items[0].cleanTitle == "Happy Birthday")
        #expect(items[0].originalURL.absoluteString.hasSuffix("downsized.gif"))
    }

    @Test func parsesPagination() throws {
        let json = """
        {"data":[{"id":"a","title":"Cat GIF","images":{"fixed_width":{"url":"https://x/a.gif"},"fixed_width_downsampled":{"url":"https://x/a-small.gif"}}}],
         "pagination":{"total_count":120,"count":1,"offset":25}}
        """
        let page = try GiphyClient.parsePage(Data(json.utf8), kind: .gif, offset: 25)
        #expect(page.nextOffset == 26)
        #expect(page.hasMore)
        #expect(page.items.first?.smallURL?.absoluteString == "https://x/a-small.gif")
        let last = try GiphyClient.parsePage(Data(json.replacingOccurrences(of: "120", with: "26").utf8), kind: .gif, offset: 25)
        #expect(!last.hasMore)
    }

    @Test func resultsSummaryCountsBothSources() {
        let text = PickerItem.resultsSummary(libraryCount: 3, giphyCount: 50, query: "cat", firstLabel: "Cat", note: "", sendHint: "Return pastes it.")
        #expect(text == "3 of yours and 50 from GIPHY. First: Cat. Return pastes it.")
        #expect(PickerItem.resultsSummary(libraryCount: 0, giphyCount: 0, query: "", firstLabel: nil, note: "", sendHint: "", giphyPending: true) == "Searching GIPHY…")
    }

    @Test func missingKeyIsExplained() async {
        await #expect(throws: GiphyError.self) {
            _ = try await GiphyClient(apiKey: "").search("cat", kind: .gif)
        }
    }
}

@Suite("Screen reader labels")
struct LabelTests {
    let stickerSize = CGSize(width: 138, height: 208)

    @Test func whatsAppStickerLabelsAreStickers() {
        let labels = [
            "\u{200E}\u{200E}Mis pegatinas [AB12CD] sticker from Ana Example, 15:48, \u{200E}Received in Family Group",
            "Sticker from Ana, 4:05 PM, Received",
            "Cute Cats sticker, 15:48, Sent to Family Group, Read",
            "Sticker",
            "スタンプ, 15:48",
        ]
        for label in labels {
            #expect(LabelClassifier.classify(label: label, size: stickerSize, isImageElement: false) == .sticker, "\(label)")
        }
    }

    @Test func gifLabelsAreGIFs() {
        #expect(LabelClassifier.classify(label: "GIF from Ana, 15:48, Received in Family", size: stickerSize, isImageElement: false) == .gif)
        #expect(LabelClassifier.classify(label: "animated image", size: stickerSize, isImageElement: false) == .gif)
    }

    @Test func textMessagesMentioningStickersAreNot() {
        let labels = [
            "Message from Ana Example, send me that sticker pack you used yesterday please, 15:49, Received in Family Group",
            "Your message, lol where did you get that gif from it is amazing, 15:50, Sent to Family Group, Read",
            "Message from Ana, Bye, 15:48, Received in Family Group",
        ]
        for label in labels {
            #expect(LabelClassifier.classify(label: label, size: stickerSize, isImageElement: false) == .notSticker, "\(label)")
        }
    }

    @Test func textAboutStickersIsNotASticker() {
        // Real lines VoiceOver read from this project's README on GitHub.
        let readme = [
            ("Someone sends you a sticker. Your screen reader, ever the diplomat, says \"sticker\". Thanks.", CGSize(width: 700, height: 90)),
            ("StickerSense describes stickers, GIFs and pictures out loud, in any app, for people who use a screen reader.", CGSize(width: 700, height: 60)),
            ("A sticker it hasn't seen before: about 1.2 seconds.", CGSize(width: 500, height: 24)),
            ("A sticker, $3 link", CGSize(width: 110, height: 22)),
            ("A GIF, $5 link", CGSize(width: 90, height: 22)),
            ("Nobody has sent a sticker yet, so the first name here could be yours.", CGSize(width: 300, height: 120)),
        ]
        for (label, size) in readme {
            #expect(LabelClassifier.classify(label: label, size: size, isImageElement: false, inBrowser: true) == .notSticker, "\(label)")
            #expect(LabelClassifier.classify(label: label, size: size, isImageElement: false, inBrowser: false) == .notSticker, "\(label)")
        }
    }

    @Test func webStickersNeedImageSemantics() {
        let size = CGSize(width: 160, height: 160)
        #expect(LabelClassifier.classify(label: "Sticker, image", size: size, isImageElement: false, inBrowser: true) == .sticker)
        #expect(LabelClassifier.classify(label: "GIF", size: size, isImageElement: true, inBrowser: true) == .gif)
        #expect(LabelClassifier.classify(label: "Sticker", size: size, isImageElement: false, inBrowser: true) == .notSticker)
    }

    @Test func textShapedRegionsAreNeverStickers() {
        #expect(!LabelClassifier.isStickerShaped(CGSize(width: 700, height: 60)))
        #expect(!LabelClassifier.isStickerShaped(CGSize(width: 110, height: 22)))
        #expect(LabelClassifier.isStickerShaped(CGSize(width: 138, height: 208)))
        #expect(LabelClassifier.isStickerShaped(CGSize(width: 300, height: 170)))
    }

    @Test func unlabelledImagesUseSize() {
        #expect(LabelClassifier.classify(label: "image", size: CGSize(width: 150, height: 150), isImageElement: true) == .sticker)
        #expect(LabelClassifier.classify(label: "image", size: CGSize(width: 40, height: 40), isImageElement: true) == .notSticker) // avatar
        #expect(LabelClassifier.classify(label: "image", size: CGSize(width: 400, height: 300), isImageElement: true) == .notSticker) // photo
    }

    @Test func labelTextIsRemovedFromOCR() {
        let label = "Mis pegatinas sticker from Ana Example, 15:48, Received in Family Group"
        #expect(TextReader.removingLabelText("Ana Example\nidk lol", label: label) == "idk lol")
        #expect(TextReader.removingLabelText("Ana Example", label: label) == "")
        #expect(TextReader.removingLabelText("good morning", label: nil) == "good morning")
        #expect(TextReader.isInLabel("Ana Example", label: label))
        #expect(!TextReader.isInLabel("idk", label: label))
    }

    @Test func motionJoinsWithoutRepeatingTheSubject() {
        #expect(StickerDescriber.joinMotion("Cartoon cat", "cat's mouth opens, then closes") == "Cartoon cat, mouth opens, then closes")
        #expect(StickerDescriber.joinMotion("Cartoon cat", "The cat waves, then winks.") == "Cartoon cat, waves, then winks")
        #expect(StickerDescriber.joinMotion("Shiba dog", "Wags its tail") == "Shiba dog, wags its tail")
    }
}

@Suite("Screenshots")
struct ScreenshotTextTests {
    @Test func screenSummaryIsNewestFirst() {
        let readings = [StickerReading(kind: .sticker, subject: "Party hat", text: "yay!"), StickerReading(kind: .sticker, subject: "Dog", text: "idk lol")]
        #expect(SpokenText.screen(readings) == "Newest: Party hat, “yay!”. Before that: Dog, “idk lol”.")
        #expect(SpokenText.screen([]) == "No stickers or pictures on screen.")
        #expect(SpokenText.screen([readings[1]]) == "Dog, “idk lol”.")
    }
}

@Suite("Chat chrome masking")
struct MaskingTests {
    @Test func senderNameAndTimeArePaintedOut() async {
        await TextReader.warmUp().value
        let label = "Cute pack sticker from Ana Example, 15:48, Received in Family Group"
        let capture = Fixtures.groupChatCapture(sender: "Ana Example", sticker: "🐱", caption: "hey")
        let lines = await TextReader.readLines(capture)
        #expect(lines.contains { $0.text.contains("Ana") }, "OCR should see the sender: \(lines.map(\.text))")
        let boxes = TextReader.chromeBoxes(in: lines, label: label)
        #expect(boxes.count >= 2, "name and time: \(lines.map(\.text))")
        #expect(TextReader.text(of: lines, label: label).lowercased() == "hey")

        let masked = ImagePrep.masking(capture, boxes: boxes)
        let after = await TextReader.readLines(masked).map(\.text).joined(separator: " ")
        #expect(!after.contains("Ana"), "still visible: \(after)")
        #expect(!after.contains("15:48"), "still visible: \(after)")
        #expect(after.lowercased().contains("hey"))
    }

    @Test func sameStickerFromDifferentSendersHitsTheCache() async {
        await TextReader.warmUp().value
        let library = StickerLibrary(directory: Fixtures.tempDirectory())
        let pipeline = StickerPipeline(describer: StickerDescriber(), library: library)
        let fromAna = Fixtures.groupChatCapture(sender: "Ana Example", sticker: "🐱", caption: "hey")
        let fromRaj = Fixtures.groupChatCapture(sender: "Raj Sample", sticker: "🐱", caption: "hey")
        let probeA = await pipeline.probe(fromAna, screenReaderLabel: "Pack sticker from Ana Example, 15:48")
        await library.remember(frames: [fromAna], fingerprint: probeA.fingerprint, reading: StickerReading(kind: .sticker, subject: "Cat", text: "hey"),
                               ocrText: probeA.ocrText, source: .received(app: "test"))
        let probeB = await pipeline.probe(fromRaj, screenReaderLabel: "Pack sticker from Raj Sample, 15:48")
        #expect(probeB.cached != nil, "distance \(probeA.fingerprint.hammingDistance(to: probeB.fingerprint)) colour \(probeA.fingerprint.thumb.difference(to: probeB.fingerprint.thumb))")
    }
}

@Suite("Sticker text verification")
struct VerifiedTextTests {
    @Test func textNeedsOCRBacking() {
        // The model "read" a description off the picture; OCR saw no text at all.
        #expect(StickerDescriber.verifiedText("Red lips, green eyes, purple band", ocrText: "", label: nil) == "")
        // OCR agrees: use OCR's spelling.
        #expect(StickerDescriber.verifiedText("Hi saab karey", ocrText: "Hi SAAB KAREY??", label: nil) == "Hi SAAB KAREY??")
        // The model adds a word OCR didn't see: stay with what OCR actually read.
        #expect(StickerDescriber.verifiedText("good morning sunshine", ocrText: "good morning", label: nil) == "good morning")
        // Partial word overlap keeps the model's words.
        #expect(StickerDescriber.verifiedText("love you lots", ocrText: "love yu lots xx", label: nil) == "love you lots")
        // Disagreement: short OCR text wins.
        #expect(StickerDescriber.verifiedText("hello", ocrText: "zzz", label: nil) == "zzz")
        // Sender names from the label never count as sticker text.
        #expect(StickerDescriber.verifiedText("Ana Example", ocrText: "Ana Example", label: "Pack sticker from Ana Example, 15:48") == "")
    }
}

@Suite("Blank captures")
struct BlankCaptureTests {
    @Test func flatImagesAreBlankAndNeverCached() async {
        let ctx = CGContext(data: nil, width: 300, height: 120, bitsPerComponent: 8, bytesPerRow: 0,
                            space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue)!
        ctx.setFillColor(CGColor(gray: 0, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 0, width: 300, height: 120))
        let black = ctx.makeImage()!
        #expect(ImagePrep.isBlank(black))
        #expect(!ImagePrep.isBlank(Fixtures.sticker("🐶", caption: "idk")))

        let library = StickerLibrary(directory: Fixtures.tempDirectory())
        await library.remember(frames: [black], fingerprint: Fingerprint(black), reading: StickerReading(kind: .illustration, subject: "Black rectangle"),
                               ocrText: "", source: .received(app: "x"))
        #expect(await library.count == 0)
        #expect(await library.match(Fingerprint(black), ocrText: "") == nil)
    }
}

@Suite("Race")
struct RaceTests {
    @Test func timesOutWithoutWaitingForSlowWork() async {
        let slow = Task<Int, Never> {
            // Ignores cancellation on purpose, like a model load.
            let end = Date().addingTimeInterval(2)
            while Date() < end { try? await Task.sleep(for: .milliseconds(50)) }
            return 1
        }
        let start = Date()
        let value = await Race.value(of: slow, timeout: .milliseconds(200))
        #expect(value == nil)
        #expect(Date().timeIntervalSince(start) < 0.6)
    }

    @Test func returnsFastValue() async {
        let fast = Task<Int, Never> { 42 }
        #expect(await Race.value(of: fast, timeout: .seconds(1)) == 42)
    }
}

@Suite("GIFs")
struct GIFTests {
    @Test func ownGIFLabelsCount() {
        let size = CGSize(width: 517, height: 515)
        #expect(LabelClassifier.classify(label: "\u{200E}Your GIF, 14:19, \u{200E}Sent to Family Group, \u{200E}Sent", size: size, isImageElement: false) == .gif)
        #expect(LabelClassifier.classify(label: "Your sticker, 14:19, Sent to Family Group, Read", size: size, isImageElement: false) == .sticker)
        #expect(LabelClassifier.classify(label: "Message from Ana, can you send me your sticker, 15:49", size: size, isImageElement: false) == .notSticker)
    }

    @Test func strayLettersAreNotStickerText() {
        let lines = [TextReader.Line(text: "V", box: .zero), TextReader.Line(text: "OK!", box: .zero)]
        #expect(TextReader.text(of: lines, label: nil) == "OK!")
    }

    @Test func sentGIFIsRecognisedFromAnyFrames() async {
        let library = StickerLibrary(directory: Fixtures.tempDirectory())
        // A "GIF" of 6 frames, as sent from the picker.
        let gif = (0..<6).map { i in Fixtures.sticker(i < 3 ? "🐱" : "😻", caption: "hi!", emojiX: 0.02 + Double(i) * 0.08) }
        let record = await library.remember(frames: gif, fingerprint: Fingerprint(gif[0]),
                                            reading: StickerReading(kind: .gif, subject: "Cat slides right", animated: true),
                                            ocrText: "hi!", source: .giphy(id: "g1"))
        #expect((record.frameFingerprints?.count ?? 0) == 6)
        // Later, the chat shows it smaller, starting mid-loop.
        let seen = [gif[2], gif[3], gif[5]].map { Fingerprint(Fixtures.onScreenCapture(of: $0, scale: 0.5)) }
        #expect(await library.matchAnimated(seen)?.id == record.id)
        // A different animation doesn't match.
        let other = ["🐶", "🐕", "🦮"].map { Fingerprint(Fixtures.sticker($0)) }
        #expect(await library.matchAnimated(other) == nil)
    }
}

@Suite("Recently sent")
struct RecentlySentTests {
    @Test func stillPreviewOfASentGIFIsRecognised() async {
        let library = StickerLibrary(directory: Fixtures.tempDirectory())
        let gif = (0..<4).map { i in Fixtures.sticker("🐱", caption: "hi!", emojiX: 0.02 + Double(i) * 0.1) }
        let record = await library.remember(frames: gif, fingerprint: Fingerprint(gif[0]),
                                            reading: StickerReading(kind: .gif, subject: "Cat slides right", animated: true),
                                            ocrText: "", source: .giphy(id: "g2"))
        // Not sent yet: no match.
        let preview = Fingerprint(Fixtures.onScreenCapture(of: gif[0], scale: 0.6))
        #expect(await library.matchRecentlySent(preview) == nil)
        await library.markSent(record.id)
        #expect(await library.matchRecentlySent(preview)?.id == record.id)
        // Something unrelated isn't mistaken for it.
        #expect(await library.matchRecentlySent(Fingerprint(Fixtures.sticker("🎂", caption: "yay"))) == nil)
    }
}

@Suite("One GIF, one description")
struct GIFConsistencyTests {
    @Test func pausedPreviewCopyIsFoldedIntoTheAnimatedRecord() async {
        let library = StickerLibrary(directory: Fixtures.tempDirectory())
        let gif = (0..<4).map { i in Fixtures.sticker("👽", caption: "", emojiX: 0.02 + Double(i) * 0.1) }
        // First seen paused: a still description of frame 0.
        await library.remember(frames: [gif[0]], fingerprint: Fingerprint(gif[0]),
                               reading: StickerReading(kind: .gif, subject: "Glowing blob"), ocrText: "", source: .received(app: "w"))
        // Later seen playing: described with motion.
        let animated = await library.remember(frames: [gif[1], gif[2], gif[3]], fingerprint: Fingerprint(gif[1]),
                                              reading: StickerReading(kind: .gif, subject: "Alien explodes", animated: true),
                                              ocrText: "", source: .received(app: "w"), replaceReading: true)
        // Whatever frame is on screen next time, the answer is the animated one.
        for frame in gif {
            #expect(await library.match(Fingerprint(frame), ocrText: "")?.reading?.subject == "Alien explodes")
        }
        #expect(await library.history().filter { $0.reading?.subject == "Glowing blob" }.isEmpty)
        _ = animated
    }

    @Test func pausedPreviewSeenLaterIsRecognised() async {
        let library = StickerLibrary(directory: Fixtures.tempDirectory())
        let gif = (0..<4).map { i in Fixtures.sticker("👽", caption: "", emojiX: 0.02 + Double(i) * 0.1) }
        await library.remember(frames: [gif[1], gif[2], gif[3]], fingerprint: Fingerprint(gif[1]),
                               reading: StickerReading(kind: .gif, subject: "Alien explodes", animated: true),
                               ocrText: "", source: .received(app: "w"))
        #expect(await library.matchAnimationFrame(Fingerprint(gif[0]))?.reading?.subject == "Alien explodes")
        // Remembered now: a plain lookup finds it too.
        #expect(await library.match(Fingerprint(gif[0]), ocrText: "")?.reading?.subject == "Alien explodes")
        // A different still isn't mistaken for it.
        #expect(await library.matchAnimationFrame(Fingerprint(Fixtures.sticker("🎂", caption: "yay"))) == nil)
    }
}
