import CoreGraphics
import Foundation
import FoundationModels

/// What the image was captured from — changes how the model is told to look at it.
public enum DescriptionContext: Sendable, Equatable {
    /// A crop of the item under the VoiceOver cursor. May include chat chrome (timestamps, ticks).
    case cursorItem(screenReaderLabel: String?)
    /// A clean sticker / GIF file (library, WhatsApp import, GIPHY, share sheet).
    case file
}

public enum EngineStatus: Sendable, Equatable {
    case ready
    case appleIntelligenceOff
    case deviceNotEligible
    case modelNotReady
    case noVision

    public var isReady: Bool { self == .ready }

    /// Spoken/visible explanation with what to do about it.
    public var explanation: String {
        switch self {
        case .ready:
            "The on-device model is ready."
        case .appleIntelligenceOff:
            "Apple Intelligence is turned off. Turn it on in System Settings, Apple Intelligence & Siri, so StickerSense can describe stickers on this device."
        case .deviceNotEligible:
            "This device can't run Apple's on-device model, which StickerSense needs."
        case .modelNotReady:
            "Apple's on-device model is still downloading. Leave the device on Wi-Fi and power for a while, then try again."
        case .noVision:
            "This version of Apple's on-device model can't see images. Update the system to the latest version."
        }
    }
}

public enum DescribeError: Error, LocalizedError, Sendable, Equatable {
    case engineUnavailable(EngineStatus)
    case blockedBySafetyFilter
    case busy
    case failed(String)

    public var errorDescription: String? {
        switch self {
        case .engineUnavailable(let status): status.explanation
        case .blockedBySafetyFilter: "Apple's safety filter wouldn't describe this one."
        case .busy: "Still working on the last one."
        case .failed(let message): "Couldn't describe it: \(message)"
        }
    }
}

public struct DescriberOptions: Sendable, Equatable {
    /// Allow Apple Private Cloud Compute for *detailed* descriptions (short ones always stay on device).
    public var allowPrivateCloudForDetail: Bool
    /// Language for descriptions; `nil` = the system language.
    public var language: Locale.Language?
    /// Also generate what the sticker means. Off by default: every extra field costs time.
    public var includeMeaning: Bool

    public init(allowPrivateCloudForDetail: Bool = false, language: Locale.Language? = nil, includeMeaning: Bool = false) {
        self.allowPrivateCloudForDetail = allowPrivateCloudForDetail
        self.language = language
        self.includeMeaning = includeMeaning
    }
}

// The quick readings generate only what gets spoken. Text on the sticker comes from Vision's OCR
// (more exact, and free), so the model doesn't spend time writing it out.

private let kindGuide = "What this is"
private let subjectGuide = "What it shows in at most 12 words. Name the character or animal and its action or expression, e.g. 'Shiba dog shrugging with both paws up'. No leading 'A sticker of'."
private let animatedSubjectGuide = "Who or what it shows in at most 8 words, e.g. 'Cartoon cat'. No leading 'A sticker of'."
private let motionGuide = "Only what moves or changes across the frames, in at most 10 words, starting with a verb and without naming the character again, e.g. 'waves, then winks' or 'slides right and its eyes turn into hearts'. Compare the frames carefully."
private let vibeGuide = "What a person means by sending this, 2 to 6 words, e.g. 'I don't know, whatever' or 'happy birthday, celebrating'."

@Generable(description: "A very short description of one chat sticker or image")
struct QuickReading {
    @Guide(description: kindGuide, .anyOf(StickerKind.allCases.map(\.rawValue))) var kind: String
    @Guide(description: subjectGuide) var subject: String
}

@Generable(description: "A very short description of one chat sticker or image")
struct QuickReadingWithMeaning {
    @Guide(description: kindGuide, .anyOf(StickerKind.allCases.map(\.rawValue))) var kind: String
    @Guide(description: subjectGuide) var subject: String
    @Guide(description: vibeGuide) var vibe: String
}

@Generable(description: "A very short description of one animated chat sticker or GIF")
struct QuickAnimatedReading {
    @Guide(description: kindGuide, .anyOf(StickerKind.allCases.map(\.rawValue))) var kind: String
    @Guide(description: animatedSubjectGuide) var subject: String
    @Guide(description: motionGuide) var motion: String
}

@Generable(description: "A very short description of one animated chat sticker or GIF")
struct QuickAnimatedReadingWithMeaning {
    @Guide(description: kindGuide, .anyOf(StickerKind.allCases.map(\.rawValue))) var kind: String
    @Guide(description: animatedSubjectGuide) var subject: String
    @Guide(description: motionGuide) var motion: String
    @Guide(description: vibeGuide) var vibe: String
}

@Generable(description: "One sticker or picture seen in a chat screenshot")
struct GeneratedScreenItem {
    @Guide(description: "What it shows in at most 12 words, e.g. 'Shiba dog shrugging'. No leading 'A sticker of'.")
    var subject: String

    @Guide(description: "Words written on the sticker itself, exactly as written; empty if none. Never the text of chat messages, names or times.")
    var text: String
}

@Generable(description: "The stickers and pictures in a chat screenshot")
struct GeneratedScreen {
    @Guide(description: "Every sticker, GIF, emoji art, meme or photo in the chat, in order from the TOP of the screen to the BOTTOM. Do not include text-only message bubbles, avatars or app icons.", .maximumCount(8))
    var items: [GeneratedScreenItem]
}

/// Describes stickers with Apple's on-device foundation model (macOS 27 / iOS 27).
public actor StickerDescriber {
    public private(set) var options: DescriberOptions
    private var warmSession: LanguageModelSession?
    private var warmDetailSession: LanguageModelSession?

    public init(options: DescriberOptions = DescriberOptions()) {
        self.options = options
    }

    public func update(options: DescriberOptions) {
        guard options != self.options else { return }
        self.options = options
        warmSession = nil
        warmDetailSession = nil
    }

    // MARK: Availability

    public static var status: EngineStatus {
        let model = SystemLanguageModel.default
        switch model.availability {
        case .available:
            return model.capabilities.contains(.vision) ? .ready : .noVision
        case .unavailable(.appleIntelligenceNotEnabled):
            return .appleIntelligenceOff
        case .unavailable(.deviceNotEligible):
            return .deviceNotEligible
        case .unavailable(.modelNotReady):
            return .modelNotReady
        case .unavailable:
            return .modelNotReady
        }
    }

    public static var privateCloudAvailable: Bool {
        let pcc = PrivateCloudComputeLanguageModel()
        return pcc.isAvailable && pcc.capabilities.contains(.vision)
    }

    static let quickPromptPrefix = "Describe this sticker or image:"

    /// Loads the model and the start of the prompt ahead of time so the next description starts
    /// sooner (measured: about 0.3 s faster per description).
    public func prewarm() {
        guard Self.status.isReady else { return }
        let session = makeShortSession()
        session.prewarm(promptPrefix: Prompt(Self.quickPromptPrefix))
        warmSession = session
    }

    /// Readies the detailed-description session — call it right after speaking a short
    /// description, since a second press for detail usually follows within seconds.
    public func prewarmDetail() {
        guard Self.status.isReady, warmDetailSession == nil, !options.allowPrivateCloudForDetail else { return }
        let session = LanguageModelSession(instructions: detailInstructions())
        session.prewarm()
        warmDetailSession = session
    }

    // MARK: Short description

    /// One quick structured reading of a sticker. `frames` are in time order; pass several
    /// for animated stickers so the motion gets described.
    public func read(frames: [CGImage], context: DescriptionContext, ocrText: String = "") async throws -> StickerReading {
        let status = Self.status
        guard status.isReady else { throw DescribeError.engineUnavailable(status) }
        let prepared = frames.prefix(4).map(ImagePrep.prepareForModel)
        guard !prepared.isEmpty else { throw DescribeError.failed("no image") }

        let session = warmSession ?? makeShortSession()
        warmSession = nil
        defer { prewarm() }

        let animated = prepared.count > 1
        let label = Self.screenReaderLabel(in: context)
        var hint = TextReader.removingLabelText(TextReader.stripChrome(ocrText), label: label)
        if animated {
            // Words often appear partway through an animation: read the other frames too, in parallel.
            let others = await withTaskGroup(of: (Int, String).self) { group in
                for (index, frame) in prepared.enumerated().dropFirst() {
                    group.addTask { (index, TextReader.text(of: await TextReader.readLines(frame), label: label)) }
                }
                var texts: [(Int, String)] = []
                for await result in group { texts.append(result) }
                return texts.sorted { $0.0 < $1.0 }.map(\.1)
            }
            for more in others {
                for line in more.split(separator: "\n").map(String.init) where !TextReader.normalized(hint).contains(TextReader.normalized(line)) {
                    hint += hint.isEmpty ? line : "\n" + line
                }
            }
        }
        let options = GenerationOptions(temperature: 0.2, maximumResponseTokens: 100)
        let withMeaning = self.options.includeMeaning
        let text = Self.stickerText(fromOCR: hint)
        do {
            var kind: String, subject: String, vibe = ""
            if animated {
                let intro = "These are \(prepared.count) frames, in time order, of one animated sticker or GIF. Describe it and what changes between the frames:"
                if withMeaning {
                    let g = try await session.respond(generating: QuickAnimatedReadingWithMeaning.self, options: options) {
                        intro; for frame in prepared { Attachment(frame) }; Self.hints(hint, label)
                    }.content
                    (kind, subject, vibe) = (g.kind, Self.joinMotion(g.subject, g.motion), g.vibe)
                } else {
                    let g = try await session.respond(generating: QuickAnimatedReading.self, options: options) {
                        intro; for frame in prepared { Attachment(frame) }; Self.hints(hint, label)
                    }.content
                    (kind, subject) = (g.kind, Self.joinMotion(g.subject, g.motion))
                }
            } else if withMeaning {
                let g = try await session.respond(generating: QuickReadingWithMeaning.self, options: options) {
                    Self.quickPromptPrefix; for frame in prepared { Attachment(frame) }; Self.hints(hint, label)
                }.content
                (kind, subject, vibe) = (g.kind, g.subject, g.vibe)
            } else {
                let g = try await session.respond(generating: QuickReading.self, options: options) {
                    Self.quickPromptPrefix; for frame in prepared { Attachment(frame) }; Self.hints(hint, label)
                }.content
                (kind, subject) = (g.kind, g.subject)
            }
            var resolvedKind = StickerKind(rawValue: kind.lowercased()) ?? .sticker
            if animated, resolvedKind == .photo { resolvedKind = .gif }
            return StickerReading(kind: resolvedKind, subject: Self.cleanSubject(subject), text: text,
                                  vibe: vibe.trimmingCharacters(in: .whitespacesAndNewlines), animated: animated)
        } catch {
            throw Self.map(error)
        }
    }

    /// OCR hint and screen reader label, appended to a quick prompt.
    static func hints(_ hint: String, _ label: String?) -> Prompt {
        var parts: [String] = []
        if !hint.isEmpty { parts.append("Text that OCR found on it: \(hint.replacingOccurrences(of: "\n", with: " "))") }
        if let label, !label.isEmpty { parts.append("The screen reader calls this item: \(label)") }
        return Prompt(parts.joined(separator: "\n"))
    }

    /// The sticker's words, straight from OCR (already stripped of chat chrome and label text).
    static func stickerText(fromOCR hint: String) -> String {
        let flat = hint.replacingOccurrences(of: "\n", with: " ").trimmingCharacters(in: .whitespacesAndNewlines)
        guard flat.count > 160 else { return flat }
        // Long text means a screenshot or document, not a sticker caption: keep the start.
        let cut = flat.prefix(160)
        return (cut.lastIndex(of: " ").map { String(cut[..<$0]) } ?? String(cut)) + "…"
    }

    static func joinMotion(_ subject: String, _ motion: String) -> String {
        let s = subject.trimmingCharacters(in: .whitespacesAndNewlines.union(CharacterSet(charactersIn: ".")))
        var m = motion.trimmingCharacters(in: .whitespacesAndNewlines.union(CharacterSet(charactersIn: ".")))
        guard !m.isEmpty else { return s }
        guard !s.isEmpty else { return m }
        // Models often repeat the subject ("Cartoon cat" + "cat's mouth opens"): drop the repeat.
        let subjectWords = Set(s.lowercased().split(separator: " ").map { $0.trimmingCharacters(in: .punctuationCharacters) })
        let motionWords = m.split(separator: " ", omittingEmptySubsequences: true)
        if let firstWord = motionWords.first {
            let bare = firstWord.lowercased().replacingOccurrences(of: "'s", with: "").replacingOccurrences(of: "’s", with: "")
            if ["the", "a", "an", "it", "its"].contains(bare) || subjectWords.contains(bare) {
                var rest = motionWords.dropFirst()
                if bare == "the" || bare == "a" || bare == "an", let next = rest.first,
                   subjectWords.contains(next.lowercased().replacingOccurrences(of: "'s", with: "").replacingOccurrences(of: "’s", with: "")) {
                    rest = rest.dropFirst()
                }
                if !rest.isEmpty { m = rest.joined(separator: " ") }
            }
        }
        return "\(s), \(m.prefix(1).lowercased() + m.dropFirst())"
    }

    // MARK: Whole screenshots (iOS Back Tap)

    /// Finds and describes the stickers in a full chat screenshot. Returned newest first
    /// (lowest on screen first), because that's usually the one the user is asking about.
    public func readScreenshot(_ screenshot: CGImage) async throws -> [StickerReading] {
        let status = Self.status
        guard status.isReady else { throw DescribeError.engineUnavailable(status) }
        let session = LanguageModelSession(instructions: "You help a blind person understand chat screenshots. You only describe stickers and pictures, never the text messages.\(languageInstruction())")
        // OCR the whole screen alongside, to check any words the model says are on a sticker.
        async let screenText = TextReader.read(screenshot)
        do {
            let response = try await session.respond(generating: GeneratedScreen.self, options: GenerationOptions(temperature: 0.2, maximumResponseTokens: 400)) {
                "List the stickers and pictures in this chat screenshot."
                Attachment(ImagePrep.flattened(screenshot))
            }
            let ocr = await screenText
            return response.content.items.reversed().map { item in
                // Keep the words only if they're really on screen.
                let claimed = TextReader.stripChrome(item.text)
                let found = !claimed.isEmpty && !TextReader.isChatChrome(claimed)
                    && TextReader.normalized(ocr).contains(TextReader.normalized(claimed))
                let text = found ? claimed : ""
                return StickerReading(kind: .sticker, subject: Self.cleanSubject(item.subject), text: text)
            }
        } catch {
            throw Self.map(error)
        }
    }

    // MARK: Detailed description

    /// A richer few-sentence description for when the short one isn't enough.
    public func detailed(frames: [CGImage], context: DescriptionContext, ocrText: String = "", shortReading: StickerReading? = nil) async throws -> String {
        let status = Self.status
        guard status.isReady else { throw DescribeError.engineUnavailable(status) }
        let prepared = frames.prefix(4).map(ImagePrep.prepareForModel)
        guard !prepared.isEmpty else { throw DescribeError.failed("no image") }
        let animated = prepared.count > 1
        let instructions = detailInstructions()

        let session: LanguageModelSession
        if options.allowPrivateCloudForDetail, Self.privateCloudAvailable {
            session = LanguageModelSession(model: PrivateCloudComputeLanguageModel(), instructions: instructions)
        } else if let warm = warmDetailSession {
            session = warm
            warmDetailSession = nil
        } else {
            session = LanguageModelSession(instructions: instructions)
        }
        let label = Self.screenReaderLabel(in: context)
        do {
            let response = try await session.respond(options: GenerationOptions(temperature: 0.3, maximumResponseTokens: 220)) {
                if animated {
                    "These are \(prepared.count) frames, in time order, of one animated sticker or GIF. Describe it in detail, including how it moves:"
                } else {
                    "Describe this sticker or image in detail:"
                }
                for frame in prepared { Attachment(frame) }
                if !ocrText.isEmpty {
                    "Text that OCR found in the picture (may include chat timestamps to ignore): \(ocrText)"
                }
                if let label, !label.isEmpty {
                    "The screen reader calls this item: \(label)"
                }
                if let shortReading {
                    "A quick first look said: \(shortReading.subject). Correct it if it was wrong."
                }
            }
            return response.content.trimmingCharacters(in: .whitespacesAndNewlines)
        } catch {
            throw Self.map(error)
        }
    }

    // MARK: Sessions & prompts

    private func makeShortSession() -> LanguageModelSession {
        LanguageModelSession(instructions: shortInstructions())
    }

    private func languageInstruction() -> String {
        let language = options.language ?? Locale.current.language
        guard let code = language.languageCode?.identifier, code != "en" else { return "" }
        let name = Locale(identifier: "en").localizedString(forLanguageCode: code) ?? code
        return " Write your descriptions in \(name), but copy text that is written on the sticker exactly as written, in its original language."
    }

    private func shortInstructions() -> String {
        """
        You describe chat stickers, GIFs, emoji and memes for a blind person who uses a screen reader. \
        The picture is usually cropped from a chat app around one message, so ignore chat interface parts: \
        the sender's name, timestamps, read-receipt ticks, reaction counts and bubble backgrounds. Describe only the sticker or picture itself. \
        Be concrete and brief: who or what it is (name well-known cartoon characters, memes and emoji when you are sure), \
        what they are doing, and their facial expression. Never guess the names of real people.\(languageInstruction())
        """
    }

    private func detailInstructions() -> String {
        """
        You describe chat stickers, GIFs, emoji and memes in rich detail for a blind person who uses a screen reader. \
        The picture may be cropped from a chat app; ignore chat interface parts such as the sender's name, timestamps, ticks and bubble backgrounds. \
        In 3 or 4 short, plain sentences, cover: the character or subject (name well-known cartoon characters and memes when you are sure), \
        pose and facial expression, notable colours and art style, any words on the sticker in quotation marks, any motion if there are several frames, \
        and finally what people usually mean when they send it. Speak naturally, as if telling a friend; don't repeat these instructions, \
        don't use lists or markdown, and don't mention frames. Never guess the names of real people.\(languageInstruction())
        """
    }

    static func screenReaderLabel(in context: DescriptionContext) -> String? {
        if case .cursorItem(let label) = context { return label?.trimmingCharacters(in: .whitespacesAndNewlines) }
        return nil
    }

    /// The sticker's own words, only when OCR backs them up. Vision reads text far more reliably
    /// than the language model, which sometimes "reads" descriptions ("red lips") off a picture.
    static func verifiedText(_ modelText: String, ocrText: String, label: String?) -> String {
        let ocr = ocrText.replacingOccurrences(of: "\n", with: " ").trimmingCharacters(in: .whitespacesAndNewlines)
        guard !modelText.isEmpty, !TextReader.isChatChrome(modelText), !TextReader.isInLabel(modelText, label: label) else { return "" }
        guard !ocr.isEmpty else { return "" }
        let a = TextReader.normalized(modelText), b = TextReader.normalized(ocr)
        guard !a.isEmpty, !b.isEmpty else { return "" }
        if b.contains(a) || a.contains(b) || StickerLibrary.textsAgree(a, b) {
            return ocr.count <= 80 ? ocr : modelText
        }
        // Partial agreement: keep the model's words that OCR also saw.
        let ocrWords = Set(StickerLibrary.tokens(ocr))
        let modelWords = StickerLibrary.tokens(modelText)
        let shared = modelWords.filter(ocrWords.contains)
        if !modelWords.isEmpty, Double(shared.count) / Double(modelWords.count) >= 0.5 { return modelText }
        return ocr.count <= 40 ? ocr : ""
    }

    static func cleanSubject(_ subject: String) -> String {
        var s = subject.trimmingCharacters(in: .whitespacesAndNewlines)
        for prefix in ["a sticker of ", "sticker of ", "this sticker shows ", "this image shows ", "the image shows ", "an image of ", "a gif of "] {
            if s.lowercased().hasPrefix(prefix) { s = String(s.dropFirst(prefix.count)) }
        }
        while s.hasSuffix(".") { s.removeLast() }
        guard let first = s.first else { return s }
        return first.uppercased() + s.dropFirst()
    }

    static func map(_ error: any Error) -> DescribeError {
        if let error = error as? DescribeError { return error }
        if let error = error as? LanguageModelError {
            switch error {
            case .guardrailViolation, .refusal: return .blockedBySafetyFilter
            case .rateLimited: return .busy
            case .unsupportedCapability: return .engineUnavailable(.noVision)
            case .timeout: return .failed("it took too long")
            default: return .failed(error.localizedDescription)
            }
        }
        return .failed(error.localizedDescription)
    }
}
