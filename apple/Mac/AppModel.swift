import AppKit
import Observation
import StickerKit

/// Shared state for the menu, windows and controllers.
@MainActor
@Observable
final class AppModel {
    let settings: SettingsStore
    let library: StickerLibrary
    let pipeline: StickerPipeline
    /// A second describer so background work (imports, GIPHY results) never queues in
    /// front of a description the user asked for.
    let backgroundPipeline: StickerPipeline
    let pickerSearch: PickerSearch
    let announcer: Announcer
    let earcons: Earcons
    let log: DebugLog
    let voiceOver: VoiceOverBridge
    let stickerMaker = StickerMaker()

    var engineStatus: EngineStatus = StickerDescriber.status
    var libraryCount = 0
    var undescribedCount = 0
    var importStatus: String = ""
    var lastUserActivity = Date.distantPast
    /// The sticker model being downloaded, with how far it's got (0 to 100).
    var stickerDownload: (model: StickerModel, percent: Int)?
    /// Bumped when a sticker model finishes downloading, so views re-check what's installed.
    var stickerModelsChanged = 0
    private var stickerDownloadTask: Task<Bool, Never>?

    private var indexer: Task<Void, Never>?

    init(settings: SettingsStore, voiceOver: VoiceOverBridge, announcer: Announcer, earcons: Earcons, log: DebugLog) {
        self.settings = settings
        self.voiceOver = voiceOver
        self.announcer = announcer
        self.earcons = earcons
        self.log = log
        library = StickerLibrary(directory: StickerLibrary.defaultDirectory())
        pipeline = StickerPipeline(describer: StickerDescriber(options: settings.describerOptions), library: library)
        backgroundPipeline = StickerPipeline(describer: StickerDescriber(options: settings.describerOptions), library: library)
        pickerSearch = PickerSearch(library: library, describer: backgroundPipeline.describer)
        NotificationCenter.default.addObserver(forName: .stickerLibraryChanged, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.refreshCounts() }
        }
        refreshCounts()
    }

    func applySettings() {
        let options = settings.describerOptions
        Task {
            await pipeline.describer.update(options: options)
            await backgroundPipeline.describer.update(options: options)
        }
        earcons.isEnabled = settings.earcons
        earcons.volume = Float(settings.earconVolume)
        announcer.route = settings.speechRoute
    }

    func refreshCounts() {
        Task {
            libraryCount = await library.search("").count
            undescribedCount = await library.undescribed(limit: 100_000).count
        }
    }

    func noteUserActivity() { lastUserActivity = Date() }

    // MARK: Sticker model

    /// Downloads the sticker model, saying how it's going every quarter. Returns whether it's
    /// installed afterwards. A second call while one is running waits for that one.
    @discardableResult
    func downloadStickerModel(_ model: StickerModel) async -> Bool {
        if let running = stickerDownloadTask { return await running.value && StickerMaker.isInstalled(model) }
        if StickerMaker.isInstalled(model) { return true }
        let task = Task<Bool, Never> {
            stickerDownload = (model, 0)
            defer {
                stickerDownload = nil
                stickerDownloadTask = nil
                stickerModelsChanged += 1
            }
            announcer.say("Downloading the sticker maker, \(model.spokenSize). You can keep using StickerSense meanwhile.", interrupt: false)
            log.add("Downloading sticker model \(model.rawValue)")
            do {
                try await stickerMaker.download(model) { fraction in
                    Task { @MainActor in self.stickerDownloadProgressed(model, fraction) }
                }
                log.add("Sticker model \(model.rawValue) downloaded")
                earcons.play(.ready)
                announcer.say("The sticker maker is ready.", interrupt: false)
                Task { await stickerMaker.warmUp(model) }
                return true
            } catch is CancellationError {
                return false
            } catch {
                log.add("Sticker model download failed: \(error.localizedDescription)")
                earcons.play(.failed)
                announcer.say((error as? LocalizedError)?.errorDescription ?? "The sticker maker didn't download. Try again later.")
                return false
            }
        }
        stickerDownloadTask = task
        return await task.value
    }

    private func stickerDownloadProgressed(_ model: StickerModel, _ fraction: Double) {
        let percent = Int(fraction * 100)
        let previous = stickerDownload?.percent ?? 0
        guard stickerDownload != nil, percent > previous else { return }
        stickerDownload = (model, percent)
        if percent / 25 > previous / 25, percent < 100 {
            announcer.say("Sticker maker download: \(percent / 25 * 25) percent.", interrupt: false)
        }
    }

    // MARK: WhatsApp import

    func importWhatsApp(announce: Bool = true) async {
        guard WhatsAppImporter.isInstalled else {
            importStatus = "WhatsApp for Mac isn't installed."
            if announce { announcer.say(importStatus) }
            return
        }
        guard let folder = WhatsAppImporter.askForFolder() else {
            importStatus = "Import cancelled."
            return
        }
        importStatus = "Looking for WhatsApp stickers…"
        if announce { announcer.say(importStatus, interrupt: false) }
        let result = await WhatsAppImporter.importAll(from: [folder], into: library)
        if result.permissionDenied {
            importStatus = "macOS still didn't let StickerSense read WhatsApp's stickers. Turn on StickerSense in System Settings, Privacy and Security, Full Disk Access, then import again."
            NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles")!)
        } else if result.found == 0 {
            importStatus = "No WhatsApp sticker files found yet. Stickers appear after WhatsApp has downloaded them."
        } else {
            importStatus = "Found \(result.found) WhatsApp stickers, \(result.added) new. Describing them in the background while you're idle."
        }
        log.add("WhatsApp import: \(importStatus)")
        if announce { announcer.say(importStatus, interrupt: false) }
        refreshCounts()
        startIndexing()
    }

    // MARK: Background describing

    /// Describes imported stickers one by one, but only while the Mac is genuinely idle — no
    /// keyboard, trackpad or mouse input for 90 seconds — so the on-device model never competes
    /// with VoiceOver while the user is working. Stops the moment any key is pressed.
    func startIndexing() {
        guard indexer == nil else { return }
        indexer = Task { [weak self] in
            while !Task.isCancelled {
                guard let self else { return }
                guard Self.systemIdleSeconds >= 90, !ProcessInfo.processInfo.isLowPowerModeEnabled,
                      StickerDescriber.status.isReady else {
                    try? await Task.sleep(for: .seconds(15))
                    continue
                }
                let batch = await self.library.undescribed(limit: 1)
                guard let record = batch.first else { break }
                do {
                    try await self.backgroundPipeline.describeStored(record)
                } catch {
                    // Unreadable or blocked: mark it so we don't retry forever.
                    await self.library.setReading(StickerReading(kind: .sticker, subject: "Sticker that couldn't be described"), ocrText: "", for: record.id)
                    self.log.add("Background describe failed: \(error.localizedDescription)")
                }
                self.refreshCounts()
                try? await Task.sleep(for: .seconds(4))
            }
            self?.indexer = nil
            self?.refreshCounts()
        }
    }

    /// Seconds since the last keyboard, mouse or trackpad event, system-wide.
    static var systemIdleSeconds: Double {
        CGEventSource.secondsSinceLastEventType(.combinedSessionState, eventType: CGEventType(rawValue: ~0)!)
    }
}
