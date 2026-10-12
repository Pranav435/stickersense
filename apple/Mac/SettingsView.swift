import AppKit
import StickerKit
import SwiftUI

struct SettingsView: View {
    @Bindable var settings: SettingsStore
    let app: AppModel
    var openSetup: () -> Void
    var openLog: () -> Void
    @State private var launchAtLogin = false

    private let letters = KeyCombo.letters.map(\.0)

    var body: some View {
        Form {
            Section("Describing") {
                Toggle("Describe stickers and GIFs automatically when the VoiceOver cursor lands on them", isOn: $settings.autoDescribe)
                Picker("Watch for stickers in", selection: $settings.autoScope) {
                    ForEach(AutoScope.allCases) { Text($0.title).tag($0) }
                }
                .disabled(!settings.autoDescribe)
                Toggle("Add what the sticker means to short descriptions", isOn: $settings.includeMeaning)
                Toggle("Use Apple Private Cloud Compute for detailed descriptions", isOn: $settings.allowPrivateCloud)
                    .disabled(!StickerDescriber.privateCloudAvailable)
                Text(StickerDescriber.privateCloudAvailable
                     ? "Off: everything stays on this Mac. On: only detailed descriptions (pressing the shortcut twice) may be sent to Apple's privacy-preserving servers for better quality. Short and automatic descriptions always stay on this Mac."
                     : "Private Cloud Compute isn't available on this Mac, so everything stays on device.")
                    .font(.callout).foregroundStyle(.secondary)
            }

            Section("Sounds and speech") {
                Toggle("Play quiet sounds while working and before speaking", isOn: $settings.earcons)
                Slider(value: $settings.earconVolume, in: 0.05...1) { Text("Sound volume") }
                    .disabled(!settings.earcons)
                    .accessibilityValue("\(Int(settings.earconVolume * 100)) percent")
                Picker("Send descriptions to VoiceOver with", selection: $settings.speechRoute) {
                    ForEach(Announcer.Route.allCases) { Text($0.title).tag($0) }
                }
            }

            Section("Keyboard shortcuts (Option Shift Command plus a key)") {
                shortcutPicker("Describe what the VoiceOver cursor is on (press twice for detail)", selection: $settings.describeKey)
                shortcutPicker("Open the sticker picker", selection: $settings.pickerKey)
                shortcutPicker("Open sticker history", selection: $settings.historyKey)
                shortcutPicker("Turn automatic descriptions on or off", selection: $settings.autoToggleKey)
            }

            Section("Sticker picker") {
                Toggle("Type two colons (::) in a chat to open the sticker picker", isOn: $settings.colonTrigger)
                Text("The colons are replaced by the sticker you pick. Press Escape to keep them as typed.")
                    .font(.callout).foregroundStyle(.secondary)
                HStack {
                    Text("\(app.libraryCount) stickers in your library\(app.undescribedCount > 0 ? ", \(app.undescribedCount) still being described" : "").")
                    Spacer()
                    Button("Import WhatsApp Stickers") { Task { await app.importWhatsApp() } }
                        .disabled(!WhatsAppImporter.isInstalled)
                }
                if !app.importStatus.isEmpty {
                    Text(app.importStatus).font(.callout).foregroundStyle(.secondary)
                }
                SecureField("GIPHY API key", text: $settings.giphyKey, prompt: Text("Paste your key"))
                    .accessibilityHint("Needed to search GIPHY. Get a free key at developers dot giphy dot com.")
                Link("Get a free GIPHY API key", destination: URL(string: "https://developers.giphy.com/dashboard/")!)
            }

            Section("General") {
                Toggle("Open StickerSense when I log in", isOn: $launchAtLogin)
                    .onChange(of: launchAtLogin) { _, value in settings.launchAtLogin = value }
                HStack {
                    Button("Run Setup Again…", action: openSetup)
                    Button("Show Activity Log", action: openLog)
                }
            }
        }
        .formStyle(.grouped)
        .frame(minWidth: 560, minHeight: 620)
        .onAppear { launchAtLogin = settings.launchAtLogin }
    }

    private func shortcutPicker(_ title: String, selection: Binding<String>) -> some View {
        Picker(title, selection: selection) {
            ForEach(letters, id: \.self) { Text("⌥⇧⌘\($0)").accessibilityLabel("Option Shift Command \($0)").tag($0) }
        }
    }
}
