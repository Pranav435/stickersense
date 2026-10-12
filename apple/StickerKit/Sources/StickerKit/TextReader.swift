import CoreGraphics
import Foundation
import Vision

/// On-device OCR. Stickers very often carry the actual message as text ("idk", "Happy
/// birthday!"), so we read it with Vision and hand it to the model as a hint — Vision is
/// far more reliable at exact spelling than a vision-language model.
public enum TextReader {
    /// One line of recognised text and where it is (normalised 0…1, origin bottom-left, as Vision reports).
    public struct Line: Sendable, Equatable {
        public var text: String
        public var box: CGRect
    }

    /// The user's languages that Vision can read, plus English. Fixed languages (rather than
    /// auto-detection) stop emoji being "read" as Arabic, and load fewer models.
    static let languages: [Locale.Language] = {
        let supported = Set(RecognizeTextRequest().supportedRecognitionLanguages.map { $0.minimalIdentifier })
        var picked: [Locale.Language] = []
        for identifier in Locale.preferredLanguages + ["en-US"] {
            let language = Locale.Language(identifier: identifier)
            if supported.contains(language.minimalIdentifier) || supported.contains(language.languageCode?.identifier ?? ""),
               !picked.contains(where: { $0.minimalIdentifier == language.minimalIdentifier }) {
                picked.append(language)
            }
        }
        return picked.isEmpty ? [Locale.Language(identifier: "en-US")] : picked
    }()

    static func makeRequest() -> RecognizeTextRequest {
        var request = RecognizeTextRequest()
        request.recognitionLevel = .accurate
        // Language correction "fixes" sticker slang (idk, lmaooo) and is very slow to load.
        request.usesLanguageCorrection = false
        request.recognitionLanguages = languages
        return request
    }

    /// The first recognition in a process takes 15–25 seconds while Vision loads its models;
    /// later ones take ~0.1 s. Call this at launch so the user never waits for that.
    @discardableResult
    public static func warmUp() -> Task<Void, Never> {
        Task.detached(priority: .utility) {
            let tiny = CGContext(data: nil, width: 64, height: 32, bitsPerComponent: 8, bytesPerRow: 0,
                                 space: CGColorSpaceCreateDeviceGray(), bitmapInfo: CGImageAlphaInfo.none.rawValue)?.makeImage()
            if let tiny { _ = try? await makeRequest().perform(on: tiny) }
        }
    }

    /// Recognised lines. Gives up after `timeout` (returning nothing) rather than making the user
    /// wait while Vision is still warming up; the warm-up carries on in the background.
    public static func readLines(_ image: CGImage, timeout: Duration = .milliseconds(1500)) async -> [Line] {
        let recognize = Task.detached(priority: .userInitiated) { () -> [Line] in
            guard let observations = try? await makeRequest().perform(on: image) else { return [] }
            return observations.compactMap { observation in
                guard let candidate = observation.topCandidates(1).first, candidate.confidence >= 0.4 else { return nil }
                return Line(text: candidate.string, box: observation.boundingBox.cgRect)
            }
        }
        return await Race.value(of: recognize, timeout: timeout) ?? []
    }

    public static func read(_ image: CGImage) async -> String {
        text(of: await readLines(image), label: nil)
    }

    /// The sticker's own text: recognised lines minus chat chrome and anything the screen reader label says.
    public static func text(of lines: [Line], label: String?) -> String {
        lines.map { stripChrome($0.text) }
            .filter { !$0.isEmpty && !isChatChrome($0) && !isInLabel($0, label: label) }
            .joined(separator: "\n")
    }

    /// Lines that belong to the chat around the sticker (sender name, time, ticks), to be painted out
    /// of the picture before the model sees it.
    public static func chromeBoxes(in lines: [Line], label: String?) -> [CGRect] {
        lines.filter { line in
            let stripped = stripChrome(line.text)
            return stripped.isEmpty || isChatChrome(stripped) || isInLabel(stripped, label: label)
                || stripped.count < line.text.trimmingCharacters(in: .whitespaces).count - 3 // had a timestamp in it
        }.map(\.box)
    }

    /// Drops OCR lines that also appear in what the screen reader says about the item — in a
    /// chat that's the sender's name, the time and the chat name, never the sticker's own text.
    public static func removingLabelText(_ ocr: String, label: String?) -> String {
        guard let label, !label.isEmpty else { return ocr }
        let spoken = normalized(label)
        let kept = ocr.split(separator: "\n").map(String.init).filter { line in
            let n = normalized(line)
            return n.isEmpty == false && !(n.count >= 3 && spoken.contains(n))
        }
        return kept.joined(separator: "\n")
    }

    /// True when `text` is just something the screen reader already said (sender name etc.).
    public static func isInLabel(_ text: String, label: String?) -> Bool {
        guard let label else { return false }
        let n = normalized(text)
        return n.count >= 3 && normalized(label).contains(n)
    }

    /// Removes timestamps and read-receipt ticks embedded inside a line of text.
    public static func stripChrome(_ text: String) -> String {
        var t = text
        let patterns = [
            #"\b\d{1,2}[:.]\d{2}(\s?[aApP]\.?[mM]\.?)?\b"#,
            #"[✓✔︎]+"#,
            #"\b(Edited|edited)\b"#,
        ]
        for pattern in patterns {
            t = t.replacingOccurrences(of: pattern, with: "", options: .regularExpression)
        }
        return t.replacingOccurrences(of: #"\s{2,}"#, with: " ", options: .regularExpression)
            .trimmingCharacters(in: .whitespacesAndNewlines.union(.punctuationCharacters.subtracting(CharacterSet(charactersIn: "!?.\"'"))))
    }

    /// Timestamps, read receipts and other chat-interface text that sits around a sticker
    /// inside the VoiceOver cursor's rectangle but isn't part of the sticker.
    public static func isChatChrome(_ line: String) -> Bool {
        let lowered = line.lowercased()
        let patterns = [
            #"^\d{1,2}[:.]\d{2}(\s?[ap]\.?m\.?)?$"#,          // 4:05, 16:05, 4:05 PM
            #"^(today|yesterday)(,?\s.*)?$"#,
            #"^(edited|delivered|read|seen|sent|forwarded)(\s.*)?$"#,
            #"^[✓✔︎✓✓\s]+$"#,
            #"^\d+$"#,                                          // reaction counters
        ]
        return patterns.contains { lowered.range(of: $0, options: .regularExpression) != nil }
    }

    /// Normalises text for comparisons (case, punctuation and spacing don't matter).
    public static func normalized(_ text: String) -> String {
        text.lowercased()
            .folding(options: [.diacriticInsensitive, .widthInsensitive], locale: nil)
            .unicodeScalars.filter { CharacterSet.alphanumerics.contains($0) }
            .map(String.init).joined()
    }
}
