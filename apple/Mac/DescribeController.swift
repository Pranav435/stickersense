import AppKit
import ApplicationServices
import StickerKit

/// The heart of the Mac app: describes whatever the VoiceOver cursor is on, when the
/// shortcut is pressed or (auto mode) when the cursor lands on a sticker or GIF.
@MainActor
final class DescribeController {
    private let pipeline: StickerPipeline
    private let voiceOver: VoiceOverBridge
    private let capturer: ScreenCapturer
    private let announcer: Announcer
    private let earcons: Earcons
    private let settings: SettingsStore
    private let log: DebugLog

    /// How long after a description a second press means "more detail".
    let detailWindow: TimeInterval = 3

    private enum Level { case short, detailed }

    private struct Spoken {
        var target: VOCursor
        var record: StickerRecord
        var frames: [CGImage]
        var context: DescriptionContext
        var level: Level
        var text: String
        var at: Date
        var viaAuto: Bool
    }

    private enum JobKind { case short, detail }

    private struct Job {
        var id = UUID()
        var target: VOCursor
        var kind: JobKind
        var auto: Bool
        var task: Task<Void, Never>
    }

    private var last: Spoken?
    /// The one description being worked on right now. Only one runs at a time, so repeated
    /// presses never pile up duplicate answers.
    private var job: Job?
    /// The user pressed again on the same item while its short description was still coming.
    private var detailRequested = false
    private var autoLoop: Task<Void, Never>?
    private var lastPolledCursor: VOCursor?
    private var lastCursorQuery = Date.distantPast
    private var quietUntil = Date.distantPast
    private var appSwitchObserver: NSObjectProtocol?
    private var lastEngineWarning: Date?

    init(pipeline: StickerPipeline, voiceOver: VoiceOverBridge, capturer: ScreenCapturer, announcer: Announcer,
         earcons: Earcons, settings: SettingsStore, log: DebugLog) {
        self.pipeline = pipeline
        self.voiceOver = voiceOver
        self.capturer = capturer
        self.announcer = announcer
        self.earcons = earcons
        self.settings = settings
        self.log = log
    }

    // MARK: Shortcut

    func shortcutPressed() {
        Task { await handleShortcut() }
    }

    private func handleShortcut() async {
        let target: VOCursor
        do {
            target = try await currentTarget()
        } catch {
            fail(error)
            return
        }
        log.add("Shortcut on: \(target.text.isEmpty ? "(no label)" : target.text) \(target.bounds)")

        if let job {
            if job.target.isLikelySameItem(as: target) {
                // Already working on this item. A press during the short description asks for
                // the detail straight after; a press during the detail just confirms we heard it.
                if job.kind == .short { detailRequested = true }
                earcons.play(.tick)
                return
            }
            // The user moved on: drop the old request and start on the new item.
            job.task.cancel()
            self.job = nil
        }

        if let last, last.target.isLikelySameItem(as: target) {
            let recent = Date().timeIntervalSince(last.at) < detailWindow
            // After an automatic description, the shortcut on the same item always means "more".
            if recent || last.viaAuto {
                switch last.level {
                case .short: start(.detail, target: target, auto: false) { await self.runDetail(for: last) }
                case .detailed: speak(last.text, interrupt: true, chime: false)
                }
                return
            }
        }
        await describeShort(target, auto: false, verdict: nil)
    }

    /// The VoiceOver cursor, or (VoiceOver off) the focused element or the item under the mouse.
    private func currentTarget() async throws -> VOCursor {
        if voiceOver.isRunning {
            return try await voiceOver.cursor()
        }
        if let rect = FocusFallback.focusedElementFrame() {
            return VOCursor(bounds: rect, text: "")
        }
        return VOCursor(bounds: FocusFallback.rectUnderMouse(), text: "")
    }

    // MARK: Jobs

    private func start(_ kind: JobKind, target: VOCursor, auto: Bool, work: @escaping @MainActor () async -> Void) {
        let id = UUID()
        let task = Task { @MainActor in
            await work()
            if self.job?.id == id { self.job = nil }
        }
        job = Job(id: id, target: target, kind: kind, auto: auto, task: task)
        // Never hang silently: give up after a generous limit.
        let limit: Duration = kind == .short ? .seconds(25) : .seconds(30)
        Task { @MainActor in
            try? await Task.sleep(for: limit)
            guard self.job?.id == id else { return }
            task.cancel()
            self.job = nil
            self.log.add("Timed out after \(limit)")
            if !auto { self.fail(message: "That took too long. Try again in a moment.") }
        }
    }

    /// Feedback while waiting: a soft blip after a moment (skipped for instant cache hits),
    /// then a blip every few seconds, and "Still working" once if it's taking a while.
    private func startProgressFeedback() -> Task<Void, Never> {
        Task { @MainActor in
            try? await Task.sleep(for: .milliseconds(180))
            guard !Task.isCancelled else { return }
            earcons.play(.working)
            var ticks = 0
            while !Task.isCancelled {
                try? await Task.sleep(for: .milliseconds(2200))
                guard !Task.isCancelled else { return }
                ticks += 1
                if ticks == 3 {
                    announcer.say("Still working.", interrupt: false)
                } else {
                    earcons.play(.working)
                }
            }
        }
    }

    // MARK: Describing

    private func describeShort(_ target: VOCursor, auto: Bool, verdict: StickerDetector.Verdict?) async {
        if isTooLarge(target.bounds) {
            if !auto {
                fail(message: "The VoiceOver cursor is on a large area, like a whole list or window. Move it onto the sticker, then press the shortcut again.")
            }
            return
        }
        detailRequested = false
        start(.short, target: target, auto: auto) { await self.runShort(target, auto: auto, verdict: verdict) }
    }

    private func runShort(_ target: VOCursor, auto: Bool, verdict: StickerDetector.Verdict?) async {
        let started = Date()
        var timings: [String] = []
        func mark(_ name: String, since: Date) { timings.append(String(format: "%@ %.2f", name, Date().timeIntervalSince(since))) }
        let progress = auto ? nil : startProgressFeedback()
        defer { progress?.cancel() }
        do {
            let rect = target.bounds
            let label = target.text.isEmpty ? nil : target.text
            var stage = Date()
            let first = try await capturer.capture(rect)
            mark("capture", since: stage); stage = Date()
            // A flat, empty capture (blank or protected screen area): nothing to describe.
            if ImagePrep.isBlank(first) {
                log.add("Blank capture at \(rect), not describing")
                if !auto { fail(message: "There's nothing visible there to describe.") }
                return
            }
            // When VoiceOver's label says "sticker from Ana…", everything in it (name, time, chat) is
            // chat chrome. On a text message the label *is* the message, so don't treat it as chrome.
            let labelVerdict = verdict ?? LabelClassifier.classify(label: target.text, size: rect.size, isImageElement: false)
            let probe = await pipeline.probe(first, screenReaderLabel: labelVerdict == .notSticker ? nil : label)
            mark("ocr+cache", since: stage); stage = Date()
            let context = DescriptionContext.cursorItem(screenReaderLabel: label)
            let app = NSWorkspace.shared.frontmostApplication?.bundleIdentifier ?? "unknown"
            let source = StickerSource.received(app: app)
            // Paint out the sender's name and the time so neither the model nor the library sees them.
            let maskedFirst = ImagePrep.masking(first, boxes: probe.chromeBoxes)
            var frames: [CGImage] = [maskedFirst]
            let record: StickerRecord
            if probe.cached != nil {
                record = try await pipeline.describe(frames: frames, probe: probe, context: context, source: source)
            } else {
                // Most stickers are still: start describing the first frame right away while we
                // check for motion, instead of waiting for the check to finish.
                let describer = pipeline.describer
                let ocr = probe.ocrText
                let speculative = verdict == .gif ? nil : Task { try await describer.read(frames: [maskedFirst], context: context, ocrText: ocr) }
                let (reading, captured) = try await withTaskCancellationHandler {
                    var captured = try await capturer.captureMotion(rect, first: first, alreadyKnownAnimated: verdict == .gif)
                    mark("motion", since: stage); stage = Date()
                    // VoiceOver scrolls items into view; a scroll mid-capture looks like animation.
                    if captured.count > 1, voiceOver.isRunning, let now = try? await voiceOver.cursor(), now.bounds != rect {
                        log.add("Cursor moved during capture (scroll?), treating as still")
                        captured = [first]
                    }
                    let masked = captured.map { ImagePrep.masking($0, boxes: probe.chromeBoxes) }
                    if masked.count == 1, let speculative {
                        return (try await speculative.value, masked)
                    }
                    speculative?.cancel()
                    return (try await describer.read(frames: masked, context: context, ocrText: ocr), masked)
                } onCancel: {
                    speculative?.cancel()
                }
                frames = captured
                record = await pipeline.store(frames: frames, probe: probe, reading: reading, source: source)
            }
            mark("model", since: stage)
            try Task.checkCancellation()
            guard let reading = record.reading else { return }
            progress?.cancel()

            if auto {
                // Only stickers and GIFs are announced automatically, and only if the user is still there.
                guard reading.kind.isCollectible else {
                    log.add("Auto: skipped \(reading.kind.rawValue)")
                    return
                }
                guard let now = try? await voiceOver.cursor(), now.isLikelySameItem(as: target) else {
                    log.add("Auto: cursor moved on, not speaking")
                    return
                }
            }
            let text = SpokenText.short(reading, includeVibe: settings.includeMeaning)
            log.add(String(format: "%@ in %.2fs%@ [%@, %d frame%@]: %@", auto ? "Auto" : "Short", Date().timeIntervalSince(started),
                           probe.cached != nil ? " (cached)" : "", timings.joined(separator: ", "), frames.count, frames.count == 1 ? "" : "s", text))
            speak(text, interrupt: !auto, chime: true)
            // A press for the detail usually follows within seconds: get its session ready now.
            Task { await pipeline.describer.prewarmDetail() }
            let spoken = Spoken(target: target, record: record, frames: frames, context: context, level: .short, text: text, at: Date(), viaAuto: auto)
            last = spoken
            NotificationCenter.default.post(name: .stickerLibraryChanged, object: nil)

            if detailRequested {
                detailRequested = false
                await runDetail(for: spoken)
            }
        } catch is CancellationError {
            log.add("Cancelled (moved to another item)")
        } catch {
            if Task.isCancelled {
                log.add("Cancelled (moved to another item)")
                return
            }
            if auto {
                log.add("Auto failed: \(error.localizedDescription)")
                warnOnceIfEngineUnavailable(error)
            } else {
                fail(error)
            }
        }
    }

    private func runDetail(for spoken: Spoken) async {
        let progress = startProgressFeedback()
        defer { progress.cancel() }
        do {
            let started = Date()
            let text = try await pipeline.detail(for: spoken.record, frames: spoken.frames, context: spoken.context)
            try Task.checkCancellation()
            progress.cancel()
            log.add(String(format: "Detail in %.2fs: %@", Date().timeIntervalSince(started), text))
            speak(text, interrupt: true, chime: true)
            var updated = spoken
            updated.level = .detailed
            updated.text = text
            updated.at = Date()
            updated.viaAuto = false
            last = updated
        } catch is CancellationError {
            log.add("Detail cancelled")
        } catch {
            if !Task.isCancelled { fail(error) }
        }
    }

    // MARK: Auto mode

    func setAutoDescribe(running: Bool) {
        autoLoop?.cancel()
        autoLoop = nil
        if let job, job.auto { job.task.cancel(); self.job = nil }
        lastPolledCursor = nil
        guard running else { return }
        if appSwitchObserver == nil {
            appSwitchObserver = NSWorkspace.shared.notificationCenter.addObserver(
                forName: NSWorkspace.didActivateApplicationNotification, object: nil, queue: .main
            ) { [weak self] _ in
                MainActor.assumeIsolated {
                    self?.quietUntil = Date().addingTimeInterval(1.5)
                    self?.lastPolledCursor = nil
                }
            }
        }
        autoLoop = Task { @MainActor [weak self] in
            while !Task.isCancelled {
                guard let self else { return }
                if self.shouldLookAtCursor() {
                    self.lastCursorQuery = Date()
                    await self.autoTick()
                }
                try? await Task.sleep(for: .milliseconds(120))
            }
        }
    }

    /// Only ask VoiceOver where its cursor is after the user has pressed a key or touched the
    /// trackpad (the cursor can't move otherwise), plus a slow check every few seconds.
    /// Reading the last-input time is a cheap local call; it never touches VoiceOver or the
    /// chat app, and it sees VoiceOver's own key commands even though VoiceOver swallows them.
    private func shouldLookAtCursor() -> Bool {
        // VoiceOver is busiest right after an app switch; stay out of its way until it settles.
        if Date() < quietUntil { return false }
        let sinceInput = CGEventSource.secondsSinceLastEventType(.hidSystemState, eventType: CGEventType(rawValue: ~0)!)
        let lastInput = Date().addingTimeInterval(-sinceInput)
        let sinceQuery = Date().timeIntervalSince(lastCursorQuery)
        // New input since we last looked, and VoiceOver has had a moment to move.
        if lastInput > lastCursorQuery, sinceInput >= 0.2, sinceQuery >= 0.25 { return true }
        return sinceQuery >= 2.5
    }

    private func autoTick() async {
        guard settings.autoDescribe, voiceOver.isRunning else { return }
        let front = NSWorkspace.shared.frontmostApplication
        guard front?.bundleIdentifier != Bundle.main.bundleIdentifier,
              StickerDetector.isWatched(bundleID: front?.bundleIdentifier, scope: settings.autoScope) else { return }
        // Poll only the rectangle (VoiceOver answers in ~1 ms); ask for the label only after a move.
        guard let bounds = try? await voiceOver.cursorBounds() else { return }
        if let lastPolledCursor, lastPolledCursor.bounds == bounds { return }
        guard let cursor = try? await voiceOver.cursor() else { return }
        lastPolledCursor = cursor
        // The cursor moved: an automatic description for the previous item is no longer wanted.
        if let job, job.auto, !job.target.isLikelySameItem(as: cursor) { job.task.cancel(); self.job = nil }

        // Decide from VoiceOver's label alone when possible. Asking the app for the element's role
        // is a hit-test on the chat app's main thread — the same thread VoiceOver is waiting on —
        // so only do it when the label is ambiguous ("image", or nothing).
        var verdict = StickerDetector.verdict(for: cursor, axRole: nil)
        if verdict == .notSticker, StickerDetector.labelIsAmbiguous(cursor.text) {
            verdict = StickerDetector.verdict(for: cursor, axRole: StickerDetector.role(at: cursor.bounds))
        }
        guard verdict != .notSticker else { return }
        // Don't repeat ourselves if we just spoke about exactly this item, or are already on it.
        if let last, last.target.isLikelySameItem(as: cursor), Date().timeIntervalSince(last.at) < 20 { return }
        if job != nil { return }
        log.add("Auto: \(verdict) — \(cursor.text)")
        // Let VoiceOver finish its own short announcement first, and skip items the user
        // is only passing over quickly.
        try? await Task.sleep(for: .milliseconds(120))
        guard let still = try? await voiceOver.cursor(), still.isLikelySameItem(as: cursor), job == nil else { return }
        await describeShort(cursor, auto: true, verdict: verdict)
    }

    // MARK: Output helpers

    private func speak(_ text: String, interrupt: Bool, chime: Bool) {
        if chime { earcons.play(.ready) }
        announcer.say(text, interrupt: interrupt)
    }

    private func fail(_ error: any Error) {
        fail(message: (error as? LocalizedError)?.errorDescription ?? error.localizedDescription)
    }

    private func fail(message: String) {
        log.add("Error: \(message)")
        earcons.play(.failed)
        announcer.say(message, interrupt: true)
    }

    private func warnOnceIfEngineUnavailable(_ error: any Error) {
        guard case DescribeError.engineUnavailable(let status)? = error as? DescribeError else { return }
        if let lastEngineWarning, Date().timeIntervalSince(lastEngineWarning) < 600 { return }
        lastEngineWarning = Date()
        announcer.say("StickerSense: \(status.explanation)", interrupt: false)
    }

    private func isTooLarge(_ rect: CGRect) -> Bool {
        let largestScreen = NSScreen.screens.map { $0.frame.width * $0.frame.height }.max() ?? .greatestFiniteMagnitude
        return rect.width * rect.height > largestScreen * 0.45
    }
}

extension Notification.Name {
    static let stickerLibraryChanged = Notification.Name("StickerSenseLibraryChanged")
}

/// Where to look when VoiceOver is off.
enum FocusFallback {
    static func focusedElementFrame() -> CGRect? {
        guard AXIsProcessTrusted() else { return nil }
        let system = AXUIElementCreateSystemWide()
        var focused: CFTypeRef?
        guard AXUIElementCopyAttributeValue(system, kAXFocusedUIElementAttribute as CFString, &focused) == .success,
              let focused, CFGetTypeID(focused) == AXUIElementGetTypeID() else { return nil }
        let element = focused as! AXUIElement
        var positionValue: CFTypeRef?, sizeValue: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, kAXPositionAttribute as CFString, &positionValue) == .success,
              AXUIElementCopyAttributeValue(element, kAXSizeAttribute as CFString, &sizeValue) == .success,
              let positionValue, let sizeValue else { return nil }
        var point = CGPoint.zero, size = CGSize.zero
        AXValueGetValue(positionValue as! AXValue, .cgPoint, &point)
        AXValueGetValue(sizeValue as! AXValue, .cgSize, &size)
        return CGRect(origin: point, size: size)
    }

    /// A sticker-sized square around the mouse pointer (global, top-left origin).
    static func rectUnderMouse() -> CGRect {
        let mouse = NSEvent.mouseLocation
        let primaryHeight = NSScreen.screens.first?.frame.height ?? 0
        let topLeft = CGPoint(x: mouse.x, y: primaryHeight - mouse.y)
        return CGRect(x: topLeft.x - 110, y: topLeft.y - 110, width: 220, height: 220)
    }
}
