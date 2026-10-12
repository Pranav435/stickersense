import AppKit
import AVFoundation

/// Gets text to the user's ears (and braille display).
///
/// VoiceOver only speaks accessibility announcements from the app in front, and StickerSense
/// is usually in the background (the user is in WhatsApp). So when we're not in front we
/// use VoiceOver's AppleScript `output` command, which VoiceOver always speaks in the user's
/// own voice. With VoiceOver off, the system speech synthesizer speaks instead.
@MainActor
final class Announcer {
    enum Route: String, CaseIterable, Identifiable {
        case automatic, announcement, voiceOverOutput
        var id: String { rawValue }
        var title: String {
            switch self {
            case .automatic: "Automatic (recommended)"
            case .announcement: "Accessibility announcement only"
            case .voiceOverOutput: "VoiceOver AppleScript output only"
            }
        }
    }

    private let voiceOver: VoiceOverBridge
    private let synthesizer = AVSpeechSynthesizer()
    var route: Route = .automatic
    var log: DebugLog?

    init(voiceOver: VoiceOverBridge) {
        self.voiceOver = voiceOver
    }

    /// - Parameter interrupt: cut off whatever VoiceOver is saying (used for answers the user
    ///   explicitly asked for with the shortcut). Auto descriptions queue politely instead.
    func say(_ text: String, interrupt: Bool = true) {
        guard !text.isEmpty else { return }
        guard voiceOver.isRunning else {
            speakWithSynthesizer(text, interrupt: interrupt)
            return
        }
        let useAnnouncement = route == .announcement || (route == .automatic && NSApp.isActive)
        if useAnnouncement {
            let priority: NSAccessibilityPriorityLevel = interrupt ? .high : .medium
            NSAccessibility.post(
                element: NSApp as Any,
                notification: .announcementRequested,
                userInfo: [.announcement: text, .priority: priority.rawValue]
            )
        } else {
            Task {
                do {
                    try await voiceOver.output(text)
                } catch {
                    log?.add("VoiceOver output failed (\(error.localizedDescription)); using system voice")
                    speakWithSynthesizer(text, interrupt: interrupt)
                }
            }
        }
        verifySpoken(text, via: useAnnouncement ? "announcement" : "VoiceOver output")
    }

    /// Checks VoiceOver's last phrase shortly afterwards and logs whether it really spoke —
    /// the only way to notice a silently dropped announcement.
    private func verifySpoken(_ text: String, via route: String) {
        guard let log else { return }
        let probe = String(text.prefix(24))
        // One look, not a poll loop: every query runs on VoiceOver's main thread.
        Task {
            try? await Task.sleep(for: .milliseconds(700))
            let phrase = (try? await voiceOver.lastPhrase()) ?? ""
            log.add(phrase.contains(probe) ? "Spoken via \(route) ✓" : "Not confirmed spoken via \(route) (VoiceOver may have moved on)")
        }
    }

    private func speakWithSynthesizer(_ text: String, interrupt: Bool) {
        if interrupt, synthesizer.isSpeaking { synthesizer.stopSpeaking(at: .immediate) }
        let utterance = AVSpeechUtterance(string: text)
        utterance.prefersAssistiveTechnologySettings = true
        synthesizer.speak(utterance)
    }
}
