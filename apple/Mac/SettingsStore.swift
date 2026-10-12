import Foundation
import Observation
import ServiceManagement
import StickerKit

/// Which apps auto-describe watches.
enum AutoScope: String, CaseIterable, Identifiable {
    case chatApps, everywhere
    var id: String { rawValue }
    var title: String {
        switch self {
        case .chatApps: "Chat apps and browsers"
        case .everywhere: "Every app"
        }
    }
}

/// User preferences, saved in UserDefaults.
@MainActor
@Observable
final class SettingsStore {
    private let defaults: UserDefaults

    var autoDescribe: Bool { didSet { defaults.set(autoDescribe, forKey: "autoDescribe"); onChange() } }
    var autoScope: AutoScope { didSet { defaults.set(autoScope.rawValue, forKey: "autoScope"); onChange() } }
    var earcons: Bool { didSet { defaults.set(earcons, forKey: "earcons"); onChange() } }
    var earconVolume: Double { didSet { defaults.set(earconVolume, forKey: "earconVolume"); onChange() } }
    var includeMeaning: Bool { didSet { defaults.set(includeMeaning, forKey: "includeMeaning"); onChange() } }
    var allowPrivateCloud: Bool { didSet { defaults.set(allowPrivateCloud, forKey: "allowPrivateCloud"); onChange() } }
    var speechRoute: Announcer.Route { didSet { defaults.set(speechRoute.rawValue, forKey: "speechRoute"); onChange() } }
    var describeKey: String { didSet { defaults.set(describeKey, forKey: "describeKey"); onChange(hotkeys: true) } }
    var pickerKey: String { didSet { defaults.set(pickerKey, forKey: "pickerKey"); onChange(hotkeys: true) } }
    var historyKey: String { didSet { defaults.set(historyKey, forKey: "historyKey"); onChange(hotkeys: true) } }
    var autoToggleKey: String { didSet { defaults.set(autoToggleKey, forKey: "autoToggleKey"); onChange(hotkeys: true) } }
    var hasCompletedSetup: Bool { didSet { defaults.set(hasCompletedSetup, forKey: "hasCompletedSetup") } }
    var colonTrigger: Bool { didSet { defaults.set(colonTrigger, forKey: "colonTrigger"); onChange() } }

    var giphyKey: String {
        didSet { Keychain.set(giphyKey.trimmingCharacters(in: .whitespacesAndNewlines), for: Keychain.giphyAccount) }
    }

    var launchAtLogin: Bool {
        get { SMAppService.mainApp.status == .enabled }
        set {
            do {
                if newValue { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
            } catch {
                NSLog("StickerSense: launch at login change failed: \(error)")
            }
        }
    }

    /// Called after any change so the app can apply it.
    var changed: ((_ hotkeysChanged: Bool) -> Void)?

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        defaults.register(defaults: [
            "autoDescribe": true,
            "autoScope": AutoScope.chatApps.rawValue,
            "earcons": true,
            "earconVolume": 0.35,
            "includeMeaning": false,
            "allowPrivateCloud": false,
            "speechRoute": Announcer.Route.automatic.rawValue,
            "describeKey": "D",
            "pickerKey": "S",
            "historyKey": "H",
            "autoToggleKey": "A",
            "hasCompletedSetup": false,
            "colonTrigger": true,
        ])
        autoDescribe = defaults.bool(forKey: "autoDescribe")
        autoScope = AutoScope(rawValue: defaults.string(forKey: "autoScope") ?? "") ?? .chatApps
        earcons = defaults.bool(forKey: "earcons")
        earconVolume = defaults.double(forKey: "earconVolume")
        includeMeaning = defaults.bool(forKey: "includeMeaning")
        allowPrivateCloud = defaults.bool(forKey: "allowPrivateCloud")
        speechRoute = Announcer.Route(rawValue: defaults.string(forKey: "speechRoute") ?? "") ?? .automatic
        describeKey = defaults.string(forKey: "describeKey") ?? "D"
        pickerKey = defaults.string(forKey: "pickerKey") ?? "S"
        historyKey = defaults.string(forKey: "historyKey") ?? "H"
        autoToggleKey = defaults.string(forKey: "autoToggleKey") ?? "A"
        hasCompletedSetup = defaults.bool(forKey: "hasCompletedSetup")
        colonTrigger = defaults.bool(forKey: "colonTrigger")
        giphyKey = Keychain.string(for: Keychain.giphyAccount) ?? ""
    }

    var describeCombo: KeyCombo { .optionShiftCommand(describeKey) }
    var pickerCombo: KeyCombo { .optionShiftCommand(pickerKey) }
    var historyCombo: KeyCombo { .optionShiftCommand(historyKey) }
    var autoToggleCombo: KeyCombo { .optionShiftCommand(autoToggleKey) }

    var describerOptions: DescriberOptions {
        DescriberOptions(allowPrivateCloudForDetail: allowPrivateCloud, language: nil, includeMeaning: includeMeaning)
    }

    private func onChange(hotkeys: Bool = false) {
        changed?(hotkeys)
    }
}
