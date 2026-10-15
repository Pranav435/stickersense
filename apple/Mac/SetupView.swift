import AppKit
import ApplicationServices
import StickerKit
import SwiftUI

@MainActor
@Observable
final class SetupModel {
    enum State: Equatable {
        case ok(String)
        case needsAction(String)
        case optional(String)

        var isOK: Bool { if case .ok = self { true } else { false } }
        var text: String {
            switch self {
            case .ok(let t), .needsAction(let t), .optional(let t): t
            }
        }
    }

    let app: AppModel
    var engine: State = .needsAction("Checking…")
    var voiceOver: State = .needsAction("Checking…")
    var screenRecording: State = .needsAction("Checking…")
    var accessibility: State = .needsAction("Checking…")
    var whatsApp: State = .optional("")
    var stickerMaker: State = .optional("")
    private var poller: Task<Void, Never>?

    init(app: AppModel) {
        self.app = app
    }

    var allRequiredOK: Bool { engine.isOK && voiceOver.isOK && screenRecording.isOK }

    func startPolling() {
        poller?.cancel()
        poller = Task { [weak self] in
            while !Task.isCancelled {
                await self?.refresh()
                try? await Task.sleep(for: .seconds(2))
            }
        }
    }

    func stopPolling() { poller?.cancel() }

    func refresh() async {
        let status = StickerDescriber.status
        app.engineStatus = status
        engine = status.isReady ? .ok("Apple's on-device model is ready.") : .needsAction(status.explanation)

        if !app.voiceOver.isRunning {
            voiceOver = .needsAction("VoiceOver is off. Turn it on with Command F5. (StickerSense can also speak on its own, but works best with VoiceOver.)")
        } else {
            do {
                _ = try await app.voiceOver.cursor()
                voiceOver = .ok("StickerSense can follow the VoiceOver cursor.")
            } catch {
                voiceOver = .needsAction((error as? LocalizedError)?.errorDescription ?? error.localizedDescription)
            }
        }

        screenRecording = ScreenCapturer.hasPermission
            ? .ok("Screen Recording is allowed, so StickerSense can see stickers and their animation.")
            : .needsAction("Screen Recording isn't allowed yet. Without it, StickerSense uses VoiceOver's screenshots, which are slower and can't see animation.")

        accessibility = AXIsProcessTrusted()
            ? .ok("Accessibility is allowed, so the picker can paste into your chat.")
            : .needsAction("Accessibility isn't allowed yet. It's needed to paste stickers from the picker straight into your chat.")

        if !WhatsAppImporter.isInstalled {
            whatsApp = .optional("WhatsApp for Mac isn't installed, so there's nothing to import.")
        } else if app.libraryCount > 0 || !app.importStatus.isEmpty {
            whatsApp = .ok(app.importStatus.isEmpty ? "Your library has \(app.libraryCount) stickers." : app.importStatus)
        } else {
            whatsApp = .optional("Optional: import your WhatsApp stickers so you can find and send them from the picker.")
        }

        let stickerModel = app.settings.stickerModel
        if StickerMaker.isInstalled(stickerModel) {
            stickerMaker = .ok("The sticker maker is ready: type in the picker and press Command G to make your own stickers.")
        } else if let download = app.stickerDownload {
            stickerMaker = .optional("Downloading the sticker maker: \(download.percent) percent.")
        } else {
            stickerMaker = .optional("Optional: download the sticker maker (\(stickerModel.spokenSize)) now, so the picker can make stickers on this Mac. Otherwise it downloads the first time you make one.")
        }
    }

    // MARK: Actions

    func downloadStickerMaker() {
        Task { await app.downloadStickerModel(app.settings.stickerModel) }
    }

    func openAppleIntelligenceSettings() {
        open("x-apple.systempreferences:com.apple.Siri-Settings.extension")
    }

    func fixVoiceOver() {
        if !app.voiceOver.isRunning { return }
        // VoiceOver Utility holds the "Allow VoiceOver to be controlled with AppleScript" checkbox.
        NSWorkspace.shared.openApplication(at: URL(fileURLWithPath: "/System/Applications/Utilities/VoiceOver Utility.app"), configuration: .init())
        app.announcer.say("Opening VoiceOver Utility. In the General category, turn on Allow VoiceOver to be controlled with AppleScript. If macOS asks whether StickerSense may control VoiceOver, choose Allow.", interrupt: false)
    }

    func requestScreenRecording() {
        if !ScreenCapturer.requestPermission() {
            open("x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture")
            app.announcer.say("Opening Screen and System Audio Recording settings. Turn on StickerSense. macOS may ask you to quit and reopen StickerSense.", interrupt: false)
        }
    }

    func requestAccessibility() {
        // The value of kAXTrustedCheckOptionPrompt (a global var Swift 6 won't touch directly).
        let options = ["AXTrustedCheckOptionPrompt": true] as CFDictionary
        if !AXIsProcessTrustedWithOptions(options) {
            app.announcer.say("In the Accessibility settings that opened, turn on StickerSense.", interrupt: false)
        }
    }

    private func open(_ string: String) {
        if let url = URL(string: string) { NSWorkspace.shared.open(url) }
    }
}

struct SetupView: View {
    @Bindable var model: SetupModel
    var describeKeyName: String
    var done: () -> Void

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                Text("Welcome to StickerSense")
                    .font(.title).bold()
                    .accessibilityAddTraits(.isHeader)
                Text("StickerSense describes stickers, GIFs and pictures for you, on this Mac, in any app. Put the VoiceOver cursor on a sticker and press \(describeKeyName). Press it again within 3 seconds for more detail. Stickers and GIFs in chats are also described automatically when the VoiceOver cursor lands on them.")

                Text("Checklist").font(.title2).bold().accessibilityAddTraits(.isHeader)
                SetupRow(title: "On-device model", state: model.engine, actionTitle: "Open Apple Intelligence Settings", action: model.openAppleIntelligenceSettings)
                SetupRow(title: "VoiceOver", state: model.voiceOver, actionTitle: "Open VoiceOver Utility", action: model.fixVoiceOver)
                SetupRow(title: "Screen Recording", state: model.screenRecording, actionTitle: "Allow Screen Recording", action: model.requestScreenRecording)
                SetupRow(title: "Accessibility", state: model.accessibility, actionTitle: "Allow Accessibility", action: model.requestAccessibility)
                SetupRow(title: "WhatsApp stickers", state: model.whatsApp, actionTitle: "Import WhatsApp Stickers") {
                    Task { await model.app.importWhatsApp() }
                }
                SetupRow(title: "Sticker maker", state: model.stickerMaker, actionTitle: "Download Sticker Maker", action: model.downloadStickerMaker)

                Text("Try it").font(.title2).bold().accessibilityAddTraits(.isHeader)
                HStack(alignment: .center, spacing: 16) {
                    PracticeSticker()
                        .frame(width: 140, height: 140)
                        .accessibilityElement()
                        .accessibilityLabel("Practice sticker")
                        .accessibilityAddTraits(.isImage)
                        .accessibilityHint("Press \(describeKeyName) to hear it described.")
                    Text("Move the VoiceOver cursor to the practice sticker, then press \(describeKeyName). Press it again quickly for the detailed description.")
                }

                HStack {
                    Spacer()
                    Button(model.allRequiredOK ? "Done" : "Finish Later", action: done)
                        .keyboardShortcut(.defaultAction)
                }
            }
            .padding(20)
        }
        .frame(minWidth: 600, minHeight: 640)
        .onAppear { model.startPolling() }
        .onDisappear { model.stopPolling() }
    }
}

private struct SetupRow: View {
    let title: String
    let state: SetupModel.State
    let actionTitle: String
    let action: () -> Void

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: state.isOK ? "checkmark.circle.fill" : (stateIsOptional ? "circle.dashed" : "exclamationmark.circle.fill"))
                .foregroundStyle(state.isOK ? .green : (stateIsOptional ? .secondary : .orange))
                .font(.title2)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 4) {
                Text(title).font(.headline)
                Text(state.text).fixedSize(horizontal: false, vertical: true)
            }
            .accessibilityElement(children: .combine)
            .accessibilityLabel("\(title): \(state.isOK ? "done" : (stateIsOptional ? "optional" : "needs attention")). \(state.text)")
            Spacer()
            if !state.isOK {
                Button(actionTitle, action: action)
            }
        }
    }

    private var stateIsOptional: Bool { if case .optional = state { true } else { false } }
}

/// A sticker drawn in SwiftUI, for practising the shortcut. It's sample *content* to be
/// described, so it deliberately looks like a real chat sticker (emoji and caption).
private struct PracticeSticker: View {
    var body: some View {
        ZStack {
            Circle().fill(.yellow)
            VStack(spacing: 2) {
                Text("🦄").font(.system(size: 64))
                Text("you got this!")
                    .font(.system(size: 16, weight: .heavy, design: .rounded))
                    .foregroundStyle(.purple)
            }
        }
    }
}
