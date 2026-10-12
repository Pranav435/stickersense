import StickerKit
import SwiftUI

/// StickerSense's main window: what it's doing right now, and a way into everything.
struct HomeView: View {
    struct Actions {
        var picker: () -> Void
        var history: () -> Void
        var settings: () -> Void
        var setup: () -> Void
        var help: () -> Void
    }

    @Bindable var settings: SettingsStore
    let app: AppModel
    let actions: Actions

    var body: some View {
        Form {
            Section {
                LabeledContent("On-device model", value: app.engineStatus.isReady ? "Ready" : app.engineStatus.explanation)
                Toggle("Describe stickers automatically", isOn: $settings.autoDescribe)
                LabeledContent("Sticker library", value: libraryText)
            } header: {
                Text("Status").accessibilityAddTraits(.isHeader)
            }

            Section {
                Button("Open Sticker Picker", action: actions.picker)
                Button("Sticker History", action: actions.history)
                Button("Settings", action: actions.settings)
                Button("Setup and Permissions", action: actions.setup)
                Button("Hear What StickerSense Can Do", action: actions.help)
            } header: {
                Text("Open").accessibilityAddTraits(.isHeader)
            }

            Section {
                shortcut(settings.describeCombo, "Describe what the VoiceOver cursor is on. Press again within 3 seconds for detail.")
                shortcut(settings.pickerCombo, "Sticker picker")
                if settings.colonTrigger {
                    Text("Two colons (::) in a chat: sticker picker")
                }
                shortcut(settings.historyCombo, "Sticker history")
                shortcut(settings.autoToggleCombo, "Automatic descriptions on or off")
            } header: {
                Text("Shortcuts, from any app").accessibilityAddTraits(.isHeader)
            } footer: {
                Text("StickerSense keeps running in the menu bar when this window is closed.")
            }
        }
        .formStyle(.grouped)
        .frame(minWidth: 460, minHeight: 520)
        .onAppear { app.refreshCounts() }
    }

    private var libraryText: String {
        var text = "\(app.libraryCount) sticker\(app.libraryCount == 1 ? "" : "s")"
        if app.undescribedCount > 0 { text += ", \(app.undescribedCount) waiting to be described" }
        return text
    }

    private func shortcut(_ combo: KeyCombo, _ what: String) -> some View {
        LabeledContent(combo.symbolName, value: what)
            .accessibilityElement(children: .ignore)
            .accessibilityLabel("\(combo.spokenName): \(what)")
    }
}
